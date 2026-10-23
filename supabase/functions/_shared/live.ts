// Live duels (phase 2, D1b; Docs/LIVE_DUELS.md): the pure state machine of one live match.
//
// Both phones are online at once. The SERVER owns the seed and decides when to send DRAW; each phone measures
// its own reaction from its own cue presentation (D2) and reports a ShotReport. Reports are ordered by the
// reaction they carry, never by arrival (Fair-Ordering Service, NetGames '03): an attempt is resolved only
// when both slots have reported or the report deadline has passed, and then by the SAME rules as a ghost
// duel, by replaying every attempt so far through playRecordedMatch (the TS port of LocalAuthority). A live
// match is therefore scored exactly like the two players' recordings would be scored against each other.
//
// Pure and deterministic: every transition takes the state and the server time `nowMs` (integer ms) and
// returns the new state plus the broadcast messages it causes. No I/O, no clock, no randomness (the seed is
// chosen by the caller). live_service.ts stores the state (optimistic version, pn_live_save) and sends the
// messages through a LiveBus. Mirrors: Tools/Parity/live_reference.py (independent reference that writes
// Tools/Parity/live_vectors.json) and Assets/PawNoon/Code/Core/LiveMatchState.cs (client, engine-free).
//
//   lobby --join--> ready --both ready--> countdown --cue due--> draw --both reported / deadline--> (resolve)
//     |               |  ^                   |                     |                                  |
//     |               |  +---- next attempt -+---------------------+---- match not over <-------------+
//     v               v (ready deadline: the missing player forfeits; nobody ready = abandoned)
//  cancelled       finished <-- KO / 3 rounds / 9 attempts / forfeit / abandoned
import { anticipationNs, type DuelConfig, noShotTimeoutNs, reportSlackNs } from "./duel/config.ts";
import { curveFor, type GunId, isGunId } from "./duel/loadouts.ts";
import { type AttemptInput, type AttemptResult, type EndReason, playRecordedMatch } from "./duel/recorded_match.ts";
import { schedulesForSeed } from "./duel/schedule.ts";
import { suddenDeathTriggers } from "./duel/sudden_death.ts";
import { LIVE_PLAUS } from "./server_config.ts";

/** Rules of the live protocol. Not secret: the client mirror (LiveRules in LiveMatchState.cs) uses the same. */
export const LIVE_RULES = {
  version: 1,
  /** D7: 3 resolved rounds, at most 9 countdowns (void replays). Same as ghost duels. */
  rounds: 3,
  maxAttempts: 9,
  /** A friend lobby waits this long for the friend; a quick lobby this long for a stranger (then the client
   *  falls back to a ghost duel). */
  friendLobbyMs: 600_000,
  quickLobbyMs: 30_000,
  /** Reconnection window N: after a join or a resolved attempt, both players must send `ready` for the next
   *  attempt within this. The one who does not forfeits; when neither does, the match is abandoned. */
  readyTimeoutMs: 20_000,
  /** round_start is broadcast this long before "3" so both phones can show "3" at the same server instant. */
  startLeadMs: 800,
  /** Reports may arrive this long after cue + noShotTimeout + reportSlack (network). Later = Late. */
  networkGraceMs: 1_500,
  /** An Early (pressed before the cue reached that phone) may arrive this long after the cue was sent. */
  earlyGraceMs: 1_000,
  /** Client-side cue offset ("3" -> presented cue) against the server's countdown + hold (+ the cue's own
   *  lateness): down-link latency and an unsynced "3" make it later, never much earlier. */
  cueEarlyToleranceNs: 200_000_000,
  cueLateToleranceNs: 2_000_000_000,
} as const;

export type LiveKind = "friend" | "quick";
export type LiveMode = "casual" | "ranked";
export type LivePhase = "lobby" | "ready" | "countdown" | "draw" | "finished" | "cancelled";
export type LiveEnd = EndReason | "forfeit" | "abandoned" | "lobby_expired" | "host_left";
export type LiveVerdict =
  | "Accepted"
  | "Duplicate"
  | "Stale"
  | "WrongRound"
  | "Implausible"
  | "Inconsistent"
  | "Late"
  | "NotPlaying"
  | "Taken";

