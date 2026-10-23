// Live duels: the pure state machine (live.ts), the service shell with in-memory adapters (live_service.ts,
// live_bus.ts), the finish plan (live_finish.ts) and the parity vectors (Tools/Parity/live_vectors.json, written
// by the independent Python reference Tools/Parity/live_reference.py, replayed by the C# LiveMatchStateTests too).
//   deno test --allow-read supabase/functions/_shared/live.test.ts     (or: node Tools/Parity/run_node_tests.ts)
import { assert, assertClose, assertEquals, readText } from "./testing.ts";
import { hardConfig } from "./duel/load_config.ts";
import { type AttemptInput, playRecordedMatch } from "./duel/recorded_match.ts";
import {
  createLive,
  forfeitLive,
  joinLive,
  LIVE_RULES,
  type LiveReportIn,
  type LiveState,
  liveView,
  parseLiveReport,
  readyLive,
  reportLive,
  reportWindowMs,
  type Step,
  tickLive,
} from "./live.ts";
import { broadcastBody, MemoryBus } from "./live_bus.ts";
import { flaggedRounds, livePlayable, type LivePlayerCtx, perspectiveRounds, planLiveFinish } from "./live_finish.ts";
import { type LiveClock, LiveError, type LiveRow, LiveService, type LiveStore, startedRound } from "./live_service.ts";
import type { StoredRating } from "./ratings.ts";
import { LIVE_PLAUS } from "./server_config.ts";

const cfg = hardConfig();
const MS = 1_000_000;
const T0 = 1_700_000_000_000; // server ms

// ------------------------------------------------------------------------------------------ a simulated phone
/** A phone: its monotonic clock (uptime ns) is (server ms - T0) * 1e6 + offsetNs; DRAW reaches it downMs after it was sent, its
 *  reports reach the server upMs after the press. It shows "3" at round_start's startAtMs (ClockSync). */
interface Phone {
  offsetNs: number;
  downMs: number;
  upMs: number;
  presentNs: number;
  rttMs?: number;
}
const FAST: Phone = { offsetNs: 5_000_000_000, downMs: 20, upMs: 20, presentNs: 8 * MS, rttMs: 45 };
const SLOW: Phone = { offsetNs: 77_000_000_000, downMs: 150, upMs: 140, presentNs: 16 * MS, rttMs: 300 };

function local(p: Phone, serverMs: number): number {
  return (serverMs - T0) * MS + p.offsetNs;
}

/** The shot `p` reports for the round in play when it reacts in `reactionMs`, and when it reaches the server. */
function shot(s: LiveState, p: Phone, reactionMs: number): { rep: LiveReportIn; at: number } {
  const r = s.round!;
  const cue = local(p, r.cueSentAtMs! + p.downMs) + p.presentNs;
  const reaction = Math.round(reactionMs * MS);
  return {
    rep: { roundId: r.roundId, kind: "Shot", reactionNs: reaction, cuePresentedNs: cue, inputNs: cue + reaction, countdownStartNs: local(p, r.startAtMs), rttMs: p.rttMs },
    at: r.cueSentAtMs! + p.downMs + Math.ceil(reactionMs + p.presentNs / MS) + p.upMs,
  };
}

/** A TOO EARLY press `afterStartMs` after "3" (before any cue reached the phone). */
function early(s: LiveState, p: Phone, afterStartMs: number): { rep: LiveReportIn; at: number } {
  const r = s.round!;
  return {
    rep: { roundId: r.roundId, kind: "Early", reactionNs: 0, cuePresentedNs: 0, inputNs: local(p, r.startAtMs + afterStartMs), countdownStartNs: local(p, r.startAtMs), rttMs: p.rttMs },
    at: r.startAtMs + afterStartMs + p.upMs,
  };
}

function events(st: Step): string[] {
  return st.out.map((m) => m.event);
}

/** A joined friend match on `seed` (ready for attempt 0). */
function joined(seed = 7, mode: "casual" | "ranked" = "casual"): LiveState {
  const s = createLive({ kind: "friend", mode, seed, hostId: "A", nowMs: T0 });
  const st = joinLive(cfg, s, "B", "standard", T0 + 1000);
  assertEquals(st.verdict, "Accepted", "join");
  return st.state;
}

/** Both ready at `t` -> countdown; then the cue at its due time -> draw. */
function toDraw(s: LiveState, t: number): LiveState {
  s = readyLive(cfg, s, 0, s.attempt, t).state;
  const st = readyLive(cfg, s, 1, s.attempt, t + 5);
  assert(events(st).includes("round_start"), "round starts when both are ready");
  s = st.state;
  const cue = tickLive(cfg, s, s.round!.cueDueAtMs);
  assertEquals(events(cue), ["draw"], "cue sent at its due time");
  return cue.state;
}

type Plan = number | "early" | "noshot";

