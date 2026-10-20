// M5 unit tests: weekly seasons, room codes, ranked gating, plausibility (A1-A4, B5-B9), season carry-over,
// the pure halves of pn_complete_match's inputs (planGame) and the match-start response for rooms.
//   deno test --allow-read --allow-env supabase/functions/_shared/      (or: node Tools/Parity/run_node_tests.ts)
// The SQL halves (pn_complete_match atomicity, rooms, quarantine pool, leaderboards) run against a real
// Postgres: supabase/tests/pg/run.sh.
import { assert, assertClose, assertEquals } from "./testing.ts";
import { hardConfig } from "./duel/load_config.ts";
import { type AttemptInput, playRecordedMatch } from "./duel/recorded_match.ts";
import { schedulesForSeed } from "./duel/schedule.ts";
import { parseRecording, type PlayableAttempt, type Recording, type SwipeSample } from "./recording.ts";
import { checkSamples, checkWallClock, digitizerInterval, type MatchContext, validateAndScore } from "./validate_recording.ts";
import { isoWeek, nextWeekStart, parseRankedBoard, rankedBoardName, weeklySeasonId } from "./season.ts";
import { candidateCodes, isGeneratableCode, normalizeRoomCode, roomJoinError } from "./rooms.ts";
import { hasLinkedIdentity, parseMode, rankedGate } from "./ranked.ts";
import { assessMatch, EMPTY_STATS, matchSignals, median, pearson, rollStats, stdDev } from "./plausibility.ts";
import { loadRating, planGame, seasonStart } from "./ratings.ts";
import { buildResponse } from "./match_response.ts";
import { LEADERBOARD, PLAUSIBILITY, quarantineMatches, RATING, rankedSeason, ROOM } from "./server_config.ts";
import type { Db } from "./db.ts";

const cfg = hardConfig();
const MS = 1_000_000;
const SEED = 424242;
const env = (globalThis as unknown as { Deno: { env: { set(k: string, v: string): void; delete(k: string): void } } }).Deno.env;

/** Deterministic PRNG for the tests (mulberry32). */
function rng(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** Normal(mean, sd) from a uniform source (Box-Muller). */
function normal(r: () => number, mean: number, sd: number): number {
  const u = Math.max(1e-12, r());
  return mean + sd * Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * r());
}

/** An honest recording (timestamps as DuelClient produces them); attempts are 7 s apart. */
function honest(seed: number, attempts: AttemptInput[], roundNumbers: number[], source: "touch" | "mouse" = "touch"): Recording {
  const sched = schedulesForSeed(cfg, seed, attempts.length);
  let t = 5_000_000_000;
  const rounds = attempts.map((a, i) => {
    const start = t;
    const cue = start + sched[i].cueOffsetNs + 8 * MS;
    t = cue + 3_000 * MS;
    const base = { roundNumber: roundNumbers[i], countdownStartNs: start, inputSource: source, frameQuantized: false };
    switch (a.kind) {
      case "early":
        return { ...base, kind: a.kind, reactionNs: 0, cuePresentedNs: 0, inputNs: start + 900 * MS };
      case "noshot":
        return { ...base, kind: a.kind, reactionNs: 0, cuePresentedNs: cue, inputNs: 0, inputSource: "unknown" };
      default:
        return { ...base, kind: a.kind, reactionNs: a.reactionNs, cuePresentedNs: cue, inputNs: cue + a.reactionNs };
    }
  });
  return parseRecording({
    version: 1, seed, difficulty: "hard", character: "whiskers", loadout: { gun: "standard" },
    rounds, device: { model: "iPhone15,2", os: "iOS 18.1", refreshHz: 120, screenHeightPx: 2532 },
    clientBuild: "1.0.0+1", submittedAt: "2026-10-20T10:00:00Z",
  });
}

