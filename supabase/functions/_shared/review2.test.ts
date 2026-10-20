// Review pass 2 (Docs/REVIEW_2026-10-20_pass2.md): purchase account binding, rate limits, character id alias.
//   deno test --allow-read supabase/functions/_shared/      (or node Tools/Parity/run_node_tests.ts)
import { assert, assertEquals } from "./testing.ts";
import { bindingState, decide, deviceProof, expectedAccountToken } from "./payments/binding.ts";
import { type GrantMeta, grantBound, type GrantRow } from "./payments/grant.ts";
import { parsePurchaseRequest } from "./payments/receipt.ts";
import { type FetchFn, PurchaseError, type StoreEntitlement } from "./payments/types.ts";
import { paymentsEnvFrom } from "./payments/validate.ts";
import { noteRate, rateExhausted, takeRate } from "./rate.ts";
import { HttpError } from "./http.ts";
import { normalizeCharacter, parseRecording } from "./recording.ts";
import { buildResponse } from "./match_response.ts";
import { RATE, ROOM } from "./server_config.ts";

const USER = "5D0B1C9E-1F7E-4D53-A1A6-6F3F8B9D2C44";
const OTHER = "11111111-2222-4333-8444-555555555555";
const PRODUCT = "com.pawnoon.pixel.peacemaker";

function ent(over: Partial<StoreEntitlement>): StoreEntitlement {
  return {
    store: "app_store", productId: PRODUCT, item: "peacemaker", transactionId: "2000000812345678",
    latestTransactionId: "2000000812345678", originalTransactionId: "2000000812345678", environment: "sandbox",
    status: "valid", purchasedAt: null, source: "server_api", raw: {}, accountToken: null, ...over,
  };
}

/** A pn_grant_entitlement stand-in: `owner` holds the purchase; allowTransfer decides whether it moves. */
function fakeGrant(owner: string | null, caller: string) {
  const calls: { allowTransfer: boolean; meta: GrantMeta }[] = [];
  let holder = owner;
  const fn = (allowTransfer: boolean, meta: GrantMeta): Promise<GrantRow> => {
    calls.push({ allowTransfer, meta });
    let outcome: string;
    if (holder === null) { holder = caller; outcome = "granted"; }
    else if (holder === caller) outcome = "already_owned";
    else if (!allowTransfer) outcome = "owned_elsewhere";
    else { holder = caller; outcome = "transferred"; }
    return Promise.resolve({ outcome, owned_items: outcome === "owned_elsewhere" ? [] : ["peacemaker"], coins: 0, equipped_gun: "standard", previous_owner: null });
  };
  return { fn, calls, holder: () => holder };
}

const env = paymentsEnvFrom(() => undefined);
const noFetch: FetchFn = () => Promise.reject(new Error("no network in this test"));

Deno.test("review2: account tokens (Apple = user id, Google = sha256 hex) and binding states", async () => {
  assertEquals(await expectedAccountToken("app_store", USER), USER.toLowerCase());
  const g = await expectedAccountToken("google_play", USER);
  assert(/^[0-9a-f]{64}$/.test(g), "google token is sha256 hex");
  assertEquals(g, await expectedAccountToken("google_play", USER.toLowerCase()), "case-insensitive user id");
  assertEquals(bindingState(ent({ accountToken: USER }), USER.toLowerCase()), "match");
  assertEquals(bindingState(ent({ accountToken: OTHER }), USER.toLowerCase()), "mismatch");
  assertEquals(bindingState(ent({ accountToken: null }), USER.toLowerCase()), "absent");
  assertEquals(bindingState(ent({ accountToken: "" }), USER.toLowerCase()), "absent");
  assertEquals(deviceProof(ent({ source: "server_api" })), false, "a lookup by transaction id proves nothing");
  assertEquals(deviceProof(ent({ source: "jws" })), true);
  assertEquals(deviceProof(ent({ source: "verify_receipt" })), true);
  assertEquals(deviceProof(ent({ source: "play_api" })), true);
  assertEquals(decide("match", false), { refuse: null, allowTransfer: true });
  assertEquals(decide("absent", false), { refuse: null, allowTransfer: false });
  assertEquals(decide("absent", true), { refuse: null, allowTransfer: true });
  assertEquals(decide("mismatch", true), { refuse: null, allowTransfer: true });
  assertEquals(decide("mismatch", false).refuse?.code, "account_mismatch");
});