/** A ShotReport as a phone sends it (Protocol.ShotReport + countdownStartNs + the phone's ClockSync RTT).
 *  All *Ns on the reporting phone's own monotonic clock. The slot is never taken from the client. */
export interface LiveReportIn {
  roundId: number;
  kind: "Shot" | "Early";
  reactionNs: number;
  cuePresentedNs: number;
  inputNs: number;
  countdownStartNs: number;
  /** The phone's ClockSync round-trip estimate (ms), optional; only feeds the soft reaction-vs-RTT flags. */
  rttMs?: number | null;
}

export interface LiveReport extends LiveReportIn {
  receivedAtMs: number;
}

export interface LivePlayer {
  userId: string | null;
  /** Attempt index this player is ready for, -1 = not ready. */
  ready: number;
}

export interface LiveRound {
  attempt: number;
  roundId: number;
  roundNumber: number;
  holdNs: number;
  cueOffsetNs: number;
  startAtMs: number;
  /** SECRET until sent: never in a message or a client view. */
  cueDueAtMs: number;
  cueSentAtMs: number | null;
  reports: [LiveReport | null, LiveReport | null];
  /** Soft plausibility flags per slot (server only). */
  flags: [string[], string[]];
}

export interface LiveAttempt {
  attempt: number;
  roundId: number;
  roundNumber: number;
  holdNs: number;
  startAtMs: number;
  cueSentAtMs: number | null;
  resolvedAtMs: number;
  reports: [LiveReport | null, LiveReport | null];
  inputs: [AttemptInput, AttemptInput];
  flags: [string[], string[]];
  view: AttemptView;
}

/** One resolved attempt as both players see it. */
export interface AttemptView {
  void: boolean;
  tie: boolean;
  winner: number;
  outcomes: [string, string];
  /** Shot / anticipation reactions, else null. */
  reactionsNs: [number | null, number | null];
  damage: [number, number];
  grades: [string, string];
  hp: [number, number];
}

export interface LiveResult {
  /** 0, 1, or -1 (draw / abandoned). */
  winner: number;
  endReason: LiveEnd;
  /** Slot that forfeited (left, or missed the ready deadline), else -1. */
  forfeitedBy: number;
  hp: [number, number];
  roundWins: [number, number];
}

export interface LiveState {
  v: 1;
  kind: LiveKind;
  mode: LiveMode;
  /** SECRET until the match is over (it decides every hold). */
  seed: number;
  rounds: number;
  maxAttempts: number;
  guns: [GunId, GunId];
  players: [LivePlayer, LivePlayer];
  phase: LivePhase;
  /** lobby: expiry; ready: ready deadline; countdown: = round.cueDueAtMs (secret); draw: report deadline. */
  deadlineMs: number;
  /** Index of the attempt in play (countdown / draw) or the next one (ready). */
  attempt: number;
  round: LiveRound | null;
  history: LiveAttempt[];
  hp: [number, number];
  roundWins: [number, number];
  result: LiveResult | null;
  createdAtMs: number;
}

export interface LiveMsg {
  event: "joined" | "ready" | "round_start" | "draw" | "round_result" | "match_over" | "cancelled";
  payload: Record<string, unknown>;
}

export interface Step {
  state: LiveState;
  out: LiveMsg[];
  verdict: LiveVerdict;
  /** The state changed (the caller must save it before broadcasting `out`). */
  changed: boolean;
}

const CONSISTENCY_TOLERANCE_NS = 1000; // 1 us, as LocalAuthority.Validate
const TERMINAL: readonly string[] = ["ko", "double_ko", "rounds", "max_attempts"];
const MS = 1_000_000;

function clone(s: LiveState): LiveState {
  return JSON.parse(JSON.stringify(s)) as LiveState;
}

function ceilMs(ns: number): number {
  return Math.floor((ns + MS - 1) / MS);
}

/** Report deadline after the cue: noShotTimeout + reportSlack + networkGrace. */
export function reportWindowMs(cfg: DuelConfig): number {
  return ceilMs(noShotTimeoutNs(cfg) + reportSlackNs(cfg)) + LIVE_RULES.networkGraceMs;
}

