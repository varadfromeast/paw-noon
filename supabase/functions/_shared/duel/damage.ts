// Port of PawNoon.Core.DamageCurve and Grading.
import type { DuelConfig } from "./config.ts";

export type Grade = "None" | "Lightning" | "Perfect" | "Great" | "Good" | "Slow" | "Slowpoke";

/** A damage curve: piecewise-linear anchors (ms -> damage). */
export interface DamageCurveDef {
  damageMs: readonly number[];
  damageValue: readonly number[];
}

/** DamageCurve.Evaluate. Below the first anchor = first value; above the last anchor = 0. */
export function evaluateDamage(curve: DamageCurveDef, reactionMs: number): number {
  const xs = curve.damageMs;
  const ys = curve.damageValue;
  const n = Math.min(xs.length, ys.length);
  if (n === 0) return 0;
  if (Number.isNaN(reactionMs) || reactionMs < 0) return 0;
  if (reactionMs <= xs[0]) return ys[0];
  if (reactionMs > xs[n - 1]) return 0;
  for (let k = 1; k < n; k++) {
    if (reactionMs <= xs[k]) {
      const span = xs[k] - xs[k - 1];
      const t = span <= 0 ? 1.0 : (reactionMs - xs[k - 1]) / span;
      return ys[k - 1] + (ys[k] - ys[k - 1]) * t;
    }
  }
  return ys[n - 1];
}

/** Grading.FromMs. */
export function gradeFromMs(cfg: DuelConfig, reactionMs: number): Grade {
  if (reactionMs <= cfg.gradeLightningMs) return "Lightning";
  if (reactionMs <= cfg.gradePerfectMs) return "Perfect";
  if (reactionMs <= cfg.gradeGreatMs) return "Great";
  if (reactionMs <= cfg.gradeGoodMs) return "Good";
  if (reactionMs <= cfg.gradeSlowMs) return "Slow";
  return "Slowpoke";
}

/** Grading.Label. */
export function gradeLabel(g: Grade): string {
  switch (g) {
    case "Lightning": return "LIGHTNING!";
    case "Perfect": return "PERFECT!";
    case "Great": return "GREAT!";
    case "Good": return "GOOD";
    case "Slow": return "SLOW";
    case "Slowpoke": return "SLOWPOKE";
    default: return "";
  }
}
