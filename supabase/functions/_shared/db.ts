// Service-role Supabase client. Bypasses RLS: only ever used inside Edge Functions, after the
// caller has been authenticated with requireUser(). SUPABASE_URL and SUPABASE_SERVICE_ROLE_KEY are
// injected by the Edge Runtime (locally by `supabase functions serve`).
import { createClient, type SupabaseClient } from "jsr:@supabase/supabase-js@2";
import { HttpError } from "./http.ts";

export type Db = SupabaseClient;

let client: Db | null = null;

export function admin(): Db {
  if (client) return client;
  const url = Deno.env.get("SUPABASE_URL");
  const key = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  if (!url || !key) throw new Error("SUPABASE_URL / SUPABASE_SERVICE_ROLE_KEY missing");
  client = createClient(url, key, { auth: { persistSession: false, autoRefreshToken: false } });
  return client;
}

/** Throws a logged 500 on a PostgREST error, else returns data typed as the caller's row type
 *  (the client is untyped: no generated database types). */
export function must<T>(res: { data: unknown; error: { message: string } | null }, what: string): T {
  if (res.error) {
    console.error(`db error (${what}):`, res.error.message);
    throw new HttpError(500, "db_error", `database error: ${what}`);
  }
  return res.data as T;
}