export function createLive(
  o: { kind: LiveKind; mode: LiveMode; seed: number; hostId: string; hostGun?: GunId; nowMs: number },
): LiveState {
  if (!Number.isInteger(o.seed) || o.seed < -2147483648 || o.seed > 2147483647) throw new RangeError("seed must be an int32");
  return {
    v: 1,
    kind: o.kind,
    mode: o.mode,
    seed: o.seed,
    rounds: LIVE_RULES.rounds,
    maxAttempts: LIVE_RULES.maxAttempts,
    guns: [o.hostGun && isGunId(o.hostGun) ? o.hostGun : "standard", "standard"],
    players: [{ userId: o.hostId, ready: -1 }, { userId: null, ready: -1 }],
    phase: "lobby",
    deadlineMs: o.nowMs + (o.kind === "friend" ? LIVE_RULES.friendLobbyMs : LIVE_RULES.quickLobbyMs),
    attempt: 0,
    round: null,
    history: [],
    hp: [0, 0],
    roundWins: [0, 0],
    result: null,
    createdAtMs: o.nowMs,
  };
}

/** Which slot `userId` plays, or -1. */
export function slotOf(s: LiveState, userId: string): number {
  if (s.players[0].userId === userId) return 0;
  if (s.players[1].userId === userId) return 1;
  return -1;
}

export function isOver(s: LiveState): boolean {
  return s.phase === "finished" || s.phase === "cancelled";
}

// ------------------------------------------------------------------------------------------ transitions

/** Applies every deadline that has passed at `nowMs` (lobby expiry, ready timeout, the cue, the report deadline). */
export function tickLive(cfg: DuelConfig, s0: LiveState, nowMs: number): Step {
  const s = clone(s0);
  const out: LiveMsg[] = [];
  const changed = advance(cfg, s, nowMs, out);
  return { state: changed ? s : s0, out, verdict: "Accepted", changed };
}

export function joinLive(cfg: DuelConfig, s0: LiveState, userId: string, gun: GunId | undefined, nowMs: number): Step {
  const s = clone(s0);
  const out: LiveMsg[] = [];
  let changed = advance(cfg, s, nowMs, out);
  let verdict: LiveVerdict = "Accepted";
  if (s.players[1].userId === userId) verdict = "Duplicate";
  else if (s.phase !== "lobby") verdict = isOver(s) ? "NotPlaying" : "Taken";
  else if (s.players[0].userId === userId) verdict = "Taken";
  else {
    s.players[1] = { userId, ready: -1 };
    s.guns[1] = gun && isGunId(gun) ? gun : "standard";
    s.hp = [cfg.maxHp, cfg.maxHp];
    enterReady(s, nowMs);
    out.push({ event: "joined", payload: { attempt: s.attempt, readyInMs: LIVE_RULES.readyTimeoutMs } });
    changed = true;
  }
  return { state: changed ? s : s0, out, verdict, changed };
}

/** The player in `slot` is ready for attempt `attempt` (the first `ready` after a join, after every resolved
 *  attempt, and after a reconnection). When both are ready the round starts. */
export function readyLive(cfg: DuelConfig, s0: LiveState, slot: number, attempt: number, nowMs: number): Step {
  const s = clone(s0);
  const out: LiveMsg[] = [];
  let changed = advance(cfg, s, nowMs, out);
  let verdict: LiveVerdict = "Accepted";
  if (slot !== 0 && slot !== 1) verdict = "NotPlaying";
  else if (s.phase === "lobby" || isOver(s)) verdict = "NotPlaying";
  else if (s.phase === "countdown" || s.phase === "draw") verdict = attempt === s.attempt ? "Duplicate" : (attempt < s.attempt ? "Stale" : "WrongRound");
  else if (attempt < s.attempt) verdict = "Stale";
  else if (attempt > s.attempt) verdict = "WrongRound";
  else if (s.players[slot].ready === attempt) verdict = "Duplicate";
  else {
    s.players[slot].ready = attempt;
    changed = true;
    out.push({ event: "ready", payload: { slot, attempt } });
    if (s.players[1 - slot].ready === attempt) startRound(cfg, s, nowMs, out);
  }
  return { state: changed ? s : s0, out, verdict, changed };
}

