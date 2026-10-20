// Ratings table access + Glicko-lite application (ranked only).
import { decayRd, type Rating, updateRating } from "./glicko.ts";
import { type Db, must } from "./db.ts";
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

/** Applies one game (score 1 / 0.5 / 0) and writes the row. Returns the new rating. */
export async function applyGame(
  db: Db,
  userId: string,
  season: number,
  current: StoredRating,
  opponent: Rating,
  score: number,
  now = new Date(),
): Promise<Rating> {
  const next = updateRating(current, opponent, score);
  must(
    await db.from("ratings").upsert({
      user_id: userId,
      season,
      mmr: next.mmr,
      rd: next.rd,
      matches: current.matches + 1,
      wins: current.wins + (score === 1 ? 1 : 0),
      losses: current.losses + (score === 0 ? 1 : 0),
      draws: current.draws + (score === 0.5 ? 1 : 0),
      last_played_at: now.toISOString(),
    }, { onConflict: "user_id,season" }),
    "save rating",
  );
  return next;
}

/** House ghosts have a fixed, confident rating. */
export function houseRating(mmr: number): Rating {
  return { mmr, rd: 100 };
}
