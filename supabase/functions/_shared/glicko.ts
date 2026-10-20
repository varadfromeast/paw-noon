// Glicko-lite (SPEC_V2 §4.4): Glicko-1 single-game update with RD decay by idle days.
// No volatility (that is Glicko-2); one game per update, which is how ghost duels arrive.
import { RATING } from "./server_config.ts";

export interface Rating {
  mmr: number;
  rd: number;
}

const Q = Math.LN10 / 400;

function g(rd: number): number {
  return 1 / Math.sqrt(1 + (3 * Q * Q * rd * rd) / (Math.PI * Math.PI));
}

/** Expected score of `a` against `b`. */
export function expectedScore(a: Rating, b: Rating): number {
  return 1 / (1 + Math.pow(10, (-g(b.rd) * (a.mmr - b.mmr)) / 400));
}

/** RD grows back towards maxRd while a player is idle. */
export function decayRd(rd: number, idleDays: number): number {
  const d = Math.max(0, idleDays);
  return Math.min(RATING.maxRd, Math.sqrt(rd * rd + RATING.rdPerDay * RATING.rdPerDay * d));
}

/** New rating of `player` after one game against `opp`. score: 1 win, 0.5 draw, 0 loss. */
export function updateRating(player: Rating, opp: Rating, score: number): Rating {
  const gOpp = g(opp.rd);
  const e = expectedScore(player, opp);
  const d2inv = Q * Q * gOpp * gOpp * e * (1 - e); // 1 / d^2
  const denom = 1 / (player.rd * player.rd) + d2inv;
  const mmr = player.mmr + (Q / denom) * gOpp * (score - e);
  const rd = Math.min(RATING.maxRd, Math.max(RATING.minRd, Math.sqrt(1 / denom)));
  return { mmr, rd };
}