Deno.test("review2: a bare transaction id cannot take another account's purchase", async () => {
  const req = parsePurchaseRequest({ store: "app_store", productId: PRODUCT, transactionId: "2000000812345678" });
  const g = fakeGrant(OTHER, USER);
  let err: unknown = null;
  try {
    await grantBound(ent({}), req, USER, env, noFetch, g.fn);
  } catch (e) { err = e; }
  assert(err instanceof PurchaseError && err.code === "owned_elsewhere", `expected owned_elsewhere, got ${String(err)}`);
  assertEquals(g.holder(), OTHER, "the owner keeps it");
  assertEquals(g.calls.map((c) => c.allowTransfer), [false]);
});

Deno.test("review2: bound to another account and no proof is refused before the database", async () => {
  const req = parsePurchaseRequest({ store: "app_store", productId: PRODUCT, transactionId: "2000000812345678" });
  const g = fakeGrant(null, USER);
  let err: unknown = null;
  try {
    await grantBound(ent({ accountToken: OTHER }), req, USER, env, noFetch, g.fn);
  } catch (e) { err = e; }
  assert(err instanceof PurchaseError && err.code === "account_mismatch" && err.status === 409, `got ${String(err)}`);
  assertEquals(g.calls.length, 0, "never reached pn_grant_entitlement");
});

Deno.test("review2: own purchases, new purchases and proven restores still work", async () => {
  const bare = parsePurchaseRequest({ store: "app_store", productId: PRODUCT, transactionId: "2000000812345678" });
  // bound to the caller: may move here from an old guest of the same player
  let g = fakeGrant(OTHER, USER);
  let r = await grantBound(ent({ accountToken: USER.toLowerCase() }), bare, USER, env, noFetch, g.fn);
  assertEquals(r.row.outcome, "transferred");
  assertEquals(r.meta.binding, "match");
  // unbound and new: granted
  g = fakeGrant(null, USER);
  r = await grantBound(ent({}), bare, USER, env, noFetch, g.fn);
  assertEquals(r.row.outcome, "granted");
  // a StoreKit 2 JWS is device proof: a reinstall on a new guest restores
  g = fakeGrant(OTHER, USER);
  r = await grantBound(ent({ source: "jws", accountToken: OTHER }), bare, USER, env, noFetch, g.fn);
  assertEquals(r.row.outcome, "transferred");
  // Google: the purchase token is the proof
  g = fakeGrant(OTHER, USER);
  const gReq = parsePurchaseRequest({ store: "google_play", productId: PRODUCT, purchaseToken: "tok_abcdefghijklmnop" });
  r = await grantBound(ent({ store: "google_play", source: "play_api", transactionId: "GPA.1" }), gReq, USER, env, noFetch, g.fn);
  assertEquals(r.row.outcome, "transferred");
});

