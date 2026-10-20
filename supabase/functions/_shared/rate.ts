// Per-user rate limits (review pass 2): one SQL call, pn_rate_take (migrations/0061_review2.sql), counted per
// (user, kind) in a sliding window. Limits are in server_config.ts RATE. A database failure never blocks a player:
// the call is let through and logged (a limiter must not become an outage).
import { HttpError } from "./http.ts";
import { RATE, type RateKind } from "./server_config.ts";

/** The slice of the Supabase client this needs (so tests can pass a stub). */
export interface RpcClient {
  // deno-lint-ignore no-explicit-any
  rpc(fn: string, args?: any): any;
}

export function rateLimited(kind: RateKind): HttpError {
  const r = RATE[kind];
  return new HttpError(429, "rate_limited", "too many requests, wait a moment", { retryAfterSec: Math.min(r.windowSec, 120) });
}

/** Counts one call of `kind`; throws 429 rate_limited when the window is full. */
export async function takeRate(db: RpcClient, userId: string, kind: RateKind): Promise<void> {
  const r = RATE[kind];
  const { data, error }: { data: unknown; error: { message: string } | null } = await db.rpc("pn_rate_take", { p_user: userId, p_kind: kind, p_window_sec: r.windowSec, p_max: r.max });
  if (error) {
    console.error(`[rate] ${kind} check failed (let through):`, error.message);
    return;
  }
  if (data === false) throw rateLimited(kind);
}

/** True when `kind` already has `max` events in its window (failure counters such as room_miss). */
export async function rateExhausted(db: RpcClient, userId: string, kind: RateKind): Promise<boolean> {
  const r = RATE[kind];
  const { data, error }: { data: unknown; error: { message: string } | null } = await db.rpc("pn_rate_count", { p_user: userId, p_kind: kind, p_window_sec: r.windowSec });
  if (error) {
    console.error(`[rate] ${kind} count failed (let through):`, error.message);
    return false;
  }
  return typeof data === "number" && data >= r.max;
}

/** Records one failure event of `kind` (best effort). */
export async function noteRate(db: RpcClient, userId: string, kind: RateKind): Promise<void> {
  const { error }: { error: { message: string } | null } = await db.rpc("pn_rate_note", { p_user: userId, p_kind: kind });
  if (error) console.error(`[rate] ${kind} note failed:`, error.message);
}
