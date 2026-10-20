// Port of PawNoon.Core.LocalAuthority: the round state machine, report validation and the
// resolution rules (shot order by reported reaction, tie window, anticipation, no-shot, damage,
// kill-stops-the-slower-shot, HP, round winner, match over / draw).
//
// Differences from the C# class, none of which change a result:
//  - no transport: Tick() takes the reports delivered this tick as an argument, and the clock is a
//    plain `now` (ns) argument instead of an IDuelClock;
//  - the cue is "broadcast" by setting cueSent/cueDecidedNs (nobody to send it to);
//  - optional per-slot damage curves (casual loadouts, SPEC_V2 §6). Default: cfg's curve for both,
//    which is what the C# does and what ranked always uses.
import {
  anticipationNs,
  commitGraceNs,
  type DuelConfig,
  noShotTimeoutNs,
  nsToMs,
  reportSlackNs,
  tieWindowNs,
} from "./config.ts";
import { type DamageCurveDef, evaluateDamage, type Grade, gradeFromMs } from "./damage.ts";
import { MatchState, SLOTS } from "./match_state.ts";
import { NetRandom } from "./rng.ts";
import { createSchedule, type RoundSchedule } from "./schedule.ts";

export type RoundPhase = "Standoff" | "Countdown" | "Hold" | "Draw" | "Resolving" | "Result";
export type SlotOutcome = "Pending" | "Shot" | "Foul" | "Anticipation" | "NoShot" | "Stopped";
export type ReportVerdict =
  | "Accepted"
  | "BadSlot"
  | "WrongRound"
  | "Stale"
  | "Duplicate"
  | "Implausible"
  | "Inconsistent";
export type ReportKind = "Shot" | "Early";
export type DuelEventKind =
  | "RoundStarted"
  | "Cue"
  | "Foul"
  | "Shot"
  | "Stopped"
  | "NoShot"
  | "RoundVoid"
  | "RoundResolved"
  | "MatchOver";

/** Protocol.ShotReport (all times on the reporting client's own clock, ns). */
export interface ShotReport {
  roundId: number;
  slot: number;
  kind: ReportKind;
  reactionNs: number;
  cuePresentedNs: number;
  inputNs: number;
}

export interface DuelEvent {
  kind: DuelEventKind;
  roundId: number;
  slot: number;
  reactionNs: number;
  damage: number;
  targetHpAfter: number;
  killed: boolean;
  anticipation: boolean;
  simultaneous: boolean;
}

export class SlotRecord {
  outcome: SlotOutcome = "Pending";
  hasReport = false;
  report: ShotReport | null = null;
  /** Reaction in ns for accepted shots (also anticipation), else -1. */
  reactionNs = -1;
  damageDealt = 0;
  grade: Grade = "None";

  clear(): void {
    this.outcome = "Pending";
    this.hasReport = false;
    this.report = null;
    this.reactionNs = -1;
    this.damageDealt = 0;
    this.grade = "None";
  }

  get settled(): boolean {
    return this.outcome !== "Pending";
  }

  get isFoul(): boolean {
    return this.outcome === "Foul" || this.outcome === "Anticipation";
  }
}

export class RoundRecord {
  roundId = 0;
  roundNumber = 0;
  schedule: RoundSchedule | null = null;
  countdownStartNs = 0;
  cueDecidedNs = -1;
  cueSent = false;
  isVoid = false;
  isTie = false;
  winnerSlot = -1;
  readonly slots: [SlotRecord, SlotRecord] = [new SlotRecord(), new SlotRecord()];

  clear(): void {
    this.roundId = 0;
    this.roundNumber = 0;
    this.schedule = null;
    this.countdownStartNs = 0;
    this.cueDecidedNs = -1;
    this.cueSent = false;
    this.isVoid = false;
    this.isTie = false;
    this.winnerSlot = -1;
    this.slots[0].clear();
    this.slots[1].clear();
  }
}

const CONSISTENCY_TOLERANCE_NS = 1000; // 1 us, as in C#

export class LocalAuthority {
  readonly match: MatchState;
  readonly round = new RoundRecord();
  phase: RoundPhase = "Standoff";
  lastVerdict: ReportVerdict = "Accepted";
  rejectedReports = 0;
  readonly events: DuelEvent[] = [];

  private rng: NetRandom;
  private nextRoundId = 1;
  private forceCue = false;
  private readonly curves: [DamageCurveDef, DamageCurveDef];
  readonly cfg: DuelConfig;

  constructor(cfg: DuelConfig, seed: number, curves?: [DamageCurveDef, DamageCurveDef]) {
    this.cfg = cfg;
    this.rng = new NetRandom(seed);
    this.match = new MatchState(cfg.maxHp);
    this.curves = curves ?? [cfg, cfg];
  }

  reseed(seed: number): void {
    this.rng = new NetRandom(seed);
  }