/** Plays one attempt: each slot shoots (ms), jumps the gun or stays silent; reports delivered in arrival order. */
function play(s: LiveState, t: number, p0: Plan, p1: Plan, phones: [Phone, Phone] = [FAST, SLOW]): { s: LiveState; t: number; steps: Step[] } {
  s = readyLive(cfg, s, 0, s.attempt, t).state;
  s = readyLive(cfg, s, 1, s.attempt, t + 5).state;
  const steps: Step[] = [];
  const pre: { slot: number; rep: LiveReportIn; at: number }[] = [];
  const plans = [p0, p1];
  for (const slot of [0, 1]) if (plans[slot] === "early") pre.push({ slot, ...early(s, phones[slot], 900 + slot * 100) });
  pre.sort((a, b) => a.at - b.at);
  for (const e of pre) {
    const st = reportLive(cfg, s, e.slot, e.rep, e.at);
    steps.push(st);
    s = st.state;
  }
  if (s.phase === "countdown") {
    const st = tickLive(cfg, s, s.round!.cueDueAtMs);
    steps.push(st);
    s = st.state;
    const post: { slot: number; rep: LiveReportIn; at: number }[] = [];
    for (const slot of [0, 1]) {
      const pl = plans[slot];
      if (typeof pl === "number") post.push({ slot, ...shot(s, phones[slot], pl) });
    }
    post.sort((a, b) => a.at - b.at);
    for (const e of post) {
      const st2 = reportLive(cfg, s, e.slot, e.rep, e.at);
      assertEquals(st2.verdict, "Accepted", `report slot ${e.slot}`);
      steps.push(st2);
      s = st2.state;
    }
    if (s.phase === "draw") {
      const st3 = tickLive(cfg, s, s.deadlineMs);
      steps.push(st3);
      s = st3.state;
    }
  }
  return { s, t: (s.history.at(-1)?.resolvedAtMs ?? t) + 3000, steps };
}

function toInput(p: Plan): AttemptInput {
  if (p === "early") return { kind: "early", reactionNs: 0 };
  if (p === "noshot") return { kind: "noshot", reactionNs: 0 };
  return { kind: p * MS < 100 * MS ? "anticipation" : "shot", reactionNs: Math.round(p * MS) };
}

// ------------------------------------------------------------------------------------------ state machine
Deno.test("live: a normal match is scored exactly like the two recordings against each other", () => {
  const plans: [Plan, Plan][] = [[231, 268], [301, 247], [255, 290]];
  let s = joined(1);
  let t = T0 + 2000;
  for (const [a, b] of plans) ({ s, t } = play(s, t, a, b));
  assertEquals(s.phase, "finished", "finished");
  const ref = playRecordedMatch(cfg, { seed: 1, rounds: 3, maxAttempts: 9 }, plans.map((p) => toInput(p[0])), plans.map((p) => toInput(p[1])));
  assertEquals(s.result!.endReason, ref.endReason, "end reason");
  assertEquals(s.result!.winner, ref.winner, "winner");
  assertClose(s.hp[0], ref.hp[0], 1e-9, "hp0");
  assertClose(s.hp[1], ref.hp[1], 1e-9, "hp1");
  assertEquals(s.roundWins, ref.roundWins, "round wins");
  assertEquals(s.history.map((h) => h.holdNs), ref.attempts.map((a) => a.holdNs), "holds come from the seed");
});

Deno.test("live: fair ordering — the faster reaction wins even when its report arrives last", () => {
  let s = toDraw(joined(3), T0 + 2000);
  const cue = s.round!.cueSentAtMs!;
  // slot 0 on a slow phone reacts in 240 ms, slot 1 on a fast phone in 260 ms: slot 1's report arrives first
  const slow = { ...SLOW, rttMs: 300 };
  const r0 = shot(s, slow, 240);
  const r1 = shot(s, FAST, 260);
  assert(r1.at < r0.at, "slot 1 arrives first");
  let st = reportLive(cfg, s, 1, r1.rep, r1.at);
  assertEquals(events(st), [], "nothing is decided on the first arrival");
  st = reportLive(cfg, st.state, 0, r0.rep, r0.at);
  s = st.state;
  assertEquals(events(st), ["round_result"], "resolved once both are in");
  const h = s.history[0];
  assertEquals(h.view.winner, 0, "the faster reaction wins");
  assertEquals(h.view.outcomes, ["Shot", "Shot"], "both fire (no kill at full HP)");
  assert(cue > 0, "cue sent");
});

Deno.test("live: tie window — both shots land, no round winner", () => {
  let { s } = play(joined(5), T0 + 2000, 250, 262);
  const h = s.history[0];
  assertEquals(h.view.tie, true, "tie");
  assertEquals(h.view.winner, -1, "no winner");
  assert(h.view.damage[0] > 0 && h.view.damage[1] > 0, "both hit");
  ({ s } = play(s, s.history[0].resolvedAtMs + 3000, 250, 266));
  assertEquals(s.history[1].view.tie, false, "16 ms apart is not a tie");
});

