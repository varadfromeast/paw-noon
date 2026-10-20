// Port of PawNoon.Core.SuddenDeath ("LAST LIGHT", SPEC_V2 §8.4).
// When BOTH duelists are alive and at or below cfg.suddenDeathHp as a standoff starts, that round's
// damage is multiplied by cfg.suddenDeathDamageMultiplier (x2). The match driver evaluates it before every
// countdown and hands it to LocalAuthority.setDamageMultiplier; the authority applies it once per shot.
import type { DuelConfig } from "./config.ts";
import type { MatchState } from "./match_state.ts";

export function suddenDeathTriggers(cfg: DuelConfig, hp0: number, hp1: number): boolean {
  if (!cfg.suddenDeathEnabled) return false;
  return hp0 > 0 && hp1 > 0 && hp0 <= cfg.suddenDeathHp && hp1 <= cfg.suddenDeathHp;
}

/** SuddenDeath.MultiplierFor: the multiplier for the next round of this match. */
export function suddenDeathMultiplier(cfg: DuelConfig, m: MatchState): number {
  return !m.over && suddenDeathTriggers(cfg, m.hp[0], m.hp[1]) ? cfg.suddenDeathDamageMultiplier : 1;
}
