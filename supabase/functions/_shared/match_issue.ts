// Issuing a recorded-duel match: shared by match-start (pool opponent) and room-join (the friend's reserved
// recording), so both go through exactly the same path: throttle, stale cleanup, abandon penalty, seed choice,
// deadline, opponent snapshot, the matches row and the response (supabase/README.md "HTTP contract").
import { type Db, must } from "./db.ts";
import { hardConfig } from "./duel/load_config.ts";
import { isGunId } from "./duel/loadouts.ts";
import { HttpError } from "./http.ts";
import { buildResponse } from "./match_response.ts";
import type { MatchStartResponse, Mode, OpponentSnapshot } from "./match_types.ts";
import { applyGame, houseRating, loadRating } from "./ratings.ts";
import type { PlayableAttempt } from "./recording.ts";
import type { MatchRow, RecordingRow } from "./rows.ts";
import { MATCH, matchDurationSec, rankedSeason } from "./server_config.ts";

export { buildResponse };

export function freshSeed(): number {
  return crypto.getRandomValues(new Uint32Array(1))[0] & 0x7fffffff;
}

/**
 * Open matches the player walked away from are expired. In ranked an abandoned match counts as a
 * loss, otherwise starting over would let anyone skip opponents they do not like.
 */
export async function expireOpenMatches(db: Db, userId: string): Promise<void> {
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
export async function enforceStartRate(db: Db, userId: string): Promise<void> {
  const since = new Date(Date.now() - 60_000).toISOString();
  const { count, error } = await db.from("matches").select("id", { count: "exact", head: true })
    .eq("player_id", userId).gte("issued_at", since);
  if (error) must({ data: null, error }, "count recent starts");
  if ((count ?? 0) >= MATCH.maxStartsPerMinute) {
    throw new HttpError(429, "rate_limited", "too many matches started, wait a minute", { retryAfterSec: 60 });
  }
}

/** Matches stuck in `submitting` (the submit function died mid-way) are voided, never left hanging. */
export async function voidStaleSubmissions(db: Db, userId: string): Promise<void> {
  const before = new Date(Date.now() - MATCH.staleSubmitMinutes * 60_000).toISOString();
  must(
    await db.from("matches").update({ status: "void", void_reason: "stale_submit" })
      .eq("player_id", userId).eq("status", "submitting").lt("submitted_at", before),
    "void stale submissions",
  );
}

/** The bookkeeping every issue starts with (match-start and room-join alike). */
export async function beforeIssue(db: Db, userId: string): Promise<void> {
  await enforceStartRate(db, userId);
  await voidStaleSubmissions(db, userId);
  await expireOpenMatches(db, userId);
}

export interface IssueOptions {
  /** Friend duel: the room this match was issued from (matches.room_id) and its code (echoed back). */
  room?: { id: string; code: string };
}

/** Inserts the match against `picked` and builds the match-start response. */
export async function issueMatch(db: Db, userId: string, mode: Mode, picked: RecordingRow, opts: IssueOptions = {}): Promise<MatchStartResponse> {
  const season = rankedSeason();
  const you = await loadRating(db, userId, season);
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
      player_id: userId,
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
      room_id: opts.room?.id ?? null,
    }).select("id").single(),
    "insert match",
  );

  return buildResponse(match.id, mode, mode === "ranked" ? season : null, seed, issuedAt.toISOString(), deadlineAt.toISOString(),
    { mmr: Math.round(you.mmr), rd: Math.round(you.rd) }, snapshot, opts.room?.code);
}

/** The match-start response for a match row that is still `issued` (room-join retry after a lost answer). */
export async function responseFromMatch(db: Db, userId: string, m: MatchRow & { issued_at: string; room_id?: string | null }, roomCode?: string): Promise<MatchStartResponse> {
  const season = m.season ?? rankedSeason();
  const you = await loadRating(db, userId, season);
  return buildResponse(m.id, m.mode, m.season, m.seed, m.issued_at, m.deadline_at,
    { mmr: Math.round(you.mmr), rd: Math.round(you.rd) }, m.opponent_snapshot, roomCode);
}