Deno.test("live: fouls — early, anticipation, both early voids and replays the same round", () => {
  let { s, t } = play(joined(9), T0 + 2000, "early", 280);
  assertEquals(s.history[0].view.outcomes, ["Foul", "Shot"], "early fouls, the other shoots");
  ({ s, t } = play(s, t, 80, 300));
  assertEquals(s.history[1].view.outcomes, ["Anticipation", "Shot"], "under 100 ms is anticipation");
  const before = s.history[1].roundNumber;
  ({ s, t } = play(s, t, "early", "early"));
  const v = s.history[2];
  assertEquals(v.view.void, true, "both early: void");
  assertEquals(v.cueSentAtMs, null, "voided before the cue: DRAW never sent");
  assertEquals(v.roundNumber, before + 1, "void attempt numbered as the round it replays");
  ({ s, t } = play(s, t, 240, 250));
  assertEquals(s.history[3].roundNumber, v.roundNumber, "the void round is replayed with the same number");
  assertEquals(s.phase, "finished", "3 resolved rounds");
  assertEquals(s.result!.endReason, "rounds", "end");
});

Deno.test("live: both no-shot and a single timeout resolve at the report deadline", () => {
  let s = toDraw(joined(11), T0 + 2000);
  const deadline = s.deadlineMs;
  assertEquals(deadline - s.round!.cueSentAtMs!, reportWindowMs(cfg), "report window");
  assertEquals(reportWindowMs(cfg), 1550 + LIVE_RULES.networkGraceMs, "1.5 s + 50 ms + network grace");
  let st = tickLive(cfg, s, deadline - 1);
  assertEquals(st.changed, false, "nothing before the deadline");
  st = tickLive(cfg, s, deadline);
  assertEquals(st.state.history[0].view.outcomes, ["NoShot", "NoShot"], "both no-shot");
  assertEquals(st.state.hp, [100, 100], "no damage");
  s = toDraw(st.state, deadline + 2000);
  const r = shot(s, FAST, 300);
  s = reportLive(cfg, s, 0, r.rep, r.at).state;
  st = tickLive(cfg, s, s.deadlineMs);
  assertEquals(st.state.history[1].view.outcomes, ["Shot", "NoShot"], "the silent one times out");
});

Deno.test("live: duplicate, late, stale and out-of-order messages", () => {
  let s = joined(13);
  assertEquals(readyLive(cfg, s, 0, 1, T0 + 2000).verdict, "WrongRound", "ready for a future attempt");
  s = readyLive(cfg, s, 0, 0, T0 + 2000).state;
  assertEquals(readyLive(cfg, s, 0, 0, T0 + 2001).verdict, "Duplicate", "ready twice");
  s = readyLive(cfg, s, 1, 0, T0 + 2002).state;
  assertEquals(readyLive(cfg, s, 1, 0, T0 + 2003).verdict, "Duplicate", "ready after the start");
  // a shot before DRAW was sent is impossible
  const fake: LiveReportIn = { roundId: 1, kind: "Shot", reactionNs: 200 * MS, cuePresentedNs: 9e9, inputNs: 9e9 + 200 * MS, countdownStartNs: 5e9 };
  assertEquals(reportLive(cfg, s, 0, fake, T0 + 2500).verdict, "Implausible", "shot before the cue");
  s = tickLive(cfg, s, s.round!.cueDueAtMs).state;
  const r0 = shot(s, FAST, 250);
  assertEquals(reportLive(cfg, s, 0, { ...r0.rep, roundId: 2 }, r0.at).verdict, "WrongRound", "future round id");
  const st = reportLive(cfg, s, 0, r0.rep, r0.at);
  assertEquals(st.verdict, "Accepted", "accepted");
  const dup = reportLive(cfg, st.state, 0, r0.rep, r0.at + 50);
  assertEquals(dup.verdict, "Duplicate", "a retried report");
  assertEquals(dup.changed, false, "a duplicate changes nothing");
  // an Early claimed long after DRAW was sent
  const e1 = early(st.state, SLOW, 500);
  assertEquals(reportLive(cfg, st.state, 1, e1.rep, st.state.round!.cueSentAtMs! + LIVE_RULES.earlyGraceMs + 1).verdict, "Late", "late early");
  // the round resolves at the deadline (slot 1 silent); a report arriving after it is stale
  const late = shot(st.state, SLOW, 400);
  const after = reportLive(cfg, st.state, 1, late.rep, st.state.deadlineMs + 10);
  assertEquals(after.verdict, "Stale", "a report after the deadline is for a resolved round");
  assertEquals(after.state.history[0].view.outcomes, ["Shot", "NoShot"], "it timed out as a no-shot");
  assertEquals(events(after), ["round_result"], "the deadline was applied first");
});