const shot = (ms: number): AttemptInput => ({ kind: "shot", reactionNs: Math.round(ms * MS) });
const ghost = (...ms: number[]): PlayableAttempt[] => ms.map((v, i) => ({ roundNumber: i + 1, kind: "shot", reactionNs: v * MS }));
const span = (rec: Recording) => {
  const ts = rec.rounds.flatMap((r) => [r.countdownStartNs, r.cuePresentedNs, r.inputNs]).filter((x) => x > 0);
  return Math.max(...ts) - Math.min(...ts);
};

// ------------------------------------------------------------------------------------------------ seasons

Deno.test("weekly season ids follow ISO-8601 weeks", () => {
  assertEquals(isoWeek(new Date("2026-10-20T10:00:00Z")), { year: 2026, week: 43 });
  assertEquals(weeklySeasonId(new Date("2026-10-20T10:00:00Z")), 202643);
  assertEquals(isoWeek(new Date("2021-01-03T23:59:59Z")), { year: 2020, week: 53 }, "Sunday belongs to the old ISO year");
  assertEquals(isoWeek(new Date("2024-12-30T00:00:00Z")), { year: 2025, week: 1 }, "Monday in the next ISO year");
  assertEquals(weeklySeasonId(new Date("2026-10-25T23:59:59Z")), 202643, "Sunday night is still the same week");
  assertEquals(weeklySeasonId(new Date("2026-10-26T00:00:00Z")), 202644, "Monday 00:00 UTC rolls over");
  assertEquals(nextWeekStart(new Date("2026-10-20T10:00:00Z")).toISOString(), "2026-10-26T00:00:00.000Z");
  assertEquals(nextWeekStart(new Date("2026-10-26T00:00:00Z")).toISOString(), "2026-11-02T00:00:00.000Z");
});

Deno.test("ranked board names round-trip", () => {
  assertEquals(rankedBoardName(202643), "ranked_2026_43");
  assertEquals(rankedBoardName(202601), "ranked_2026_01");
  assertEquals(rankedBoardName(7), "ranked_season_7");
  assertEquals(parseRankedBoard("ranked_2026_43"), 202643);
  assertEquals(parseRankedBoard("ranked_2026_01"), 202601);
  assertEquals(parseRankedBoard("ranked_season_7"), 7);
  assertEquals(parseRankedBoard("ranked_2026_54"), null);
  assertEquals(parseRankedBoard("ranked_2026_00"), null);
  assertEquals(parseRankedBoard("best_reaction_hard"), null);
  assertEquals(parseRankedBoard("ranked_2026_43; drop table"), null);
});

Deno.test("rankedSeason: weekly by default, PN_SEASON pins it", () => {
  assertEquals(rankedSeason(new Date("2026-10-20T10:00:00Z")), 202643);
  env.set("PN_SEASON", "3");
  try {
    assertEquals(rankedSeason(new Date("2026-10-20T10:00:00Z")), 3);
  } finally {
    env.delete("PN_SEASON");
  }
  env.set("PN_SEASON", "nonsense");
  try {
    assertEquals(rankedSeason(new Date("2026-10-20T10:00:00Z")), 202643, "a bad value falls back to weekly");
  } finally {
    env.delete("PN_SEASON");
  }
});

// ------------------------------------------------------------------------------------------------ rooms

Deno.test("room codes: join input is normalised, generated codes avoid I/O and blocked words", () => {
  assertEquals(normalizeRoomCode("abcd"), "ABCD");
  assertEquals(normalizeRoomCode(" Ab-Cd "), "ABCD");
  assertEquals(normalizeRoomCode("ABIO"), "ABIO", "joining accepts every letter (the keypad has all 26)");
  assertEquals(normalizeRoomCode("ABC"), null);
  assertEquals(normalizeRoomCode("ABCDE"), null);
  assertEquals(normalizeRoomCode("AB1D"), null);
  assertEquals(normalizeRoomCode("ÄBCD"), null);
  assertEquals(normalizeRoomCode(1234), null);
  assertEquals(isGeneratableCode("ABCD"), true);
  assertEquals(isGeneratableCode("ABCI"), false);
  assertEquals(isGeneratableCode("OBCD"), false);
  for (const w of ROOM.blocked) assertEquals(isGeneratableCode(w), false, w);

  const codes = candidateCodes(ROOM.candidates, rng(1));
  assertEquals(codes.length, ROOM.candidates);
  assertEquals(new Set(codes).size, codes.length, "distinct");
  for (const c of codes) assert(/^[A-HJ-NP-Z]{4}$/.test(c) && isGeneratableCode(c), c);
  // a source that always lands on a blocked word still terminates (and yields nothing bad)
  const stuck = candidateCodes(3, () => 0);
  for (const c of stuck) assert(isGeneratableCode(c), c);
});

