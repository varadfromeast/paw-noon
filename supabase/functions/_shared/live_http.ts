// Live duels: what the five live-* Edge Functions share (service wiring, error mapping, the answer shape, the
// background round driver). The functions themselves stay thin, like match-start / room-join.
import type { Db } from "./db.ts";
import { hardConfig } from "./duel/load_config.ts";
import { HttpError } from "./http.ts";
import { liveView, type LiveVerdict } from "./live.ts";
import { RealtimeRestBus } from "./live_bus.ts";
import { type ApplyResult, LiveError, LiveService, startedRound, topicOf, verdictOk } from "./live_service.ts";
import { SupabaseLiveStore } from "./live_store.ts";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function liveService(db: Db): LiveService {
  const url = Deno.env.get("SUPABASE_URL");
  const key = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  if (!url || !key) throw new Error("SUPABASE_URL / SUPABASE_SERVICE_ROLE_KEY missing");
  const cfg = hardConfig();
  return new LiveService(cfg, new SupabaseLiveStore(db, cfg), new RealtimeRestBus(url, key));
}

export function matchIdOf(body: Record<string, unknown>): string {
  const id = body.matchId;
  if (typeof id !== "string" || !UUID.test(id)) throw new HttpError(400, "bad_request", "matchId must be a uuid");
  return id;
}

/** LiveService errors -> HTTP (the same error body as every function). */
export async function liveCall(fn: () => Promise<ApplyResult>): Promise<ApplyResult> {
  try {
    return await fn();
  } catch (e) {
    if (e instanceof LiveError) {
      if (e.code === "not_found") throw new HttpError(404, "match_not_found", e.message);
      if (e.code === "not_member") throw new HttpError(404, "match_not_found", "no such live match for this player");
      throw new HttpError(503, "busy", e.message);
    }
    throw e;
  }
}

/** Every live-* answer: the caller's view of the match + the server time (the phone's ClockSync sample: it
 *  stamps t0 before the request and t3 after the answer, t1 = t2 = serverNowMs). */
export function liveAnswer(svc: LiveService, r: ApplyResult, verdict: LiveVerdict = r.step.verdict) {
  const now = svc.clock.nowMs();
  return {
    matchId: r.row.id,
    channel: topicOf(r.row.id),
    verdict,
    ok: verdictOk(verdict),
    view: liveView(r.row.state, r.slot, now),
    serverNowMs: now,
  };
}

/**
 * Keeps the round driver running after the answer was sent (Supabase Edge Runtime background task). The driver
 * sleeps through countdown + hold (at most ~7 s) and the report window (~3 s): well inside the free plan's 150 s
 * wall clock, and sleeping costs no CPU time. Without EdgeRuntime (tests) the promise just runs.
 */
export function runInBackground(p: Promise<unknown>): void {
  const guarded = p.catch((e) => console.error("[live] driver failed:", e));
  const rt = (globalThis as { EdgeRuntime?: { waitUntil(p: Promise<unknown>): void } }).EdgeRuntime;
  if (rt) rt.waitUntil(guarded);
}

/** Starts the driver when this action started a round (live-ready) or sent a late cue (live-round). */
export function driveIfNeeded(svc: LiveService, r: ApplyResult): void {
  const started = startedRound(r.step);
  const cue = r.step.out.find((m) => m.event === "draw");
  const roundId = started || (cue ? cue.payload.roundId as number : 0);
  if (roundId) runInBackground(svc.drive(r.row.id, roundId));
}
