// Docs/M5_PLAUSIBILITY.md, the parts that do not reject: A4 (margin against the known ghost) and the
// rolling per-account statistics B5-B9, plus the containment decision (C10 hold / C11 shadow flag).
// Pure: match-submit loads the account's rolling row, calls assessMatch, and hands the result to
// pn_complete_match, which writes it in the same transaction as the match. The rejecting checks
// (A1 wall clock, A2 ranked input source, A3 raw samples) live in validate_recording.ts.
import type { MatchResult } from "./duel/recorded_match.ts";
import type { PlayableAttempt, Recording } from "./recording.ts";
import { PLAUSIBILITY } from "./server_config.ts";

/** One clean shot: [reactionNs, holdNs] (compact: it is stored in account_stats.shots jsonb). */
export type ShotPair = [number, number];

/** account_stats row (migrations/0003_m5.sql). */
export interface AccountStats {
  shots: ShotPair[];
  /** Rounds where both fired and the player was faster (cumulative). */
  wonShots: number;
  /** ... of which by under narrowMarginNs (cumulative, B9). */
  narrowWins: number;
  /** Validated matches counted (cumulative). */
  matches: number;
}

export const EMPTY_STATS: AccountStats = { shots: [], wonShots: 0, narrowWins: 0, matches: 0 };

export interface MatchSignals {
  shots: ShotPair[];
  wonShots: number;
  narrowWins: number;
  /** A4: this match's recording should not be served to anybody. */
  hold: boolean;
}

/** A4: per match, against the ghost's known reactions. Only attempts where both sides really fired count. */
export function matchSignals(rec: Recording, result: MatchResult, opponent: readonly PlayableAttempt[]): MatchSignals {
  const P = PLAUSIBILITY;
  const shots: ShotPair[] = [];
  let wonShots = 0;
  let narrowWins = 0;
  for (let i = 0; i < result.attempts.length; i++) {
    const a = result.attempts[i];
    if (a.void || a.outcomes[0] !== "Shot") continue;
    const mine = rec.rounds[i].reactionNs;
    shots.push([mine, a.holdNs]);
    const opp = opponent[i];
    if (a.outcomes[1] !== "Shot" || !opp) continue;
    if (mine < opp.reactionNs) {
      wonShots++;
      if (opp.reactionNs - mine < P.narrowMarginNs) narrowWins++;
    }
  }
  const hold = narrowWins >= P.narrowHoldMinRounds && narrowWins >= P.narrowHoldShare * wonShots;
  return { shots, wonShots, narrowWins, hold };
}

export function median(xs: readonly number[]): number {
  const s = [...xs].sort((a, b) => a - b);
  const n = s.length;
  if (n === 0) return NaN;
  return n % 2 ? s[(n - 1) / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
}

export function stdDev(xs: readonly number[]): number {
  const n = xs.length;
  if (n < 2) return NaN;
  const m = xs.reduce((a, b) => a + b, 0) / n;
  return Math.sqrt(xs.reduce((a, b) => a + (b - m) * (b - m), 0) / (n - 1));
}

/** Pearson correlation of two equal-length series (NaN when undefined). */
export function pearson(xs: readonly number[], ys: readonly number[]): number {
  const n = xs.length;
  if (n < 3 || ys.length !== n) return NaN;
  const mx = xs.reduce((a, b) => a + b, 0) / n;
  const my = ys.reduce((a, b) => a + b, 0) / n;
  let sxy = 0, sxx = 0, syy = 0;
  for (let i = 0; i < n; i++) {
    sxy += (xs[i] - mx) * (ys[i] - my);
    sxx += (xs[i] - mx) ** 2;
    syy += (ys[i] - my) ** 2;
  }
  return sxx > 0 && syy > 0 ? sxy / Math.sqrt(sxx * syy) : NaN;
}

export type RollingTrip = "too_fast" | "too_steady" | "anticipation_hug" | "margin_ratio";

export interface RollingResult {
  next: AccountStats;
  trips: RollingTrip[];
  metrics: { shots: number; medianNs: number | null; sdNs: number | null; hugShare: number | null; marginShare: number | null; foreperiodR: number | null };
}

/** B5-B9 over the account's rolling window after adding this match. */
export function rollStats(prev: AccountStats | null, sig: MatchSignals): RollingResult {
  const P = PLAUSIBILITY;
  const p = prev ?? EMPTY_STATS;
  const shots = [...p.shots, ...sig.shots].slice(-P.window);
  const next: AccountStats = {
    shots,
    wonShots: p.wonShots + sig.wonShots,
    narrowWins: p.narrowWins + sig.narrowWins,
    matches: p.matches + 1,
  };
  const r = shots.map((s) => s[0]);
  const trips: RollingTrip[] = [];
  let medianNs: number | null = null, sdNs: number | null = null, hugShare: number | null = null, foreperiodR: number | null = null;
  if (r.length >= P.minShots) {
    medianNs = median(r);
    sdNs = stdDev(r);
    hugShare = r.filter((x) => x >= P.hugLowNs && x < P.hugHighNs).length / r.length;
    if (medianNs < P.tooFastMedianNs) trips.push("too_fast");
    if (sdNs < P.tooSteadySdNs) trips.push("too_steady");
    if (hugShare > P.hugShare) trips.push("anticipation_hug");
  }
  if (r.length >= P.foreperiodMinShots) {
    const fr = pearson(shots.map((s) => s[1]), r);
    foreperiodR = Number.isNaN(fr) ? null : fr;
  }
  let marginShare: number | null = null;
  if (next.matches >= P.marginMinMatches && next.wonShots > 0) {
    marginShare = next.narrowWins / next.wonShots;
    if (next.wonShots >= P.marginMinMatches && marginShare >= P.marginShare) trips.push("margin_ratio");
  }
  return { next, trips, metrics: { shots: r.length, medianNs, sdNs, hugShare, marginShare, foreperiodR } };
}

export interface PlausibilityLog {
  check: string;
  action: "hold" | "shadow" | "log";
  detail: Record<string, unknown>;
}

export interface Assessment {
  signals: MatchSignals;
  rolling: RollingResult;
  /** Store the new recording but never serve it (C10). */
  hold: boolean;
  /** Set the account's shadow flag (C11): pool and leaderboards only with other flagged players. */
  shadow: boolean;
  /** Nothing tripped: the owner's quarantined recordings move one match closer to release. */
  clean: boolean;
  log: PlausibilityLog[];
}

/** Everything match-submit needs to decide containment for one validated match. */
export function assessMatch(prev: AccountStats | null, rec: Recording, result: MatchResult, opponent: readonly PlayableAttempt[]): Assessment {
  const signals = matchSignals(rec, result, opponent);
  const rolling = rollStats(prev, signals);
  const log: PlausibilityLog[] = [];
  if (signals.hold) {
    log.push({ check: "margin_vs_ghost", action: "hold", detail: { narrowWins: signals.narrowWins, wonShots: signals.wonShots } });
  }
  for (const t of rolling.trips) log.push({ check: t, action: "shadow", detail: { ...rolling.metrics } });
  // B8 is a signal to study later, not a rule: logged every 10th match once there are enough shots
  if (rolling.metrics.foreperiodR !== null && rolling.next.matches % 10 === 0) {
    log.push({ check: "foreperiod", action: "log", detail: { r: rolling.metrics.foreperiodR, shots: rolling.metrics.shots } });
  }
  const shadow = rolling.trips.length > 0;
  const hold = signals.hold || shadow;
  return { signals, rolling, hold, shadow, clean: !hold, log };
}
