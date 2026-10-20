// SPEC_V2 §4.3: server-side acceptance of a submitted recording, then the authoritative recompute.
// Pure (no I/O) so it is unit-tested with `deno test`. The function adds the two checks that need
// the database: arrival deadline and one submission per match id.
import {
  anticipationNs,
  type DuelConfig,
  noShotTimeoutNs,
} from "./duel/config.ts";
import type { DamageCurveDef } from "./duel/damage.ts";
import { type AttemptInput, type MatchResult, playRecordedMatch } from "./duel/recorded_match.ts";
import { schedulesForSeed } from "./duel/schedule.ts";
import { type PlayableAttempt, type Recording, RECORDING_VERSION } from "./recording.ts";
import { MATCH, PLAUSIBILITY } from "./server_config.ts";
import type { RecordingRound } from "./recording.ts";

export type RejectReason =
  | "version"
  | "seed_mismatch"
  | "difficulty"
  | "round_count"
  | "round_numbering"
  | "reaction_bounds"
  | "kind_mismatch"
  | "inconsistent_reaction"
  | "non_monotonic"
  | "cue_timing"
  | "input_source"
  // M5 plausibility (Docs/M5_PLAUSIBILITY.md A1-A3)
  | "wall_clock"
  | "samples";

export interface MatchContext {
  seed: number;
  rounds: number;
  maxAttempts: number;
  opponent: readonly PlayableAttempt[];
  curves?: [DamageCurveDef, DamageCurveDef];
  /** Ranked (A2 touch-only input, A3 raw samples). */
  ranked?: boolean;
  /** A1: server-measured time from match-start (issued_at) to this submission, ns. */
  elapsedNs?: number;
  /** A3: ranked recordings must carry samples (PN_REQUIRE_SAMPLES). Present samples are always checked in ranked. */
  requireSamples?: boolean;
}

export type Verdict =
  | { ok: true; result: MatchResult }
  | { ok: false; reason: RejectReason; detail: string };

const CONSISTENCY_TOLERANCE_NS = 1000; // same 1 us as LocalAuthority.Validate

function reject(reason: RejectReason, detail: string): Verdict {
  return { ok: false, reason, detail };
}

export function validateAndScore(
  cfg: DuelConfig,
  rec: Recording,
  ctx: MatchContext,
  opts: { allowInjected: boolean },
): Verdict {
  if (rec.version !== RECORDING_VERSION) return reject("version", `unsupported version ${rec.version}`);
  if (rec.seed !== ctx.seed) return reject("seed_mismatch", `seed ${rec.seed} != issued ${ctx.seed}`);
  if (rec.difficulty !== "hard") return reject("difficulty", "online PvP is always Hard (D3)");
  if (rec.rounds.length > ctx.maxAttempts) {
    return reject("round_count", `${rec.rounds.length} attempts > max ${ctx.maxAttempts}`);
  }

  const antic = anticipationNs(cfg);
  const timeout = noShotTimeoutNs(cfg);

  // ---- per attempt: kind, bounds, reaction == input - cue
  for (let i = 0; i < rec.rounds.length; i++) {
    const r = rec.rounds[i];
    const at = `rounds[${i}]`;
    if (r.inputSource === "bot" || r.inputSource === "remote" || (r.inputSource === "injected" && !opts.allowInjected)) {
      return reject("input_source", `${at}: input source ${r.inputSource} not allowed`);
    }
    // A2: ranked accepts only real touch input with OS timestamps (mouse, keyboard and frame-quantized
    // input are casual only). A no-shot carries no input, so it has no source to check.
    if (ctx.ranked && r.kind !== "noshot") {
      const touch = r.inputSource === "touch" || (r.inputSource === "injected" && opts.allowInjected);
      if (!touch || r.frameQuantized) {
        return reject("input_source", `${at}: ranked needs touch input with OS timestamps (${r.inputSource}${r.frameQuantized ? ", frame-quantized" : ""})`);
      }
    }
    switch (r.kind) {
      case "shot":
      case "anticipation": {
        if (r.cuePresentedNs <= 0 || r.inputNs <= 0) return reject("non_monotonic", `${at}: missing cue/input time`);
        const diff = (r.inputNs - r.cuePresentedNs) - r.reactionNs;
        if (Math.abs(diff) > CONSISTENCY_TOLERANCE_NS) {
          return reject("inconsistent_reaction", `${at}: inputNs - cuePresentedNs != reactionNs (off by ${diff} ns)`);
        }
        if (r.reactionNs > timeout) return reject("reaction_bounds", `${at}: reaction ${r.reactionNs} ns > no-shot timeout`);
        if (r.reactionNs < 0) return reject("reaction_bounds", `${at}: negative reaction`);
        const isAntic = r.reactionNs < antic;
        if (r.kind === "shot" && isAntic) return reject("kind_mismatch", `${at}: shot under ${cfg.anticipationMs} ms is anticipation`);
        if (r.kind === "anticipation" && !isAntic) return reject("kind_mismatch", `${at}: anticipation at or above ${cfg.anticipationMs} ms`);
        break;
      }
      case "early": {
        if (r.inputNs <= 0) return reject("non_monotonic", `${at}: early without input time`);
        if (r.cuePresentedNs !== 0 && r.inputNs >= r.cuePresentedNs) {
          return reject("inconsistent_reaction", `${at}: early press at or after the presented cue`);
        }
        const expected = r.cuePresentedNs !== 0 ? r.inputNs - r.cuePresentedNs : 0;
        if (Math.abs(r.reactionNs - expected) > CONSISTENCY_TOLERANCE_NS) {
          return reject("inconsistent_reaction", `${at}: early reactionNs must be ${expected}`);
        }
        break;
      }
      case "noshot": {
        if (r.cuePresentedNs <= 0) return reject("non_monotonic", `${at}: no-shot without a presented cue`);
        if (r.inputNs !== 0 || r.reactionNs !== 0) return reject("inconsistent_reaction", `${at}: no-shot carries no input`);
        break;
      }
    }
  }

  // ---- authoritative recompute (never the client's result)
  const toInput = (a: { kind: AttemptInput["kind"]; reactionNs: number }): AttemptInput => ({
    kind: a.kind,
    reactionNs: a.kind === "shot" || a.kind === "anticipation" ? a.reactionNs : 0,
  });
  const result = playRecordedMatch(
    cfg,
    { seed: ctx.seed, rounds: ctx.rounds, maxAttempts: ctx.maxAttempts, curves: ctx.curves },
    rec.rounds.map(toInput),
    ctx.opponent.map(toInput),
  );
  if (result.endReason === "player_exhausted" || result.attemptsUsed !== rec.rounds.length) {
    return reject(
      "round_count",
      `the match takes ${result.endReason === "player_exhausted" ? "more than " + rec.rounds.length : result.attemptsUsed} attempts, recording has ${rec.rounds.length}`,
    );
  }
  for (let i = 0; i < result.attempts.length; i++) {
    if (rec.rounds[i].roundNumber !== result.attempts[i].roundNumber) {
      return reject("round_numbering", `rounds[${i}].roundNumber ${rec.rounds[i].roundNumber} != ${result.attempts[i].roundNumber}`);
    }
  }

  // ---- timestamps: monotonic, and consistent with this seed's hold lengths
  const sched = schedulesForSeed(cfg, ctx.seed, rec.rounds.length);
  const timing = checkTiming(rec, sched);
  if (timing) return timing;

  // ---- A1: wall-clock budget
  const wall = checkWallClock(rec, sched, ctx.elapsedNs);
  if (wall) return wall;

  // ---- A3: raw swipe samples (ranked)
  if (ctx.ranked) {
    const samples = checkAllSamples(rec, ctx.requireSamples === true);
    if (samples) return samples;
  }

  return { ok: true, result };
}