/** A ShotReport from `slot` (the caller's own slot, from the auth user, never from the body). */
export function reportLive(cfg: DuelConfig, s0: LiveState, slot: number, rep: LiveReportIn, nowMs: number): Step {
  const s = clone(s0);
  const out: LiveMsg[] = [];
  let changed = advance(cfg, s, nowMs, out);
  const verdict = validateReport(cfg, s, slot, rep, nowMs);
  if (verdict === "Accepted") {
    const r = s.round!;
    const stored: LiveReport = {
      roundId: rep.roundId,
      kind: rep.kind,
      reactionNs: rep.reactionNs,
      cuePresentedNs: rep.cuePresentedNs,
      inputNs: rep.inputNs,
      countdownStartNs: rep.countdownStartNs,
      rttMs: typeof rep.rttMs === "number" && Number.isFinite(rep.rttMs) ? rep.rttMs : null,
      receivedAtMs: nowMs,
    };
    r.reports[slot] = stored;
    if (rep.kind === "Shot") r.flags[slot] = shotFlags(r, stored, nowMs);
    changed = true;
    // fair ordering: nothing is decided on arrival; once both slots have spoken the reactions decide
    if (r.reports[0] && r.reports[1]) resolveAttempt(cfg, s, nowMs, out);
  }
  return { state: changed ? s : s0, out, verdict, changed };
}

/** `slot` leaves (or the client gives up). Lobby: the host closes it. Playing: the other player wins. */
export function forfeitLive(cfg: DuelConfig, s0: LiveState, slot: number, nowMs: number): Step {
  const s = clone(s0);
  const out: LiveMsg[] = [];
  let changed = advance(cfg, s, nowMs, out);
  let verdict: LiveVerdict = "Accepted";
  if (isOver(s) || (slot !== 0 && slot !== 1)) verdict = "NotPlaying";
  else if (s.phase === "lobby") {
    cancel(s, "host_left", out);
    changed = true;
  } else {
    finish(s, 1 - slot, "forfeit", slot, out);
    changed = true;
  }
  return { state: changed ? s : s0, out, verdict, changed };
}

// ------------------------------------------------------------------------------------------ internals

function enterReady(s: LiveState, nowMs: number): void {
  s.phase = "ready";
  s.round = null;
  s.players[0].ready = -1;
  s.players[1].ready = -1;
  s.deadlineMs = nowMs + LIVE_RULES.readyTimeoutMs;
}

function nextRoundNumber(s: LiveState): number {
  return s.history.filter((h) => !h.view.void).length + 1;
}

function startRound(cfg: DuelConfig, s: LiveState, nowMs: number, out: LiveMsg[]): void {
  const k = s.attempt;
  const sched = schedulesForSeed(cfg, s.seed, k + 1)[k];
  const startAtMs = nowMs + LIVE_RULES.startLeadMs;
  const cueDueAtMs = startAtMs + ceilMs(sched.cueOffsetNs);
  s.round = {
    attempt: k,
    roundId: k + 1,
    roundNumber: nextRoundNumber(s),
    holdNs: sched.holdNs,
    cueOffsetNs: sched.cueOffsetNs,
    startAtMs,
    cueDueAtMs,
    cueSentAtMs: null,
    reports: [null, null],
    flags: [[], []],
  };
  s.phase = "countdown";
  s.deadlineMs = cueDueAtMs;
  const lastLight = suddenDeathTriggers(cfg, s.hp[0], s.hp[1]);
  out.push({
    event: "round_start",
    payload: {
      roundId: s.round.roundId,
      attempt: k,
      roundNumber: s.round.roundNumber,
      startAtMs,
      countdownBeats: sched.beats,
      beatNs: sched.beatNs,
      damageMultiplier: lastLight ? cfg.suddenDeathDamageMultiplier : 1,
    },
  });
}

