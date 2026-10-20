// Row shapes of the tables in supabase/migrations/0001_init.sql, as read by the functions.
import type { OpponentSnapshot } from "./match_types.ts";

export interface ProfileRow {
  display_name: string;
  owned_items: string[] | null;
  flagged: boolean;
}

export interface RatingRow {
  mmr: number;
  rd: number;
  matches: number;
  wins: number;
  losses: number;
  draws: number;
  last_played_at: string | null;
}

export interface RecordingRow {
  id: string;
  owner_id: string | null;
  is_house: boolean;
  seed: number;
  mode: "casual" | "ranked";
  character: string;
  gun: string;
  display_name: string;
  mmr: number;
  attempts: unknown;
}

export interface MatchRow {
  id: string;
  player_id: string;
  mode: "casual" | "ranked";
  season: number | null;
  seed: number;
  rounds: number;
  max_attempts: number;
  status: string;
  deadline_at: string;
  opponent_snapshot: OpponentSnapshot;
  opponent_is_house: boolean;
}
