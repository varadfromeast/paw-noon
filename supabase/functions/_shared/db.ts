// Service-role Supabase client. Bypasses RLS: only ever used inside Edge Functions, after the
// caller has been authenticated with requireUser(). SUPABASE_URL and SUPABASE_SERVICE_ROLE_KEY are
// injected by the Edge Runtime (locally by `supabase functions serve`).
import { createClient, type SupabaseClient } from "jsr:@supabase/supabase-js@2";

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

// `must` lives in must.ts (no supabase-js import) so pure modules such as ratings.ts can be unit-tested
// without the Supabase client; re-exported here for the functions.
export { must } from "./must.ts";
