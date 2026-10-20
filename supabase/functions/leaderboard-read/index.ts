// POST /functions/v1/leaderboard-read  { board?: "ranked" | "ranked_YYYY_WW" | "best_reaction_hard" }
// SPEC_V2 §7. `ranked` (default) = this week's ranked season by MMR (at least LEADERBOARD.minRankedMatches rated
// matches that week); `best_reaction_hard` = fastest server-validated shot, all time. Top 50 + the caller's row.
import { requireUser } from "../_shared/auth.ts";
import { admin, must } from "../_shared/db.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import type { LeaderboardResponse } from "../_shared/match_types.ts";
import { takeRate } from "../_shared/rate.ts";
import { isWeeklySeason, nextWeekStart, parseRankedBoard, rankedBoardName } from "../_shared/season.ts";
import { LEADERBOARD, rankedSeason } from "../_shared/server_config.ts";

serve("leaderboard-read", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req, 1024);
  await takeRate(db, user.id, "leaderboard_read");
  const name = body.board === undefined || body.board === null || body.board === "" ? "ranked" : body.board;
  if (typeof name !== "string") throw new HttpError(400, "bad_request", "board must be a string");

  let kind: LeaderboardResponse["kind"];
  let season: number | null = null;
  if (name === "best_reaction_hard") kind = "best_reaction_hard";
  else if (name === "ranked") {
    kind = "ranked";
    season = rankedSeason();
  } else {
    const s = parseRankedBoard(name);
    if (s === null) throw new HttpError(400, "bad_request", 'board must be "ranked", "ranked_YYYY_WW" or "best_reaction_hard"');
    kind = "ranked";
    season = s;
  }

  const minMatches = kind === "ranked" ? LEADERBOARD.minRankedMatches : 0;
  const data = must<{ total: number; top: LeaderboardResponse["top"]; you: LeaderboardResponse["you"] } | null>(
    await db.rpc("pn_leaderboard", { p_board: kind, p_season: season, p_user: user.id, p_limit: LEADERBOARD.top, p_min_matches: minMatches }),
    "read leaderboard",
  );

  const res: LeaderboardResponse = {
    board: kind === "ranked" ? rankedBoardName(season!) : "best_reaction_hard",
    kind,
    unit: kind === "ranked" ? "mmr" : "ns",
    season,
    closesAt: kind === "ranked" && isWeeklySeason(season!) && season === rankedSeason() ? nextWeekStart(new Date()).toISOString() : null,
    minMatches,
    total: data?.total ?? 0,
    top: data?.top ?? [],
    you: data?.you ?? null,
  };
  return json(res);
});
