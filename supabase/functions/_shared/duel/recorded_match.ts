// Recorded ("ghost") PvP match: replays two recordings through the ported LocalAuthority
// (SPEC_V2 §4.0, §4.4). This is the server's authoritative recompute; it never looks at any
// result the client computed.
//
// Model (must match ParityVectorsTests.PlayMatch in C# and the client's GhostOpponent, M4):
//  - Both players play the same seed, so countdown attempt k has the same hold for both
//    (LocalAuthority draws one schedule per StartCountdown, void attempts included).
//  - recording.rounds[k] is the k-th ATTEMPT (countdown) of that recording, in play order.
//    Slot 0 = the submitting player, slot 1 = the opponent recording; attempt k is played against
//    attempt k.
//  - Each attempt is resolved by LocalAuthority's real rules (fouls, anticipation, tie window,
//    no-shot, kill stops the slower shot, HP carries over, void when both foul).
//  - The match ends, checked before each attempt in this order:
//      ko / double_ko       LocalAuthority ended the match (HP 0)
//      rounds               `rounds` non-void attempts were resolved (3 in PvP, D7)
//      max_attempts         safety cap on void replays
//      opponent_exhausted   the opponent recording has no attempt k (their own match needed fewer)
//      player_exhausted     the submitter has no attempt k -> the server rejects the recording
//  - Winner: the KO winner; otherwise more HP left (= more total damage dealt); equal = draw.
import type { DuelConfig } from "./config.ts";
import type { DamageCurveDef, Grade } from "./damage.ts";
import { LocalAuthority, type ShotReport, type SlotOutcome } from "./authority.ts";

/** One attempt as recorded (§4.2 kind + reaction). Anything else in the recording is audit data. */
export type AttemptKind = "shot" | "anticipation" | "early" | "noshot";

export interface AttemptInput {
  kind: AttemptKind;
  /** shot / anticipation: inputNs - cuePresentedNs. Ignored for early / noshot. */
  reactionNs: number;
}

export type EndReason =
  | "ko"
  | "double_ko"
  | "rounds"
  | "max_attempts"
  | "opponent_exhausted"
  | "player_exhausted";

export interface AttemptResult {
  roundNumber: number;
  holdNs: number;
  cueOffsetNs: number;
  void: boolean;
  tie: boolean;
  winner: number;
  outcomes: [SlotOutcome, SlotOutcome];
  damage: [number, number];
  grades: [Grade, Grade];
  hp: [number, number];
  matchOver: boolean;
  matchWinner: number;
}

export interface MatchResult {
  attempts: AttemptResult[];
  attemptsUsed: number;
  resolvedRounds: number;
  endReason: EndReason;
  /** 0, 1, or -1 for a draw. */
  winner: number;
  hp: [number, number];
  roundWins: [number, number];
}

export interface MatchOptions {
  seed: number;
  rounds: number;
  maxAttempts: number;
  /** Per-slot damage curves (casual loadouts). Omit for the standard curve (ranked). */
  curves?: [DamageCurveDef, DamageCurveDef];
}

// Synthetic authority clock. Values are arbitrary but fixed so the C# harness can mirror them.
export const HARNESS = {
  t0Ns: 10_000_000_000,
  /** Both reports are delivered this long after the cue: past timeout + slack, so nothing is pending. */
  afterCueNs: 2_000_000_000,
  betweenNs: 1_000_000_000,
  earlyAfterStartNs: 1_000_000_000,
} as const;

function toReport(auth: LocalAuthority, slot: number, a: AttemptInput, cueNs: number, startNs: number): ShotReport | null {
  switch (a.kind) {
    case "noshot":
      return null;
    case "early":
      return {
        roundId: auth.round.roundId,
        slot,
        kind: "Early",
        reactionNs: 0,
        cuePresentedNs: 0,
        inputNs: startNs + HARNESS.earlyAfterStartNs,
      };
    case "shot":
    case "anticipation":
      // The authority derives anticipation from the reaction itself, exactly like C#.
      return {
        roundId: auth.round.roundId,
        slot,
        kind: "Shot",
        reactionNs: a.reactionNs,
        cuePresentedNs: cueNs,
        inputNs: cueNs + a.reactionNs,
      };
  }
}

