// POST /functions/v1/live-join  { code: "ABCD" }
// Live friend duel: takes the guest slot of the waiting lobby with this code (case-insensitive). Calling it again
// after a lost answer or a reconnect returns the same match (`rejoin`).
// 200 { matchId, channel, verdict, ok, view, serverNowMs }   view.you = 1
// Errors: 400 bad_request, 404 room_not_found, 410 room_expired | room_used, 409 own_room | room_taken | no_profile,
// 429 rate_limited (also after 12 wrong codes in 10 minutes), 503 busy.
import { requireUser } from "../_shared/auth.ts";
import { admin, must } from "../_shared/db.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import { joinLive } from "../_shared/live.ts";
import { liveAnswer, liveCall, liveService } from "../_shared/live_http.ts";
import { verifiedGun } from "../_shared/live_store.ts";
import { noteRate, rateExhausted, rateLimited, takeRate } from "../_shared/rate.ts";
import { normalizeRoomCode, type RoomJoinState, roomJoinError } from "../_shared/rooms.ts";

serve("live-join", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req, 1024);
  const code = normalizeRoomCode(body.code);
  if (!code) throw new HttpError(400, "bad_request", "code must be 4 letters A-Z");
  await takeRate(db, user.id, "live_join");
  if (await rateExhausted(db, user.id, "live_miss")) throw rateLimited("live_miss");

  const profile = must<{ equipped_gun: string | null; owned_items: string[] | null } | null>(
    await db.from("profiles").select("equipped_gun, owned_items").eq("id", user.id).maybeSingle(),
    "load profile",
  );
  if (!profile) throw new HttpError(409, "no_profile", "profile missing; sign in again");

  const ans = must<{ state: RoomJoinState | "joined"; id?: string } | null>(
    await db.rpc("pn_live_join", { p_guest: user.id, p_code: code }),
    "join live match",
  );
  if (!ans) throw new HttpError(500, "db_error", "database error: join live match");
  if (ans.state !== "joined" && ans.state !== "rejoin") {
    const err = roomJoinError(ans.state);
    await noteRate(db, user.id, "live_miss");
    if (err) throw new HttpError(err.status, err.code, err.message);
    throw new HttpError(500, "db_error", `unexpected join state ${ans.state}`);
  }

  const svc = liveService(db);
  const gun = verifiedGun(profile.equipped_gun, profile.owned_items);
  const r = ans.state === "joined"
    ? await liveCall(() => svc.apply(ans.id!, null, (s, _slot, now) => joinLive(svc.cfg, s, user.id, gun, now)))
    : await liveCall(() => svc.tick(ans.id!, user.id));
  if (r.step.verdict === "NotPlaying") throw new HttpError(410, "room_expired", "the lobby closed");
  if (r.step.verdict === "Taken") throw new HttpError(409, "room_taken", "another player already joined this room");
  return json(liveAnswer(svc, { ...r, slot: 1 }, ans.state === "rejoin" ? "Duplicate" : r.step.verdict));
});
