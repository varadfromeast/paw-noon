// Friend duels (SPEC_V2 §4.4): 4-letter room codes. Pure helpers; the room rows are written by the
// pn_room_* SQL functions (migrations/0003_m5.sql) so claiming a code and claiming a room are atomic.
import { ROOM } from "./server_config.ts";

/** Joining accepts any 4 letters A-Z (the keypad has all 26), case-insensitive, blanks ignored. */
export function normalizeRoomCode(input: unknown): string | null {
  if (typeof input !== "string") return null;
  const s = input.replace(/[\s-]/g, "").toUpperCase();
  return new RegExp(`^[A-Z]{${ROOM.codeLength}}$`).test(s) ? s : null;
}

/** A code room-create may hand out: generator alphabet only (no I / O) and not on the block list. */
export function isGeneratableCode(code: string): boolean {
  if (code.length !== ROOM.codeLength) return false;
  for (const c of code) if (!ROOM.alphabet.includes(c)) return false;
  return !ROOM.blocked.includes(code);
}

/** `count` distinct candidate codes from a uniform random source (crypto in production, seeded in tests). */
export function candidateCodes(count: number, random: () => number = cryptoRandom): string[] {
  const out: string[] = [];
  const a = ROOM.alphabet;
  for (let guard = 0; out.length < count && guard < count * 50; guard++) {
    let code = "";
    for (let i = 0; i < ROOM.codeLength; i++) code += a[Math.floor(random() * a.length) % a.length];
    if (isGeneratableCode(code) && !out.includes(code)) out.push(code);
  }
  return out;
}

function cryptoRandom(): number {
  return crypto.getRandomValues(new Uint32Array(1))[0] / 4294967296;
}

/** pn_room_join outcome -> HTTP. `ready` / `rejoin` are handled by room-join itself (a match is issued). */
export type RoomJoinState = "waiting" | "ready" | "rejoin" | "not_found" | "expired" | "used" | "own_room" | "taken";

export function roomJoinError(state: RoomJoinState): { status: number; code: string; message: string } | null {
  switch (state) {
    case "not_found": return { status: 404, code: "room_not_found", message: "no room with that code" };
    case "expired": return { status: 410, code: "room_expired", message: "the room closed before a duel was recorded" };
    case "used": return { status: 410, code: "room_used", message: "somebody already played this room" };
    case "own_room": return { status: 409, code: "own_room", message: "this is your own room: share the code with a friend" };
    case "taken": return { status: 409, code: "room_taken", message: "another player already joined this room" };
    default: return null;
  }
}
