// Port of the rules-relevant part of PawNoon.Core.DuelConfig (FromJson + Sanitize + ns helpers).
// Presentation and bot tunables are ignored: the server never runs the bot or draws anything.
//
// The JSON is ../duel_config.json, a COPY of Assets/PawNoon/Resources/Data/duel_config.json.
// Keep it in sync with supabase/scripts/sync_duel_config.sh (the parity test fails if they differ).

export interface DuelConfig {
  countdownBeats: number;
  beatSeconds: number;
  holdMinSeconds: number;
  holdMaxSeconds: number;
  noShotTimeoutSeconds: number;
  commitGraceMs: number;
  anticipationMs: number;
  tieWindowMs: number;
  reportSlackMs: number;
  maxHp: number;
  damageMs: number[];
  damageValue: number[];
  gradeLightningMs: number;
  gradePerfectMs: number;
  gradeGreatMs: number;
  gradeGoodMs: number;
  gradeSlowMs: number;
}

export function defaultConfig(): DuelConfig {
  return {
    countdownBeats: 3,
    beatSeconds: 0.70,
    holdMinSeconds: 1.0,
    holdMaxSeconds: 4.0,
    noShotTimeoutSeconds: 1.5,
    commitGraceMs: 25.0,
    anticipationMs: 100.0,
    tieWindowMs: 15.0,
    reportSlackMs: 50.0,
    maxHp: 100.0,
    damageMs: [150, 200, 250, 300, 400, 600, 1000, 1500],
    damageValue: [40, 36, 30, 24, 16, 8, 4, 2],
    gradeLightningMs: 200,
    gradePerfectMs: 250,
    gradeGreatMs: 320,
    gradeGoodMs: 420,
    gradeSlowMs: 700,
  };
}

// ---------------------------------------------------------------- C# numeric helpers

/** C# Math.Round(double): round half to even (banker's rounding). */
export function roundHalfEven(x: number): number {
  if (!Number.isFinite(x)) return x;
  const r = Math.round(x);
  if (Math.abs(x % 1) === 0.5) return 2 * Math.round(x / 2);
  return r;
}

/** DuelConfig.SecToNs: (long)Math.Round(s * 1e9). */
export function secToNs(s: number): number {
  return roundHalfEven(s * 1e9);
}

/** DuelConfig.MsToNs: (long)Math.Round(ms * 1e6). */
export function msToNs(ms: number): number {
  return roundHalfEven(ms * 1e6);
}

/** DuelConfig.NsToMs: ns / 1e6. */
export function nsToMs(ns: number): number {
  return ns / 1e6;
}

// ---------------------------------------------------------------- PnJson-like readers

type Json = Record<string, unknown>;

/** PnJson.ToNum: numbers as-is, bools 1/0, numeric strings parsed (invariant culture), else def. */
function toNum(v: unknown, def: number): number {
  if (typeof v === "number") return v;
  if (typeof v === "boolean") return v ? 1 : 0;
  if (typeof v === "string") {
    const t = v.trim();
    if (t !== "" && /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/.test(t)) return Number(t);
  }
  return def;
}

function num(root: Json, key: string, def: number): number {
  return key in root ? toNum(root[key], def) : def;
}

function int(root: Json, key: string, def: number): number {
  return roundHalfEven(num(root, key, def));
}

function numArray(root: Json, key: string): number[] | null {
  const a = root[key];
  if (!Array.isArray(a)) return null;
  return a.map((x) => toNum(x, 0.0));
}

/** DuelConfig.FromJson for the rules keys. Never throws; bad input yields defaults. */
export function configFromJson(input: unknown): DuelConfig {
  const c = defaultConfig();
  if (input === null || typeof input !== "object" || Array.isArray(input)) return c;
  const root = input as Json;
  c.countdownBeats = int(root, "countdownBeats", c.countdownBeats);
  c.beatSeconds = num(root, "beatSeconds", c.beatSeconds);
  c.holdMinSeconds = num(root, "holdMinSeconds", c.holdMinSeconds);
  c.holdMaxSeconds = num(root, "holdMaxSeconds", c.holdMaxSeconds);
  c.noShotTimeoutSeconds = num(root, "noShotTimeoutSeconds", c.noShotTimeoutSeconds);
  c.commitGraceMs = num(root, "commitGraceMs", c.commitGraceMs);
  c.anticipationMs = num(root, "anticipationMs", c.anticipationMs);
  c.tieWindowMs = num(root, "tieWindowMs", c.tieWindowMs);
  c.reportSlackMs = num(root, "reportSlackMs", c.reportSlackMs);
  c.maxHp = num(root, "maxHp", c.maxHp);
  const dm = numArray(root, "damageMs");
  const dv = numArray(root, "damageValue");
  if (dm !== null && dv !== null && dm.length === dv.length && dm.length >= 2) {
    c.damageMs = dm;
    c.damageValue = dv;
  }
  c.gradeLightningMs = num(root, "gradeLightningMs", c.gradeLightningMs);
  c.gradePerfectMs = num(root, "gradePerfectMs", c.gradePerfectMs);
  c.gradeGreatMs = num(root, "gradeGreatMs", c.gradeGreatMs);
  c.gradeGoodMs = num(root, "gradeGoodMs", c.gradeGoodMs);
  c.gradeSlowMs = num(root, "gradeSlowMs", c.gradeSlowMs);
  sanitize(c);
  return c;
}

/** DuelConfig.Sanitize, rules keys only. */
export function sanitize(c: DuelConfig): void {
  if (c.countdownBeats < 1) c.countdownBeats = 1;
  if (c.beatSeconds < 0.05) c.beatSeconds = 0.05;
  if (c.holdMinSeconds < 0) c.holdMinSeconds = 0;
  if (c.holdMaxSeconds < c.holdMinSeconds) c.holdMaxSeconds = c.holdMinSeconds;
  if (c.noShotTimeoutSeconds < 0.2) c.noShotTimeoutSeconds = 0.2;
  if (c.commitGraceMs < 0) c.commitGraceMs = 0;
  if (c.anticipationMs < 0) c.anticipationMs = 0;
  if (c.tieWindowMs < 0) c.tieWindowMs = 0;
  if (c.reportSlackMs < 0) c.reportSlackMs = 0;
  if (c.maxHp < 1) c.maxHp = 1;
}

// ---------------------------------------------------------------- derived (DuelConfig properties)

export const beatNs = (c: DuelConfig): number => secToNs(c.beatSeconds);
export const noShotTimeoutNs = (c: DuelConfig): number => secToNs(c.noShotTimeoutSeconds);
export const commitGraceNs = (c: DuelConfig): number => msToNs(c.commitGraceMs);
export const anticipationNs = (c: DuelConfig): number => msToNs(c.anticipationMs);
export const tieWindowNs = (c: DuelConfig): number => msToNs(c.tieWindowMs);
export const reportSlackNs = (c: DuelConfig): number => msToNs(c.reportSlackMs);
