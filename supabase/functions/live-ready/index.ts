// POST /functions/v1/live-ready  { matchId, attempt }
// "I am ready for attempt N" (after the join, after every round result, after a reconnect). When both players are
// ready the round starts: round_start is broadcast at once and this request keeps the round driver running in the
// background (secret cue time -> DRAW broadcast -> report deadline -> no-shots resolved).
// 200 { matchId, channel, verdict, ok, view, serverNowMs }  verdict: Accepted | Duplicate | Stale | WrongRound | NotPlaying
// Errors: 400 bad_request, 404 match_not_found, 429 rate_limited, 503 busy.
import { requireUser } from "../_shared/auth.ts";
import { admin } from "../_shared/db.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import { driveIfNeeded, liveAnswer, liveCall, liveService, matchIdOf } from "../_shared/live_http.ts";
import { takeRate } from "../_shared/rate.ts";

serve("live-ready", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req, 1024);
  const matchId = matchIdOf(body);
  const attempt = body.attempt;
  if (typeof attempt !== "number" || !Number.isInteger(attempt) || attempt < 0 || attempt > 64) {
    throw new HttpError(400, "bad_request", "attempt must be an integer >= 0");
  }
  await takeRate(db, user.id, "live_ready");
  const svc = liveService(db);
  const r = await liveCall(() => svc.ready(matchId, user.id, attempt));
  driveIfNeeded(svc, r);
  return json(liveAnswer(svc, r));
});
