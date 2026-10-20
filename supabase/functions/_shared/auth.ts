// Auth helper: the caller is whoever owns the Supabase Auth JWT in `Authorization: Bearer <jwt>`.
// The API gateway already verifies the signature (verify_jwt = true in config.toml); we resolve the
// user through Auth so a deleted or banned user is refused too, and to learn is_anonymous (guest).
import { admin, type Db } from "./db.ts";
import { HttpError } from "./http.ts";

export interface AuthedUser {
  id: string;
  /** Guest (device) account, not yet linked to Apple/Google (SPEC_V2 §5). */
  isAnonymous: boolean;
  /** Linked identity providers, e.g. ["apple"], ["google"]. */
  providers: string[];
}

export function bearer(req: Request): string {
  const h = req.headers.get("Authorization") ?? "";
  const m = /^Bearer\s+(.+)$/i.exec(h);
  if (!m) throw new HttpError(401, "unauthorized", "missing bearer token");
  return m[1].trim();
}

export async function requireUser(req: Request, db: Db = admin()): Promise<AuthedUser> {
  const jwt = bearer(req);
  const { data, error } = await db.auth.getUser(jwt);
  if (error || !data?.user) throw new HttpError(401, "unauthorized", "invalid or expired session");
  const u = data.user;
  const providers = ((u.identities ?? []) as { provider: string }[]).map((i) => i.provider).filter((p) => p !== "anonymous");
  return { id: u.id, isAnonymous: u.is_anonymous === true, providers };
}

// Ranked needs a linked Apple/Google identity (§4.3): pure helper in ranked.ts (unit-tested without supabase-js).
export { hasLinkedIdentity } from "./ranked.ts";
