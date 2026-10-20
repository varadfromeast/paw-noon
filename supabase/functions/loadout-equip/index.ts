// POST /functions/v1/loadout-equip   (SPEC_V2 §6, M7)
//
// Request:  { "gun": "standard" | "peacemaker" }
// 200:      { "equippedGun": "peacemaker", "ownedItems": ["peacemaker"] }
// 403 not_owned when the gun is not in profiles.owned_items (written by purchase-validate only);
// 400 bad_request for an unknown gun. The equipped gun only matters for presentation and for the client's
// own damage curve in solo / casual; match-submit checks recording.loadout.gun against owned_items itself.
import { requireUser } from "../_shared/auth.ts";
import { admin } from "../_shared/db.ts";
import { isGunId } from "../_shared/duel/loadouts.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";

serve("loadout-equip", async (req) => {
  const user = await requireUser(req);
  const body = await readJson(req, 1024);
  if (!isGunId(body.gun)) throw new HttpError(400, "bad_request", "gun must be a known gun id");
  const db = admin();
  const { data, error } = await db.rpc("pn_set_equipped_gun", { p_user: user.id, p_gun: body.gun });
  if (error) {
    console.error("[loadout-equip]", error);
    throw new HttpError(500, "db_error", "could not equip");
  }
  const { data: prof } = await db.from("profiles").select("owned_items, equipped_gun").eq("id", user.id).maybeSingle();
  if (data === null || data === undefined) {
    throw new HttpError(403, "not_owned", `${body.gun} is not owned`, { ownedItems: prof?.owned_items ?? [] });
  }
  return json({ equippedGun: data, ownedItems: prof?.owned_items ?? [] });
});