/** Every deadline that has passed, in order. Returns true when anything changed. */
function advance(cfg: DuelConfig, s: LiveState, nowMs: number, out: LiveMsg[]): boolean {
  let changed = false;
  for (let guard = 0; guard < 8; guard++) {
    if (isOver(s) || nowMs < s.deadlineMs) return changed;
    changed = true;
    switch (s.phase) {
      case "lobby":
        cancel(s, "lobby_expired", out);
        break;
      case "ready": {
        const r0 = s.players[0].ready === s.attempt;
        const r1 = s.players[1].ready === s.attempt;
        if (r0 && !r1) finish(s, 0, "forfeit", 1, out);
        else if (r1 && !r0) finish(s, 1, "forfeit", 0, out);
        else finish(s, -1, "abandoned", -1, out);
        break;
      }
      case "countdown": {
        const r = s.round!;
        r.cueSentAtMs = nowMs;
        s.phase = "draw";
        s.deadlineMs = nowMs + reportWindowMs(cfg);
        out.push({ event: "draw", payload: { roundId: r.roundId, cueSeq: 1 } });
        break;
      }
      case "draw":
        resolveAttempt(cfg, s, nowMs, out);
        break;
    }
  }
  return changed;
}

function validateReport(cfg: DuelConfig, s: LiveState, slot: number, rep: LiveReportIn, nowMs: number): LiveVerdict {
  if (slot !== 0 && slot !== 1) return "NotPlaying";
  if (s.phase === "lobby" || isOver(s)) return "NotPlaying";
  if (s.phase === "ready") return rep.roundId <= s.attempt ? "Stale" : "WrongRound";
  const r = s.round!;
  if (rep.roundId < r.roundId) return "Stale";
  if (rep.roundId !== r.roundId) return "WrongRound";
  if (r.reports[slot]) return "Duplicate";
  if (!Number.isSafeInteger(rep.reactionNs) || !Number.isSafeInteger(rep.cuePresentedNs) ||
    !Number.isSafeInteger(rep.inputNs) || !Number.isSafeInteger(rep.countdownStartNs)) return "Inconsistent";
  if (rep.countdownStartNs <= 0) return "Inconsistent";
  const lateNs = Math.max(0, (r.cueSentAtMs ?? r.cueDueAtMs) - r.cueDueAtMs) * MS;
  const expectedOffset = r.cueOffsetNs + lateNs;

  if (rep.kind === "Early") {
    if (r.cueSentAtMs !== null && nowMs > r.cueSentAtMs + LIVE_RULES.earlyGraceMs) return "Late";
    if (rep.inputNs < rep.countdownStartNs) return "Inconsistent";
    if (rep.cuePresentedNs !== 0 && rep.inputNs >= rep.cuePresentedNs) return "Inconsistent";
    if (rep.inputNs - rep.countdownStartNs > expectedOffset + LIVE_RULES.cueLateToleranceNs) return "Inconsistent";
    return "Accepted";
  }
  if (rep.kind !== "Shot") return "Implausible";
  if (r.cueSentAtMs === null) return "Implausible"; // nobody can react to a cue that was not sent
  if (nowMs > s.deadlineMs) return "Late";
  if (rep.reactionNs < 0 || rep.reactionNs > noShotTimeoutNs(cfg)) return "Implausible";
  const diff = (rep.inputNs - rep.cuePresentedNs) - rep.reactionNs;
  if (diff > CONSISTENCY_TOLERANCE_NS || diff < -CONSISTENCY_TOLERANCE_NS) return "Inconsistent";
  if (rep.cuePresentedNs <= rep.countdownStartNs) return "Inconsistent";
  const off = rep.cuePresentedNs - rep.countdownStartNs;
  if (off < expectedOffset - LIVE_RULES.cueEarlyToleranceNs || off > expectedOffset + LIVE_RULES.cueLateToleranceNs) return "Inconsistent";
  // the server saw this much time pass since it sent the cue; a reaction cannot be longer (LocalAuthority's rule)
  if ((nowMs - r.cueSentAtMs) * MS + reportSlackNs(cfg) < rep.reactionNs) return "Implausible";
  return "Accepted";
}

