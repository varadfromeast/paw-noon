// Server-only tunables (not in duel_config.json because the client never needs them).
// Values that ops may want to change without a deploy are read from env (see supabase/README.md).
import type { DuelConfig } from "./duel/config.ts";
import { schedulesForSeed } from "./duel/schedule.ts";

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

export const DAILY = {
  winsRequired: 3,
  coins: 20,
} as const;

export function rankedSeason(): number {
  const v = Number(env("PN_SEASON") ?? "1");
  return Number.isInteger(v) && v > 0 ? v : 1;
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