Deno.test("room-join states map to the documented HTTP errors", () => {
  assertEquals(roomJoinError("not_found")?.status, 404);
  assertEquals(roomJoinError("not_found")?.code, "room_not_found");
  assertEquals(roomJoinError("expired")?.code, "room_expired");
  assertEquals(roomJoinError("used")?.code, "room_used");
  assertEquals(roomJoinError("own_room")?.status, 409);
  assertEquals(roomJoinError("taken")?.code, "room_taken");
  assertEquals(roomJoinError("waiting"), null);
  assertEquals(roomJoinError("ready"), null);
  assertEquals(roomJoinError("rejoin"), null);
  assertEquals(ROOM.ttlSec, 600, "10 minute rooms");
  assertEquals(ROOM.pollSec, 3, "client polls every 3 s");
});

Deno.test("room-join issues through the match-start response shape, tagged with the room", () => {
  const snap = { recordingId: "r", ownerId: "o", isHouse: false, displayName: "Host", character: "whiskers", gun: "standard" as const, mmr: 1250, attempts: ghost(250, 260, 270) };
  const res = buildResponse("m", "casual", null, 4242, "2026-10-20T10:00:00Z", "2026-10-20T10:05:00Z", { mmr: 1200, rd: 350 }, snap, "HOWL");
  assertEquals(res.room, { code: "HOWL" });
  assertEquals(res.seed, 4242, "the host's seed, so the friend gets the same holds");
  assertEquals(res.opponent.displayName, "Host");
  assertEquals(res.gunsNormalised, false);
  assertEquals("room" in buildResponse("m", "ranked", 202643, 1, "a", "b", { mmr: 1, rd: 1 }, snap), false);
});

// ------------------------------------------------------------------------------------------------ ranked gate

Deno.test("ranked gating: link_required for guests, build allow-list, flagged", () => {
  const guest = { isAnonymous: true, providers: [] };
  const linked = { isAnonymous: false, providers: ["apple"] };
  const emailOnly = { isAnonymous: false, providers: ["email"] };
  assertEquals(hasLinkedIdentity(guest), false);
  assertEquals(hasLinkedIdentity(linked), true);
  assertEquals(hasLinkedIdentity(emailOnly), false, "only Apple / Google count");
  env.set("PN_RANKED_BUILDS", "1.0.0+42, 1.0.1+43");
  try {
    let d = rankedGate(guest, { flagged: false }, "1.0.0+42");
    assertEquals(d?.code, "link_required");
    assertEquals(d?.reasons, ["link_account"]);
    assertEquals(rankedGate(linked, { flagged: false }, "1.0.1+43"), null);
    d = rankedGate(linked, { flagged: false }, "0.9.0+1");
    assertEquals(d?.code, "ranked_not_allowed");
    assertEquals(d?.reasons, ["client_build"]);
    d = rankedGate(linked, { flagged: true }, "1.0.0+42");
    assertEquals(d?.reasons, ["account_flagged"]);
    d = rankedGate(guest, { flagged: true }, "x");
    assertEquals(d?.code, "link_required", "linking is named first: it is what the player can fix");
    assertEquals(d?.reasons, ["link_account", "account_flagged", "client_build"]);
    env.set("PN_RANKED_ALLOW_GUESTS", "true");
    assertEquals(rankedGate(guest, { flagged: false }, "1.0.0+42"), null, "local stack only");
  } finally {
    env.delete("PN_RANKED_BUILDS");
    env.delete("PN_RANKED_ALLOW_GUESTS");
  }
  assertEquals(rankedGate(linked, { flagged: false }, "1.0.0+42")?.reasons, ["client_build"], "empty allow-list = ranked closed");
});