  startMatch(): void {
    this.match.reset(this.cfg.maxHp);
    this.round.clear();
    this.events.length = 0;
    this.forceCue = false;
    this.phase = "Standoff";
  }

  /** Standoff -> Countdown at `now` ("3" appears). Consumes one schedule from the RNG. */
  startCountdown(now: number): boolean {
    if (this.phase !== "Standoff" || this.match.over) return false;
    this.round.clear();
    this.round.roundId = this.nextRoundId++;
    this.round.roundNumber = this.match.roundNumber;
    this.round.schedule = createSchedule(this.cfg, this.rng);
    this.round.countdownStartNs = now;
    this.forceCue = false;
    this.phase = "Countdown";
    this.push("RoundStarted", -1);
    return true;
  }

  forceCueNow(): void {
    this.forceCue = true;
  }

  nextRound(): boolean {
    if (this.phase !== "Result" || this.match.over) return false;
    this.match.roundNumber++;
    this.phase = "Standoff";
    return true;
  }

  voidRound(): boolean {
    if (this.phase === "Standoff" || this.phase === "Result") return false;
    this.doVoid();
    return true;
  }

  /** LocalAuthority.Tick with the reports that arrived this tick. */
  tick(now: number, reports: readonly ShotReport[] = []): void {
    const r = this.round;
    if (this.phase === "Countdown" && (this.forceCue || now - r.countdownStartNs >= r.schedule!.countdownNs)) {
      this.phase = "Hold";
    }
    if (this.phase === "Hold" && (this.forceCue || now - r.countdownStartNs >= r.schedule!.cueOffsetNs)) {
      this.forceCue = false;
      r.cueSent = true;
      r.cueDecidedNs = now;
      this.phase = "Draw";
      this.push("Cue", -1);
    }
    for (const rep of reports) {
      const v = this.submit(rep, now);
      if (v !== "Accepted") this.rejectedReports++;
    }
    if (this.phase === "Draw" || this.phase === "Resolving") {
      this.commit(now);
      this.timeouts(now);
      this.commit(now);
      if (r.slots[0].settled && r.slots[1].settled) this.resolve();
    }
  }

  submit(r: ShotReport, now: number): ReportVerdict {
    this.lastVerdict = this.validate(r, now);
    if (this.lastVerdict !== "Accepted") return this.lastVerdict;
    const s = this.round.slots[r.slot];
    s.hasReport = true;
    s.report = r;
    if (r.kind === "Early") {
      s.outcome = "Foul";
      this.push("Foul", r.slot);
      this.checkBothFoul();
      return this.lastVerdict;
    }
    s.reactionNs = r.reactionNs;
    if (r.reactionNs < anticipationNs(this.cfg)) {
      s.outcome = "Anticipation";
      const e = this.make("Foul", r.slot);
      e.anticipation = true;
      e.reactionNs = r.reactionNs;
      this.events.push(e);
      this.checkBothFoul();
    }
    return this.lastVerdict;
  }

  validate(r: ShotReport, now: number): ReportVerdict {
    const round = this.round;
    if (r.slot < 0 || r.slot >= SLOTS) return "BadSlot";
    if (r.roundId < round.roundId) return "Stale";
    if (r.roundId !== round.roundId) return "WrongRound";
    if (this.phase === "Standoff" || this.phase === "Result" || round.isVoid) return "Stale";
    const s = round.slots[r.slot];
    if (s.hasReport) return "Duplicate";
    if (s.settled) return "Stale";

    if (r.kind === "Early") {
      if (r.cuePresentedNs !== 0 && r.inputNs >= r.cuePresentedNs) return "Inconsistent";
      return "Accepted";
    }
    if (r.kind !== "Shot") return "Implausible";
    if (!round.cueSent) return "Implausible";
    if (r.reactionNs < 0) return "Implausible";
    if (r.reactionNs > noShotTimeoutNs(this.cfg) + reportSlackNs(this.cfg)) return "Implausible";
    const diff = (r.inputNs - r.cuePresentedNs) - r.reactionNs;
    if (diff > CONSISTENCY_TOLERANCE_NS || diff < -CONSISTENCY_TOLERANCE_NS) return "Inconsistent";
    if ((now - round.cueDecidedNs) + reportSlackNs(this.cfg) < r.reactionNs) return "Implausible";
    return "Accepted";
  }

  // ------------------------------------------------------------------ internals

  private checkBothFoul(): void {
    if (this.round.slots[0].isFoul && this.round.slots[1].isFoul) this.doVoid();
  }

  private doVoid(): void {
    this.round.isVoid = true;
    this.phase = "Standoff";
    this.forceCue = false;
    this.push("RoundVoid", -1);
  }

  private isPendingShot(s: SlotRecord): boolean {
    return s.hasReport && s.outcome === "Pending" && s.report!.kind === "Shot";
  }