Deno.test("review2: server-API lookup + the device's app receipt proves ownership (verifyReceipt, only when needed)", async () => {
  const receiptOk = {
    status: 0, environment: "Sandbox",
    receipt: { bundle_id: "com.pawnoon.pixel", in_app: [{ product_id: PRODUCT, transaction_id: "2000000899999999", original_transaction_id: "2000000812345678" }] },
  };
  const receiptOther = {
    status: 0, environment: "Sandbox",
    receipt: { bundle_id: "com.pawnoon.pixel", in_app: [{ product_id: PRODUCT, transaction_id: "2000000700000000", original_transaction_id: "2000000700000000" }] },
  };
  const unity = JSON.stringify({ Store: "AppleAppStore", TransactionID: "2000000899999999", Payload: "MIIbase64receipt" });
  const req = parsePurchaseRequest({ store: "app_store", productId: PRODUCT, receipt: unity, restore: true });
  const fetchWith = (body: unknown) => {
    let n = 0;
    const f: FetchFn = () => { n++; return Promise.resolve(new Response(JSON.stringify(body), { status: 200 })); };
    return { f, count: () => n };
  };
  // nobody else owns it: no verifyReceipt call at all
  let fx = fetchWith(receiptOk);
  let g = fakeGrant(null, USER);
  let r = await grantBound(ent({ latestTransactionId: "2000000899999999" }), req, USER, env, fx.f, g.fn);
  assertEquals(r.row.outcome, "granted");
  assertEquals(fx.count(), 0, "no extra store call in the normal flow");
  // owned elsewhere: the receipt proves it -> second grant with transfer allowed
  fx = fetchWith(receiptOk);
  g = fakeGrant(OTHER, USER);
  r = await grantBound(ent({ latestTransactionId: "2000000899999999" }), req, USER, env, fx.f, g.fn);
  assertEquals(r.row.outcome, "transferred");
  assertEquals(g.calls.map((c) => c.allowTransfer), [false, true]);
  assertEquals(fx.count(), 1);
  // a receipt that does not hold this purchase proves nothing
  fx = fetchWith(receiptOther);
  g = fakeGrant(OTHER, USER);
  let err: unknown = null;
  try { await grantBound(ent({ latestTransactionId: "2000000899999999" }), req, USER, env, fx.f, g.fn); } catch (e) { err = e; }
  assert(err instanceof PurchaseError && err.code === "owned_elsewhere", `got ${String(err)}`);
  assertEquals(g.holder(), OTHER);
  // the store being down is transient (the client keeps the purchase pending and retries)
  const down: FetchFn = () => Promise.resolve(new Response("busy", { status: 503 }));
  g = fakeGrant(OTHER, USER);
  err = null;
  try { await grantBound(ent({ latestTransactionId: "2000000899999999" }), req, USER, env, down, g.fn); } catch (e) { err = e; }
  assert(err instanceof PurchaseError && err.status === 503, `got ${String(err)}`);
});

Deno.test("review2: store answers carry the account token (Apple appAccountToken, Google obfuscatedExternalAccountId)", async () => {
  const { entitlementFromAppleTransaction, entitlementFromVerifyReceipt } = await import("./payments/apple.ts");
  const { entitlementFromProductPurchase } = await import("./payments/google.ts");
  const a = entitlementFromAppleTransaction({
    bundleId: "com.pawnoon.pixel", productId: PRODUCT, type: "Non-Consumable", environment: "Sandbox",
    transactionId: "1", originalTransactionId: "1", appAccountToken: USER.toLowerCase(),
  }, PRODUCT, "peacemaker", "com.pawnoon.pixel", "jws");
  assertEquals(a.accountToken, USER.toLowerCase());
  const v = entitlementFromVerifyReceipt({ status: 0, receipt: { bundle_id: "com.pawnoon.pixel", in_app: [
    { product_id: PRODUCT, transaction_id: "2", original_transaction_id: "2", app_account_token: USER } ] } },
    PRODUCT, "peacemaker", "com.pawnoon.pixel");
  assertEquals(v.accountToken, USER);
  const gp = await entitlementFromProductPurchase({ orderId: "GPA.1", purchaseState: 0, obfuscatedExternalAccountId: "abc" }, PRODUCT, "peacemaker", "tok");
  assertEquals(gp.accountToken, "abc");
  const none = await entitlementFromProductPurchase({ orderId: "GPA.2", purchaseState: 0 }, PRODUCT, "peacemaker", "tok");
  assertEquals(none.accountToken, null);
});

// ---------------------------------------------------------------- rate limits