/** Soft reaction-vs-RTT flags (never reject, never shown): see LIVE_PLAUS in server_config.ts. */
function shotFlags(r: LiveRound, rep: LiveReport, nowMs: number): string[] {
  const flags: string[] = [];
  if (rep.rttMs === null || rep.rttMs === undefined || rep.rttMs < 0 || r.cueSentAtMs === null) return flags;
  const excessMs = (nowMs - r.cueSentAtMs) - rep.reactionNs / MS;
  if (excessMs > rep.rttMs * LIVE_PLAUS.excessRttFactor + LIVE_PLAUS.excessSlackMs) flags.push("excess_latency");
  if (rep.rttMs > excessMs + LIVE_PLAUS.rttInflateSlackMs) flags.push("rtt_inflated");
  return flags;
}

function inputOf(cfg: DuelConfig, rep: LiveReport | null): AttemptInput {
  if (!rep) return { kind: "noshot", reactionNs: 0 };
  if (rep.kind === "Early") return { kind: "early", reactionNs: 0 };
  return { kind: rep.reactionNs < anticipationNs(cfg) ? "anticipation" : "shot", reactionNs: rep.reactionNs };
}

function viewOf(a: AttemptResult, inputs: [AttemptInput, AttemptInput]): AttemptView {
  const shotLike = (i: 0 | 1) => (a.outcomes[i] === "Shot" || a.outcomes[i] === "Anticipation") ? inputs[i].reactionNs : null;
  return {
    void: a.void,
    tie: a.tie,
    winner: a.winner,
    outcomes: [a.outcomes[0], a.outcomes[1]],
    reactionsNs: [shotLike(0), shotLike(1)],
    damage: [a.damage[0], a.damage[1]],
    grades: [a.grades[0], a.grades[1]],
    hp: [a.hp[0], a.hp[1]],
  };
}

/** Curves of this match: ranked = standard for both (§6), casual = each player's server-verified gun. */
export function curvesOf(cfg: DuelConfig, s: LiveState) {
  const ranked = s.mode === "ranked";
  return [curveFor(cfg, s.guns[0], ranked), curveFor(cfg, s.guns[1], ranked)] as [ReturnType<typeof curveFor>, ReturnType<typeof curveFor>];
}

/** Recomputes the match from every attempt so far (the ghost-duel rules, playRecordedMatch). */
export function replayLive(cfg: DuelConfig, s: LiveState) {
  return playRecordedMatch(
    cfg,
    { seed: s.seed, rounds: s.rounds, maxAttempts: s.maxAttempts, curves: curvesOf(cfg, s) },
    s.history.map((h) => h.inputs[0]),
    s.history.map((h) => h.inputs[1]),
  );
}

function resolveAttempt(cfg: DuelConfig, s: LiveState, nowMs: number, out: LiveMsg[]): void {
  const r = s.round!;
  const inputs: [AttemptInput, AttemptInput] = [inputOf(cfg, r.reports[0]), inputOf(cfg, r.reports[1])];
  const entry: LiveAttempt = {
    attempt: r.attempt,
    roundId: r.roundId,
    roundNumber: r.roundNumber,
    holdNs: r.holdNs,
    startAtMs: r.startAtMs,
    cueSentAtMs: r.cueSentAtMs,
    resolvedAtMs: nowMs,
    reports: r.reports,
    inputs,
    flags: r.flags,
    view: null as unknown as AttemptView,
  };
  s.history.push(entry);
  const res = replayLive(cfg, s);
  if (res.attempts.length !== s.history.length) throw new Error(`live replay played ${res.attempts.length} of ${s.history.length} attempts`);
  const a = res.attempts[res.attempts.length - 1];
  if (a.roundNumber !== r.roundNumber) throw new Error(`live round number ${r.roundNumber} != replay ${a.roundNumber}`);
  entry.view = viewOf(a, inputs);
  s.hp = [res.hp[0], res.hp[1]];
  s.roundWins = [res.roundWins[0], res.roundWins[1]];
  s.round = null;
  const over = TERMINAL.includes(res.endReason);
  out.push({
    event: "round_result",
    payload: { roundId: r.roundId, attempt: r.attempt, roundNumber: r.roundNumber, ...entry.view, roundWins: s.roundWins, matchOver: over },
  });
  s.attempt = s.history.length;
  if (over) finish(s, res.winner, res.endReason, -1, out);
  else enterReady(s, nowMs);
}

