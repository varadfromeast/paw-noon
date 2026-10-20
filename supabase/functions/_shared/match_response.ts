// The match-start response (schemas/match-start.response.schema.json). Pure, so it is unit-tested without
// supabase-js; match_issue.ts uses it for match-start and room-join.
import type { MatchStartResponse, Mode, OpponentSnapshot } from "./match_types.ts";
import { normalizeCharacter } from "./recording.ts";
import { MATCH } from "./server_config.ts";

export function buildResponse(
  matchId: string, mode: Mode, season: number | null, seed: number, issuedAt: string, deadlineAt: string,
  you: { mmr: number; rd: number }, snap: OpponentSnapshot, roomCode?: string,
): MatchStartResponse {
  const res: MatchStartResponse = {
    matchId,
    mode,
    season,
    difficulty: "hard",
    seed,
    rounds: MATCH.rounds,
    maxAttempts: MATCH.maxAttempts,
    gunsNormalised: mode === "ranked",
    issuedAt,
    deadlineAt,
    you,
    opponent: {
      recordingId: snap.recordingId,
      displayName: snap.displayName,
      character: normalizeCharacter(snap.character) ?? snap.character,   // snapshots from before 0061 may say "blackpaw"
      loadout: { gun: snap.gun },
      mmr: snap.mmr,
      isHouse: snap.isHouse,
      rounds: snap.attempts,
    },
  };
  if (roomCode) res.room = { code: roomCode };
  return res;
}
