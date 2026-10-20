// Parity: replays Tools/Parity/vectors.json (written by the C# EditMode test ParityVectorsTests)
// through the TypeScript port. Run from the repo root:
//   deno test --allow-read supabase/functions/_shared/duel/duel.test.ts
import { assert, assertEquals, readText } from "../testing.ts";
import { configFromJson } from "./config.ts";
import { hardConfig } from "./load_config.ts";
import { checkAll, checkDamage, checkMatches, checkMeta, checkRounds, checkSchedules } from "./parity.ts";
import { NetRandom } from "./rng.ts";
import { createSchedule } from "./schedule.ts";
import { playRecordedMatch } from "./recorded_match.ts";

const ROOT = new URL("../../../../", import.meta.url);
const VECTORS = new URL("Tools/Parity/vectors.json", ROOT);
const UNITY_CONFIG = new URL("Assets/PawNoon/Resources/Data/duel_config.json", ROOT);
const SERVER_CONFIG = new URL("supabase/functions/_shared/duel_config.json", ROOT);

// deno-lint-ignore no-explicit-any
async function vectors(): Promise<any> {
  return JSON.parse(await readText(VECTORS));
}

function report(fails: string[]): void {
  if (fails.length) throw new Error(`${fails.length} parity mismatch(es):\n  ` + fails.slice(0, 40).join("\n  "));
}

Deno.test("server duel_config.json is a byte copy of the Unity one", async () => {
  const a = await readText(UNITY_CONFIG);
  const b = await readText(SERVER_CONFIG);
  assert(a === b, "supabase/functions/_shared/duel_config.json is stale: run supabase/scripts/sync_duel_config.sh");
});

Deno.test("parity: config snapshot", async () => report(checkMeta(hardConfig(), await vectors())));
Deno.test("parity: System.Random + RoundSchedule hold lengths", async () => report(checkSchedules(hardConfig(), await vectors())));
Deno.test("parity: damage curve + grades", async () => report(checkDamage(hardConfig(), await vectors())));
Deno.test("parity: single exchanges (tie, anticipation, foul, no-shot, kill, void)", async () => report(checkRounds(hardConfig(), await vectors())));
Deno.test("parity: 3-round recorded matches", async () => report(checkMatches(hardConfig(), await vectors())));

Deno.test("parity checker notices a divergence", async () => {
  const v = await vectors();
  v.rounds[0].expect.damage[0] += 1e-6;
  v.schedules[0].holdNs[3] += 1;
  v.matches[0].expect.winner = 1 - v.matches[0].expect.winner;
  assertEquals(checkAll(hardConfig(), v).length, 3);
});

Deno.test("System.Random anchors known from .NET", () => {
  // Widely published values of new Random(seed).NextDouble() / Next().
  assertEquals(new NetRandom(0).nextDouble(), 0.7262432699679598);
  assertEquals(new NetRandom(1).nextDouble(), 0.24866858415709278);
  assertEquals(new NetRandom(42).nextDouble(), 0.6681064659115423);
  assertEquals(new NetRandom(0).next(), 1559595546);
});

Deno.test("hold lengths stay in [holdMin, holdMax] and repeat per seed", () => {
  const cfg = hardConfig();
  const rng = new NetRandom(1234);
  for (let i = 0; i < 5000; i++) {
    const s = createSchedule(cfg, rng);
    assert(s.holdNs >= 1e9 && s.holdNs <= 4e9, `hold ${s.holdNs}`);
    assertEquals(s.beatNs, 700_000_000);
  }
  assertEquals(createSchedule(cfg, new NetRandom(7)).holdNs, createSchedule(cfg, new NetRandom(7)).holdNs);
});

Deno.test("config parsing mirrors DuelConfig.FromJson", () => {
  const c = configFromJson({ beatSeconds: "0.5", tieWindowMs: 4, damageMs: [1, 2, 3], damageValue: [1, 2], countdownBeats: 2.5 });
  assertEquals(c.beatSeconds, 0.5);
  assertEquals(c.tieWindowMs, 4);
  assertEquals(c.damageMs.length, 8, "mismatched curve arrays are ignored");
  assertEquals(c.countdownBeats, 2, "Math.Round is half-to-even");
  assertEquals(configFromJson("nope").noShotTimeoutSeconds, 1.5);
});

Deno.test("peacemaker curve applies only when passed (casual)", () => {
  const cfg = hardConfig();
  const shots = [{ kind: "shot" as const, reactionNs: 150_000_000 }];
  const std = playRecordedMatch(cfg, { seed: 1, rounds: 1, maxAttempts: 3 }, shots, [{ kind: "noshot", reactionNs: 0 }]);
  const pm = playRecordedMatch(cfg, {
    seed: 1, rounds: 1, maxAttempts: 3,
    curves: [{ damageMs: cfg.damageMs, damageValue: [48, 42, 34, 26, 16, 8, 4, 2] }, cfg],
  }, shots, [{ kind: "noshot", reactionNs: 0 }]);
  assertEquals(std.hp[1], 60);
  assertEquals(pm.hp[1], 52);
});
