// POST /functions/v1/live-create  { kind: "friend" | "quick", mode?: "casual" | "ranked", clientBuild: string }
// Live duels (Docs/LIVE_DUELS.md). friend: opens a lobby with a 4-letter code for one friend (casual only).
// quick: joins the waiting quick lobby closest in MMR (same mode, same pool), or opens one and waits for a stranger
// (LIVE_RULES.quickLobbyMs; the phone falls back to a ghost duel when nobody comes).
// 200 { matchId, channel, code, kind, mode, verdict, ok, view, serverNowMs }   view.you = your slot (0 host, 1 guest)
// Errors: 400 bad_request, 403 link_required | ranked_not_allowed, 409 no_profile, 429 rate_limited, 503 busy.
import { requireUser } from "../_shared/auth.ts";
import { admin, must } from "../_shared/db.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import { createLive, joinLive, type LiveKind, LIVE_RULES } from "../_shared/live.ts";
import { liveAnswer, liveCall, liveService } from "../_shared/live_http.ts";
import { verifiedGun } from "../_shared/live_store.ts";
import { freshSeed } from "../_shared/match_issue.ts";
import { parseMode, rankedGate } from "../_shared/ranked.ts";
import { takeRate } from "../_shared/rate.ts";
import { loadRating } from "../_shared/ratings.ts";
import { candidateCodes } from "../_shared/rooms.ts";
import { rankedSeason, ROOM } from "../_shared/server_config.ts";

/** Quick pairing band: +-150 MMR, widening by 20 per second the lobby has waited (same start as the ghost pool). */
const QUICK_BAND = 150;
const QUICK_BAND_PER_SEC = 20;

interface LiveProfile {
  flagged: boolean;
  shadow: boolean;
  equipped_gun: string | null;
  owned_items: string[] | null;
}

serve("live-create", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req, 1024);
  const kind = body.kind as LiveKind;
  if (kind !== "friend" && kind !== "quick") throw new HttpError(400, "bad_request", 'kind must be "friend" or "quick"');
  const mode = parseMode(body.mode);
  if (!mode) throw new HttpError(400, "bad_request", 'mode must be "casual" or "ranked"');
  if (kind === "friend" && mode === "ranked") throw new HttpError(400, "bad_request", "friend duels are casual");
  const clientBuild = typeof body.clientBuild === "string" ? body.clientBuild.slice(0, 64) : "";
  if (!clientBuild) throw new HttpError(400, "bad_request", "clientBuild is required");
  await takeRate(db, user.id, "live_create");

  const profile = must<LiveProfile | null>(
    await db.from("profiles").select("flagged, shadow, equipped_gun, owned_items").eq("id", user.id).maybeSingle(),
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
  const gun = verifiedGun(profile.equipped_gun, profile.owned_items);
  const season = mode === "ranked" ? rankedSeason() : null;
  const mmr = Math.round((await loadRating(db, user.id, season ?? rankedSeason())).mmr);
  const shadow = profile.flagged || profile.shadow;
  const svc = liveService(db);

  if (kind === "quick") {
    const pair = must<{ status: string; id?: string } | null>(
      await db.rpc("pn_live_quick", { p_user: user.id, p_mode: mode, p_mmr: mmr, p_shadow: shadow, p_band: QUICK_BAND, p_band_per_sec: QUICK_BAND_PER_SEC }),
      "pair quick",
    );
    if (pair?.status === "joined" && pair.id) {
      const r = await liveCall(() => svc.apply(pair.id!, null, (s, _slot, now) => joinLive(svc.cfg, s, user.id, gun, now)));
      if (r.step.verdict === "Accepted" || r.step.verdict === "Duplicate") {
        return json({ kind, mode, code: null, ...liveAnswer(svc, { ...r, slot: 1 }) });
      }
      // the lobby closed between the pairing and the join: host one instead
    }
  }

  const now = svc.clock.nowMs();
  const state = createLive({ kind, mode, seed: freshSeed(), hostId: user.id, hostGun: gun, nowMs: now });
  const ttl = kind === "friend" ? LIVE_RULES.friendLobbyMs : LIVE_RULES.quickLobbyMs;
  const made = must<{ status: string; id?: string; code?: string | null } | null>(
    await db.rpc("pn_live_create", {
      p_host: user.id, p_kind: kind, p_mode: mode, p_codes: kind === "friend" ? candidateCodes(ROOM.candidates) : null,
      p_seed: state.seed, p_state: state, p_ttl_ms: ttl, p_mmr: mmr, p_shadow: shadow, p_season: season,
    }),
    "create live match",
  );
  if (made?.status !== "created" || !made.id) throw new HttpError(503, "busy", "no free code right now, try again");
  const answer = liveAnswer(svc, { step: { state, out: [], verdict: "Accepted", changed: true }, row: { id: made.id, state, version: 0 }, slot: 0 });
  return json({ kind, mode, code: made.code ?? null, ...answer });
});
