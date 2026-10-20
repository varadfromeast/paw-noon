// Weekly ranked seasons (SPEC_V2 §4.4, §7): one season per ISO-8601 week (Monday 00:00 UTC to Sunday),
// stored as the integer YYYYWW in ratings.season / matches.season and named `ranked_YYYY_WW` on the
// leaderboard. Pure (no I/O, no imports) so the client mock and the tests can use the same rules.

/** ISO week-numbering year and week (1..53) of a UTC instant. */
export function isoWeek(d: Date): { year: number; week: number } {
  // Thursday of this week decides the year (ISO 8601)
  const day = Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate());
  const dow = (new Date(day).getUTCDay() + 6) % 7; // Monday = 0
  const thursday = new Date(day + (3 - dow) * 86_400_000);
  const year = thursday.getUTCFullYear();
  const jan1 = Date.UTC(year, 0, 1);
  const week = 1 + Math.floor((thursday.getTime() - jan1) / (7 * 86_400_000));
  return { year, week };
}

/** 2026-10-20 (ISO 2026-W43) -> 202643. */
export function weeklySeasonId(d: Date = new Date()): number {
  const { year, week } = isoWeek(d);
  return year * 100 + week;
}

/** A weekly id has the YYYYWW shape (anything else is a pinned PN_SEASON number). */
export function isWeeklySeason(season: number): boolean {
  const week = season % 100;
  return Number.isInteger(season) && season >= 200001 && season <= 999953 && week >= 1 && week <= 53;
}

/** 202643 -> "ranked_2026_43"; a pinned season N -> "ranked_season_N". */
export function rankedBoardName(season: number): string {
  if (!isWeeklySeason(season)) return `ranked_season_${season}`;
  const w = season % 100;
  return `ranked_${Math.floor(season / 100)}_${w < 10 ? "0" + w : String(w)}`;
}

/** "ranked_2026_43" -> 202643, "ranked_season_7" -> 7, anything else null. */
export function parseRankedBoard(name: string): number | null {
  let m = /^ranked_(\d{4})_(\d{2})$/.exec(name);
  if (m) {
    const id = Number(m[1]) * 100 + Number(m[2]);
    return isWeeklySeason(id) ? id : null;
  }
  m = /^ranked_season_(\d{1,6})$/.exec(name);
  if (m) {
    const n = Number(m[1]);
    return n > 0 ? n : null;
  }
  return null;
}

/** Monday 00:00 UTC that starts the week after `d` (when the weekly board rolls over). */
export function nextWeekStart(d: Date): Date {
  const day = Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate());
  const dow = (new Date(day).getUTCDay() + 6) % 7;
  return new Date(day + (7 - dow) * 86_400_000);
}
