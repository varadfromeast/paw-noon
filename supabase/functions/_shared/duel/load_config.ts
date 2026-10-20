// The shipped rules: _shared/duel_config.json, a byte-for-byte copy of
// Assets/PawNoon/Resources/Data/duel_config.json (sync: supabase/scripts/sync_duel_config.sh).
import raw from "../duel_config.json" with { type: "json" };
import { configFromJson, type DuelConfig } from "./config.ts";

let cached: DuelConfig | null = null;

/** Hard rules (online PvP always uses Hard, D3). */
export function hardConfig(): DuelConfig {
  if (!cached) cached = configFromJson(raw);
  // hand out a copy so a caller can never mutate the shared rules
  return { ...cached, damageMs: [...cached.damageMs], damageValue: [...cached.damageValue] };
}