Deno.test("match-start mode defaults to casual", () => {
  assertEquals(parseMode(undefined), "casual");
  assertEquals(parseMode(null), "casual");
  assertEquals(parseMode("casual"), "casual");
  assertEquals(parseMode("ranked"), "ranked");
  assertEquals(parseMode("RANKED"), null);
  assertEquals(parseMode(3), null);
});

// ------------------------------------------------------------------------------------------------ A1 wall clock

Deno.test("A1 wall clock: a recording cannot be longer than the time the server saw pass", () => {
  const rec = honest(SEED, [shot(210), shot(250), shot(231)], [1, 2, 3]);
  const s = span(rec);
  const sched = schedulesForSeed(cfg, SEED, 3);
  assertEquals(checkWallClock(rec, sched, s + 5_000 * MS), null, "real time");
  assertEquals(checkWallClock(rec, sched, s - 1_500 * MS), null, "within the 2 s slack");
  assertEquals(checkWallClock(rec, sched, s - 2_500 * MS)?.ok, false);
  const c: MatchContext = { seed: SEED, rounds: 3, maxAttempts: 9, opponent: ghost(260, 240, 300), elapsedNs: 3_000 * MS };
  const v = validateAndScore(cfg, rec, c, { allowInjected: false });
  assertEquals(v.ok ? "ok" : v.reason, "wall_clock", "fabricated 3 s after match-start");
  const ok = validateAndScore(cfg, rec, { ...c, elapsedNs: s + 20_000 * MS }, { allowInjected: false });
  assertEquals(ok.ok, true);
});

Deno.test("A1 wall clock: a recording cannot be shorter than its countdowns and holds", () => {
  const rec = honest(SEED, [shot(210), shot(250), shot(231)], [1, 2, 3]);
  const sched = schedulesForSeed(cfg, SEED, 3);
  // squeeze every attempt onto the first one's clock: the spans the seed forces cannot fit
  const squeezed = structuredClone(rec);
  for (const r of squeezed.rounds) {
    const shift = r.countdownStartNs - rec.rounds[0].countdownStartNs;
    r.countdownStartNs -= shift - 1; r.cuePresentedNs -= shift - 1; r.inputNs -= shift - 1;
  }
  assertEquals(checkWallClock(squeezed, sched, undefined)?.ok, false);
});

// ------------------------------------------------------------------------------------------------ A2 ranked input

Deno.test("A2 ranked accepts only touch with OS timestamps (casual accepts mouse)", () => {
  const ctx: MatchContext = { seed: SEED, rounds: 3, maxAttempts: 9, opponent: ghost(260, 240, 300) };
  const mouse = honest(SEED, [shot(210), shot(250), shot(231)], [1, 2, 3], "mouse");
  assertEquals(validateAndScore(cfg, mouse, ctx, { allowInjected: false }).ok, true, "casual: mouse is fine");
  const r = validateAndScore(cfg, mouse, { ...ctx, ranked: true }, { allowInjected: false });
  assertEquals(r.ok ? "ok" : r.reason, "input_source");
  const fq = honest(SEED, [shot(210), shot(250), shot(231)], [1, 2, 3]);
  fq.rounds[1].frameQuantized = true;
  const q = validateAndScore(cfg, fq, { ...ctx, ranked: true }, { allowInjected: false });
  assertEquals(q.ok ? "ok" : q.reason, "input_source");
  const ns = honest(SEED, [shot(210), { kind: "noshot", reactionNs: 0 }, shot(231)], [1, 2, 3]);
  assertEquals(validateAndScore(cfg, ns, { ...ctx, ranked: true }, { allowInjected: false }).ok, true, "a no-shot has no input source");
});

