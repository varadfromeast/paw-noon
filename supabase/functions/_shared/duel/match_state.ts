// Port of PawNoon.Core.MatchState.

export const SLOTS = 2;

export class MatchState {
  hp: [number, number] = [0, 0];
  roundWins: [number, number] = [0, 0];
  maxHp = 0;
  /** 1-based number of the round being played (void rounds replay the same number). */
  roundNumber = 1;
  over = false;
  /** Winning slot, or -1 when not over or when the match is a draw (both fell in one tie). */
  winner = -1;
  private readonly reactions: [number[], number[]] = [[], []];

  constructor(maxHp: number) {
    this.reset(maxHp);
  }

  get isDraw(): boolean {
    return this.over && this.winner < 0;
  }

  finish(winner: number): void {
    this.over = true;
    this.winner = winner >= 0 && winner < SLOTS ? winner : -1;
  }

  reset(maxHp: number): void {
    this.maxHp = maxHp;
    this.hp = [maxHp, maxHp];
    this.roundWins = [0, 0];
    this.reactions[0].length = 0;
    this.reactions[1].length = 0;
    this.roundNumber = 1;
    this.over = false;
    this.winner = -1;
  }

  recordReaction(slot: number, reactionNs: number): void {
    if (slot < 0 || slot >= SLOTS) return;
    this.reactions[slot].push(reactionNs);
  }

  reactionCount(slot: number): number {
    return this.reactions[slot].length;
  }

  /** Best (lowest) valid reaction this match, or -1. */
  bestNs(slot: number): number {
    let best = -1;
    for (const r of this.reactions[slot]) if (best < 0 || r < best) best = r;
    return best;
  }

  /** Average valid reaction this match (C# long division, truncated), or -1. */
  averageNs(slot: number): number {
    const l = this.reactions[slot];
    if (l.length === 0) return -1;
    let sum = 0;
    for (const r of l) sum += r;
    return Math.trunc(sum / l.length);
  }

  hpFraction(slot: number): number {
    return this.maxHp <= 0 ? 0 : this.hp[slot] / this.maxHp;
  }
}
