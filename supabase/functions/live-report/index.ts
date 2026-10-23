// POST /functions/v1/live-report  { matchId, report: { roundId, kind: "Shot" | "Early", reactionNs, cuePresentedNs,
//                                   inputNs, countdownStartNs, rttMs? } }
// The phone's ShotReport for the round in play (all *Ns on its own monotonic clock; the reaction is measured from its
// own cue presentation, D2). The slot is the caller's, never taken from the body. Nothing is decided on arrival:
// the attempt resolves when both have reported (or at the report deadline), ordered by the reactions carried.
// 200 { matchId, channel, verdict, ok, view, serverNowMs }
//   verdict: Accepted | Duplicate (a retry of an accepted report) | Stale | WrongRound | Late | Implausible |
//   Inconsistent | NotPlaying. A refused report changes nothing (the slot times out as a no-shot); Implausible and
//   Inconsistent are logged and count towards the account flag (§4.3, three = flagged).
// Errors: 400 bad_request | bad_report, 404 match_not_found, 429 rate_limited, 503 busy.
import { requireUser } from "../_shared/auth.ts";
import { admin, must } from "../_shared/db.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import { parseLiveReport } from "../_shared/live.ts";
import { liveAnswer, liveCall, liveService, matchIdOf } from "../_shared/live_http.ts";
import { takeRate } from "../_shared/rate.ts";
import { MATCH } from "../_shared/server_config.ts";

serve("live-report", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req, 4096);
  const matchId = matchIdOf(body);
  const report = parseLiveReport(body.report);
  if (!report) throw new HttpError(400, "bad_report", "report must be { roundId, kind, reactionNs, cuePresentedNs, inputNs, countdownStartNs, rttMs? }");
  await takeRate(db, user.id, "live_report");
  const svc = liveService(db);
  const r = await liveCall(() => svc.report(matchId, user.id, report));
  const v = r.step.verdict;
  if (v === "Implausible" || v === "Inconsistent") {
    console.warn(`[live-report] refused match=${matchId} user=${user.id} verdict=${v} report=${JSON.stringify(report)}`);
    const { error } = await db.from("plausibility_log").insert({
      user_id: user.id, match_id: null, check_name: "live_report", action: "reject",
      detail: { liveMatchId: matchId, verdict: v, report },
    });
    if (error) console.error("[live-report] log failed:", error.message);
    must(await db.rpc("pn_register_rejection", { p_user: user.id, p_flag_at: MATCH.rejectionsToFlag }), "register rejection");
  }
  return json(liveAnswer(svc, r));
});