// ------------------------------------------------------------------------------------------------ A3 raw samples

/** OS samples around SwipeDraw's crossing that put the crossing exactly at r.inputNs (screen 2532 px). */
function samplesFor(inputNs: number, jitter = [120, -210, 330]): SwipeSample[] {
  const H = 2532;
  const th = Math.max(8, H * PLAUSIBILITY.sampleThresholdFraction);
  const ys = [200, 280, 360, 440, 520];
  const d = [8_333_333 + jitter[0], 8_333_333 + jitter[1], 8_333_333 + jitter[2]];
  const k = (th - (ys[3] - ys[0])) / (ys[4] - ys[3]);
  const t3 = inputNs - Math.trunc(d[2] * k);
  const t2 = t3 - d[1];
  const t1 = t2 - d[0];
  const t0 = t1 - 40 * MS;
  const x = 600;
  return [
    { tNs: t0, x, y: ys[0], phase: "began" },
    { tNs: t1, x: x + 2, y: ys[1], phase: "moved" },
    { tNs: t2, x: x + 5, y: ys[2], phase: "moved" },
    { tNs: t3, x: x + 7, y: ys[3], phase: "moved" },
    { tNs: t3 + d[2], x: x + 9, y: ys[4], phase: "moved" },
  ];
}

Deno.test("A3 raw samples: the crossing is recomputed from the OS samples", () => {
  assert(digitizerInterval(8_333_333) && digitizerInterval(16_667_000) && digitizerInterval(4_200_000) && digitizerInterval(25_000_000));
  assert(!digitizerInterval(11_000_000) && !digitizerInterval(1_000_000));

  const rec = honest(SEED, [shot(210), shot(250), shot(231)], [1, 2, 3]);
  for (const r of rec.rounds) r.samples = samplesFor(r.inputNs);
  const ctx: MatchContext = { seed: SEED, rounds: 3, maxAttempts: 9, opponent: ghost(260, 240, 300), ranked: true, requireSamples: true };
  const why = (x: Recording) => {
    const v = validateAndScore(cfg, x, ctx, { allowInjected: false });
    return v.ok ? "ok" : `${v.reason}: ${v.detail}`;
  };
  assertEquals(why(rec), "ok");

  let t = structuredClone(rec);
  t.rounds[1].samples = samplesFor(t.rounds[1].inputNs - 5 * MS); // the claimed input 5 ms after the real crossing
  assert(why(t).startsWith("samples:") && why(t).includes("cross"), why(t));

  // a fabricated, perfectly regular sample train (the geometry is irrelevant: spacing is checked first)
  const regular: SwipeSample[] = [{ tNs: 900 * MS, x: 600, y: 200, phase: "began" }];
  for (let i = 0; i < 4; i++) regular.push({ tNs: 1_000 * MS + i * 8_333_333, x: 600, y: 280 + 80 * i, phase: "moved" });
  assertEquals(checkSamples({ ...rec.rounds[0], samples: regular }, 2532), "sample spacing is exactly regular");

  t = structuredClone(rec);
  t.rounds[2].samples![3].tNs = t.rounds[2].samples![2].tNs + 11 * MS; // 11 ms: no digitizer reports at that rate
  assert(why(t).includes("digitizer"), why(t));

  t = structuredClone(rec);
  for (const s of t.rounds[0].samples!) s.y += 1500; // started mid-screen, not in the holster
  assert(why(t).includes("holster"), why(t));

  t = structuredClone(rec);
  t.rounds[0].samples = t.rounds[0].samples!.map((s, i) => ({ ...s, x: s.x + i * 400 })); // sideways
  assert(why(t).includes("sideways"), why(t));

  t = structuredClone(rec);
  delete t.rounds[1].samples;
  assert(why(t).includes("raw swipe samples"), why(t));
  assertEquals(validateAndScore(cfg, t, { ...ctx, requireSamples: false }, { allowInjected: false }).ok, true, "optional until the client sends them");

  t = structuredClone(rec);
  delete t.device.screenHeightPx;
  assert(why(t).includes("screenHeightPx"), why(t));
  assertEquals(validateAndScore(cfg, rec, { ...ctx, ranked: false }, { allowInjected: false }).ok, true, "casual never checks samples");
});

