// POST /functions/v1/match-start  { mode?: "casual" | "ranked" (default casual), clientBuild: string }
// Issues a recorded-duel match: picks an opponent recording (§4.4), returns its playable attempts
// and the seed both players use (§4.0). Contract: supabase/README.md.
import { requireUser } from "../_shared/auth.ts";
import { admin, must } from "../_shared/db.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import { beforeIssue, issueMatch } from "../_shared/match_issue.ts";
import { parseMode, rankedGate } from "../_shared/ranked.ts";
import { loadRating } from "../_shared/ratings.ts";
import type { ProfileRow, RecordingRow } from "../_shared/rows.ts";
import { rankedSeason } from "../_shared/server_config.ts";

serve("match-start", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req);

  const mode = parseMode(body.mode);
  if (!mode) throw new HttpError(400, "bad_request", 'mode must be "casual" or "ranked"');
  const clientBuild = typeof body.clientBuild === "string" ? body.clientBuild.slice(0, 64) : "";
  if (!clientBuild) throw new HttpError(400, "bad_request", "clientBuild is required");

  const profile = must<Pick<ProfileRow, "flagged"> | null>(
    await db.from("profiles").select("flagged").eq("id", user.id).maybeSingle(),
    "load profile",
  );
  if (!profile) throw new HttpError(409, "no_profile", "profile missing; sign in again");

  if (mode === "ranked") {
    const denied = rankedGate(user, profile, clientBuild);
    if (denied) {
      const msg = denied.code === "link_required" ? "sign in with Apple or Google to play ranked" : "ranked is not available";
      throw new HttpError(denied.status, denied.code, msg, { reasons: denied.reasons });
    }
  }

  await beforeIssue(db, user.id);

  const you = await loadRating(db, user.id, rankedSeason());
  const picked = must<RecordingRow | null>(
    await db.rpc("pn_pick_opponent", { p_user: user.id, p_mmr: Math.round(you.mmr), p_mode: mode }),
    "pick opponent",
  );
  if (!picked || !picked.id) throw new HttpError(503, "no_opponent", "no opponent available (house ghosts missing?)");

  return json(await issueMatch(db, user.id, mode, picked));
});
