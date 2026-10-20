// Shapes stored in matches.opponent_snapshot and returned by the match functions.
// JSON Schemas: _shared/schemas/match-start.response.schema.json, match-submit.response.schema.json.
import type { GunId } from "./duel/loadouts.ts";
import type { PlayableAttempt } from "./recording.ts";

export type Mode = "casual" | "ranked";

/** What match-start issued as the opponent; match-submit recomputes against exactly this. */
export interface OpponentSnapshot {
  recordingId: string;
  ownerId: string | null;
  isHouse: boolean;
  displayName: string;
  character: string;
  gun: GunId;
  mmr: number;
  attempts: PlayableAttempt[];
}

export interface MatchStartResponse {
  matchId: string;
  mode: Mode;
  season: number | null;
  difficulty: "hard";
  seed: number;
  rounds: number;
  maxAttempts: number;
  /** Ranked: both guns use the standard damage curve (cosmetic only, §6). */
  gunsNormalised: boolean;
  issuedAt: string;
  deadlineAt: string;
  you: { mmr: number; rd: number };
  opponent: {
    recordingId: string;
    displayName: string;
    character: string;
    loadout: { gun: GunId };
    mmr: number;
    isHouse: boolean;
    rounds: PlayableAttempt[];
  };
  /** Friend duel (room-join): the room this match came from. */
  room?: { code: string };
}

/** 200 from room-create. */
export interface RoomCreateResponse {
  code: string;
  status: "open";
  expiresAt: string;
  ttlSec: number;
}

/** 202 from room-join while the host has not recorded yet (poll again after pollAfterSec). */
export interface RoomWaitingResponse {
  status: "waiting";
  code: string;
  hostName: string;
  expiresAt: string;
  pollAfterSec: number;
}

export interface LeaderboardRow {
  rank: number;
  displayName: string;
  character: string;
  /** ranked: MMR (integer); best_reaction_hard: ns (integer). */
  value: number;
  matches: number;
  you: boolean;
}

/** 200 from leaderboard-read. */
export interface LeaderboardResponse {
  board: string;
  kind: "ranked" | "best_reaction_hard";
  unit: "mmr" | "ns";
  season: number | null;
  /** ranked: when this week's board closes (next Monday 00:00 UTC). */
  closesAt: string | null;
  minMatches: number;
  total: number;
  top: LeaderboardRow[];
  you: { rank: number | null; value: number; matches: number; eligible: boolean } | null;
}

export type Side = "you" | "opponent";

export interface SlotView {
  outcome: string;
  reactionNs: number | null;
  damage: number;
  grade: string;
}

export interface RoundView {
  attempt: number;
  roundNumber: number;
  holdNs: number;
  void: boolean;
  tie: boolean;
  winner: Side | null;
  you: SlotView;
  opponent: SlotView;
  hpAfter: { you: number; opponent: number };
}

export interface MatchSubmitResponse {
  matchId: string;
  status: "completed";
  mode: Mode;
  winner: Side | "draw";
  endReason: string;
  hp: { you: number; opponent: number };
  roundWins: { you: number; opponent: number };
  rounds: RoundView[];
  rating: { season: number; before: number; after: number; delta: number; rd: number } | null;
  /** Your recording, now in the pool as somebody's next opponent (null if not pooled). */
  recordingId: string | null;
  /** Friend duel: this recording was reserved for the friend of your open room (room-create). */
  room?: { code: string; status: "recorded" };
}
