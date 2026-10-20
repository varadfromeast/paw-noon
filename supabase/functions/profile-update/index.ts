// POST /functions/v1/profile-update  { displayName?: string, character?: "whiskers" | "pawtruth" }
// The only way to change a display name (M6): the database no longer lets clients write profiles.display_name
// directly (migrations/0060_m6_accounts.sql). Validation = display_name.ts (same rules and vectors as the C#
// keypad). A new name is also written onto the player's own pooled ghost recordings, so opponents meet the
// current name. Answers the full profile (same shape as profile-read).
import { requireUser } from "../_shared/auth.ts";
import { admin, must } from "../_shared/db.ts";
import { validateDisplayName } from "../_shared/display_name.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import { readProfile } from "../_shared/profile.ts";
import { CHARACTERS, normalizeCharacter } from "../_shared/recording.ts";
import { takeRate } from "../_shared/rate.ts";

serve("profile-update", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req, 4 * 1024);
  await takeRate(db, user.id, "profile_update");
  const patch: Record<string, string> = {};

  if ("displayName" in body) {
    const v = validateDisplayName(body.displayName);
    if (!v.ok) throw new HttpError(400, "bad_display_name", "display name refused", { reason: v.error });
    patch.display_name = v.name;
  }
  if ("character" in body) {
    const c = normalizeCharacter(typeof body.character === "string" ? body.character.toLowerCase() : null);
    if (c === null) throw new HttpError(400, "bad_request", `character must be one of ${CHARACTERS.join(", ")}`);
    patch.character = c;
  }
  if (Object.keys(patch).length === 0) throw new HttpError(400, "bad_request", "nothing to update (displayName, character)");

  const updated = must<{ id: string }[] | null>(
    await db.from("profiles").update(patch).eq("id", user.id).select("id"),
    "update profile",
  ) ?? [];
  if (updated.length === 0) throw new HttpError(409, "no_profile", "no profile row for this user");

  if (patch.display_name) {
    // pooled ghosts carry a name snapshot; refresh the player's own (house ghosts have no owner)
    const { error } = await db.from("recordings").update({ display_name: patch.display_name }).eq("owner_id", user.id);
    if (error) console.error("[profile-update] ghost rename", user.id, error.message); // the profile change stands
    console.log(`[profile-update] ${user.id} is now "${patch.display_name}"`);
  }

  const p = await readProfile(db, user);
  if (!p) throw new HttpError(409, "no_profile", "no profile row for this user");
  return json(p);
});