/** StartCountdown at `now`, tick at the scheduled cue, deliver both reports 2 s later. */
export function runExchange(auth: LocalAuthority, now: number, s0: AttemptInput, s1: AttemptInput): number {
  if (!auth.startCountdown(now)) throw new Error("startCountdown refused");
  const start = now;
  now = start + auth.round.schedule!.cueOffsetNs;
  auth.tick(now);
  if (auth.phase !== "Draw") throw new Error(`expected Draw, got ${auth.phase}`);
  const cue = auth.round.cueDecidedNs;
  now = cue + HARNESS.afterCueNs;
  const reports: ShotReport[] = [];
  const r0 = toReport(auth, 0, s0, cue, start);
  const r1 = toReport(auth, 1, s1, cue, start);
  if (r0) reports.push(r0);
  if (r1) reports.push(r1);
  auth.tick(now, reports);
  return now;
}

export function attemptView(auth: LocalAuthority): AttemptResult {
  const r = auth.round;
  const m = auth.match;
  return {
    roundNumber: r.roundNumber,
    holdNs: r.schedule!.holdNs,
    cueOffsetNs: r.schedule!.cueOffsetNs,
    void: r.isVoid,
    tie: r.isTie,
    winner: r.winnerSlot,
    outcomes: [r.slots[0].outcome, r.slots[1].outcome],
    damage: [r.slots[0].damageDealt, r.slots[1].damageDealt],
    grades: [r.slots[0].grade, r.slots[1].grade],
    hp: [m.hp[0], m.hp[1]],
    matchOver: m.over,
    matchWinner: m.winner,
  };
}

/** Plays a whole recorded match. Pure and deterministic. */
export function playRecordedMatch(
  cfg: DuelConfig,
  opts: MatchOptions,
  player: readonly AttemptInput[],
  opponent: readonly AttemptInput[],
): MatchResult {
  const auth = new LocalAuthority(cfg, opts.seed, opts.curves);
  auth.startMatch();
  let now: number = HARNESS.t0Ns;
  const attempts: AttemptResult[] = [];
  let resolved = 0;
  let k = 0;
  let end: EndReason;
  for (;;) {
    if (auth.match.over) {
      end = auth.match.winner >= 0 ? "ko" : "double_ko";
      break;
    }
    if (resolved >= opts.rounds) {
      end = "rounds";
      break;
    }
    if (k >= opts.maxAttempts) {
      end = "max_attempts";
      break;
    }
    if (k >= opponent.length) {
      end = "opponent_exhausted";
      break;
    }
    if (k >= player.length) {
      end = "player_exhausted";
      break;
    }
    now = runExchange(auth, now, player[k], opponent[k]);
    attempts.push(attemptView(auth));
    if (!auth.round.isVoid) resolved++;
    k++;
    now += HARNESS.betweenNs;
    if (auth.phase === "Result" && !auth.match.over) auth.nextRound();
  }
  const hp = auth.match.hp;
  const winner = auth.match.over ? auth.match.winner : (hp[0] > hp[1] ? 0 : (hp[1] > hp[0] ? 1 : -1));
  return {
    attempts,
    attemptsUsed: k,
    resolvedRounds: resolved,
    endReason: end,
    winner,
    hp: [hp[0], hp[1]],
    roundWins: [auth.match.roundWins[0], auth.match.roundWins[1]],
  };
}

/** Single exchange from given HP (parity "rounds" vectors; seed 1 as in the C# harness). */
export function playSingleExchange(
  cfg: DuelConfig,
  hp: [number, number],
  s0: AttemptInput,
  s1: AttemptInput,
): Omit<AttemptResult, "roundNumber" | "holdNs" | "cueOffsetNs"> {
  const auth = new LocalAuthority(cfg, 1);
  auth.startMatch();
  auth.match.hp[0] = hp[0];
  auth.match.hp[1] = hp[1];
  runExchange(auth, HARNESS.t0Ns, s0, s1);
  const { roundNumber: _r, holdNs: _h, cueOffsetNs: _c, ...rest } = attemptView(auth);
  return rest;
}
