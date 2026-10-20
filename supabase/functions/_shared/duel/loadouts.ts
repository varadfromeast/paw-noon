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

export function isGunId(v: unknown): v is GunId {
  return typeof v === "string" && (GUNS as readonly string[]).includes(v);
}

/** Guns.Effective: ranked scores every gun with the standard curve. */
export function effectiveGun(gun: GunId, ranked: boolean): GunId {
  return ranked ? "standard" : gun;
}

/** Item a store product unlocks (purchase-validate), or null. */
export function gunForProduct(productId: string): Exclude<GunId, "standard"> | null {
  for (const [gun, pid] of Object.entries(GUN_PRODUCT)) if (pid === productId) return gun as Exclude<GunId, "standard">;
  return null;
}

/**
 * Guns.DamageValues + DamageCurve: the curve a shooter fires with. Peacemaker = cfg.damageValuePeacemaker
 * (duel_config.json, top three bands up, slow bands unchanged, same ms anchors as the standard curve).
 */
export function curveFor(cfg: DuelConfig, gun: GunId, ranked: boolean): DamageCurveDef {
  if (effectiveGun(gun, ranked) === "peacemaker" && cfg.damageValuePeacemaker.length >= 2) {
    return { damageMs: cfg.damageMs, damageValue: cfg.damageValuePeacemaker };
  }
  return { damageMs: cfg.damageMs, damageValue: cfg.damageValue };
}