Deno.test("live: the §4.3 per-round checks", () => {
  const s = toDraw(joined(17), T0 + 2000);
  const ok = shot(s, FAST, 250);
  const bad = (patch: Partial<LiveReportIn>, at = ok.at) => reportLive(cfg, s, 0, { ...ok.rep, ...patch }, at).verdict;
  assertEquals(bad({ inputNs: ok.rep.inputNs + 5000 }), "Inconsistent", "input - cue != reaction");
  assertEquals(bad({ reactionNs: -1, inputNs: ok.rep.cuePresentedNs - 1 }), "Implausible", "negative reaction");
  assertEquals(bad({ reactionNs: 1501 * MS, inputNs: ok.rep.cuePresentedNs + 1501 * MS }, s.round!.cueSentAtMs! + 1700), "Implausible", "over the no-shot timeout");
  assertEquals(bad({ countdownStartNs: ok.rep.cuePresentedNs - 1 * MS }), "Inconsistent", "cue 1 ms after \"3\" (seed says seconds)");
  assertEquals(bad({ countdownStartNs: ok.rep.countdownStartNs - 5000 * MS }), "Inconsistent", "cue far later than the seed's hold");
  assertEquals(bad({ countdownStartNs: 0 }), "Inconsistent", "no countdown anchor");
  // the server saw 120 ms pass since it sent DRAW: a 250 ms reaction cannot have happened yet
  assertEquals(bad({}, s.round!.cueSentAtMs! + 120), "Implausible", "reaction longer than the time the server saw pass");
  assertEquals(bad({}), "Accepted", "the honest report");
});

Deno.test("live: soft reaction-vs-RTT flags never reject", () => {
  const s = toDraw(joined(19), T0 + 2000);
  const cue = s.round!.cueSentAtMs!;
  const base = shot(s, FAST, 200).rep;
  // reported 200 ms but the report took 1.4 s to arrive on a 45 ms RTT phone: shaved
  let st = reportLive(cfg, s, 0, base, cue + 1400);
  assertEquals(st.verdict, "Accepted", "accepted");
  assertEquals(st.state.round!.flags[0], ["excess_latency"], "excess latency flagged");
  // claims 900 ms RTT but showed only ~250 ms of latency
  st = reportLive(cfg, s, 0, { ...base, rttMs: 900 }, cue + 450);
  assertEquals(st.state.round!.flags[0], ["rtt_inflated"], "inflated RTT flagged");
  st = reportLive(cfg, s, 0, { ...base, rttMs: null }, cue + 1400);
  assertEquals(st.state.round!.flags[0], [], "no RTT, no flag");
  assert(LIVE_PLAUS.excessSlackMs > 0, "thresholds are server-side");
});

Deno.test("live: ready deadline — the missing player forfeits, nobody ready = abandoned, lobby expiry", () => {
  let s = joined(21);
  s = readyLive(cfg, s, 0, 0, T0 + 2000).state;
  let st = tickLive(cfg, s, s.deadlineMs);
  assertEquals(st.state.phase, "finished", "finished");
  assertEquals(st.state.result!.endReason, "forfeit", "forfeit");
  assertEquals(st.state.result!.winner, 0, "the ready player wins");
  assertEquals(st.state.result!.forfeitedBy, 1, "slot 1 forfeited");
  st = tickLive(cfg, joined(21), T0 + 1000 + LIVE_RULES.readyTimeoutMs);
  assertEquals(st.state.result!.endReason, "abandoned", "nobody ready");
  assertEquals(st.state.result!.winner, -1, "no winner");
  const lobby = createLive({ kind: "quick", mode: "casual", seed: 1, hostId: "A", nowMs: T0 });
  st = tickLive(cfg, lobby, T0 + LIVE_RULES.quickLobbyMs);
  assertEquals(st.state.phase, "cancelled", "quick lobby expires");
  assertEquals(events(st), ["cancelled"], "cancel broadcast");
  assertEquals(joinLive(cfg, lobby, "B", "standard", T0 + LIVE_RULES.quickLobbyMs).verdict, "NotPlaying", "too late to join");
  assertEquals(joinLive(cfg, lobby, "A", "standard", T0 + 1).verdict, "Taken", "the host cannot be the guest");
  assertEquals(joinLive(cfg, joined(1), "C", "standard", T0 + 1).verdict, "Taken", "a third player");
});

