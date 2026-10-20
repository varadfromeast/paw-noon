// POST /functions/v1/match-start  { mode: "casual" | "ranked", clientBuild: string }
// Issues a recorded-duel match: picks an opponent recording (§4.4), returns its playable attempts
// and the seed both players use (§4.0). Contract: supabase/README.md.
import { hasLinkedIdentity, requireUser } from "../_shared/auth.ts";
import { admin, type Db, must } from "../_shared/db.ts";
import { hardConfig } from "../_shared/duel/load_config.ts";
import { isGunId } from "../_shared/duel/loadouts.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import type { MatchStartResponse, Mode, OpponentSnapshot } from "../_shared/match_types.ts";
import { applyGame, houseRating, loadRating } from "../_shared/ratings.ts";
import type { PlayableAttempt } from "../_shared/recording.ts";
import type { MatchRow, ProfileRow, RecordingRow } from "../_shared/rows.ts";
import {
  MATCH,
  matchDurationSec,
  rankedAllowGuests,
  rankedBuildAllowed,
  rankedSeason,
} from "../_shared/server_config.ts";

function freshSeed(): number {
  return crypto.getRandomValues(new Uint32Array(1))[0] & 0x7fffffff;
}

/**
 * Open matches the player walked away from are expired. In ranked an abandoned match counts as a
 * loss, otherwise starting over would let anyone skip opponents they do not like.
 */
async function expireOpenMatches(db: Db, userId: string): Promise<void> {
  const open = must<Pick<MatchRow, "id" | "mode" | "season" | "opponent_snapshot" | "opponent_is_house">[] | null>(
    await db.from("matches").update({ status: "expired", void_reason: "abandoned" })
      .eq("player_id", userId).eq("status", "issued")
      .select("id, mode, season, opponent_snapshot, opponent_is_house"),
    "expire open matches",
  ) ?? [];
  for (const m of open) {
    if (m.mode !== "ranked" || m.season == null) continue;
    const snap = m.opponent_snapshot;
    const opp = snap.isHouse || !snap.ownerId
      ? houseRating(snap.mmr)
      : await loadRating(db, snap.ownerId, m.season);
    const mine = await loadRating(db, userId, m.season);
    await applyGame(db, userId, m.season, mine, opp, 0);
  }
}

/** Throttle (review 2026-10-20): every start burns a pool `use` and reveals a ghost's reactions. */
async function enforceStartRate(db: Db, userId: string): Promise<void> {
  const since = new Date(Date.now() - 60_000).toISOString();
  const { count, error } = await db.from("matches").select("id", { count: "exact", head: true })
    .eq("player_id", userId).gte("issued_at", since);
  if (error) must({ data: null, error }, "count recent starts");
  if ((count ?? 0) >= MATCH.maxStartsPerMinute) {
    throw new HttpError(429, "rate_limited", "too many matches started, wait a minute", { retryAfterSec: 60 });
  }
}

/** Matches stuck in `submitting` (the submit function died mid-way) are voided, never left hanging. */
async function voidStaleSubmissions(db: Db, userId: string): Promise<void> {
  const before = new Date(Date.now() - MATCH.staleSubmitMinutes * 60_000).toISOString();
  must(
    await db.from("matches").update({ status: "void", void_reason: "stale_submit" })
      .eq("player_id", userId).eq("status", "submitting").lt("submitted_at", before),
    "void stale submissions",
  );
}

serve("match-start", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req);

  const mode = body.mode as Mode;
  if (mode !== "casual" && mode !== "ranked") throw new HttpError(400, "bad_request", 'mode must be "casual" or "ranked"');
  const clientBuild = typeof body.clientBuild === "string" ? body.clientBuild.slice(0, 64) : "";
  if (!clientBuild) throw new HttpError(400, "bad_request", "clientBuild is required");

  const profile = must<Pick<ProfileRow, "flagged"> | null>(
    await db.from("profiles").select("flagged").eq("id", user.id).maybeSingle(),
    "load profile",
  );
  if (!profile) throw new HttpError(409, "no_profile", "profile missing; sign in again");

  if (mode === "ranked") {
    const reasons: string[] = [];
    if (!hasLinkedIdentity(user) && !rankedAllowGuests()) reasons.push("link_account");
    if (profile.flagged) reasons.push("account_flagged");
    if (!rankedBuildAllowed(clientBuild)) reasons.push("client_build");
    if (reasons.length) throw new HttpError(403, "ranked_not_allowed", "ranked is not available", { reasons });
  }

  await enforceStartRate(db, user.id);
  await voidStaleSubmissions(db, user.id);
  await expireOpenMatches(db, user.id);

  const season = rankedSeason();
  const you = await loadRating(db, user.id, season);

  const picked = must<RecordingRow | null>(
    await db.rpc("pn_pick_opponent", { p_user: user.id, p_mmr: Math.round(you.mmr), p_mode: mode }),
    "pick opponent",
  );
  if (!picked || !picked.id) throw new HttpError(503, "no_opponent", "no opponent available (house ghosts missing?)");

  const attempts = picked.attempts as PlayableAttempt[];
  const gun = isGunId(picked.gun) ? picked.gun : "standard";
  // House ghosts never foul early, so they are replayed on a fresh seed (see seed.sql).
  const seed: number = picked.is_house ? freshSeed() : picked.seed;
  const cfg = hardConfig();
  const issuedAt = new Date();
  const deadlineAt = new Date(issuedAt.getTime() + (matchDurationSec(cfg, seed) + MATCH.submitSlackSec) * 1000);

  const snapshot: OpponentSnapshot = {
    recordingId: picked.id,
    ownerId: picked.owner_id,
    isHouse: picked.is_house,
    displayName: picked.display_name,
    character: picked.character,
    gun,
    mmr: picked.mmr,
    attempts,
  };

  const match = must<{ id: string }>(
    await db.from("matches").insert({
      player_id: user.id,
      opponent_recording_id: picked.id,
      opponent_owner_id: picked.owner_id,
      opponent_is_house: picked.is_house,
      opponent_snapshot: snapshot,
      mode,
      season: mode === "ranked" ? season : null,
      seed,
      rounds: MATCH.rounds,
      max_attempts: MATCH.maxAttempts,
      issued_at: issuedAt.toISOString(),
      deadline_at: deadlineAt.toISOString(),
    }).select("id").single(),
    "insert match",
  );

  const res: MatchStartResponse = {
    matchId: match.id,
    mode,
    season: mode === "ranked" ? season : null,
    difficulty: "hard",
    seed,
    rounds: MATCH.rounds,
    maxAttempts: MATCH.maxAttempts,
    gunsNormalised: mode === "ranked",
    issuedAt: issuedAt.toISOString(),
    deadlineAt: deadlineAt.toISOString(),
    you: { mmr: Math.round(you.mmr), rd: Math.round(you.rd) },
    opponent: {
      recordingId: picked.id,
      displayName: picked.display_name,
      character: picked.character,
      loadout: { gun },
      mmr: picked.mmr,
      isHouse: picked.is_house,
      rounds: attempts,
    },
  };
  return json(res);
});
