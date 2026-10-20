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
import { MATCH } from "./server_config.ts";

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
  | "input_source";

export interface MatchContext {
  seed: number;
  rounds: number;
  maxAttempts: number;
  opponent: readonly PlayableAttempt[];
  curves?: [DamageCurveDef, DamageCurveDef];
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
  const timing = checkTiming(rec, schedulesForSeed(cfg, ctx.seed, rec.rounds.length));
  if (timing) return timing;

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
    // within an attempt: countdown start < cue; input after countdown start
    if (r.countdownStartNs !== undefined) {
      if (r.cuePresentedNs && r.cuePresentedNs <= r.countdownStartNs) return reject("non_monotonic", `${at}: cue before "3"`);
      if (r.inputNs && r.inputNs < r.countdownStartNs) return reject("non_monotonic", `${at}: input before "3"`);
    }
    // across attempts: strictly later than everything in the previous attempt
    const first = Math.min(...times);
    if (!(first > lastTime)) return reject("non_monotonic", `${at}: timestamps go back in time`);
    lastTime = Math.max(...times);

    const s = sched[i];
    // cue presented relative to "3": cueOffset (+ frame + present latency)
    if (r.countdownStartNs !== undefined && r.cuePresentedNs) {
      const off = r.cuePresentedNs - r.countdownStartNs;
      if (off < s.cueOffsetNs - early || off > s.cueOffsetNs + late) {
        return reject("cue_timing", `${at}: cue ${off} ns after "3", seed says ${s.cueOffsetNs}`);
      }
    }
    // an early press without a cue must still be before the cue could have been presented
    if (r.kind === "early" && r.cuePresentedNs === 0 && r.countdownStartNs !== undefined) {
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