function finish(s: LiveState, winner: number, endReason: LiveEnd, forfeitedBy: number, out: LiveMsg[]): void {
  s.phase = "finished";
  s.round = null;
  s.deadlineMs = Number.MAX_SAFE_INTEGER;
  s.result = { winner, endReason, forfeitedBy, hp: [s.hp[0], s.hp[1]], roundWins: [s.roundWins[0], s.roundWins[1]] };
  // the seed is revealed only now (anyone may verify every hold of the match)
  out.push({ event: "match_over", payload: { ...s.result, seed: s.seed, attempts: s.history.length } });
}

function cancel(s: LiveState, endReason: LiveEnd, out: LiveMsg[]): void {
  s.phase = "cancelled";
  s.round = null;
  s.deadlineMs = Number.MAX_SAFE_INTEGER;
  s.result = { winner: -1, endReason, forfeitedBy: -1, hp: [s.hp[0], s.hp[1]], roundWins: [0, 0] };
  out.push({ event: "cancelled", payload: { endReason } });
}

// ------------------------------------------------------------------------------------------ client view

/** What a player may see (live-round snapshot, reconnection). Never the seed (until over) or a cue due time. */
export function liveView(s: LiveState, slot: number, nowMs: number): Record<string, unknown> {
  const r = s.round;
  const showDeadline = s.phase === "lobby" || s.phase === "ready" || s.phase === "draw";
  return {
    phase: s.phase,
    kind: s.kind,
    mode: s.mode,
    you: slot,
    attempt: s.attempt,
    ready: [s.players[0].ready === s.attempt && s.phase === "ready", s.players[1].ready === s.attempt && s.phase === "ready"],
    opponentJoined: s.players[1].userId !== null,
    deadlineInMs: showDeadline && s.deadlineMs !== Number.MAX_SAFE_INTEGER ? Math.max(0, s.deadlineMs - nowMs) : null,
    round: r
      ? {
        roundId: r.roundId,
        roundNumber: r.roundNumber,
        startAtMs: r.startAtMs,
        // a reconnecting phone that missed the broadcast presents the cue from here (its reaction is still
        // measured from its own presentation, D2)
        cue: r.cueSentAtMs !== null ? { roundId: r.roundId, cueSeq: 1 } : null,
        reported: slot === 0 || slot === 1 ? r.reports[slot] !== null : false,
      }
      : null,
    hp: s.hp,
    roundWins: s.roundWins,
    history: s.history.map((h) => ({ attempt: h.attempt, roundId: h.roundId, roundNumber: h.roundNumber, ...h.view })),
    result: s.result ? { ...s.result, seed: s.seed } : null,
    rules: { rounds: s.rounds, maxAttempts: s.maxAttempts, readyTimeoutMs: LIVE_RULES.readyTimeoutMs, startLeadMs: LIVE_RULES.startLeadMs },
  };
}

/** Parses a report body (shape only; the rules are in reportLive). Returns null when malformed. */
export function parseLiveReport(v: unknown): LiveReportIn | null {
  if (v === null || typeof v !== "object" || Array.isArray(v)) return null;
  const o = v as Record<string, unknown>;
  const int = (k: string) => (typeof o[k] === "number" && Number.isSafeInteger(o[k]) ? o[k] as number : null);
  const roundId = int("roundId"), reactionNs = int("reactionNs"), cuePresentedNs = int("cuePresentedNs");
  const inputNs = int("inputNs"), countdownStartNs = int("countdownStartNs");
  if (roundId === null || reactionNs === null || cuePresentedNs === null || inputNs === null || countdownStartNs === null) return null;
  if (o.kind !== "Shot" && o.kind !== "Early") return null;
  let rttMs: number | null = null;
  if (o.rttMs !== undefined && o.rttMs !== null) {
    if (typeof o.rttMs !== "number" || !Number.isFinite(o.rttMs) || o.rttMs < 0 || o.rttMs > 60_000) return null;
    rttMs = o.rttMs;
  }
  return { roundId, kind: o.kind, reactionNs, cuePresentedNs, inputNs, countdownStartNs, rttMs };
}