function fakeRpc(answers: Record<string, unknown>, error: { message: string } | null = null) {
  const calls: { fn: string; args: Record<string, unknown> }[] = [];
  return {
    calls,
    rpc(fn: string, args: Record<string, unknown>) {
      calls.push({ fn, args });
      return Promise.resolve({ data: error ? null : answers[fn], error });
    },
  };
}

Deno.test("review2: rate limits: 429 when the window is full, let through when the database fails", async () => {
  const db = fakeRpc({ pn_rate_take: false });
  let err: unknown = null;
  try { await takeRate(db, USER, "room_join"); } catch (e) { err = e; }
  assert(err instanceof HttpError && err.status === 429 && err.code === "rate_limited", `got ${String(err)}`);
  assertEquals(db.calls[0].args, { p_user: USER, p_kind: "room_join", p_window_sec: RATE.room_join.windowSec, p_max: RATE.room_join.max });
  await takeRate(fakeRpc({ pn_rate_take: true }), USER, "profile_update");
  await takeRate(fakeRpc({}, { message: "down" }), USER, "profile_update");   // must not throw
  assertEquals(await rateExhausted(fakeRpc({ pn_rate_count: RATE.room_miss.max }), USER, "room_miss"), true);
  assertEquals(await rateExhausted(fakeRpc({ pn_rate_count: RATE.room_miss.max - 1 }), USER, "room_miss"), false);
  assertEquals(await rateExhausted(fakeRpc({}, { message: "down" }), USER, "room_miss"), false);
  const n = fakeRpc({});
  await noteRate(n, USER, "room_miss");
  assertEquals(n.calls[0].fn, "pn_rate_note");
});

Deno.test("review2: room-join limits leave room for a friend polling a whole room lifetime", () => {
  const pollsPerMinute = 60 / ROOM.pollSec;
  assert(RATE.room_join.max >= pollsPerMinute * 1.5, "polling every pollSec fits with slack");
  assert(RATE.room_miss.max <= 20, "guessing stays slow");
  // a code sweep: 26^4 codes / misses per 10 minutes -> years per account
  const sweepDays = (26 ** 4 / RATE.room_miss.max) * RATE.room_miss.windowSec / 86400;
  assert(sweepDays > 200, `sweeping every code takes ${sweepDays} days`);
});

// ---------------------------------------------------------------- character ids

Deno.test("review2: blackpaw is a legacy alias of pawtruth on the wire and in old snapshots", () => {
  assertEquals(normalizeCharacter("blackpaw"), "pawtruth");
  assertEquals(normalizeCharacter("pawtruth"), "pawtruth");
  assertEquals(normalizeCharacter("whiskers"), "whiskers");
  assertEquals(normalizeCharacter("tiger"), null);
  assertEquals(normalizeCharacter("constructor"), null, "no prototype keys");
  assertEquals(normalizeCharacter(7), null);
  const base = {
    version: 1, seed: 1, difficulty: "hard", loadout: { gun: "standard" },
    rounds: [{ roundNumber: 1, kind: "noshot", reactionNs: 0, cuePresentedNs: 5, inputNs: 0, countdownStartNs: 1, inputSource: "touch", frameQuantized: false }],
    device: { model: "x", os: "y", refreshHz: 60 }, clientBuild: "1", submittedAt: "2026-10-20T10:00:00Z",
  };
  assertEquals(parseRecording({ ...base, character: "blackpaw" }).character, "pawtruth");
  assertEquals(parseRecording({ ...base, character: "pawtruth" }).character, "pawtruth");
  let threw = false;
  try { parseRecording({ ...base, character: "tiger" }); } catch { threw = true; }
  assert(threw, "unknown character refused");
  const res = buildResponse("m", "casual", null, 1, "a", "b", { mmr: 1200, rd: 350 },
    { recordingId: "r", ownerId: null, isHouse: true, displayName: "Old Tom", character: "blackpaw", gun: "standard", mmr: 1400, attempts: [] });
  assertEquals(res.opponent.character, "pawtruth", "pre-0061 snapshots are normalised in the response");
});
