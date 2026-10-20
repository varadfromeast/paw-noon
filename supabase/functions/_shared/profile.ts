// Profile answer shared by profile-read and profile-update (M6, schemas/profile.response.schema.json).
// Stats are the server-validated PvP counters on `profiles` (matches_played, wins, best_reaction_ns, lightning_count),
// which match-submit adds to after every completed match (pn_record_match_stats). Solo duels against the bot happen
// offline and are not in here (the client shows its own all-time best next to the server's).
import type { AuthedUser } from "./auth.ts";
import type { Db } from "./db.ts";
import { must } from "./must.ts";
import { loadRating } from "./ratings.ts";
import { rankedSeason } from "./server_config.ts";
import { NAME_MAX, NAME_MIN } from "./display_name.ts";

export const PROFILE_COLUMNS =
  "display_name, character, owned_items, coins, matches_played, wins, best_reaction_ns, lightning_count, daily_streak, linked_at";

export interface ProfileFullRow {
  display_name: string;
  character: string;
  owned_items: string[] | null;
  coins: number;
  matches_played: number;
  wins: number;
  best_reaction_ns: number | string | null;
  lightning_count: number;
  daily_streak: number;
  linked_at: string | null;
}

export interface RatingSummary {
  season: number;
  mmr: number;
  rd: number;
  matches: number;
}

export interface ProfileResponse {
  userId: string;
  displayName: string;
  character: string;
  isGuest: boolean;
  providers: string[];
  linkedAt: string | null;
  stats: { matches: number; wins: number; winRate: number; bestReactionNs: number | null; lightning: number };
  rating: RatingSummary;
  coins: number;
  dailyStreak: number;
  ownedItems: string[];
  nameRules: { min: number; max: number; chars: string };
}

/** Pure: row + caller + rating -> the JSON the client parses (ProfileInfo.Parse). */
export function profileResponse(user: AuthedUser, row: ProfileFullRow, rating: RatingSummary): ProfileResponse {
  const matches = Math.max(0, row.matches_played | 0);
  const wins = Math.min(matches, Math.max(0, row.wins | 0));
  const best = row.best_reaction_ns == null ? null : Number(row.best_reaction_ns);
  return {
    userId: user.id,
    displayName: row.display_name,
    character: row.character,
    isGuest: !(user.providers.includes("apple") || user.providers.includes("google")) || user.isAnonymous,
    providers: user.providers,
    linkedAt: row.linked_at,
    stats: {
      matches,
      wins,
      winRate: matches > 0 ? Math.round((1000 * wins) / matches) / 1000 : 0,
      bestReactionNs: best != null && Number.isFinite(best) && best > 0 ? best : null,
      lightning: Math.max(0, row.lightning_count | 0),
    },
    rating,
    coins: Math.max(0, row.coins | 0),
    dailyStreak: Math.max(0, row.daily_streak | 0),
    ownedItems: row.owned_items ?? [],
    nameRules: { min: NAME_MIN, max: NAME_MAX, chars: "A-Z 0-9 space" },
  };
}

export async function readProfile(db: Db, user: AuthedUser): Promise<ProfileResponse | null> {
  const row = must<ProfileFullRow | null>(
    await db.from("profiles").select(PROFILE_COLUMNS).eq("id", user.id).maybeSingle(),
    "read profile",
  );
  if (!row) return null;
  const season = rankedSeason();
  const r = await loadRating(db, user.id, season);
  return profileResponse(user, row, { season, mmr: Math.round(r.mmr), rd: Math.round(r.rd), matches: r.matches });
}
