// POST /functions/v1/room-join  { code: "ABCD" }
// Friend duel (SPEC_V2 §4.4). The first caller with the code holds the room's friend slot.
//   202 { status: "waiting", ... }   the host has not recorded yet: poll again after pollAfterSec (3 s),
//                                    for at most the room's lifetime (expiresAt, 10 min after room-create)
//   200 <match-start response> + room { code }   the host's recording is the opponent, issued through the same
//                                    path as match-start (casual, the recording's own seed)
// Errors: 400 bad_request (code), 404 room_not_found, 410 room_expired | room_used, 409 own_room | room_taken,
// 409 no_profile, 429 rate_limited.
import { requireUser } from "../_shared/auth.ts";
import { admin, must } from "../_shared/db.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import { beforeIssue, issueMatch, responseFromMatch } from "../_shared/match_issue.ts";
import type { RoomWaitingResponse } from "../_shared/match_types.ts";
import { normalizeRoomCode, type RoomJoinState, roomJoinError } from "../_shared/rooms.ts";
import type { MatchRow, RecordingRow } from "../_shared/rows.ts";
import { ROOM } from "../_shared/server_config.ts";

interface JoinAnswer {
  state: RoomJoinState;
  roomId?: string;
  code?: string;
  hostName?: string;
  expiresAt?: string;
  guestMatchId?: string | null;
  recording?: RecordingRow;
}

serve("room-join", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req);
  const code = normalizeRoomCode(body.code);
  if (!code) throw new HttpError(400, "bad_request", "code must be 4 letters A-Z");

  const profile = must<{ id: string } | null>(
    await db.from("profiles").select("id").eq("id", user.id).maybeSingle(),
    "load profile",
  );
  if (!profile) throw new HttpError(409, "no_profile", "profile missing; sign in again");

  const ans = must<JoinAnswer | null>(await db.rpc("pn_room_join", { p_guest: user.id, p_code: code }), "join room");
  if (!ans) throw new HttpError(500, "db_error", "database error: join room");

  const err = roomJoinError(ans.state);
  if (err) throw new HttpError(err.status, err.code, err.message);

  if (ans.state === "waiting") {
    const res: RoomWaitingResponse = {
      status: "waiting",
      code,
      hostName: ans.hostName ?? "Stranger",
      expiresAt: ans.expiresAt ?? new Date(Date.now() + ROOM.ttlSec * 1000).toISOString(),
      pollAfterSec: ROOM.pollSec,
    };
    return json(res, 202);
  }

  if (ans.state === "rejoin") {
    // the answer to an earlier `ready` got lost: hand out the same match while it is still open
    const m = ans.guestMatchId
      ? must<(MatchRow & { issued_at: string }) | null>(
        await db.from("matches").select("*").eq("id", ans.guestMatchId).eq("player_id", user.id).maybeSingle(),
        "load room match",
      )
      : null;
    if (!m || m.status !== "issued") throw new HttpError(410, "room_used", "you already played this room");
    return json({ status: "ready", ...(await responseFromMatch(db, user.id, m, code)) });
  }

  // ready: the room is now ours. Issue the match through the match-start path; give the room back on failure.
  const roomId = ans.roomId!;
  try {
    await beforeIssue(db, user.id);
    const res = await issueMatch(db, user.id, "casual", ans.recording!, { room: { id: roomId, code } });
    const { error } = await db.from("rooms").update({ guest_match_id: res.matchId }).eq("id", roomId);
    if (error) console.error(`[room-join] room ${roomId} match link failed:`, error.message);
    return json({ status: "ready", ...res });
  } catch (e) {
    const { error } = await db.rpc("pn_room_unclaim", { p_room: roomId, p_guest: user.id });
    if (error) console.error(`[room-join] unclaim ${roomId} failed:`, error.message);
    throw e;
  }
});
