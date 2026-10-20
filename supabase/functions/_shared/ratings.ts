// Ratings table access + Glicko-lite application (ranked only).
import { decayRd, type Rating, updateRating } from "./glicko.ts";
import type { Db } from "./db.ts";
import { HttpError } from "./http.ts";
import { must } from "./must.ts";
import type { RatingRow } from "./rows.ts";
import { LEADERBOARD, RATING } from "./server_config.ts";

export interface StoredRating extends Rating {
  exists: boolean;
  matches: number;
  wins: number;
  losses: number;
  draws: number;
}

const DAY_MS = 86_400_000;

function idleDays(row: RatingRow, now: Date): number {
  return row.last_played_at ? (now.getTime() - Date.parse(row.last_played_at)) / DAY_MS : 0;
}

/**
 * M5 weekly seasons: the first game of a week starts from last season's rating pulled towards the initial
 * MMR (LEADERBOARD.seasonCarry) with RD at least LEADERBOARD.seasonStartRd, so matchmaking keeps working
 * on Monday and the new board still has to be earned.
 */
export function seasonStart(prev: { mmr: number; rd: number } | null, prevIdleDays = 0): { mmr: number; rd: number } {
  if (!prev) return { mmr: RATING.initialMmr, rd: RATING.initialRd };
  const mmr = RATING.initialMmr + (prev.mmr - RATING.initialMmr) * LEADERBOARD.seasonCarry;
  const rd = Math.min(RATING.maxRd, Math.max(LEADERBOARD.seasonStartRd, decayRd(prev.rd, prevIdleDays)));
  return { mmr, rd };
}

/** Current rating for a season, RD decayed by idle days. Unrated this season: carried over from the
 *  player's latest earlier season (seasonStart), else 1200 / 350. */
export async function loadRating(db: Db, userId: string, season: number, now = new Date()): Promise<StoredRating> {
  const row = must<RatingRow | null>(
    await db.from("ratings").select("mmr, rd, matches, wins, losses, draws, last_played_at")
      .eq("user_id", userId).eq("season", season).maybeSingle(),
    "load rating",
  );
  if (!row) {
    const prev = must<RatingRow | null>(
      await db.from("ratings").select("mmr, rd, matches, wins, losses, draws, last_played_at")
        .eq("user_id", userId).lt("season", season).order("season", { ascending: false }).limit(1).maybeSingle(),
      "load previous season",
    );
    const start = seasonStart(prev, prev ? idleDays(prev, now) : 0);
    return { exists: false, mmr: start.mmr, rd: start.rd, matches: 0, wins: 0, losses: 0, draws: 0 };
  }
  const idle = idleDays(row, now);
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

/** The ratings row after one game (score 1 / 0.5 / 0), computed from `cur`. Pure: pn_complete_match writes it
 *  only if the row's `matches` still equals `expectMatches` (-1 = no row yet). */
export function planGame(cur: StoredRating, opponent: Rating, score: number): {
  next: Rating;
  expectMatches: number;
  row: { mmr: number; rd: number; matches: number; wins: number; losses: number; draws: number };
} {
  const next = updateRating(cur, opponent, score);
  return {
    next,
    expectMatches: cur.exists ? cur.matches : -1,
    row: {
      mmr: next.mmr,
      rd: next.rd,
      matches: cur.matches + 1,
      wins: cur.wins + (score === 1 ? 1 : 0),
      losses: cur.losses + (score === 0 ? 1 : 0),
      draws: cur.draws + (score === 0.5 ? 1 : 0),
    },
  };
}

/** Concurrent writers to one rating row (e.g. two players finishing against the same ghost at once). */
export const RATING_WRITE_ATTEMPTS = 4;

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
    const plan = planGame(cur, opponent, score);
    const next = plan.next;
    const row = { ...plan.row, last_played_at: now.toISOString() };
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