Deno.test("live: forfeit — leaving mid-round or closing the lobby", () => {
  let s = toDraw(joined(23), T0 + 2000);
  const st = forfeitLive(cfg, s, 1, s.round!.cueSentAtMs! + 100);
  assertEquals(st.state.result!.endReason, "forfeit", "forfeit");
  assertEquals(st.state.result!.winner, 0, "the other wins");
  assertEquals(events(st), ["match_over"], "broadcast");
  assertEquals(st.state.round, null, "the round is dropped (not resolved)");
  assertEquals(forfeitLive(cfg, st.state, 0, T0 + 99_999).verdict, "NotPlaying", "over is over");
  s = createLive({ kind: "friend", mode: "casual", seed: 1, hostId: "A", nowMs: T0 });
  assertEquals(forfeitLive(cfg, s, 0, T0 + 5).state.result!.endReason, "host_left", "host closes the lobby");
});

Deno.test("live: reconnection — a dropped player no-shots, comes back within the window, sees the cue it missed", () => {
  let s = toDraw(joined(29), T0 + 2000);
  // slot 1 dropped before DRAW reached it: it reconnects mid-draw and asks for the snapshot
  const v = liveView(s, 1, s.round!.cueSentAtMs! + 300);
  assertEquals((v.round as { cue: unknown }).cue, { roundId: 1, cueSeq: 1 }, "the snapshot carries the missed cue");
  s = tickLive(cfg, s, s.deadlineMs).state;
  assertEquals(s.history[0].view.outcomes, ["NoShot", "NoShot"], "nobody shot");
  // back 15 s later (inside the 20 s window): the match goes on
  s = readyLive(cfg, s, 0, 1, s.history[0].resolvedAtMs + 1000).state;
  const back = readyLive(cfg, s, 1, 1, s.history[0].resolvedAtMs + 15_000);
  assertEquals(back.verdict, "Accepted", "reconnected in time");
  assert(events(back).includes("round_start"), "the next round starts");
  // a phone back after the window has lost
  s = tickLive(cfg, back.state, back.state.round!.cueDueAtMs).state;
  const gone = tickLive(cfg, s, s.deadlineMs);
  const s2 = readyLive(cfg, gone.state, 0, 2, gone.state.history[1].resolvedAtMs + 1000).state;
  const late = readyLive(cfg, s2, 1, 2, gone.state.history[1].resolvedAtMs + LIVE_RULES.readyTimeoutMs + 1);
  assertEquals(late.verdict, "NotPlaying", "too late");
  assertEquals(late.state.result!.forfeitedBy, 1, "forfeited by the absent player");
});

Deno.test("live: secrets — no seed or cue time reaches a phone before the end", () => {
  let s = joined(31);
  s = readyLive(cfg, s, 0, 0, T0 + 2000).state;
  const st = readyLive(cfg, s, 1, 0, T0 + 2001);
  const start = st.out.find((m) => m.event === "round_start")!;
  for (const k of ["cueDueAtMs", "holdNs", "cueOffsetNs", "seed"]) assert(!(k in start.payload), `round_start has no ${k}`);
  const view = JSON.stringify(liveView(st.state, 0, T0 + 2002));
  assert(!view.includes(String(st.state.round!.cueDueAtMs)), "view has no cue due time");
  assert(!view.includes('"seed"'), "view has no seed");
  assertEquals((liveView(st.state, 0, T0 + 2002) as { deadlineInMs: unknown }).deadlineInMs, null, "no countdown deadline either");
  let fin = st.state;
  let t = T0 + 2000;
  for (let i = 0; i < 3; i++) ({ s: fin, t } = play(fin, t, 240, 300));
  const over = JSON.parse(JSON.stringify(liveView(fin, 0, t)));
  assertEquals(over.result.seed, 31, "the seed is revealed at the end");
});

Deno.test("live: parseLiveReport", () => {
  const good = { roundId: 1, kind: "Shot", reactionNs: 1, cuePresentedNs: 2, inputNs: 3, countdownStartNs: 1, rttMs: 40 };
  assertEquals(parseLiveReport(good)?.rttMs, 40, "ok");
  assertEquals(parseLiveReport({ ...good, kind: "Bot" }), null, "kind");
  assertEquals(parseLiveReport({ ...good, reactionNs: 1.5 }), null, "integer ns");
  assertEquals(parseLiveReport({ ...good, rttMs: -1 }), null, "rtt range");
  assertEquals(parseLiveReport({ ...good, rttMs: undefined })?.rttMs, null, "rtt optional");
  assertEquals(parseLiveReport([1]), null, "array");
});

// ------------------------------------------------------------------------------------------ finish plan
function rating(mmr: number, matches: number): StoredRating {
  return { exists: matches > 0, mmr, rd: 200, matches, wins: 0, losses: 0, draws: 0 };
}

