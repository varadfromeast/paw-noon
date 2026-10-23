// Live duels: what a finished live match writes (pure). live_store.ts loads the inputs (both ratings, profiles,
// rolling plausibility rows) and hands the plan to pn_live_finish (migrations/0070_live.sql), which writes it in
// ONE transaction, like pn_complete_match does for a ghost duel (review D1).
//
// A live match is recorded exactly as if each player had played the other's ghost on the match seed:
//  - one `matches` row per player (status completed, live_match_id set, the other player as opponent snapshot), so
//    stats, the daily bounty, history and "never meet the same ghost twice" work unchanged;
//  - one pooled `recordings` row per player for a match played to the end (rounds / KO), so a live duel also
//    feeds the ghost pool (quarantine C10 and holds apply as in match-submit);
//  - ranked: both players rated once, from their pre-match ratings (Glicko-lite, optimistic versions).
import type { DuelConfig } from "./duel/config.ts";
import type { Rating } from "./glicko.ts";
import type { LiveAttempt, LiveState } from "./live.ts";
import { type AccountStats, type PlausibilityLog, rollStats } from "./plausibility.ts";
import { planGame, type StoredRating } from "./ratings.ts";
import type { PlayableAttempt } from "./recording.ts";
import { LIVE_PLAUS, MATCH } from "./server_config.ts";

const DAY_MS = 86_400_000;
const POOLED: readonly string[] = ["rounds", "ko", "double_ko"];

export interface LivePlayerCtx {
  userId: string;
  displayName: string;
  character: string;
  /** Ranked: the player's rating this season (loadRating). Ignored in casual. */
  rating: StoredRating | null;
  /** account_stats row (rolling window), null = none yet. */
  stats: AccountStats | null;
}

export interface LiveFinishCtx {
  players: [LivePlayerCtx, LivePlayerCtx];
  season: number | null;
  now: Date;
  quarantineLeft: number;
}

type Side = "you" | "opponent";

function side(slot: number, me: 0 | 1): Side | null {
  return slot < 0 ? null : slot === me ? "you" : "opponent";
}

/** The attempts a later ghost opponent replays (recording.ts PlayableAttempt). */
export function livePlayable(history: readonly LiveAttempt[], slot: 0 | 1): PlayableAttempt[] {
  return history.map((h) => {
    const inp = h.inputs[slot];
    const a: PlayableAttempt = { roundNumber: h.roundNumber, kind: inp.kind, reactionNs: inp.kind === "shot" || inp.kind === "anticipation" ? inp.reactionNs : 0 };
    const rep = h.reports[slot];
    if (inp.kind === "early" && rep) a.inputOffsetNs = Math.max(0, rep.inputNs - rep.countdownStartNs);
    return a;
  });
}

/** match-submit's RoundView from one player's side. */
export function perspectiveRounds(history: readonly LiveAttempt[], me: 0 | 1) {
  const o = (1 - me) as 0 | 1;
  return history.map((h) => ({
    attempt: h.attempt + 1,
    roundNumber: h.roundNumber,
    holdNs: h.holdNs,
    void: h.view.void,
    tie: h.view.tie,
    winner: side(h.view.winner, me),
    you: { outcome: h.view.outcomes[me], reactionNs: h.view.reactionsNs[me], damage: h.view.damage[me], grade: h.view.grades[me] },
    opponent: { outcome: h.view.outcomes[o], reactionNs: h.view.reactionsNs[o], damage: h.view.damage[o], grade: h.view.grades[o] },
    hpAfter: { you: h.view.hp[me], opponent: h.view.hp[o] },
  }));
}

function bestShot(history: readonly LiveAttempt[], slot: 0 | 1): number | null {
  let best: number | null = null;
  for (const h of history) {
    if (h.view.outcomes[slot] !== "Shot") continue;
    const r = h.view.reactionsNs[slot];
    if (r !== null && (best === null || r < best)) best = r;
  }
  return best;
}

/** Rounds of this match in which `slot`'s shot carried a soft reaction-vs-RTT flag. */
export function flaggedRounds(history: readonly LiveAttempt[], slot: 0 | 1): number {
  return history.filter((h) => h.flags[slot].length > 0).length;
}

