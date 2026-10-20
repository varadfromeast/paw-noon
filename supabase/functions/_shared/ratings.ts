// Ratings table access + Glicko-lite application (ranked only).
import { decayRd, type Rating, updateRating } from "./glicko.ts";
import type { Db } from "./db.ts";
import { HttpError } from "./http.ts";
import { must } from "./must.ts";
import type { RatingRow } from "./rows.ts";
import { RATING } from "./server_config.ts";

export interface StoredRating extends Rating {
  exists: boolean;
  matches: number;
  wins: number;
  losses: number;
  draws: number;
}

const DAY_MS = 86_400_000;

/** Current rating for a season, RD decayed by idle days (default 1200 / 350 when unrated). */
export async function loadRating(db: Db, userId: string, season: number, now = new Date()): Promise<StoredRating> {
  const row = must<RatingRow | null>(
    await db.from("ratings").select("mmr, rd, matches, wins, losses, draws, last_played_at")
      .eq("user_id", userId).eq("season", season).maybeSingle(),
    "load rating",
  );
  if (!row) {
    return { exists: false, mmr: RATING.initialMmr, rd: RATING.initialRd, matches: 0, wins: 0, losses: 0, draws: 0 };
  }
  const idle = row.last_played_at ? (now.getTime() - Date.parse(row.last_played_at)) / DAY_MS : 0;
  return {
    exists: true,
    mmr: row.mmr,
    rd: decayRd(row.rd, idle),
    matches: row.matches,
    wins: row.wins,
    losses: row.losses,
    draws: row.draws,
  };
}

/** Concurrent writers to one rating row (e.g. two players finishing against the same ghost at once). */
const RATING_WRITE_ATTEMPTS = 4;

/**
 * Applies one game (score 1 / 0.5 / 0) and writes the row. Returns the new rating.
 *
 * Optimistic concurrency (review 2026-10-20): the row's `matches` counter is the version. The update only
 * lands when `matches` still equals what `current` was read with (an insert only when the row still does
 * not exist); otherwise somebody else rated this player in between, so the row is re-read and the game is
 * applied on top of the fresh value. The old read-then-upsert silently dropped one of two concurrent
 * updates (a lost MMR change for a popular ghost owner).
 */
export async function applyGame(
  db: Db,
  userId: string,
  season: number,
  current: StoredRating,
  opponent: Rating,
  score: number,
  now = new Date(),
): Promise<Rating> {
  let cur = current;
  for (let attempt = 0; attempt < RATING_WRITE_ATTEMPTS; attempt++) {
    const next = updateRating(cur, opponent, score);
    const row = {
      mmr: next.mmr,
      rd: next.rd,
      matches: cur.matches + 1,
      wins: cur.wins + (score === 1 ? 1 : 0),
      losses: cur.losses + (score === 0 ? 1 : 0),
      draws: cur.draws + (score === 0.5 ? 1 : 0),
      last_played_at: now.toISOString(),
    };
    if (cur.exists) {
      const updated = must<{ user_id: string }[] | null>(
        await db.from("ratings").update(row)
          .eq("user_id", userId).eq("season", season).eq("matches", cur.matches)
          .select("user_id"),
        "save rating",
      );
      if (updated && updated.length > 0) return next;
    } else {
      const res = await db.from("ratings").insert({ user_id: userId, season, ...row });
      if (!res.error) return next;
      // 23505 unique_violation: the first rating row was created concurrently -> re-read and retry
      if ((res.error as { code?: string }).code !== "23505") must(res, "save rating");
    }
    cur = await loadRating(db, userId, season, now);
  }
  throw new HttpError(503, "busy", "rating is being updated concurrently, retry");
}

/** House ghosts have a fixed, confident rating. */
export function houseRating(mmr: number): Rating {
  return { mmr, rd: 100 };
}