// ------------------------------------------------------------------------------------------------ A4 + B rolling

Deno.test("A4 margin vs the known ghost: winning every round by a hair holds the recording", () => {
  const opp = ghost(260, 240, 300);
  const cheat = honest(SEED, [shot(255), shot(235), shot(295)], [1, 2, 3]);
  const res = playRecordedMatch(cfg, { seed: SEED, rounds: 3, maxAttempts: 9 }, [shot(255), shot(235), shot(295)], opp);
  const sig = matchSignals(cheat, res, opp);
  assertEquals([sig.wonShots, sig.narrowWins, sig.hold], [3, 3, true]);

  const fair = honest(SEED, [shot(205), shot(232), shot(250)], [1, 2, 3]);
  const res2 = playRecordedMatch(cfg, { seed: SEED, rounds: 3, maxAttempts: 9 }, [shot(205), shot(232), shot(250)], opp);
  const sig2 = matchSignals(fair, res2, opp);
  assertEquals([sig2.wonShots, sig2.narrowWins, sig2.hold], [3, 1, false], "one close round is normal");
  assertEquals(sig2.shots.length, 3);
  assertEquals(sig2.shots[0][1], res2.attempts[0].holdNs, "hold length kept for the foreperiod check");
});

Deno.test("statistics helpers", () => {
  assertEquals(median([3, 1, 2]), 2);
  assertEquals(median([4, 1, 2, 3]), 2.5);
  assertClose(stdDev([2, 4, 4, 4, 5, 5, 7, 9]), 2.138089935, 1e-6);
  assertClose(pearson([1, 2, 3, 4], [2, 4, 6, 8]), 1, 1e-12);
  assertClose(pearson([1, 2, 3, 4], [8, 6, 4, 2]), -1, 1e-12);
  assert(Number.isNaN(pearson([1, 1, 1], [1, 2, 3])));
});

function feed(shotsMs: number[], perMatch = 3, narrowPerMatch = 0, wonPerMatch = 0) {
  let st = EMPTY_STATS;
  let last = rollStats(null, { shots: [], wonShots: 0, narrowWins: 0, hold: false });
  for (let i = 0; i < shotsMs.length; i += perMatch) {
    const shots = shotsMs.slice(i, i + perMatch).map((ms, j) => [Math.round(ms * MS), (1 + ((i + j) % 4)) * 1_000 * MS] as [number, number]);
    last = rollStats(st, { shots, wonShots: wonPerMatch, narrowWins: narrowPerMatch, hold: false });
    st = last.next;
  }
  return last;
}

Deno.test("B rolling checks: honest players never trip, each cheat pattern does", () => {
  const r = rng(7);
  for (let player = 0; player < 200; player++) {
    const mean = 190 + r() * 110; // 190..300 ms
    const sd = 20 + r() * 20;
    const shots = Array.from({ length: 60 }, () => Math.min(1400, Math.max(140, normal(r, mean, sd))));
    const out = feed(shots, 3, r() < 0.5 ? 0 : 1, 2);
    assertEquals(out.trips, [], `honest player ${player} (mean ${mean.toFixed(0)}, sd ${sd.toFixed(0)}): ${JSON.stringify(out.metrics)}`);
  }
  assertEquals(feed(Array.from({ length: 15 }, () => normal(r, 140, 25))).trips.includes("too_fast"), true);
  assertEquals(feed(Array.from({ length: 15 }, () => normal(r, 230, 4))).trips.includes("too_steady"), true);
  const hug = Array.from({ length: 20 }, (_, i) => (i % 3 === 0 ? 112 + (i % 7) : normal(r, 240, 30)));
  assertEquals(feed(hug).trips.includes("anticipation_hug"), true);
  assertEquals(feed(Array.from({ length: 6 }, () => 250), 1, 1, 1).trips, ["margin_ratio"], "5+ matches, every win by a hair");
  assertEquals(feed(Array.from({ length: 4 }, () => 250), 1, 1, 1).trips, [], "not before 5 matches");
  assertEquals(feed(Array.from({ length: 14 }, () => 120)).trips, [], "under 15 shots nothing is judged");
});

