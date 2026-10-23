// POST /functions/v1/live-round  { matchId, action?: "sync" | "forfeit" }
// sync (default): the snapshot of the match for this player (reconnection, a missed broadcast, a ClockSync sample),
//   after applying every deadline that has passed. This is also how a match moves on when the round driver died:
//   a late cue is sent now (it only lengthens the hold), a missing ready forfeits that player, missing reports are
//   no-shots. A phone calls it when a deadline it knows about has passed without news, or after reconnecting.
// forfeit: leave. In a lobby the host closes it; in a match the other player wins (ranked: rated as a loss).
// 200 { matchId, channel, verdict, ok, view, serverNowMs }
// Errors: 400 bad_request, 404 match_not_found, 429 rate_limited, 503 busy.
import { requireUser } from "../_shared/auth.ts";
import { admin } from "../_shared/db.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import { driveIfNeeded, liveAnswer, liveCall, liveService, matchIdOf } from "../_shared/live_http.ts";
import { takeRate } from "../_shared/rate.ts";

serve("live-round", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req, 1024);
  const matchId = matchIdOf(body);
  const action = body.action ?? "sync";
  if (action !== "sync" && action !== "forfeit") throw new HttpError(400, "bad_request", 'action must be "sync" or "forfeit"');
  await takeRate(db, user.id, "live_round");
  const svc = liveService(db);
  const r = await liveCall(() => action === "forfeit" ? svc.forfeit(matchId, user.id) : svc.tick(matchId, user.id));
  driveIfNeeded(svc, r);
  return json(liveAnswer(svc, r));
});
