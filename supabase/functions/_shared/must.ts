// PostgREST result helper, kept free of the supabase-js import so pure modules (ratings.ts) stay
// unit-testable under `deno test` / the Node fallback runner without network access.
import { HttpError } from "./http.ts";

/** Throws a logged 500 on a PostgREST error, else returns data typed as the caller's row type
 *  (the client is untyped: no generated database types). */
export function must<T>(res: { data: unknown; error: { message: string } | null }, what: string): T {
  if (res.error) {
    console.error(`db error (${what}):`, res.error.message);
    throw new HttpError(500, "db_error", `database error: ${what}`);
  }
  return res.data as T;
}