Deno.test("live finish: ranked plan rates both once, pools both recordings, swaps perspectives", () => {
  let s = joined(37, "ranked");
  s.guns = ["peacemaker", "standard"]; // ranked normalises: scored with the standard curve
  let t = T0 + 2000;
  for (const [a, b] of [[231, 268], ["early", 247], [255, 290], [240, 241]] as [Plan, Plan][]) {
    if (s.phase === "finished") break;
    ({ s, t } = play(s, t, a, b));
  }
  assertEquals(s.phase, "finished", "finished");
  const plan = planLiveFinish(cfg, s, {
    players: [
      { userId: "A", displayName: "ALPHA", character: "whiskers", rating: rating(1300, 10), stats: null },
      { userId: "B", displayName: "BRAVO", character: "pawtruth", rating: rating(1250, 0), stats: null },
    ],
    season: 202643,
    now: new Date(T0),
    quarantineLeft: 2,
  });
  const [p0, p1] = plan.players;
  assertEquals(plan.season, 202643, "season");
  assertEquals(p0.rating!.expectMatches, 10, "optimistic version");
  assertEquals(p1.rating!.expectMatches, -1, "first rated game inserts");
  assertEquals(p0.match.winner === "player", s.result!.winner === 0, "winner A");
  assertEquals(p1.match.winner === "player", s.result!.winner === 1, "winner B");
  assert(p0.match.result.rating !== null && p1.match.result.rating !== null, "both rated");
  assertEquals(p0.match.opponentMmrAfter, p1.match.mmrAfter, "A sees B's new rating");
  assertEquals(p0.match.opponentSnapshot.ownerId, "B", "A faced B");
  assertEquals(p0.match.opponentSnapshot.attempts, livePlayable(s.history, 1), "the snapshot is B's recording");
  assertEquals(p0.recording!.gun, "peacemaker", "the recording keeps the loadout (ranked normalises at scoring)");
  assertEquals(p1.recording!.quarantineLeft, 2, "quarantined like any new recording");
  const early = livePlayable(s.history, 0)[1];
  assertEquals(early.kind, "early", "early attempt");
  assert(typeof early.inputOffsetNs === "number" && early.inputOffsetNs > 0, "early keeps its offset after \"3\"");
  const pr = perspectiveRounds(s.history, 1);
  assertEquals(pr[0].you.reactionNs, s.history[0].view.reactionsNs[1], "B's view: you = slot 1");
  // replaying the two pooled recordings as ghosts gives the same match (they feed the pool consistently)
  const ref = playRecordedMatch(cfg, { seed: 37, rounds: 3, maxAttempts: 9 }, livePlayable(s.history, 0), livePlayable(s.history, 1));
  assertEquals(ref.winner, s.result!.winner, "ghost replay agrees");
  assertClose(ref.hp[0], s.hp[0], 1e-9, "ghost replay hp");
});

Deno.test("live finish: forfeit rates but pools nothing; flags hold the recording", () => {
  let s = toDraw(joined(41, "ranked"), T0 + 2000);
  s = forfeitLive(cfg, s, 1, s.round!.cueSentAtMs! + 50).state;
  const ctx = {
    players: [
      { userId: "A", displayName: "ALPHA", character: "whiskers", rating: rating(1200, 3), stats: null },
      { userId: "B", displayName: "BRAVO", character: "pawtruth", rating: rating(1200, 3), stats: null },
    ] as [LivePlayerCtx, LivePlayerCtx],
    season: 202643,
    now: new Date(T0),
    quarantineLeft: 2,
  };
  let plan = planLiveFinish(cfg, s, ctx);
  assertEquals(plan.players[0].recording, null, "no recording from a forfeit");
  assertEquals(plan.players[1].match.winner, "opponent", "the leaver lost");
  assert(plan.players[0].rating!.wins === 1 && plan.players[1].rating!.losses === 1, "ranked forfeit is rated");
  assertEquals(plan.players[0].plaus.clean, false, "a forfeit does not release quarantine");

  // a played match where slot 0 carried a reaction-vs-RTT flag in two rounds: held + logged, best not counted
  s = joined(43);
  let t = T0 + 2000;
  for (let i = 0; i < 3; i++) ({ s, t } = play(s, t, 240, 300));
  s.history[0].flags[0] = ["excess_latency"];
  s.history[2].flags[0] = ["excess_latency"];
  assertEquals(flaggedRounds(s.history, 0), 2, "two flagged rounds");
  plan = planLiveFinish(cfg, s, { ...ctx, season: null });
  assertEquals(plan.players[0].recording!.held, true, "held");
  assertEquals(plan.players[0].stats.bestNs, null, "no leaderboard best");
  assertEquals(plan.players[0].plaus.log[0].check, "live_rtt", "logged");
  assertEquals(plan.players[1].recording!.held, false, "the other player is untouched");
  assertEquals(plan.players[0].rating, null, "casual: unrated");
});

// ------------------------------------------------------------------------------------------ service + adapters
class MemoryStore implements LiveStore {
  rows = new Map<string, LiveRow>();
  rounds: { id: string; attempt: number }[] = [];
  finished: string[] = [];
  /** Called before a save lands (simulates a concurrent writer). */
  beforeSave: ((id: string) => void) | null = null;