function checkTiming(rec: Recording, sched: { countdownNs: number; cueOffsetNs: number }[]): Verdict | null {
  const early = MATCH.cueEarlyToleranceNs;
  const late = MATCH.cueLateToleranceNs;
  let lastTime = -Infinity; // latest timestamp of the previous attempt
  let lastCue = -1; // index of the last attempt with a presented cue
  for (let i = 0; i < rec.rounds.length; i++) {
    const r = rec.rounds[i];
    const at = `rounds[${i}]`;
    const times = [r.countdownStartNs, r.cuePresentedNs || undefined, r.inputNs || undefined]
      .filter((t): t is number => t !== undefined);
    // within an attempt: countdown start < cue; input after countdown start (countdownStartNs is
    // required by parseRecording, so these checks can no longer be skipped by omitting it)
    if (r.cuePresentedNs && r.cuePresentedNs <= r.countdownStartNs) return reject("non_monotonic", `${at}: cue before "3"`);
    if (r.inputNs && r.inputNs < r.countdownStartNs) return reject("non_monotonic", `${at}: input before "3"`);
    // across attempts: strictly later than everything in the previous attempt
    const first = Math.min(...times);
    if (!(first > lastTime)) return reject("non_monotonic", `${at}: timestamps go back in time`);
    lastTime = Math.max(...times);

    const s = sched[i];
    // cue presented relative to "3": cueOffset (+ frame + present latency)
    if (r.cuePresentedNs) {
      const off = r.cuePresentedNs - r.countdownStartNs;
      if (off < s.cueOffsetNs - early || off > s.cueOffsetNs + late) {
        return reject("cue_timing", `${at}: cue ${off} ns after "3", seed says ${s.cueOffsetNs}`);
      }
    }
    // an early press without a cue must still be before the cue could have been presented
    if (r.kind === "early" && r.cuePresentedNs === 0) {
      if (r.inputNs - r.countdownStartNs > s.cueOffsetNs + late) {
        return reject("cue_timing", `${at}: early press after the cue was due`);
      }
    }
    // a presented cue comes at least this attempt's countdown + hold after the previous cue (an
    // attempt in between that was voided before its cue can be shorter than its own schedule)
    if (r.cuePresentedNs) {
      if (lastCue >= 0) {
        const min = s.cueOffsetNs;
        const gap = r.cuePresentedNs - rec.rounds[lastCue].cuePresentedNs;
        if (gap < min - late) return reject("cue_timing", `${at}: cue ${gap} ns after previous cue, minimum ${min}`);
      }
      lastCue = i;
    }
  }
  return null;
}

