// Server-only tunables (not in duel_config.json because the client never needs them).
// Values that ops may want to change without a deploy are read from env (see supabase/README.md).
import type { DuelConfig } from "./duel/config.ts";
import { schedulesForSeed } from "./duel/schedule.ts";
import { weeklySeasonId } from "./season.ts";

function env(name: string): string | undefined {
  // Deno in Edge Functions; undefined under other runtimes (unit tests).
  const d = (globalThis as { Deno?: { env: { get(k: string): string | undefined } } }).Deno;
  try {
    return d?.env.get(name);
  } catch {
    return undefined;
  }
}

export const MATCH = {
  /** D7: a PvP match is 3 resolved rounds. */
  rounds: 3,
  /** Safety cap on countdown attempts (void replays included). */
  maxAttempts: 9,
  /** §4.3: the recording must arrive within matchDuration + 60 s of match/start. */
  submitSlackSec: 60,
  /** Upper bound of everything around one attempt that is not countdown + hold: banter lead,
   *  standoff, no-shot window, hit-stop / slow-mo, result panel. Generous on purpose. */
  perAttemptOverheadSec: 12,
  /** Intro (walk-in, name plates) + network. */
  matchOverheadSec: 15,
  /** Recording timing checks (§4.3 "consistent with the seed's hold lengths"). The cue is decided at
   *  frame start (up to a frame late, more on a hitch) and presented `presentLatency` later
   *  (calibration may be negative). */
  cueEarlyToleranceNs: 50_000_000,
  cueLateToleranceNs: 500_000_000,
  /** Recordings leave the pool after 30 days or 20 uses (§4.4). */
  poolMaxAgeDays: 30,
  poolMaxUses: 20,
  /** Three rejected recordings = account flagged, ranked disabled (§4.3). */
  rejectionsToFlag: 3,
  /** Max request body for match-submit (raw swipe samples included). */
  maxBodyBytes: 256 * 1024,
  /** match-start calls per player per minute (each call burns one `use` of a pooled recording and
   *  reveals its reactions, so unthrottled calls could drain or scrape the pool). */
  maxStartsPerMinute: 6,
  /** A match left in `submitting` this long (function crashed / timed out mid-submit) is voided by the
   *  player's next match-start, as `stale_submit` (never counts towards the cheat flag). */
  staleSubmitMinutes: 5,
} as const;

export const RATING = {
  initialMmr: 1200,
  initialRd: 350,
  minRd: 50,
  maxRd: 350,
  /** RD growth per idle day (Glicko c). sqrt(350^2-50^2)/c^2 ~ 300 days to fully decay with c = 20. */
  rdPerDay: 20,
} as const;

/** Friend duels (SPEC_V2 §4.4): a 4-letter room code reserves the host's NEXT recording for one friend. */
export const ROOM = {
  codeLength: 4,
  /** Generated codes skip I and O (they read as 1 and 0 on a small pixel font). Joining accepts A-Z. */
  alphabet: "ABCDEFGHJKLMNPQRSTUVWXYZ",
  /** The host must record and the friend must join within this window from room-create. */
  ttlSec: 600,
  /** room-join answers `waiting` with this poll interval until the host's recording is in. */
  pollSec: 3,
  /** room-create calls per host in 10 minutes. */
  maxCreatesPer10Min: 5,
  /** Candidate codes tried per room-create (unique among live rooms). */
  candidates: 12,
  /** Never generated (a code is shown big on screen and shared in messages). */
  blocked: [
    "ANAL", "ANUS", "ARSE", "BOOB", "BUTT", "COCK", "CRAP", "CUNT", "DAMN", "DICK", "DUMB", "DYKE", "FAGS",
    "FUCK", "FUKK", "GOOK", "HATE", "HELL", "HOMO", "JERK", "JIZZ", "KIKE", "KILL", "MUFF", "NAZI", "PAKI",
    "PEDO", "PISS", "PORN", "PUBE", "RAPE", "SCUM", "SEXY", "SHAT", "SHIT", "SLAG", "SLUT", "SMEG", "SPIC",
    "SUCK", "TITS", "TURD", "TWAT", "WANK", "WHOR", "DEAD",
  ] as readonly string[],
} as const;

/** leaderboard-read (SPEC_V2 §7). */
export const LEADERBOARD = {
  top: 50,
  /** A weekly ranked row appears once the player has this many rated matches in the week. */
  minRankedMatches: 5,
  /** Season soft reset: a new week starts from last week's MMR pulled this far towards the initial
   *  rating, with RD at least `seasonStartRd` (so the first games of the week move it quickly). */
  seasonCarry: 0.75,
  seasonStartRd: 200,
} as const;

/**
 * Plausibility checks for ghost duels (Docs/M5_PLAUSIBILITY.md). Server-only on purpose: the client never
 * sees these thresholds, and a tripped check is never named in a response.
 */