  load(id: string): Promise<LiveRow | null> {
    const r = this.rows.get(id);
    return Promise.resolve(r ? { id, state: JSON.parse(JSON.stringify(r.state)), version: r.version } : null);
  }
  private cas(id: string, v: number, state: LiveState, resolved: { attempt: number }[]): boolean {
    const hook = this.beforeSave;
    this.beforeSave = null;
    hook?.(id);
    const r = this.rows.get(id);
    if (!r || r.version !== v) return false;
    this.rows.set(id, { id, state, version: v + 1 });
    for (const a of resolved) this.rounds.push({ id, attempt: a.attempt });
    return true;
  }
  save(id: string, v: number, state: LiveState, resolved: { attempt: number }[]): Promise<boolean> {
    return Promise.resolve(this.cas(id, v, state, resolved));
  }
  finish(id: string, v: number, state: LiveState, resolved: { attempt: number }[]): Promise<boolean> {
    const ok = this.cas(id, v, state, resolved);
    if (ok) this.finished.push(id);
    return Promise.resolve(ok);
  }
}

class FakeClock implements LiveClock {
  t = T0;
  nowMs(): number {
    return this.t;
  }
  sleep(ms: number): Promise<void> {
    this.t += Math.max(0, ms);
    return Promise.resolve();
  }
}

function service(seed = 47) {
  const store = new MemoryStore();
  const bus = new MemoryBus();
  const clock = new FakeClock();
  const svc = new LiveService(cfg, store, bus, clock);
  store.rows.set("m1", { id: "m1", state: joined(seed), version: 1 });
  return { store, bus, clock, svc };
}

Deno.test("live service: ready starts the round, the driver sends DRAW at the cue and resolves at the deadline", async () => {
  const { store, bus, clock, svc } = service();
  await svc.ready("m1", "A", 0);
  const r = await svc.ready("m1", "B", 0);
  const roundId = startedRound(r.step);
  assertEquals(roundId, 1, "round 1 started");
  assertEquals(bus.events("live:m1"), ["ready", "ready", "round_start"], "broadcast after each save");
  const due = r.row.state.round!.cueDueAtMs;
  await svc.drive("m1", roundId);
  assertEquals(bus.events("live:m1").slice(3), ["draw", "round_result"], "DRAW, then the no-shots at the deadline");
  const draw = bus.sent.find((x) => x.msg.event === "draw")!;
  assertEquals(draw.msg.payload, { roundId: 1, cueSeq: 1 }, "DRAW carries no timing");
  const s = store.rows.get("m1")!.state;
  assertEquals(s.history[0].cueSentAtMs, due, "DRAW sent exactly at the secret cue time");
  assertEquals(s.phase, "ready", "waiting for the next ready");
  assertEquals(store.rounds.length, 1, "the resolved attempt is stored (live_rounds)");
  assert(clock.t >= s.history[0].resolvedAtMs, "clock moved through the round");
});

Deno.test("live service: concurrent writers — a conflicting save is retried on the fresh state", async () => {
  const { store, bus, clock, svc } = service();
  await svc.ready("m1", "A", 0);
  await svc.ready("m1", "B", 0);
  clock.t = store.rows.get("m1")!.state.round!.cueDueAtMs;
  await svc.tick("m1", null);
  const s = store.rows.get("m1")!.state;
  const r0 = shot(s, FAST, 250);
  const r1 = shot(s, SLOW, 230);
  // slot 1's report lands while slot 0's is being applied: slot 0's first save conflicts and is redone on top
  store.beforeSave = (id) => {
    const row = store.rows.get(id)!;
    const st = reportLive(cfg, row.state, 1, r1.rep, r1.at);
    store.rows.set(id, { id, state: st.state, version: row.version + 1 });
  };
  clock.t = r0.at;
  const res = await svc.report("m1", "A", r0.rep);
  assertEquals(res.step.verdict, "Accepted", "accepted after the retry");
  const fin = store.rows.get("m1")!.state;
  assertEquals(fin.history.length, 1, "resolved once");
  assertEquals(fin.history[0].view.winner, 1, "230 ms beats 250 ms whatever the order");
  assertEquals(bus.events().filter((e) => e === "round_result").length, 1, "one result broadcast");
});

Deno.test("live service: membership, a dead bus, finish goes through store.finish", async () => {
  const { store, bus, clock, svc } = service(53);
  let err: unknown = null;
  try {
    await svc.ready("m1", "C", 0);
  } catch (e) {
    err = e;
  }
  assert(err instanceof LiveError && err.code === "not_member", "a stranger cannot act");
  err = null;
  try {
    await svc.tick("nope", "A");
  } catch (e) {
    err = e;
  }
  assert(err instanceof LiveError && err.code === "not_found", "unknown match");
  bus.fail = true;
  await svc.ready("m1", "A", 0);
  assertEquals(store.rows.get("m1")!.state.players[0].ready, 0, "a failed broadcast never loses the stored state");
  bus.fail = false;
  await svc.forfeit("m1", "B");
  assertEquals(store.finished, ["m1"], "a result is written through finish (pn_live_finish)");
  const r = await svc.tick("m1", "A");
  assertEquals(r.step.changed, false, "nothing moves after the end");
  assert(clock.t === T0, "no sleeping outside the driver");
});

