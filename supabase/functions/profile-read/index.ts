// POST /functions/v1/profile-read  {}
// The caller's profile card (SPEC_V2 §5 / §7, M6): display name, cat, guest or linked (Apple / Google), server-
// validated PvP stats (matches, wins, best reaction, LIGHTNING count), the current ranked season's MMR, coins,
// owned items. Contract: supabase/README.md, schemas/profile.response.schema.json.
import { requireUser } from "../_shared/auth.ts";
import { admin } from "../_shared/db.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import { readProfile } from "../_shared/profile.ts";

serve("profile-read", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  await readJson(req, 1024); // body is ignored, but must be JSON (or empty)
  const p = await readProfile(db, user);
  if (!p) throw new HttpError(409, "no_profile", "no profile row for this user");
  return json(p);
});
