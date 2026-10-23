// Live duels: the thin I/O shell around the pure state machine (live.ts). Every action is
//   load (state + version) -> pure transition at the server's `now` -> compare-and-set save -> broadcast,
// retried on a version conflict, so two reports (or a report and the round driver) racing on one match can
// never lose an update or broadcast a message for a state that was not stored. Storage, broadcast and the clock
// are adapters (LiveStore / LiveBus / LiveClock), so the whole thing runs in node tests without the network:
// live_store.ts (Postgres via pn_live_*), live_bus.ts (Supabase Realtime broadcast REST), and in-memory twins in
// live.test.ts.
import type { DuelConfig } from "./duel/config.ts";
import {
  forfeitLive,
  type LiveAttempt,
  type LiveMsg,
  type LiveReportIn,
  type LiveState,
  type LiveVerdict,
  readyLive,
  reportLive,
  slotOf,
  type Step,
  tickLive,
} from "./live.ts";

export interface LiveRow {
  id: string;
  state: LiveState;
  version: number;
}

export interface LiveStore {
  load(id: string): Promise<LiveRow | null>;
  /** Compare-and-set: writes `state` (and the newly resolved attempts as live_rounds rows) only when the stored
   *  version is still `expectVersion`. False = conflict (nothing written). */
  save(id: string, expectVersion: number, state: LiveState, resolved: LiveAttempt[]): Promise<boolean>;
  /** Same, for a transition into a finished (not abandoned) match: also writes ratings, match rows, pooled
   *  recordings and stats in the same transaction (pn_live_finish). False = conflict. */
  finish(id: string, expectVersion: number, state: LiveState, resolved: LiveAttempt[]): Promise<boolean>;
}

/** The one Realtime seam: everything the server tells the phones goes through here. */
export interface LiveBus {
  send(topic: string, msgs: LiveMsg[]): Promise<void>;
}

export interface LiveClock {
  nowMs(): number;
  sleep(ms: number): Promise<void>;
}

export const realClock: LiveClock = {
  nowMs: () => Date.now(),
  sleep: (ms) => new Promise((r) => setTimeout(r, Math.max(0, ms))),
};

export function topicOf(matchId: string): string {
  return `live:${matchId}`;
}

export interface ApplyResult {
  step: Step;
  row: LiveRow;
  /** The caller's slot in this match (-1 = not a player). */
  slot: number;
}

export class LiveError extends Error {
  readonly code: "not_found" | "not_member" | "busy";
  constructor(code: "not_found" | "not_member" | "busy", message: string) {
    super(message);
    this.code = code;
  }
}

/** Version conflicts tolerated per action before answering `busy` (each retry re-reads). */
export const LIVE_SAVE_ATTEMPTS = 6;

export class LiveService {
  readonly cfg: DuelConfig;
  readonly store: LiveStore;
  readonly bus: LiveBus;
  readonly clock: LiveClock;

  constructor(cfg: DuelConfig, store: LiveStore, bus: LiveBus, clock: LiveClock = realClock) {
    this.cfg = cfg;
    this.store = store;
    this.bus = bus;
    this.clock = clock;
  }

  /** load -> fn -> CAS save -> broadcast. `userId` null = the server itself (round driver, sweeps). */
  async apply(id: string, userId: string | null, fn: (s: LiveState, slot: number, nowMs: number) => Step): Promise<ApplyResult> {
    for (let attempt = 0; attempt < LIVE_SAVE_ATTEMPTS; attempt++) {
      const row = await this.store.load(id);
      if (!row) throw new LiveError("not_found", "no such live match");
      const slot = userId === null ? -1 : slotOf(row.state, userId);
      if (userId !== null && slot < 0) throw new LiveError("not_member", "not a player of this match");
      const now = this.clock.nowMs();
      const step = fn(row.state, slot, now);
      if (!step.changed) return { step, row, slot };
      const before = row.state.history.length;
      const resolved = step.state.history.slice(before);
      const finished = step.state.phase === "finished" && step.state.result?.endReason !== "abandoned";
      const ok = finished
        ? await this.store.finish(id, row.version, step.state, resolved)
        : await this.store.save(id, row.version, step.state, resolved);
      if (!ok) continue; // somebody else moved the match on: re-read and apply on top of their state
      const saved: LiveRow = { id, state: step.state, version: row.version + 1 };
      if (step.out.length) {
        try {
          await this.bus.send(topicOf(id), step.out);
        } catch (e) {
          // the state is stored; phones that miss a message catch up with live-round (snapshot)
          console.error(`[live] broadcast failed match=${id}:`, e);
        }
      }
      return { step, row: saved, slot };
    }
    throw new LiveError("busy", "the match is changing too fast, retry");
  }

  ready(id: string, userId: string, attempt: number): Promise<ApplyResult> {
    return this.apply(id, userId, (s, slot, now) => readyLive(this.cfg, s, slot, attempt, now));
  }

  report(id: string, userId: string, rep: LiveReportIn): Promise<ApplyResult> {
    return this.apply(id, userId, (s, slot, now) => reportLive(this.cfg, s, slot, rep, now));
  }

  forfeit(id: string, userId: string): Promise<ApplyResult> {
    return this.apply(id, userId, (s, slot, now) => forfeitLive(this.cfg, s, slot, now));
  }

  /** Applies passed deadlines (any member may ask; the round driver does it at the cue and report deadline). */
  tick(id: string, userId: string | null): Promise<ApplyResult> {
    return this.apply(id, userId, (s, _slot, now) => tickLive(this.cfg, s, now));
  }

  /**
   * The round driver (the `live-round` authority in Docs/LIVE_DUELS.md). Runs in the background of the request
   * that started the round (EdgeRuntime.waitUntil in live-ready): sleeps until the secret cue time, sends DRAW,
   * sleeps until the report deadline, resolves whatever is missing as no-shots. Stops as soon as the round is
   * no longer the one it was started for (both reported, a void, a forfeit). If the worker dies, nothing is
   * lost: any later call (a report, live-round from either phone) applies the same deadlines.
   */
  async drive(id: string, roundId: number, maxSteps = 4): Promise<void> {
    for (let i = 0; i < maxSteps; i++) {
      const row = await this.store.load(id);
      if (!row) return;
      const s = row.state;
      if (!s.round || s.round.roundId !== roundId || (s.phase !== "countdown" && s.phase !== "draw")) return;
      const wait = s.deadlineMs - this.clock.nowMs();
      if (wait > 0) await this.clock.sleep(wait);
      await this.tick(id, null);
    }
  }
}

/** Did this step start a round (so the caller should run the driver)? Returns its roundId or 0. */
export function startedRound(step: Step): number {
  const m = step.out.find((x) => x.event === "round_start");
  return m ? m.payload.roundId as number : 0;
}

/** Verdicts the client should treat as "your message counted" (a retry of an accepted one is Duplicate). */
export function verdictOk(v: LiveVerdict): boolean {
  return v === "Accepted" || v === "Duplicate";
}
