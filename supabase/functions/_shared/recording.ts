// Recording wire format (SPEC_V2 §4.2) and a strict shape parser.
// JSON Schema twin: _shared/schemas/recording.schema.json (keep both in step).
//
// All *Ns fields are integer nanoseconds on the recording phone's own monotonic clock
// (TIMING.md §1). JSON numbers are exact up to 2^53 ns (~104 days of uptime), which is why
// every timestamp must be a safe integer.
import type { AttemptKind } from "./duel/recorded_match.ts";
import { type GunId, isGunId } from "./duel/loadouts.ts";

export const RECORDING_VERSION = 1;
export const CHARACTERS = ["whiskers", "blackpaw"] as const;
export type CharacterId = typeof CHARACTERS[number];
export const INPUT_SOURCES = ["unknown", "touch", "mouse", "keyboard", "bot", "injected", "remote"] as const;
export type InputSourceName = typeof INPUT_SOURCES[number];

export interface SwipeSample {
  tNs: number;
  x: number;
  y: number;
  phase: string;
}

export interface RecordingRound {
  /** 1-based round number as the client's LocalAuthority showed it (void attempts repeat it). */
  roundNumber: number;
  kind: AttemptKind;
  /** shot / anticipation: inputNs - cuePresentedNs. early: 0 (no cue yet) or negative. noshot: 0. */
  reactionNs: number;
  /** 0 when the cue was never presented (early before the cue). */
  cuePresentedNs: number;
  /** 0 for noshot. */
  inputNs: number;
  /** When "3" appeared (DuelClient.CountdownStartNs). Required (review 2026-10-20): it anchors the
   *  hold-length check, and an optional field let a tampered recording skip that check by omitting it. */
  countdownStartNs: number;
  inputSource: InputSourceName;
  frameQuantized: boolean;
  /** Ranked: the OS touch samples around the swipe threshold, for audit. */
  samples?: SwipeSample[];
}

export interface Recording {
  version: number;
  seed: number;
  difficulty: "hard";
  character: CharacterId;
  loadout: { gun: GunId };
  rounds: RecordingRound[];
  /** screenHeightPx (optional, M5): Screen.height in px, so the server can redo SwipeDraw's threshold
   *  geometry on raw samples (A3). */
  device: { model: string; os: string; refreshHz: number; screenHeightPx?: number };
  clientBuild: string;
  submittedAt: string;
}

export class ShapeError extends Error {}

type O = Record<string, unknown>;

function obj(v: unknown, path: string): O {
  if (v === null || typeof v !== "object" || Array.isArray(v)) throw new ShapeError(`${path} must be an object`);
  return v as O;
}
function str(o: O, k: string, path: string, max = 128): string {
  const v = o[k];
  if (typeof v !== "string" || v.length === 0 || v.length > max) throw new ShapeError(`${path}.${k} must be a string (1..${max})`);
  return v;
}
function safeInt(o: O, k: string, path: string): number {
  const v = o[k];
  if (typeof v !== "number" || !Number.isSafeInteger(v)) throw new ShapeError(`${path}.${k} must be a safe integer`);
  return v;
}
function num(o: O, k: string, path: string): number {
  const v = o[k];
  if (typeof v !== "number" || !Number.isFinite(v)) throw new ShapeError(`${path}.${k} must be a number`);
  return v;
}
function bool(o: O, k: string, path: string): boolean {
  const v = o[k];
  if (typeof v !== "boolean") throw new ShapeError(`${path}.${k} must be a boolean`);
  return v;
}
function oneOf<T extends string>(o: O, k: string, path: string, allowed: readonly T[]): T {
  const v = o[k];
  if (typeof v !== "string" || !(allowed as readonly string[]).includes(v)) {
    throw new ShapeError(`${path}.${k} must be one of ${allowed.join("|")}`);
  }
  return v as T;
}

const KINDS: readonly AttemptKind[] = ["shot", "anticipation", "early", "noshot"];
const MAX_ROUNDS = 32;
const MAX_SAMPLES = 16;

