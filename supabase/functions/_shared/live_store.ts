// Live duels: LiveStore on Postgres (pn_live_save / pn_live_finish, migrations/0070_live.sql). Only the Edge
// Functions use it (service role). The finish path loads what planLiveFinish needs (both ratings, names, rolling
// plausibility rows) and writes the whole result in one transaction; a rating that changed meanwhile answers
// `conflict` and LiveService re-reads and retries, exactly like match-submit's pn_complete_match loop.
import type { DuelConfig } from "./duel/config.ts";
import { isGunId } from "./duel/loadouts.ts";
import type { LiveAttempt, LiveState } from "./live.ts";
import { planLiveFinish } from "./live_finish.ts";
import type { LiveRow, LiveStore } from "./live_service.ts";
import { must } from "./must.ts";
import type { AccountStats } from "./plausibility.ts";
import { loadRating } from "./ratings.ts";
import type { AccountStatsRow } from "./rows.ts";
import { quarantineMatches } from "./server_config.ts";
import type { Db } from "./db.ts";

interface LiveDbRow {
  id: string;
  state: LiveState;
  version: number;
  season: number | null;
}

function statsFromRow(row: AccountStatsRow | null): AccountStats | null {
  if (!row) return null;
  return { shots: Array.isArray(row.shots) ? row.shots : [], wonShots: row.won_shots, narrowWins: row.narrow_wins, matches: row.matches };
}

export class SupabaseLiveStore implements LiveStore {
  private readonly db: Db;
  private readonly cfg: DuelConfig;
  private readonly seasons = new Map<string, number | null>();

  constructor(db: Db, cfg: DuelConfig) {
    this.db = db;
    this.cfg = cfg;
  }

  async load(id: string): Promise<LiveRow | null> {
    const row = must<LiveDbRow | null>(
      await this.db.from("live_matches").select("id, state, version, season").eq("id", id).maybeSingle(),
      "load live match",
    );
    if (!row) return null;
    this.seasons.set(id, row.season);
    return { id: row.id, state: row.state, version: row.version };
  }

  async save(id: string, expectVersion: number, state: LiveState, resolved: LiveAttempt[]): Promise<boolean> {
    const res = must<{ status: string } | null>(
      await this.db.rpc("pn_live_save", { p_id: id, p_expect: expectVersion, p_state: state, p_attempts: resolved }),
      "save live match",
    );
    return res?.status === "ok";
  }

  async finish(id: string, expectVersion: number, state: LiveState, resolved: LiveAttempt[]): Promise<boolean> {
    const users = [state.players[0].userId!, state.players[1].userId!];
    const season = state.mode === "ranked" ? (this.seasons.get(id) ?? null) : null;
    const profiles = must<{ id: string; display_name: string; character: string }[] | null>(
      await this.db.from("profiles").select("id, display_name, character").in("id", users),
      "load live profiles",
    ) ?? [];
    const stats = must<(AccountStatsRow & { user_id: string })[] | null>(
      await this.db.from("account_stats").select("user_id, shots, won_shots, narrow_wins, matches").in("user_id", users),
      "load live stats",
    ) ?? [];
    const now = new Date();
    const players = await Promise.all(users.map(async (u) => {
      const p = profiles.find((x) => x.id === u);
      return {
        userId: u,
        displayName: p?.display_name ?? "Stranger",
        character: p?.character ?? "whiskers",
        rating: season !== null ? await loadRating(this.db, u, season, now) : null,
        stats: statsFromRow(stats.find((x) => x.user_id === u) ?? null),
      };
    }));
    const plan = planLiveFinish(this.cfg, state, {
      players: [players[0], players[1]],
      season,
      now,
      quarantineLeft: quarantineMatches(),
    });
    const res = must<{ status: string; who?: string } | null>(
      await this.db.rpc("pn_live_finish", { p_id: id, p_expect: expectVersion, p_state: state, p_attempts: resolved, p_finish: plan }),
      "finish live match",
    );
    if (res?.status === "completed") return true;
    if (res?.status === "conflict") console.warn(`[live] finish conflict (${res.who}) match=${id}, retrying`);
    return false;
  }
}

/** The gun a player brings into a live match: the profile's equipped gun when it is owned, else standard. */
export function verifiedGun(equipped: unknown, owned: string[] | null): "standard" | "peacemaker" {
  if (!isGunId(equipped) || equipped === "standard") return "standard";
  return (owned ?? []).includes(equipped) ? equipped : "standard";
}
