// POST /functions/v1/room-create  {}
// Friend duel (SPEC_V2 §4.4): opens a 4-letter room. The caller's NEXT recording (the next match they
// complete, any mode, issued after this call) is reserved for the one friend who joins with the code.
// The room lives ROOM.ttlSec (10 min). A new room cancels the caller's room that is still waiting.
import { requireUser } from "../_shared/auth.ts";
import { admin, must } from "../_shared/db.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import type { RoomCreateResponse } from "../_shared/match_types.ts";
import { candidateCodes } from "../_shared/rooms.ts";
import { ROOM } from "../_shared/server_config.ts";

interface RoomRow {
  id: string;
  code: string;
  status: string;
  expires_at: string;
}

serve("room-create", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  await readJson(req, 1024); // body is {} (validated as JSON for a uniform 400)

  const profile = must<{ id: string } | null>(
    await db.from("profiles").select("id").eq("id", user.id).maybeSingle(),
    "load profile",
  );
  if (!profile) throw new HttpError(409, "no_profile", "profile missing; sign in again");

  const since = new Date(Date.now() - 600_000).toISOString();
  const { count, error } = await db.from("rooms").select("id", { count: "exact", head: true })
    .eq("host_id", user.id).gte("created_at", since);
  if (error) must({ data: null, error }, "count recent rooms");
  if ((count ?? 0) >= ROOM.maxCreatesPer10Min) {
    throw new HttpError(429, "rate_limited", "too many rooms opened, wait a few minutes", { retryAfterSec: 120 });
  }

  const rows = must<RoomRow[] | null>(
    await db.rpc("pn_room_create", { p_host: user.id, p_codes: candidateCodes(ROOM.candidates), p_ttl_seconds: ROOM.ttlSec }),
    "create room",
  ) ?? [];
  const room = rows[0];
  if (!room) throw new HttpError(503, "busy", "no free room code right now, try again");

  const res: RoomCreateResponse = { code: room.code, status: "open", expiresAt: room.expires_at, ttlSec: ROOM.ttlSec };
  return json(res);
});
