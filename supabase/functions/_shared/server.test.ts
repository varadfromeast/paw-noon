// Unit tests for the server-only rules: §4.3 recording validation and Glicko-lite.
//   deno test --allow-read supabase/functions/_shared/
import { assert, assertClose, assertEquals } from "./testing.ts";
import { hardConfig } from "./duel/load_config.ts";
import { type AttemptInput, playRecordedMatch } from "./duel/recorded_match.ts";
import { schedulesForSeed } from "./duel/schedule.ts";
import { parseRecording, type PlayableAttempt, playableAttempts, type Recording, ShapeError } from "./recording.ts";
import { type MatchContext, validateAndScore } from "./validate_recording.ts";
import { decayRd, expectedScore, updateRating } from "./glicko.ts";
import { matchDurationSec } from "./server_config.ts";
import { applyGame, loadRating } from "./ratings.ts";
import type { Db } from "./db.ts";

const cfg = hardConfig();
const SEED = 424242;
const MS = 1_000_000;

/** An honest recording, timestamps exactly as DuelClient produces them. */
function honest(seed: number, attempts: AttemptInput[], roundNumbers: number[]): Recording {
  const sched = schedulesForSeed(cfg, seed, attempts.length);
  let t = 5_000_000_000;
  const rounds = attempts.map((a, i) => {
    const start = t;
    const cue = start + sched[i].cueOffsetNs + 8 * MS; // one 120 Hz frame of present latency
    t = cue + 6_000 * MS;
    const base = { roundNumber: roundNumbers[i], countdownStartNs: start, inputSource: "touch" as const, frameQuantized: false };
    switch (a.kind) {
      case "early":
        return { ...base, kind: a.kind, reactionNs: 0, cuePresentedNs: 0, inputNs: start + 900 * MS };
      case "noshot":
        return { ...base, kind: a.kind, reactionNs: 0, cuePresentedNs: cue, inputNs: 0 };
      default:
        return { ...base, kind: a.kind, reactionNs: a.reactionNs, cuePresentedNs: cue, inputNs: cue + a.reactionNs };
    }
  });
  return parseRecording({
    version: 1, seed, difficulty: "hard", character: "whiskers", loadout: { gun: "standard" },
    rounds, device: { model: "iPhone15,2", os: "iOS 18.1", refreshHz: 120 },
    clientBuild: "1.0.0+1", submittedAt: "2026-10-20T10:00:00Z",
  });
}

const shot = (ms: number): AttemptInput => ({ kind: "shot", reactionNs: Math.round(ms * MS) });
const early: AttemptInput = { kind: "early", reactionNs: 0 };
const ghost: PlayableAttempt[] = [
  { roundNumber: 1, kind: "shot", reactionNs: 260 * MS },
  { roundNumber: 2, kind: "shot", reactionNs: 240 * MS },
  { roundNumber: 3, kind: "shot", reactionNs: 300 * MS },
];
const ctx: MatchContext = { seed: SEED, rounds: 3, maxAttempts: 9, opponent: ghost };
const opts = { allowInjected: false };

function reason(rec: Recording, c: MatchContext = ctx): string {
  const v = validateAndScore(cfg, rec, c, opts);
  return v.ok ? "ok" : v.reason;
}

Deno.test("honest recording is accepted and scored by the server's own recompute", () => {
  const rec = honest(SEED, [shot(210), shot(250), shot(231)], [1, 2, 3]);
  const v = validateAndScore(cfg, rec, ctx, opts);
  assert(v.ok, JSON.stringify(v));
  const again = playRecordedMatch(cfg, { seed: SEED, rounds: 3, maxAttempts: 9 }, [shot(210), shot(250), shot(231)], ghost);
  assertEquals(v.result, again);
  assertEquals(v.result.endReason, "rounds");
});

Deno.test("void attempt replays the round number", () => {
  const opp: PlayableAttempt[] = [{ roundNumber: 1, kind: "early", reactionNs: 0 }, ...ghost];
  const c = { ...ctx, opponent: opp };
  assertEquals(reason(honest(SEED, [early, shot(200), shot(200), shot(200)], [1, 1, 2, 3]), c), "ok");
  assertEquals(reason(honest(SEED, [early, shot(200), shot(200), shot(200)], [1, 2, 3, 4]), c), "round_numbering");
});

