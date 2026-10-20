// Port of PawNoon.Core.RoundSchedule.
import { beatNs, type DuelConfig, secToNs } from "./config.ts";
import { NetRandom } from "./rng.ts";

export interface RoundSchedule {
  beats: number;
  beatNs: number;
  holdNs: number;
  /** beats * beatNs */
  countdownNs: number;
  /** countdownNs + holdNs: offset from countdown start ("3" appears) at which the cue is due. */
  cueOffsetNs: number;
}

export function makeSchedule(beats: number, beatNsValue: number, holdNs: number): RoundSchedule {
  const countdownNs = beats * beatNsValue;
  return { beats, beatNs: beatNsValue, holdNs, countdownNs, cueOffsetNs: countdownNs + holdNs };
}

/** RoundSchedule.Create: consumes exactly one NextDouble(). */
export function createSchedule(cfg: DuelConfig, rng: NetRandom): RoundSchedule {
  const h = cfg.holdMinSeconds + rng.nextDouble() * (cfg.holdMaxSeconds - cfg.holdMinSeconds);
  return makeSchedule(cfg.countdownBeats, beatNs(cfg), secToNs(h));
}

/**
 * The schedules of the first `count` countdowns of a match seeded with `seed`
 * (LocalAuthority: new Random(seed), one RoundSchedule.Create per StartCountdown, void rounds included).
 */
export function schedulesForSeed(cfg: DuelConfig, seed: number, count: number): RoundSchedule[] {
  const rng = new NetRandom(seed);
  const out: RoundSchedule[] = [];
  for (let i = 0; i < count; i++) out.push(createSchedule(cfg, rng));
  return out;
}

/** RoundSchedule.NumeralAt: which countdown numeral shows at an offset (beats..1), 0 once over. */
export function numeralAt(beats: number, beatNsValue: number, offsetNs: number): number {
  if (offsetNs < 0) return beats;
  const idx = Math.trunc(offsetNs / Math.max(1, beatNsValue));
  if (idx >= beats) return 0;
  return beats - idx;
}
