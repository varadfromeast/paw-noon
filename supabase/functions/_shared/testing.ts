// Minimal assertions so the tests need nothing beyond the Deno runtime (no std, no npm).

export function assert(cond: unknown, msg = "assertion failed"): asserts cond {
  if (!cond) throw new Error(msg);
}

export function assertEquals(actual: unknown, expected: unknown, msg = ""): void {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a !== e) throw new Error(`${msg ? msg + ": " : ""}expected ${e}, got ${a}`);
}

export function assertClose(actual: number, expected: number, tol = 1e-9, msg = ""): void {
  if (!(Math.abs(actual - expected) <= tol)) throw new Error(`${msg ? msg + ": " : ""}expected ${expected} +/- ${tol}, got ${actual}`);
}

/** Reads a UTF-8 file relative to the calling module. */
export async function readText(url: URL): Promise<string> {
  return await Deno.readTextFile(url);
}