Deno.test("§4.3 rejections", () => {
  const ok = () => honest(SEED, [shot(210), shot(250), shot(231)], [1, 2, 3]);
  assertEquals(reason(ok(), { ...ctx, seed: SEED + 1 }), "seed_mismatch");

  let r = ok();
  r.rounds[1].reactionNs += 2_000; // input - cue no longer equals reaction
  assertEquals(reason(r), "inconsistent_reaction");

  r = honest(SEED, [shot(210), shot(1600), shot(231)], [1, 2, 3]);
  assertEquals(reason(r), "reaction_bounds");

  r = honest(SEED, [shot(90), shot(250), shot(231)], [1, 2, 3]); // claims "shot" under 100 ms
  assertEquals(reason(r), "kind_mismatch");
  r = honest(SEED, [{ kind: "anticipation", reactionNs: 90 * MS }, shot(250), shot(231)], [1, 2, 3]);
  assertEquals(reason(r), "ok");

  assertEquals(reason(honest(SEED, [shot(210), shot(250)], [1, 2])), "round_count");
  assertEquals(reason(honest(SEED, [shot(210), shot(250), shot(231), shot(231)], [1, 2, 3, 4])), "round_count");

  r = honest(SEED + 1, [shot(210), shot(250), shot(231)], [1, 2, 3]); // played with other holds
  r.seed = SEED;
  assertEquals(reason(r), "cue_timing");

  r = ok();
  r.rounds[2].inputSource = "injected";
  assertEquals(reason(r), "input_source");
  assertEquals(validateAndScore(cfg, r, ctx, { allowInjected: true }).ok, true);

  r = ok();
  r.rounds[2] = { ...r.rounds[0], roundNumber: 3 }; // replays round 1's timestamps
  assertEquals(reason(r), "non_monotonic");
});

Deno.test("recording shape errors are precise", () => {
  try {
    parseRecording({ version: 1, seed: 1.5 });
    throw new Error("should throw");
  } catch (e) {
    assert(e instanceof ShapeError, String(e));
  }
});

Deno.test("playable attempts expose only kind/reaction (+ early offset)", () => {
  const rec = honest(SEED, [early, shot(222)], [1, 1]);
  const p = playableAttempts(rec);
  assertEquals(p[0], { roundNumber: 1, kind: "early", reactionNs: 0, inputOffsetNs: 900 * MS });
  assertEquals(p[1], { roundNumber: 1, kind: "shot", reactionNs: 222 * MS });
});

Deno.test("glicko-lite", () => {
  const a = { mmr: 1500, rd: 200 };
  const b = { mmr: 1400, rd: 30 };
  const a2 = { mmr: 1500, rd: 80 };
  const b2 = { mmr: 1400, rd: 80 };
  assertClose(expectedScore(a2, b2) + expectedScore(b2, a2), 1, 1e-12);
  const w = updateRating(a, b, 1);
  assert(w.mmr > a.mmr && w.rd < a.rd);
  const l = updateRating(a, b, 0);
  assert(l.mmr < a.mmr);
  const d = updateRating({ mmr: 1200, rd: 350 }, { mmr: 1200, rd: 350 }, 0.5);
  assertClose(d.mmr, 1200, 1e-9);
  assertEquals(decayRd(50, 0), 50);
  assert(decayRd(50, 30) > 50 && decayRd(50, 10000) === 350);
});

Deno.test("match deadline covers the slowest seed", () => {
  const s = matchDurationSec(cfg, SEED);
  assert(s > 9 * (2.1 + 1) && s < 400, String(s));
});

Deno.test("countdownStartNs is required (the hold-length check cannot be skipped by omitting it)", () => {
  const rec = honest(SEED, [shot(210), shot(250), shot(231)], [1, 2, 3]);
  const raw = JSON.parse(JSON.stringify(rec));
  delete raw.rounds[1].countdownStartNs;
  let threw = false;
  try {
    parseRecording(raw);
  } catch (e) {
    threw = e instanceof ShapeError && String(e.message).includes("countdownStartNs");
  }
  assert(threw, "a recording without countdownStartNs must be a shape error");
});

