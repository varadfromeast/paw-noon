// Guns (SPEC_V2 §6). In ranked every gun is normalised to the standard curve; in casual each
// player's own (server-verified) loadout applies.
import type { DuelConfig } from "./config.ts";
import type { DamageCurveDef } from "./damage.ts";

export type GunId = "standard" | "peacemaker";

export const GUNS: readonly GunId[] = ["standard", "peacemaker"];

/** Store product that grants each paid gun. */
export const GUN_PRODUCT: Record<Exclude<GunId, "standard">, string> = {
  peacemaker: "com.pawnoon.pixel.peacemaker",
};

/** Peacemaker: top three bands up, slow bands unchanged (same ms anchors as the standard curve). */
const PEACEMAKER_VALUES = [48, 42, 34, 26, 16, 8, 4, 2] as const;

export function isGunId(v: unknown): v is GunId {
  return typeof v === "string" && (GUNS as readonly string[]).includes(v);
}

export function curveFor(cfg: DuelConfig, gun: GunId, ranked: boolean): DamageCurveDef {
  if (ranked || gun === "standard") return { damageMs: cfg.damageMs, damageValue: cfg.damageValue };
  return { damageMs: cfg.damageMs, damageValue: [...PEACEMAKER_VALUES] };
}