Deno.test("live bus: the REST broadcast body", () => {
  const b = broadcastBody("live:x", [{ event: "draw", payload: { roundId: 2, cueSeq: 1 } }], 123);
  assertEquals(b, { messages: [{ topic: "live:x", event: "draw", payload: { roundId: 2, cueSeq: 1, sentAtMs: 123 }, private: true }] }, "body");
});

// ------------------------------------------------------------------------------------------ parity vectors
const ROOT = new URL("../../../", import.meta.url);

// deno-lint-ignore no-explicit-any
type Json = any;

function close(a: Json, b: Json, path: string, fails: string[]): void {
  if (typeof a === "number" && typeof b === "number") {
    if (!(Math.abs(a - b) <= 1e-9 * Math.max(1, Math.abs(b)))) fails.push(`${path}: ${a} != ${b}`);
    return;
  }
  if (Array.isArray(a) && Array.isArray(b)) {
    if (a.length !== b.length) fails.push(`${path}: length ${a.length} != ${b.length}`);
    for (let i = 0; i < Math.min(a.length, b.length); i++) close(a[i], b[i], `${path}[${i}]`, fails);
    return;
  }
  if (a && b && typeof a === "object" && typeof b === "object") {
    for (const k of Object.keys(b)) close(a[k], b[k], `${path}.${k}`, fails);
    return;
  }
  if (a !== b) fails.push(`${path}: ${JSON.stringify(a)} != ${JSON.stringify(b)}`);
}

/** The snapshot every implementation compares after each op (Tools/Parity/live_reference.py `snapshot`). */
export function liveSnapshot(st: Step): Json {
  const s = st.state;
  return {
    verdict: st.verdict,
    events: st.out.map((m) => m.event),
    phase: s.phase,
    attempt: s.attempt,
    deadlineMs: s.deadlineMs,
    ready: [s.players[0].ready, s.players[1].ready],
    roundId: s.round ? s.round.roundId : 0,
    cueDueAtMs: s.round ? s.round.cueDueAtMs : 0,
    cueSentAtMs: s.round ? (s.round.cueSentAtMs ?? -1) : 0,
    hp: s.hp,
    roundWins: s.roundWins,
    history: s.history.map((h) => ({ roundNumber: h.roundNumber, kinds: [h.inputs[0].kind, h.inputs[1].kind], ...h.view })),
    flags: s.round ? s.round.flags : (s.history.length ? s.history[s.history.length - 1].flags : [[], []]),
    result: s.result,
  };
}

Deno.test("live parity: Tools/Parity/live_vectors.json (Python reference) replays identically", async () => {
  const v = JSON.parse(await readText(new URL("Tools/Parity/live_vectors.json", ROOT)));
  const meta = v.meta;
  for (const k of Object.keys(meta.rules)) assertEquals((LIVE_RULES as Record<string, unknown>)[k], meta.rules[k], `rule ${k}`);
  for (const k of Object.keys(meta.plaus)) assertEquals((LIVE_PLAUS as Record<string, unknown>)[k], meta.plaus[k], `plaus ${k}`);
  const fails: string[] = [];
  let ops = 0;
  for (const c of v.cases) {
    let s: LiveState = createLive({ kind: c.kind, mode: c.mode, seed: c.seed, hostId: "A", hostGun: c.guns[0], nowMs: c.createdAtMs });
    for (let i = 0; i < c.ops.length; i++) {
      const op = c.ops[i];
      let st: Step;
      switch (op.op) {
        case "join": st = joinLive(cfg, s, op.user, op.gun, op.t); break;
        case "ready": st = readyLive(cfg, s, op.slot, op.attempt, op.t); break;
        case "report": st = reportLive(cfg, s, op.slot, op.report, op.t); break;
        case "forfeit": st = forfeitLive(cfg, s, op.slot, op.t); break;
        default: st = tickLive(cfg, s, op.t);
      }
      s = st.state;
      ops++;
      close(liveSnapshot(st), c.expect[i], `${c.name}#${i}(${op.op})`, fails);
      if (fails.length > 20) break;
    }
  }
  if (fails.length) throw new Error(`${fails.length} live parity mismatch(es):\n  ` + fails.slice(0, 20).join("\n  "));
  assert(v.cases.length >= 10 && ops >= 100, `enough vectors (${v.cases.length} cases, ${ops} ops)`);
});