/** Everything pn_live_finish writes for a finished (not abandoned) live match. */
export function planLiveFinish(cfg: DuelConfig, s: LiveState, ctx: LiveFinishCtx) {
  const res = s.result;
  if (!res || s.phase !== "finished" || res.endReason === "abandoned") throw new Error("planLiveFinish: match not finished");
  const played = POOLED.includes(res.endReason);
  const ranked = s.mode === "ranked" && ctx.season !== null;
  const nowIso = ctx.now.toISOString();
  const score0 = res.winner === 0 ? 1 : res.winner === 1 ? 0 : 0.5;

  // ---- ranked: one game for both, each computed from the other's PRE-match rating
  const r = [ctx.players[0].rating, ctx.players[1].rating];
  const plans = ranked && r[0] && r[1]
    ? [planGame(r[0], r[1] as Rating, score0), planGame(r[1], r[0] as Rating, 1 - score0)]
    : null;

  const players = ([0, 1] as const).map((me) => {
    const o = (1 - me) as 0 | 1;
    const p = ctx.players[me];
    const opp = ctx.players[o];
    const best = bestShot(s.history, me);
    const lightning = s.history.filter((h) => h.view.outcomes[me] === "Shot" && h.view.grades[me] === "Lightning").length;
    const winner = res.winner < 0 ? "draw" : res.winner === me ? "player" : "opponent";

    // ---- plausibility: rolling B5-B7 over live shots (A4 "margin vs ghost" does not apply: nobody knows the
    // other's reaction in advance) + the live reaction-vs-RTT flags. Only a match played to the end counts.
    let plaus: { clean: boolean; shadow: boolean; stats: Record<string, unknown> | null; log: PlausibilityLog[] } = { clean: false, shadow: false, stats: null, log: [] };
    let hold = false;
    if (played) {
      const shots: [number, number][] = s.history
        .filter((h) => !h.view.void && h.view.outcomes[me] === "Shot" && h.view.reactionsNs[me] !== null)
        .map((h) => [h.view.reactionsNs[me] as number, h.holdNs]);
      const flagged = flaggedRounds(s.history, me);
      const rttHold = flagged >= LIVE_PLAUS.holdFlaggedRounds;
      const rolling = rollStats(p.stats, { shots, wonShots: 0, narrowWins: 0, hold: rttHold });
      const log: PlausibilityLog[] = [];
      if (flagged > 0) {
        log.push({
          check: "live_rtt",
          action: rttHold ? "hold" : "log",
          detail: { flaggedRounds: flagged, flags: s.history.map((h) => h.flags[me]) },
        });
      }
      for (const t of rolling.trips) log.push({ check: t, action: "shadow", detail: { ...rolling.metrics } });
      const shadow = rolling.trips.length > 0;
      hold = rttHold || shadow;
      plaus = { clean: !hold, shadow, stats: { ...rolling.next, trips: rolling.trips }, log };
    }

    const plan = plans ? plans[me] : null;
    const mmrBefore = ranked && r[me] ? Math.round(r[me]!.mmr) : null;
    const mmrAfter = plan ? Math.round(plan.next.mmr) : null;
    const oppBefore = ranked && r[o] ? Math.round(r[o]!.mmr) : null;
    const oppAfter = plans ? Math.round(plans[o].next.mmr) : null;
    const myMmr = Math.round(mmrAfter ?? r[me]?.mmr ?? 1200);

    return {
      userId: p.userId,
      match: {
        winner,
        result: {
          live: true,
          endReason: res.endReason,
          forfeit: res.forfeitedBy < 0 ? null : res.forfeitedBy === me ? "you" : "opponent",
          hp: { you: res.hp[me], opponent: res.hp[o] },
          roundWins: { you: res.roundWins[me], opponent: res.roundWins[o] },
          rounds: perspectiveRounds(s.history, me),
          rating: plan && mmrBefore !== null && mmrAfter !== null
            ? { season: ctx.season, before: mmrBefore, after: mmrAfter, delta: mmrAfter - mmrBefore, rd: Math.round(plan.next.rd) }
            : null,
        },
        opponentSnapshot: {
          recordingId: null,
          ownerId: opp.userId,
          isHouse: false,
          live: true,
          displayName: opp.displayName,
          character: opp.character,
          gun: s.guns[o],
          mmr: Math.round(r[o]?.mmr ?? 1200),
          attempts: livePlayable(s.history, o),
        },
        mmrBefore,
        mmrAfter,
        opponentMmrBefore: oppBefore,
        opponentMmrAfter: oppAfter,
      },
      rating: plan ? { expectMatches: plan.expectMatches, ...plan.row } : null,
      recording: played
        ? {
          seed: s.seed,
          mode: s.mode,
          character: p.character,
          gun: s.guns[me],
          displayName: p.displayName,
          mmr: myMmr,
          attempts: livePlayable(s.history, me),
          audit: { live: true, reports: s.history.map((h) => h.reports[me]), flags: s.history.map((h) => h.flags[me]) },
          bestReactionNs: best,
          expiresAt: new Date(ctx.now.getTime() + MATCH.poolMaxAgeDays * DAY_MS).toISOString(),
          quarantineLeft: ctx.quarantineLeft,
          held: hold,
        }
        : null,
      stats: { won: res.winner === me, bestNs: hold ? null : best, lightning },
      plaus,
    };
  });

  return {
    status: "finished",
    endReason: res.endReason,
    winnerSlot: res.winner,
    season: ranked ? ctx.season : null,
    finishedAt: nowIso,
    players,
  };
}
