// M6 accounts: display-name rules (shared vectors with the C# keypad) and the profile answer shape.
//   deno test --allow-read supabase/functions/_shared/
import { assert, assertEquals, readText } from "./testing.ts";
import { NAME_MAX, NAME_MIN, normalizeDisplayName, validateDisplayName } from "./display_name.ts";
import { profileResponse, type ProfileFullRow } from "./profile.ts";

interface NameCase { in: string; ok: boolean; name?: string; reason?: string }

const vectors = JSON.parse(await readText(new URL("../../../Tools/Parity/display_names.json", import.meta.url))) as { cases: NameCase[] };

Deno.test("display names: shared vectors (same file as AccountTests.cs)", () => {
  assert(vectors.cases.length >= 30, "vectors loaded");
  for (const c of vectors.cases) {
    const r = validateDisplayName(c.in);
    if (c.ok) {
      assert(r.ok, `${JSON.stringify(c.in)} should be accepted, got ${JSON.stringify(r)}`);
      if (r.ok) assertEquals(r.name, c.name, JSON.stringify(c.in));
    } else {
      assert(!r.ok, `${JSON.stringify(c.in)} should be refused`);
      if (!r.ok) assertEquals(r.error, c.reason, JSON.stringify(c.in));
    }
  }
});

Deno.test("display names: non-strings are refused, limits are 3..14", () => {
  assertEquals(validateDisplayName(42), { ok: false, error: "bad_chars" });
  assertEquals(validateDisplayName(null), { ok: false, error: "bad_chars" });
  assertEquals(validateDisplayName(undefined), { ok: false, error: "bad_chars" });
  assertEquals([NAME_MIN, NAME_MAX], [3, 14]);
  assertEquals(normalizeDisplayName("  a  b  c "), "A B C");
});

Deno.test("display names: every accepted name fits the database check (0060_m6_accounts.sql)", () => {
  const dbCheck = (n: string) => n.length >= 3 && n.length <= 14 && /^[A-Z0-9]([A-Z0-9 ]*[A-Z0-9])?$/.test(n) && !n.includes("  ");
  for (const c of vectors.cases) {
    const r = validateDisplayName(c.in);
    if (r.ok) assert(dbCheck(r.name), `${r.name} would violate profiles_display_name_shape`);
  }
  assert(/^Stranger #[0-9]{4}$/.test("Stranger #0042"), "guest default name shape");
});

const row: ProfileFullRow = {
  display_name: "Stranger #0042",
  character: "whiskers",
  owned_items: null,
  coins: 20,
  matches_played: 12,
  wins: 7,
  best_reaction_ns: "198412000",
  lightning_count: 3,
  daily_streak: 2,
  linked_at: null,
};

Deno.test("profile answer: guest with stats (bigint best arrives as a string)", () => {
  const p = profileResponse({ id: "u1", isAnonymous: true, providers: [] }, row, { season: 1, mmr: 1234, rd: 120, matches: 4 });
  assertEquals(p.userId, "u1");
  assertEquals(p.isGuest, true);
  assertEquals(p.stats, { matches: 12, wins: 7, winRate: 0.583, bestReactionNs: 198412000, lightning: 3 });
  assertEquals(p.rating, { season: 1, mmr: 1234, rd: 120, matches: 4 });
  assertEquals(p.ownedItems, []);
  assertEquals(p.nameRules.min, 3);
});

Deno.test("profile answer: linked Apple user, no matches, no best", () => {
  const p = profileResponse(
    { id: "u2", isAnonymous: false, providers: ["apple"] },
    { ...row, display_name: "BILLY", matches_played: 0, wins: 0, best_reaction_ns: null, lightning_count: 0, linked_at: "2026-10-20T10:00:00Z" },
    { season: 1, mmr: 1200, rd: 350, matches: 0 },
  );
  assertEquals(p.isGuest, false);
  assertEquals(p.providers, ["apple"]);
  assertEquals(p.stats, { matches: 0, wins: 0, winRate: 0, bestReactionNs: null, lightning: 0 });
  assertEquals(p.linkedAt, "2026-10-20T10:00:00Z");
});