/**
 * A1 (M5_PLAUSIBILITY): the recording's own span, from the first "3" to its last timestamp, is real phone
 * time the player spent in the match. It cannot be longer than the server-measured issued -> submitted time
 * (plus slack), and it cannot be shorter than the countdowns and holds this seed forces on every attempt that
 * reached its cue. A recording fabricated faster than real time fails one of the two.
 */
export function checkWallClock(rec: Recording, sched: { cueOffsetNs: number }[], elapsedNs: number | undefined): Verdict | null {
  let first = Infinity;
  let last = -Infinity;
  let minSpan = 0;
  for (let i = 0; i < rec.rounds.length; i++) {
    const r = rec.rounds[i];
    first = Math.min(first, r.countdownStartNs);
    last = Math.max(last, r.countdownStartNs, r.cuePresentedNs, r.inputNs);
    if (r.cuePresentedNs > 0) minSpan += sched[i].cueOffsetNs - MATCH.cueEarlyToleranceNs;
  }
  const span = last - first;
  if (span < minSpan) return reject("wall_clock", `recording spans ${span} ns, its countdowns and holds need ${minSpan}`);
  if (elapsedNs !== undefined && span > elapsedNs + PLAUSIBILITY.wallClockSlackNs) {
    return reject("wall_clock", `recording spans ${span} ns, only ${elapsedNs} ns passed since match-start`);
  }
  return null;
}

function checkAllSamples(rec: Recording, required: boolean): Verdict | null {
  for (let i = 0; i < rec.rounds.length; i++) {
    const r = rec.rounds[i];
    if (r.kind === "noshot") continue;
    if (!r.samples || r.samples.length === 0) {
      if (required) return reject("samples", `rounds[${i}]: ranked needs the raw swipe samples`);
      continue;
    }
    const why = checkSamples(r, rec.device.screenHeightPx);
    if (why) return reject("samples", `rounds[${i}]: ${why}`);
  }
  return null;
}

/** True when `dNs` is 1..4 periods of a real digitizer rate (240 / 120 / 60 Hz), within tolerance. */
export function digitizerInterval(dNs: number): boolean {
  const P = PLAUSIBILITY;
  for (const period of P.digitizerPeriodsNs) {
    for (let k = 1; k <= 4; k++) if (Math.abs(dNs - k * period) <= P.digitizerToleranceNs) return true;
  }
  return false;
}

/**
 * A3: redo SwipeDraw's crossing from the OS touch samples (y up, screen px; samples[0] is where the finger
 * went down). Returns why the samples do not fit the reported input, or null.
 * Checks: strictly increasing times; the start inside the bottom band; the threshold crossing interpolated
 * between the two samples around it equals inputNs within 1 us; upward, not sideways; spacing of real
 * digitizer reports; not perfectly regular to the nanosecond.
 */
export function checkSamples(r: RecordingRound, screenHeightPx: number | undefined): string | null {
  const P = PLAUSIBILITY;
  const s = r.samples ?? [];
  if (s.length < 2) return "fewer than 2 samples";
  for (let i = 1; i < s.length; i++) if (!(s[i].tNs > s[i - 1].tNs)) return "sample times not increasing";
  const intervals: number[] = [];
  for (let i = 1; i < s.length; i++) intervals.push(s[i].tNs - s[i - 1].tNs);
  // the finger-down sample may sit any time before the swipe; spacing is checked between movement reports
  const moves = intervals.slice(1);
  for (const d of moves) if (!digitizerInterval(d)) return `sample spacing ${d} ns is not a digitizer rate`;
  if (moves.length >= 3 && moves.every((d) => d === moves[0])) return "sample spacing is exactly regular";
  if (screenHeightPx === undefined) return "samples without device.screenHeightPx";
  const th = Math.max(8, screenHeightPx * P.sampleThresholdFraction);
  const start = s[0];
  if (start.y > screenHeightPx * P.sampleZoneFraction) return "swipe did not start in the holster band";
  let j = -1;
  for (let i = 1; i < s.length; i++) if (s[i].y - start.y >= th) { j = i; break; }
  if (j < 1) return "samples never cross the draw threshold";
  const dy = s[j].y - start.y;
  if (Math.abs(s[j].x - start.x) > dy) return "swipe is sideways";
  const prevDy = s[j - 1].y - start.y;
  const span = dy - prevDy;
  const k = span > 1e-3 ? Math.min(1, Math.max(0, (th - prevDy) / span)) : 1;
  const cross = s[j - 1].tNs + Math.trunc((s[j].tNs - s[j - 1].tNs) * k);
  if (Math.abs(cross - r.inputNs) > P.sampleCrossToleranceNs) return `samples cross at ${cross}, input says ${r.inputNs}`;
  return null;
}
