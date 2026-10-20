// Small HTTP helpers shared by every function: JSON responses, typed errors, CORS, body limits.

export const CORS_HEADERS: Record<string, string> = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
};

/** Error body for every non-2xx response: { error, message, ...extra } (schemas/error.schema.json). */
export class HttpError extends Error {
  // plain fields, not constructor parameter properties: Node's strip-only TypeScript mode (the
  // Tools/Parity/run_node_tests.ts fallback runner) cannot load parameter properties
  readonly status: number;
  readonly code: string;
  readonly extra: Record<string, unknown>;

  constructor(status: number, code: string, message: string, extra: Record<string, unknown> = {}) {
    super(message);
    this.status = status;
    this.code = code;
    this.extra = extra;
  }
}

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { ...CORS_HEADERS, "Content-Type": "application/json; charset=utf-8" },
  });
}

export async function readJson(req: Request, maxBytes = 64 * 1024): Promise<Record<string, unknown>> {
  const len = Number(req.headers.get("content-length") ?? "0");
  if (len > maxBytes) throw new HttpError(413, "body_too_large", `body over ${maxBytes} bytes`);
  const text = await req.text();
  if (text.length > maxBytes) throw new HttpError(413, "body_too_large", `body over ${maxBytes} bytes`);
  if (text.trim() === "") return {};
  let v: unknown;
  try {
    v = JSON.parse(text);
  } catch {
    throw new HttpError(400, "bad_json", "body is not valid JSON");
  }
  if (v === null || typeof v !== "object" || Array.isArray(v)) throw new HttpError(400, "bad_json", "body must be a JSON object");
  return v as Record<string, unknown>;
}

/**
 * Wraps a handler: CORS preflight, POST only, HttpError -> JSON error, anything else -> 500
 * (logged, never leaked).
 */
export function serve(name: string, handler: (req: Request) => Promise<Response>): void {
  Deno.serve(async (req: Request) => {
    if (req.method === "OPTIONS") return new Response("ok", { headers: CORS_HEADERS });
    if (req.method !== "POST") return json({ error: "method_not_allowed", message: "use POST" }, 405);
    try {
      return await handler(req);
    } catch (e) {
      if (e instanceof HttpError) return json({ error: e.code, message: e.message, ...e.extra }, e.status);
      console.error(`[${name}]`, e);
      return json({ error: "internal", message: "internal error" }, 500);
    }
  });
}