Deno.test("B rolling window keeps the newest shots and the cumulative counters", () => {
  let st = EMPTY_STATS;
  for (let m = 0; m < 30; m++) {
    st = rollStats(st, { shots: [[200 * MS + m, MS], [210 * MS, MS], [220 * MS, MS]], wonShots: 2, narrowWins: 0, hold: false }).next;
  }
  assertEquals(st.shots.length, PLAUSIBILITY.window);
  assertEquals(st.shots[st.shots.length - 3][0], 200 * MS + 29, "newest last");
  assertEquals([st.matches, st.wonShots, st.narrowWins], [30, 60, 0]);
  const out = rollStats(st, { shots: [], wonShots: 0, narrowWins: 0, hold: false });
  assert(out.metrics.foreperiodR === null, "constant holds: no correlation to report");
});

Deno.test("assessMatch: containment decision (hold / shadow / clean) and its log", () => {
  const opp = ghost(260, 240, 300);
  const res = playRecordedMatch(cfg, { seed: SEED, rounds: 3, maxAttempts: 9 }, [shot(255), shot(235), shot(295)], opp);
  const rec = honest(SEED, [shot(255), shot(235), shot(295)], [1, 2, 3]);
  const a = assessMatch(null, rec, res, opp);
  assertEquals([a.hold, a.shadow, a.clean], [true, false, false]);
  assertEquals(a.log.map((l) => l.check), ["margin_vs_ghost"]);

  const fast = { shots: Array.from({ length: 20 }, () => [130 * MS, 2_000 * MS] as [number, number]), wonShots: 0, narrowWins: 0, matches: 7 };
  const res2 = playRecordedMatch(cfg, { seed: SEED, rounds: 3, maxAttempts: 9 }, [shot(205), shot(232), shot(250)], opp);
  const b = assessMatch(fast, honest(SEED, [shot(205), shot(232), shot(250)], [1, 2, 3]), res2, opp);
  assertEquals([b.hold, b.shadow, b.clean], [true, true, false]);
  assert(b.log.some((l) => l.check === "too_fast" && l.action === "shadow"));

  const c = assessMatch(null, honest(SEED, [shot(205), shot(232), shot(250)], [1, 2, 3]), res2, opp);
  assertEquals([c.hold, c.shadow, c.clean], [false, false, true]);
});

Deno.test("quarantine length: 2 matches, PN_QUARANTINE_MATCHES overrides", () => {
  assertEquals(quarantineMatches(), 2);
  env.set("PN_QUARANTINE_MATCHES", "0");
  try {
    assertEquals(quarantineMatches(), 0);
  } finally {
    env.delete("PN_QUARANTINE_MATCHES");
  }
  env.set("PN_QUARANTINE_MATCHES", "-3");
  try {
    assertEquals(quarantineMatches(), 2);
  } finally {
    env.delete("PN_QUARANTINE_MATCHES");
  }
});

// ------------------------------------------------------------------------------------------------ ratings