function parseRound(v: unknown, i: number): RecordingRound {
  const path = `rounds[${i}]`;
  const o = obj(v, path);
  const r: RecordingRound = {
    roundNumber: safeInt(o, "roundNumber", path),
    kind: oneOf(o, "kind", path, KINDS),
    reactionNs: safeInt(o, "reactionNs", path),
    cuePresentedNs: safeInt(o, "cuePresentedNs", path),
    inputNs: safeInt(o, "inputNs", path),
    countdownStartNs: safeInt(o, "countdownStartNs", path),
    inputSource: oneOf(o, "inputSource", path, INPUT_SOURCES),
    frameQuantized: bool(o, "frameQuantized", path),
  };
  if (r.countdownStartNs <= 0) throw new ShapeError(`${path}.countdownStartNs must be > 0`);
  if (o.samples !== undefined && o.samples !== null) {
    if (!Array.isArray(o.samples) || o.samples.length > MAX_SAMPLES) {
      throw new ShapeError(`${path}.samples must be an array of at most ${MAX_SAMPLES}`);
    }
    r.samples = o.samples.map((s, j) => {
      const sp = `${path}.samples[${j}]`;
      const so = obj(s, sp);
      return { tNs: safeInt(so, "tNs", sp), x: num(so, "x", sp), y: num(so, "y", sp), phase: str(so, "phase", sp, 16) };
    });
  }
  return r;
}

function parseDevice(device: O): Recording["device"] {
  const d: Recording["device"] = {
    model: str(device, "model", "recording.device"),
    os: str(device, "os", "recording.device"),
    refreshHz: num(device, "refreshHz", "recording.device"),
  };
  if (device.screenHeightPx !== undefined && device.screenHeightPx !== null) {
    const h = num(device, "screenHeightPx", "recording.device");
    if (!(h >= 100 && h <= 20000)) throw new ShapeError("recording.device.screenHeightPx must be 100..20000");
    d.screenHeightPx = h;
  }
  return d;
}

/** Strict shape check. Throws ShapeError with a JSON-path-like message. Unknown keys are dropped. */
export function parseRecording(v: unknown): Recording {
  const o = obj(v, "recording");
  const loadout = obj(o.loadout, "recording.loadout");
  if (!isGunId(loadout.gun)) throw new ShapeError("recording.loadout.gun is not a known gun");
  const device = obj(o.device, "recording.device");
  if (!Array.isArray(o.rounds) || o.rounds.length < 1 || o.rounds.length > MAX_ROUNDS) {
    throw new ShapeError(`recording.rounds must be an array of 1..${MAX_ROUNDS}`);
  }
  const seed = safeInt(o, "seed", "recording");
  if (seed < -2147483648 || seed > 2147483647) throw new ShapeError("recording.seed must be an int32");
  const submittedAt = str(o, "submittedAt", "recording", 64);
  if (Number.isNaN(Date.parse(submittedAt))) throw new ShapeError("recording.submittedAt must be ISO-8601");
  return {
    version: safeInt(o, "version", "recording"),
    seed,
    difficulty: oneOf(o, "difficulty", "recording", ["hard"] as const),
    character: oneOf(o, "character", "recording", CHARACTERS),
    loadout: { gun: loadout.gun },
    rounds: o.rounds.map(parseRound),
    device: parseDevice(device),
    clientBuild: str(o, "clientBuild", "recording", 64),
    submittedAt,
  };
}

/** The part of a recording an opponent needs to replay it (and the server to recompute). */
export interface PlayableAttempt {
  roundNumber: number;
  kind: AttemptKind;
  reactionNs: number;
  /** early only: inputNs - countdownStartNs, so the ghost can jump the gun at the same moment. */
  inputOffsetNs?: number;
}

export function playableAttempts(rec: Recording): PlayableAttempt[] {
  return rec.rounds.map((r) => {
    const a: PlayableAttempt = {
      roundNumber: r.roundNumber,
      kind: r.kind,
      reactionNs: r.kind === "shot" || r.kind === "anticipation" ? r.reactionNs : 0,
    };
    if (r.kind === "early") a.inputOffsetNs = r.inputNs - r.countdownStartNs;
    return a;
  });
}