  private commit(now: number): void {
    if (this.phase !== "Draw" && this.phase !== "Resolving") return;
    const tie = tieWindowNs(this.cfg);
    const grace = commitGraceNs(this.cfg);
    for (let guard = 0; guard < 4; guard++) {
      const s0 = this.round.slots[0];
      const s1 = this.round.slots[1];
      const p0 = this.isPendingShot(s0);
      const p1 = this.isPendingShot(s1);
      if (!p0 && !p1) return;

      let a: number;
      if (p0 && p1) a = s0.reactionNs <= s1.reactionNs ? 0 : 1;
      else a = p0 ? 0 : 1;
      const b = 1 - a;
      const sa = this.round.slots[a];
      const sb = this.round.slots[b];

      if (this.isPendingShot(sb)) {
        if (sb.reactionNs - sa.reactionNs <= tie) {
          this.fireSimultaneous();
          continue;
        }
        this.fire(a, false);
        if (this.match.hp[b] <= 0) this.stop(b);
        continue;
      }
      if (sb.settled) {
        this.fire(a, false);
        continue;
      }
      // b has not reported yet: commit a only once b can no longer claim a faster/tied reaction
      if (now - this.round.cueDecidedNs >= sa.reactionNs + tie + grace) {
        this.fire(a, false);
        if (this.match.hp[b] <= 0) this.stop(b);
        continue;
      }
      return;
    }
  }

  private damageFor(slot: number, reactionNs: number): number {
    return evaluateDamage(this.curves[slot], nsToMs(reactionNs));
  }

  private fire(slot: number, simultaneous: boolean): void {
    const s = this.round.slots[slot];
    const target = 1 - slot;
    const dmg = this.damageFor(slot, s.reactionNs);
    this.match.hp[target] = Math.max(0, this.match.hp[target] - dmg);
    this.commitShot(slot, s, dmg, simultaneous);
  }

  private fireSimultaneous(): void {
    const s0 = this.round.slots[0];
    const s1 = this.round.slots[1];
    const d0 = this.damageFor(0, s0.reactionNs);
    const d1 = this.damageFor(1, s1.reactionNs);
    this.round.isTie = true;
    this.match.hp[1] = Math.max(0, this.match.hp[1] - d0);
    this.match.hp[0] = Math.max(0, this.match.hp[0] - d1);
    this.commitShot(0, s0, d0, true);
    this.commitShot(1, s1, d1, true);
  }

  private commitShot(slot: number, s: SlotRecord, dmg: number, simultaneous: boolean): void {
    const target = 1 - slot;
    s.outcome = "Shot";
    s.damageDealt = dmg;
    s.grade = gradeFromMs(this.cfg, nsToMs(s.reactionNs));
    this.match.recordReaction(slot, s.reactionNs);
    this.phase = "Resolving";
    const e = this.make("Shot", slot);
    e.reactionNs = s.reactionNs;
    e.damage = dmg;
    e.targetHpAfter = this.match.hp[target];
    e.killed = this.match.hp[target] <= 0;
    e.simultaneous = simultaneous;
    this.events.push(e);
  }

  private stop(slot: number): void {
    const s = this.round.slots[slot];
    if (s.settled) return;
    s.outcome = "Stopped";
    this.push("Stopped", slot);
  }

  private timeouts(now: number): void {
    if (!this.round.cueSent) return;
    const limit = noShotTimeoutNs(this.cfg) + reportSlackNs(this.cfg);
    for (let i = 0; i < SLOTS; i++) {
      const s = this.round.slots[i];
      if (!s.settled && !s.hasReport && now - this.round.cueDecidedNs > limit) {
        s.outcome = "NoShot";
        this.phase = "Resolving";
        this.push("NoShot", i);
      }
    }
  }

  private resolve(): void {
    const r = this.round;
    const d0 = r.slots[0].damageDealt;
    const d1 = r.slots[1].damageDealt;
    if (r.isTie) r.winnerSlot = -1;
    else r.winnerSlot = d0 > d1 ? 0 : (d1 > d0 ? 1 : -1);
    if (r.winnerSlot >= 0) this.match.roundWins[r.winnerSlot]++;
    this.phase = "Result";
    const re = this.make("RoundResolved", r.winnerSlot);
    re.simultaneous = r.isTie;
    this.events.push(re);

    const hp = this.match.hp;
    if (hp[0] <= 0 || hp[1] <= 0) {
      // both down (only possible in a tie) = drawn match: over, winner -1
      this.match.finish(hp[0] > 0 ? 0 : (hp[1] > 0 ? 1 : -1));
      this.push("MatchOver", this.match.winner);
    }
  }

  private make(kind: DuelEventKind, slot: number): DuelEvent {
    return {
      kind,
      slot,
      roundId: this.round.roundId,
      reactionNs: -1,
      damage: 0,
      targetHpAfter: 0,
      killed: false,
      anticipation: false,
      simultaneous: false,
    };
  }

  private push(kind: DuelEventKind, slot: number): void {
    this.events.push(this.make(kind, slot));
  }
}