export const PLAUSIBILITY = {
  // ---- A1 wall-clock budget (reject `wall_clock`)
  /** The recording's own span (first "3" to its last timestamp) may exceed the server-measured
   *  issued -> submitted time by at most this much (clock-rate differences, request latency). */
  wallClockSlackNs: 2_000_000_000,
  // ---- A3 raw samples (ranked; reject `samples`)
  /** Interpolated crossing vs inputNs. */
  sampleCrossToleranceNs: 1_000,
  /** SwipeDraw: threshold = 12 % of the screen height, holster = bottom 25 % of the safe area (we only
   *  know the screen, so the start must be within the bottom 30 % of the screen). */
  sampleThresholdFraction: 0.12,
  sampleZoneFraction: 0.30,
  /** Real digitizers report at 240 / 120 / 60 Hz (k = 1..4 periods, +- tolerance). */
  digitizerPeriodsNs: [4_166_667, 8_333_333, 16_666_667] as readonly number[],
  digitizerToleranceNs: 1_500_000,
  // ---- A4 margin against the known ghost (hold the new recording, feeds B9)
  narrowMarginNs: 20_000_000,
  /** Hold a match's recording when at least this many won shot rounds were won by under narrowMarginNs ... */
  narrowHoldMinRounds: 2,
  /** ... and they are at least this share of the won shot rounds. */
  narrowHoldShare: 0.66,
  // ---- B rolling per-account statistics (shadow flag)
  /** Shots kept per account (newest last). */
  window: 60,
  minShots: 15,
  /** B5 median shot reaction under this. */
  tooFastMedianNs: 150_000_000,
  /** B6 standard deviation under this. */
  tooSteadySdNs: 8_000_000,
  /** B7 share of shots in [100, 130) ms above this. */
  hugLowNs: 100_000_000,
  hugHighNs: 130_000_000,
  hugShare: 0.20,
  /** B8 (log only): Pearson r(hold, reaction) is computed from this many shots and logged, never flags. */
  foreperiodMinShots: 40,
  /** B9 margin ratio: share of won rounds won by under narrowMarginNs, over at least this many matches. */
  marginMinMatches: 5,
  marginShare: 0.60,
  // ---- C10 quarantine
  /** A new pooled recording is served to strangers only after its owner completes this many further
   *  clean matches (env PN_QUARANTINE_MATCHES overrides, e.g. 0 on a local stack). */
  quarantineMatches: 2,
} as const;

export function quarantineMatches(): number {
  const raw = env("PN_QUARANTINE_MATCHES");
  if (raw === undefined || raw.trim() === "") return PLAUSIBILITY.quarantineMatches;
  const v = Number(raw);
  return Number.isInteger(v) && v >= 0 && v <= 20 ? v : PLAUSIBILITY.quarantineMatches;
}

/** Ranked recordings must carry raw swipe samples (A3). Off until the client sends them (open question,
 *  Docs/DECISIONS.md M5): when samples ARE present they are always checked. */
export function requireSamplesRanked(): boolean {
  return env("PN_REQUIRE_SAMPLES") === "true";
}

/**
 * Per-user rate limits (review pass 2) for the functions match-start / room-create did not already throttle.
 * `windowSec` / `max`: at most `max` calls in any `windowSec`; enforced by pn_rate_take (migrations/0061).
 * Generous for people, tight for scripts. room-join: polling every ROOM.pollSec (3 s) is 20 calls a minute;
 * `room_miss` counts only answers that reveal nothing (no such room / taken / own room), so guessing codes is
 * slow (12 wrong codes in 10 minutes) while a waiting friend can poll for the whole room lifetime.
 */
export const RATE = {
  room_join: { windowSec: 60, max: 40 },
  room_miss: { windowSec: 600, max: 12 },
  profile_update: { windowSec: 3600, max: 12 },
  profile_read: { windowSec: 60, max: 30 },
  purchase_validate: { windowSec: 600, max: 30 },
  daily_claim: { windowSec: 60, max: 12 },
  leaderboard_read: { windowSec: 60, max: 30 },
  loadout_equip: { windowSec: 60, max: 20 },
  account_delete: { windowSec: 3600, max: 6 },
} as const;
export type RateKind = keyof typeof RATE;

export const DAILY = {
  winsRequired: 3,
  coins: 20,
} as const;

/**
 * Ranked season id (`ratings.season`, `matches.season`). M5: one season per ISO week, encoded YYYYWW
 * (2026-W43 -> 202643, leaderboard `ranked_2026_43`), see season.ts. `PN_SEASON` (a positive integer)
 * pins a fixed season instead (ops override, tests); a pinned season below 100000 is named
 * `ranked_season_N` like M3 did.
 */
export function rankedSeason(now: Date = new Date()): number {
  const raw = env("PN_SEASON");
  if (raw !== undefined && raw.trim() !== "") {
    const v = Number(raw);
    if (Number.isInteger(v) && v > 0) return v;
  }
  return weeklySeasonId(now);
}

/** Client builds allowed in ranked (comma separated, "*" = any). Empty = ranked closed. */
export function rankedBuildAllowed(build: string): boolean {
  const list = (env("PN_RANKED_BUILDS") ?? "").split(",").map((s) => s.trim()).filter(Boolean);
  return list.includes("*") || list.includes(build);
}

/** Local dev only: lets guest (anonymous) accounts play ranked, since Apple/Google sign-in is not
 *  available against `supabase start`. Never set in production. */
export function rankedAllowGuests(): boolean {
  return env("PN_RANKED_ALLOW_GUESTS") === "true";
}

/** Local dev / ReviewDirector smoke tests submit injected input; production never accepts it. */
export function allowInjectedInput(): boolean {
  return env("PN_ALLOW_INJECTED") === "true";
}

/** Day boundary for the daily bounty (IANA zone). */
export function dayTimeZone(): string {
  return env("PN_DAY_TZ") ?? "Asia/Kolkata";
}

/** Worst-case wall time of a match on this seed (all maxAttempts played), in seconds. */
export function matchDurationSec(cfg: DuelConfig, seed: number, maxAttempts: number = MATCH.maxAttempts): number {
  let ns = 0;
  for (const s of schedulesForSeed(cfg, seed, maxAttempts)) ns += s.cueOffsetNs;
  return MATCH.matchOverheadSec + maxAttempts * MATCH.perAttemptOverheadSec + ns / 1e9;
}
