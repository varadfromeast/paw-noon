// Fallback runner for machines without Deno: runs the same `Deno.test` files under Node >= 22.18
// (native TypeScript type stripping). Usage from the repo root:
//   node Tools/Parity/run_node_tests.ts
// Deno remains the real runner (`deno test --allow-read supabase/functions/_shared/`).
import { readFileSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { resolve } from "node:path";

type Fn = () => unknown | Promise<unknown>;
const tests: { name: string; fn: Fn }[] = [];

(globalThis as Record<string, unknown>).Deno = {
  test: (name: string, fn: Fn) => tests.push({ name, fn }),
  readTextFile: (u: URL | string) => Promise.resolve(readFileSync(u instanceof URL ? fileURLToPath(u) : u, "utf8")),
  env: { get: (_k: string) => undefined },
};

const root = resolve(fileURLToPath(new URL("../../", import.meta.url)));
const files = [
  "supabase/functions/_shared/duel/duel.test.ts",
  "supabase/functions/_shared/server.test.ts",
];
for (const f of files) await import(pathToFileURL(resolve(root, f)).href);

let failed = 0;
for (const t of tests) {
  try {
    await t.fn();
    console.log(`ok    ${t.name}`);
  } catch (e) {
    failed++;
    console.log(`FAIL  ${t.name}\n      ${(e as Error).message.replaceAll("\n", "\n      ")}`);
  }
}
console.log(`\n${tests.length - failed} passed, ${failed} failed`);
if (failed) (globalThis as unknown as { process: { exit(c: number): void } }).process.exit(1);