Deno.test("planGame: the row pn_complete_match writes, with its optimistic version", () => {
  const fresh = { exists: false, mmr: 1200, rd: 350, matches: 0, wins: 0, losses: 0, draws: 0 };
  const p = planGame(fresh, { mmr: 1200, rd: 100 }, 1);
  assertEquals(p.expectMatches, -1, "no row yet: insert");
  assertEquals([p.row.matches, p.row.wins, p.row.losses, p.row.draws], [1, 1, 0, 0]);
  assert(p.next.mmr > 1200 && p.row.mmr === p.next.mmr);
  const seasoned = { exists: true, mmr: 1400, rd: 80, matches: 12, wins: 7, losses: 4, draws: 1 };
  const q = planGame(seasoned, { mmr: 1500, rd: 80 }, 0.5);
  assertEquals(q.expectMatches, 12);
  assertEquals([q.row.matches, q.row.wins, q.row.losses, q.row.draws], [13, 7, 4, 2]);
});

Deno.test("season soft reset: last week's rating carries over, pulled to 1200, RD reopened", () => {
  assertEquals(seasonStart(null), { mmr: RATING.initialMmr, rd: RATING.initialRd });
  const s = seasonStart({ mmr: 1600, rd: 60 });
  assertClose(s.mmr, 1200 + 400 * LEADERBOARD.seasonCarry, 1e-9);
  assertEquals(s.rd, LEADERBOARD.seasonStartRd);
  assertEquals(seasonStart({ mmr: 1000, rd: 340 }, 30).rd, RATING.maxRd, "an idle player's RD keeps growing");
});

type Row = { user_id: string; season: number; mmr: number; rd: number; matches: number; wins: number; losses: number; draws: number; last_played_at: string | null };

/** Read-only PostgREST stand-in for loadRating: select/eq/lt/order/limit/maybeSingle. */
function readDb(rows: Row[]): Db {
  class Q {
    private f: ((r: Row) => boolean)[] = [];
    private desc = false;
    private n = Infinity;
    private key: keyof Row = "season";
    select() { return this; }
    eq(c: keyof Row, v: unknown) { this.f.push((r) => r[c] === v); return this; }
    lt(c: keyof Row, v: number) { this.f.push((r) => (r[c] as number) < v); return this; }
    order(c: keyof Row, o: { ascending: boolean }) { this.key = c; this.desc = !o.ascending; return this; }
    limit(n: number) { this.n = n; return this; }
    maybeSingle() {
      const hit = rows.filter((r) => this.f.every((p) => p(r)))
        .sort((a, b) => ((a[this.key] as number) - (b[this.key] as number)) * (this.desc ? -1 : 1)).slice(0, this.n);
      return Promise.resolve({ data: hit[0] ? { ...hit[0] } : null, error: null });
    }
  }
  return { from: () => new Q() } as unknown as Db;
}

Deno.test("loadRating starts a new week from the latest earlier season", async () => {
  const now = new Date("2026-10-20T10:00:00Z");
  const rows: Row[] = [
    { user_id: "u", season: 202641, mmr: 1300, rd: 70, matches: 20, wins: 12, losses: 8, draws: 0, last_played_at: "2026-10-10T10:00:00Z" },
    { user_id: "u", season: 202642, mmr: 1500, rd: 60, matches: 9, wins: 7, losses: 2, draws: 0, last_played_at: "2026-10-18T10:00:00Z" },
    { user_id: "v", season: 202642, mmr: 2000, rd: 50, matches: 9, wins: 9, losses: 0, draws: 0, last_played_at: "2026-10-18T10:00:00Z" },
  ];
  const r = await loadRating(readDb(rows), "u", 202643, now);
  assertEquals(r.exists, false);
  assertEquals(r.matches, 0, "a fresh weekly row");
  assertClose(r.mmr, 1200 + 300 * LEADERBOARD.seasonCarry, 1e-9);
  assertEquals(r.rd, LEADERBOARD.seasonStartRd);
  const same = await loadRating(readDb(rows), "u", 202642, now);
  assertEquals([same.exists, same.mmr, same.matches], [true, 1500, 9]);
  const stranger = await loadRating(readDb(rows), "w", 202643, now);
  assertEquals([stranger.mmr, stranger.rd], [RATING.initialMmr, RATING.initialRd]);
});
