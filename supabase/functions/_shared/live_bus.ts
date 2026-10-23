// Live duels: the Realtime adapter (the only code that talks to Supabase Realtime). The server never holds a
// socket: it posts to the Realtime broadcast REST endpoint, which fans the message out to the phones subscribed to
// the match's PRIVATE channel `live:<matchId>` (only the two players may join it: realtime.messages policies in
// migrations/0070_live.sql). Phones never broadcast on it (no insert policy), they only listen and use Presence.
import type { LiveMsg } from "./live.ts";
import type { LiveBus } from "./live_service.ts";

export interface BroadcastEnvelope {
  topic: string;
  event: string;
  payload: Record<string, unknown>;
  private: true;
}

/** The REST body for one batch (pure, unit-tested). Every payload carries the server time it was sent at. */
export function broadcastBody(topic: string, msgs: LiveMsg[], sentAtMs: number): { messages: BroadcastEnvelope[] } {
  return {
    messages: msgs.map((m) => ({ topic, event: m.event, payload: { ...m.payload, sentAtMs }, private: true })),
  };
}

/** POST {SUPABASE_URL}/realtime/v1/api/broadcast with the service role key. */
export class RealtimeRestBus implements LiveBus {
  private readonly url: string;
  private readonly key: string;
  private readonly fetchFn: typeof fetch;

  constructor(url: string, serviceKey: string, fetchFn: typeof fetch = fetch) {
    this.url = url.replace(/\/+$/, "") + "/realtime/v1/api/broadcast";
    this.key = serviceKey;
    this.fetchFn = fetchFn;
  }

  async send(topic: string, msgs: LiveMsg[]): Promise<void> {
    if (msgs.length === 0) return;
    const res = await this.fetchFn(this.url, {
      method: "POST",
      headers: { "Content-Type": "application/json", apikey: this.key, Authorization: `Bearer ${this.key}` },
      body: JSON.stringify(broadcastBody(topic, msgs, Date.now())),
    });
    if (!res.ok) throw new Error(`realtime broadcast ${res.status}: ${(await res.text()).slice(0, 200)}`);
  }
}

/** In-memory bus (tests, local dry runs): records every message per topic. */
export class MemoryBus implements LiveBus {
  readonly sent: { topic: string; msg: LiveMsg }[] = [];
  fail = false;

  send(topic: string, msgs: LiveMsg[]): Promise<void> {
    if (this.fail) return Promise.reject(new Error("bus down"));
    for (const msg of msgs) this.sent.push({ topic, msg });
    return Promise.resolve();
  }

  events(topic?: string): string[] {
    return this.sent.filter((x) => topic === undefined || x.topic === topic).map((x) => x.msg.event);
  }
}