// ---- ratings: optimistic concurrency (review 2026-10-20)

type Row = { user_id: string; season: number; mmr: number; rd: number; matches: number; wins: number; losses: number; draws: number; last_played_at: string | null };

/** Just enough of the PostgREST builder for ratings.ts: from().select/update/insert().eq()...[.maybeSingle()]. */
function fakeDb(rows: Row[], beforeWrite: () => void): Db {
  class Q {
    private op: "select" | "update" | "insert" = "select";
    private filters: [string, unknown][] = [];
    private patch: Record<string, unknown> = {};
    private one = false;
    select(_cols?: string) { return this; }
    eq(c: string, v: unknown) { this.filters.push([c, v]); return this; }
    maybeSingle() { this.one = true; return this; }
    update(p: Record<string, unknown>) { this.op = "update"; this.patch = p; return this; }
    insert(p: Record<string, unknown>) { this.op = "insert"; this.patch = p; return this; }
    private match(r: Row) { return this.filters.every(([c, v]) => (r as unknown as Record<string, unknown>)[c] === v); }
    private run(): { data: unknown; error: { message: string; code?: string } | null } {
      if (this.op === "select") {
        const hit = rows.filter((r) => this.match(r));
        return { data: this.one ? (hit[0] ? { ...hit[0] } : null) : hit, error: null };
      }
      beforeWrite();
      if (this.op === "insert") {
        const p = this.patch as unknown as Row;
        if (rows.some((r) => r.user_id === p.user_id && r.season === p.season)) {
          return { data: null, error: { message: "duplicate key", code: "23505" } };
        }
        rows.push({ ...p });
        return { data: null, error: null };
      }
      const hit = rows.filter((r) => this.match(r));
      for (const r of hit) Object.assign(r, this.patch);
      return { data: hit.map((r) => ({ user_id: r.user_id })), error: null };
    }
    then<A, B>(ok: (v: ReturnType<Q["run"]>) => A, bad?: (e: unknown) => B) {
      return Promise.resolve().then(() => this.run()).then(ok, bad);
    }
  }
  return { from: (_t: string) => new Q() } as unknown as Db;
}

Deno.test("applyGame re-reads and re-applies when the rating row changed under it (no lost update)", async () => {
  const rows: Row[] = [{ user_id: "u", season: 1, mmr: 1500, rd: 100, matches: 10, wins: 5, losses: 5, draws: 0, last_played_at: null }];
  let interfered = false;
  const db = fakeDb(rows, () => {
    if (interfered) return;
    interfered = true; // another submit rates the same player first
    Object.assign(rows[0], { mmr: 1480, matches: 11, losses: 6 });
  });
  const now = new Date("2026-10-20T10:00:00Z");
  const before = await loadRating(db, "u", 1, now);
  const opp = { mmr: 1500, rd: 100 };
  await applyGame(db, "u", 1, before, opp, 1, now);
  assertEquals(rows[0].matches, 12, "both games are counted");
  assertEquals([rows[0].wins, rows[0].losses], [6, 6]);
  const expected = updateRating({ mmr: 1480, rd: 100 }, opp, 1);
  assertClose(rows[0].mmr, expected.mmr, 1e-9, "applied on top of the concurrent result");
});

Deno.test("applyGame handles a concurrently created first rating row", async () => {
  const rows: Row[] = [];
  let interfered = false;
  const db = fakeDb(rows, () => {
    if (interfered) return;
    interfered = true;
    rows.push({ user_id: "u", season: 1, mmr: 1210, rd: 300, matches: 1, wins: 1, losses: 0, draws: 0, last_played_at: null });
  });
  const now = new Date("2026-10-20T10:00:00Z");
  const fresh = await loadRating(db, "u", 1, now);
  assert(!fresh.exists);
  await applyGame(db, "u", 1, fresh, { mmr: 1200, rd: 350 }, 0, now);
  assertEquals(rows.length, 1);
  assertEquals([rows[0].matches, rows[0].wins, rows[0].losses], [2, 1, 1]);
});
