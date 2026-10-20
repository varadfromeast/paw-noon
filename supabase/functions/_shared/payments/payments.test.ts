// Purchase validation (SPEC_V2 §6, M7): receipt parsing, Apple JWS chain checks, App Store Server API,
// verifyReceipt fallback, Google Play API with a hand-built service-account JWT. Store answers come from the
// recorded fixtures in ./fixtures (see make_fixtures.py); keys are generated here at run time.
//   deno test --allow-read supabase/functions/_shared/
import { assert, assertEquals, readText } from "../testing.ts";
import { type AppleConfig, appStoreApiJwt, entitlementFromVerifyReceipt } from "./apple.ts";
import { ab, b64ToBytes, b64urlToBytes, bytesToB64url, utf8 } from "./der.ts";
import { clearGoogleTokenCache, type GoogleConfig } from "./google.ts";
import { APPLE_ROOT_CA_G3_SHA256, decodeJwsUnverified, JwsError, verifyAppleJws } from "./jws.ts";
import { parsePurchaseRequest, parseUnityReceipt } from "./receipt.ts";
import { type FetchFn, PurchaseError } from "./types.ts";
import { type PaymentsEnv, paymentsEnvFrom, validatePurchase } from "./validate.ts";

// deno-lint-ignore no-explicit-any
type Any = any;

const PRODUCT = "com.pawnoon.pixel.peacemaker";
const load = async (f: string): Promise<Any> => JSON.parse(await readText(new URL(`./fixtures/${f}`, import.meta.url)));
const J = await load("apple_jws.json");
const API = await load("apple_server_api.json");
const VR = await load("apple_verify_receipt.json");
const G = await load("google_play.json");
const NOW: number = J.now;

// ---------------------------------------------------------------- helpers

function pem(label: string, der: ArrayBuffer): string {
  const b64 = btoa(String.fromCharCode(...new Uint8Array(der)));
  return `-----BEGIN ${label}-----\n${b64.replace(/(.{64})/g, "$1\n")}\n-----END ${label}-----\n`;
}

async function ecKeyPair() {
  const k = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]) as CryptoKeyPair;
  return { pem: pem("PRIVATE KEY", await crypto.subtle.exportKey("pkcs8", k.privateKey)), pub: k.publicKey };
}

async function rsaKeyPair() {
  const k = await crypto.subtle.generateKey(
    { name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" },
    true, ["sign", "verify"],
  ) as CryptoKeyPair;
  return { pem: pem("PRIVATE KEY", await crypto.subtle.exportKey("pkcs8", k.privateKey)), pub: k.publicKey };
}

interface Call { url: string; init?: RequestInit }

/** A fetch that answers from a route table and records every call. */
function fakeFetch(routes: [RegExp, (url: string, init?: RequestInit) => Response | Promise<Response>][]): { fetch: FetchFn; calls: Call[] } {
  const calls: Call[] = [];
  const fetch: FetchFn = async (url, init) => {
    calls.push({ url, init });
    for (const [re, fn] of routes) if (re.test(url)) return await fn(url, init);
    return new Response("no route", { status: 599 });
  };
  return { fetch, calls };
}

const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });

async function rejects(p: Promise<unknown>, code: string, msg = ""): Promise<PurchaseError | JwsError> {
  try {
    await p;
  } catch (e) {
    const got = e instanceof PurchaseError ? e.code : e instanceof JwsError ? e.reason : String(e);
    assertEquals(got, code, msg);
    return e as PurchaseError | JwsError;
  }
  throw new Error(`${msg ? msg + ": " : ""}expected rejection ${code}`);
}

function throwsCode(fn: () => unknown, code: string, msg = ""): void {
  try {
    fn();
  } catch (e) {
    assertEquals(e instanceof PurchaseError ? e.code : String(e), code, msg);
    return;
  }
  throw new Error(`${msg ? msg + ": " : ""}expected ${code}`);
}

const apple = (over: Partial<AppleConfig> = {}): AppleConfig => ({ bundleId: "com.pawnoon.pixel", rootSha256: [J.rootSha256], ...over });
const env = (over: Partial<PaymentsEnv> = {}, a: Partial<AppleConfig> = {}, g: Partial<GoogleConfig> = {}): PaymentsEnv => ({
  apple: apple(a),
  google: { packageName: "com.pawnoon.pixel", ...g },
  allowSandbox: true,
  ...over,
});

async function verifyJwt(token: string, key: CryptoKey, alg: "ES256" | "RS256"): Promise<{ header: Any; claims: Any }> {
  const [h, p, s] = token.split(".");
  const params = alg === "ES256" ? { name: "ECDSA", hash: "SHA-256" } : { name: "RSASSA-PKCS1-v1_5" };
  assert(await crypto.subtle.verify(params, key, ab(b64urlToBytes(s)), ab(utf8(`${h}.${p}`))), `${alg} JWT signature verifies`);
  const dec = new TextDecoder();
  return { header: JSON.parse(dec.decode(b64urlToBytes(h))), claims: JSON.parse(dec.decode(b64urlToBytes(p))) };
}

// ---------------------------------------------------------------- JWS chain (Apple signed data)

Deno.test("payments: Apple JWS with a good chain verifies and yields the transaction", async () => {
  const p = await verifyAppleJws(J.valid, { rootSha256: [J.rootSha256], now: NOW });
  assertEquals(p.productId, PRODUCT);
  assertEquals(p.bundleId, "com.pawnoon.pixel");
  assertEquals(p.environment, "Sandbox");
});

Deno.test("payments: Apple JWS rejects tampering, foreign roots, short chains, missing OIDs, expiry", async () => {
  const o = { rootSha256: [J.rootSha256], now: NOW };
  await rejects(verifyAppleJws(J.tampered, o), "bad_signature", "payload swapped under a valid signature");
  await rejects(verifyAppleJws(J.untrustedRoot, o), "untrusted_root", "same-shaped chain from another root");
  await rejects(verifyAppleJws(J.shortChain, o), "bad_chain", "two certificates");
  await rejects(verifyAppleJws(J.noOid, o), "bad_chain", "leaf without Apple's OID");
  assertEquals((await verifyAppleJws(J.noOid, { ...o, requireAppleOids: false })).productId, PRODUCT, "OID check is the only failure");
  await rejects(verifyAppleJws(J.valid, { ...o, now: Date.UTC(2037, 0, 1) }), "cert_expired");
  await rejects(verifyAppleJws(J.valid, { now: NOW }), "untrusted_root", "the default pin is Apple Root CA - G3 only");
  await rejects(verifyAppleJws("a.b", o), "malformed");
  const { header } = decodeJwsUnverified(J.valid);
  const noneAlg = `${bytesToB64url(utf8(JSON.stringify({ ...header, alg: "none" })))}.${J.valid.split(".")[1]}.`;
  await rejects(verifyAppleJws(noneAlg, o), "bad_alg");
  assertEquals(APPLE_ROOT_CA_G3_SHA256.length, 64);
});

Deno.test("payments: signedTransaction path maps Apple fields to an entitlement", async () => {
  const req = parsePurchaseRequest({ store: "app_store", productId: PRODUCT, signedTransaction: J.valid });
  const e = await validatePurchase(req, env(), fakeFetch([]).fetch, NOW);
  assertEquals(e.source, "jws");
  assertEquals(e.item, "peacemaker");
  assertEquals(e.transactionId, "2000000812345678");
  assertEquals(e.environment, "sandbox");
  assertEquals(e.status, "valid");
  assertEquals(e.purchasedAt, new Date(1760950000000).toISOString());
  assert(!("price" in e.raw), "raw keeps only support fields");

  const restored = await validatePurchase(parsePurchaseRequest({ store: "app_store", productId: PRODUCT, signedTransaction: J.restored, restore: true }), env(), fakeFetch([]).fetch, NOW);
  assertEquals(restored.transactionId, "2000000812345678", "a restore keys on the original transaction (idempotent)");
  assertEquals(restored.latestTransactionId, "2000000899999999");

  const run = (jws: string) => validatePurchase(parsePurchaseRequest({ store: "app_store", productId: PRODUCT, signedTransaction: jws }), env(), fakeFetch([]).fetch, NOW);
  assertEquals((await run(J.revoked)).status, "refunded");
  assertEquals((await run(J.production)).environment, "production");
  await rejects(run(J.otherBundle), "wrong_app");
  await rejects(run(J.otherProduct), "product_mismatch");
  await rejects(run(J.xcode), "invalid_receipt", "Xcode-signed StoreKit testing is never accepted");
  await rejects(run(J.tampered), "invalid_receipt");
  await rejects(validatePurchase(parsePurchaseRequest({ store: "app_store", productId: PRODUCT, signedTransaction: J.valid }), env({ allowSandbox: false }), fakeFetch([]).fetch, NOW), "sandbox_not_allowed");
  await rejects(validatePurchase(parsePurchaseRequest({ store: "app_store", productId: PRODUCT, signedTransaction: J.valid, transactionId: "1234" }), env(), fakeFetch([]).fetch, NOW),
    "invalid_receipt", "the JWS must be the transaction the client named");
});

// ---------------------------------------------------------------- App Store Server API v2

Deno.test("payments: App Store Server API: ES256 JWT, production 404 then sandbox, verified answer", async () => {
  const key = await ecKeyPair();
  const a = { issuerId: API.issuerId, keyId: API.keyId, privateKeyPem: key.pem };
  const f = fakeFetch([
    [/^https:\/\/api\.storekit\.itunes\.apple\.com\/inApps\/v1\/transactions\/2000000812345678$/, () => new Response(JSON.stringify(API.notFoundResponse), { status: 404 })],
    [/^https:\/\/api\.storekit-sandbox\.itunes\.apple\.com\/inApps\/v1\/transactions\/2000000812345678$/, () => ok(API.transactionResponse)],
  ]);
  const req = parsePurchaseRequest({ store: "app_store", productId: PRODUCT, receipt: VR.unity });
  assertEquals(req.transactionId, "2000000812345678");
  const e = await validatePurchase(req, env({}, a), f.fetch, NOW);
  assertEquals(e.source, "server_api");
  assertEquals(e.status, "valid");
  assertEquals(f.calls.length, 2);
  const auth = (f.calls[0].init?.headers as Record<string, string>).Authorization;
  const { header, claims } = await verifyJwt(auth.replace(/^Bearer /, ""), key.pub, "ES256");
  assertEquals(header, { alg: "ES256", kid: "TESTKEY123", typ: "JWT" });
  assertEquals(claims.iss, API.issuerId);
  assertEquals(claims.aud, "appstoreconnect-v1");
  assertEquals(claims.bid, "com.pawnoon.pixel");
  assert(claims.exp - claims.iat <= 3600, "Apple allows at most 60 minutes");

  // literal "\n" in the secret (how `supabase secrets set` often stores a .p8) is accepted
  const escaped = await appStoreApiJwt({ ...apple(), ...a, privateKeyPem: key.pem.replace(/\n/g, "\\n") }, 1761000000);
  await verifyJwt(escaped, key.pub, "ES256");

  const nowhere = fakeFetch([[/storekit/, () => new Response("{}", { status: 404 })]]);
  await rejects(validatePurchase(req, env({}, a), nowhere.fetch, NOW), "not_found");
  const refused = fakeFetch([[/storekit/, () => new Response("{}", { status: 401 })]]);
  await rejects(validatePurchase(req, env({}, a), refused.fetch, NOW), "store_not_configured");
  const down = fakeFetch([[/storekit/, () => new Response("{}", { status: 503 })]]);
  await rejects(validatePurchase(req, env({}, a), down.fetch, NOW), "store_unavailable");
  await rejects(validatePurchase(req, env({}, { ...a, privateKeyPem: "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----" }), f.fetch, NOW), "store_not_configured");
});

// ---------------------------------------------------------------- verifyReceipt fallback

Deno.test("payments: verifyReceipt fallback: 21007 goes to the sandbox, picks the product, sends the secret", async () => {
  const f = fakeFetch([
    [/^https:\/\/buy\.itunes\.apple\.com\/verifyReceipt$/, () => ok(VR.production21007)],
    [/^https:\/\/sandbox\.itunes\.apple\.com\/verifyReceipt$/, () => ok(VR.sandboxOk)],
  ]);
  const req = parsePurchaseRequest({ store: "app_store", productId: PRODUCT, receipt: VR.unity });
  const e = await validatePurchase(req, env({}, { sharedSecret: "s3cret" }), f.fetch, NOW);
  assertEquals(e.source, "verify_receipt");
  assertEquals(e.environment, "sandbox");
  assertEquals(e.transactionId, "2000000812345678", "the Peacemaker entry, not the other product's");
  const sent = JSON.parse(String(f.calls[0].init?.body));
  assertEquals(sent.password, "s3cret");
  assert(typeof sent["receipt-data"] === "string" && sent["receipt-data"].startsWith("MIIT"), "the Unity Payload is the app receipt");

  const one = (body: Any) => fakeFetch([[/verifyReceipt/, () => ok(body)]]).fetch;
  assertEquals((await validatePurchase(req, env(), one(VR.productionRefunded), NOW)).status, "refunded");
  await rejects(validatePurchase(req, env(), one(VR.otherBundle), NOW), "wrong_app");
  await rejects(validatePurchase(req, env(), one(VR.noProduct), NOW), "not_in_receipt");
  await rejects(validatePurchase(req, env(), one(VR.malformed), NOW), "invalid_receipt");
  await rejects(validatePurchase(req, env(), one(VR.badSecret), NOW), "store_not_configured");
  const throwing: FetchFn = () => Promise.reject(new TypeError("network down"));
  await rejects(validatePurchase(req, env(), throwing, NOW), "store_unavailable");
  // only a transaction id and no API key: nothing to check it with
  await rejects(validatePurchase(parsePurchaseRequest({ store: "app_store", productId: PRODUCT, transactionId: "2000000812345678" }), env(), one(VR.sandboxOk), NOW), "store_not_configured");
  // the mapping on its own: the hint wins when several entries match
  const twice = { ...VR.sandboxOk, receipt: { ...VR.sandboxOk.receipt, in_app: [...VR.sandboxOk.receipt.in_app, { ...VR.sandboxOk.receipt.in_app[1], transaction_id: "2000000877777777" }] } };
  assertEquals(entitlementFromVerifyReceipt(twice, PRODUCT, "peacemaker", "com.pawnoon.pixel", "2000000877777777").latestTransactionId, "2000000877777777");
});

// ---------------------------------------------------------------- Unity receipts / request parsing

Deno.test("payments: Unity IAP receipts and request shapes parse (or fail) as documented", () => {
  const a = parseUnityReceipt(VR.unity);
  assertEquals(a.store, "AppleAppStore");
  assertEquals(a.transactionId, "2000000812345678");
  const g = parsePurchaseRequest({ store: "google_play", productId: PRODUCT, receipt: G.unity });
  assertEquals(g.purchaseToken, G.purchaseToken);
  assertEquals(g.packageName, "com.pawnoon.pixel");
  assertEquals(g.item, "peacemaker");
  assertEquals(g.restore, false);
  assertEquals(parsePurchaseRequest({ store: "google_play", productId: PRODUCT, purchaseToken: "abcdefgh12", restore: true }).restore, true);

  throwsCode(() => parsePurchaseRequest({ store: "google_play", productId: PRODUCT, receipt: G.unityFake }), "fake_store");
  throwsCode(() => parsePurchaseRequest({ store: "app_store", productId: PRODUCT, receipt: G.unity }), "bad_receipt", "Google receipt sent as app_store");
  throwsCode(() => parsePurchaseRequest({ store: "google_play", productId: PRODUCT, receipt: "{not json" }), "bad_receipt");
  throwsCode(() => parsePurchaseRequest({ store: "steam", productId: PRODUCT, receipt: G.unity }), "bad_request");
  throwsCode(() => parsePurchaseRequest({ store: "google_play", productId: "com.pawnoon.pixel.golden", receipt: G.unity }), "unknown_product");
  throwsCode(() => parsePurchaseRequest({ store: "google_play", productId: PRODUCT }), "bad_request", "no proof");
  throwsCode(() => parsePurchaseRequest({ store: "app_store", productId: PRODUCT }), "bad_request", "no proof");
  throwsCode(() => parsePurchaseRequest({ store: "app_store", productId: PRODUCT, transactionId: "12; drop" }), "bad_request");
  throwsCode(() => parsePurchaseRequest({ store: "app_store", productId: PRODUCT, signedTransaction: 42 }), "bad_request");
  throwsCode(() => parsePurchaseRequest({ store: "app_store", productId: PRODUCT, signedTransaction: J.valid, restore: "yes" }), "bad_request");
  const wrongProduct = JSON.stringify({ ...JSON.parse(G.unity), Payload: JSON.stringify({ json: JSON.stringify({ productId: "com.pawnoon.pixel.other", purchaseToken: "abcdefghij" }) }) });
  throwsCode(() => parsePurchaseRequest({ store: "google_play", productId: PRODUCT, receipt: wrongProduct }), "product_mismatch");
  assertEquals(b64ToBytes("AQID").length, 3);
});

// ---------------------------------------------------------------- Google Play

Deno.test("payments: Google: service-account JWT, products.get, acknowledge, states", async () => {
  clearGoogleTokenCache();
  const sa = await rsaKeyPair();
  const saJson = JSON.stringify({ type: "service_account", client_email: G.clientEmail, private_key: sa.pem, token_uri: "https://oauth2.googleapis.com/token" });
  let purchase: Any = G.testPurchase;
  const f = fakeFetch([
    [/^https:\/\/oauth2\.googleapis\.com\/token$/, () => ok(G.tokenResponse)],
    [/:acknowledge$/, () => new Response(null, { status: 204 })],
    [/^https:\/\/androidpublisher\.googleapis\.com\/androidpublisher\/v3\/applications\/com\.pawnoon\.pixel\/purchases\/products\/com\.pawnoon\.pixel\.peacemaker\/tokens\//, () => ok(purchase)],
  ]);
  const e1 = env({}, {}, { serviceAccountJson: saJson });
  const req = parsePurchaseRequest({ store: "google_play", productId: PRODUCT, receipt: G.unity });
  const e = await validatePurchase(req, e1, f.fetch, NOW);
  assertEquals(e.status, "valid");
  assertEquals(e.environment, "sandbox", "purchaseType 0 = license tester");
  assertEquals(e.transactionId, "GPA.3301-1234-5678-90123");
  assertEquals(e.needsAcknowledge, false, "acknowledged by the function");
  assertEquals(f.calls.map((c) => c.url.replace(/tokens\/.*/, "tokens/<t>")), [
    "https://oauth2.googleapis.com/token",
    "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/com.pawnoon.pixel/purchases/products/com.pawnoon.pixel.peacemaker/tokens/<t>",
    "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/com.pawnoon.pixel/purchases/products/com.pawnoon.pixel.peacemaker/tokens/<t>",
  ]);
  assert(f.calls[2].url.endsWith(":acknowledge"));
  assertEquals((f.calls[1].init?.headers as Record<string, string>).Authorization, "Bearer ya29.TEST-access-token");

  // the OAuth assertion is an RS256 JWT for the androidpublisher scope, signed by the service account
  const form = new URLSearchParams(String(f.calls[0].init?.body));
  assertEquals(form.get("grant_type"), "urn:ietf:params:oauth:grant-type:jwt-bearer");
  const { header, claims } = await verifyJwt(form.get("assertion")!, sa.pub, "RS256");
  assertEquals(header, { alg: "RS256", typ: "JWT" });
  assertEquals(claims.iss, G.clientEmail);
  assertEquals(claims.scope, "https://www.googleapis.com/auth/androidpublisher");
  assertEquals(claims.aud, "https://oauth2.googleapis.com/token");
  assertEquals(claims.exp - claims.iat, 3600);

  // warm instance: the access token is reused
  f.calls.length = 0;
  purchase = G.realPurchaseAcked;
  const real = await validatePurchase(req, e1, f.fetch, NOW);
  assertEquals(real.environment, "production");
  assertEquals(f.calls.length, 1, "no new token, no acknowledge for an acknowledged purchase");

  purchase = G.pending;
  const pend = await validatePurchase(req, e1, f.fetch, NOW);
  assertEquals(pend.status, "pending");
  assert(pend.transactionId.startsWith("token:") && pend.transactionId.length === 70, "no order id yet: keyed by the token hash");
  purchase = G.canceled;
  assertEquals((await validatePurchase(req, e1, f.fetch, NOW)).status, "refunded");

  await rejects(validatePurchase(parsePurchaseRequest({ store: "google_play", productId: PRODUCT, receipt: G.unityOtherApp }), e1, f.fetch, NOW), "wrong_app");
  const gone = fakeFetch([[/token$/, () => ok(G.tokenResponse)], [/androidpublisher/, () => new Response("{}", { status: 404 })]]);
  await rejects(validatePurchase(req, e1, gone.fetch, NOW), "invalid_receipt");
  const forbidden = fakeFetch([[/token$/, () => ok(G.tokenResponse)], [/androidpublisher/, () => new Response("{}", { status: 403 })]]);
  await rejects(validatePurchase(req, e1, forbidden.fetch, NOW), "store_not_configured");
  clearGoogleTokenCache();
  const badSa = fakeFetch([[/token$/, () => new Response('{"error":"invalid_grant"}', { status: 400 })]]);
  await rejects(validatePurchase(req, e1, badSa.fetch, NOW), "store_not_configured");
  await rejects(validatePurchase(req, env(), f.fetch, NOW), "store_not_configured", "no service account configured");
  await rejects(validatePurchase(req, env({}, {}, { serviceAccountJson: "{nope" }), f.fetch, NOW), "store_not_configured");
  clearGoogleTokenCache();
});

Deno.test("payments: env defaults (bundle / package com.pawnoon.pixel, sandbox allowed, Google acknowledge on)", () => {
  const e = paymentsEnvFrom(() => undefined);
  assertEquals(e.apple.bundleId, "com.pawnoon.pixel");
  assertEquals(e.google.packageName, "com.pawnoon.pixel");
  assertEquals(e.allowSandbox, true);
  assertEquals(e.google.acknowledge, true);
  const off = paymentsEnvFrom((k) => ({ PN_IAP_ALLOW_SANDBOX: "false", PN_GOOGLE_ACKNOWLEDGE: "0", APPLE_KEY_ID: "K" } as Record<string, string>)[k]);
  assertEquals(off.allowSandbox, false);
  assertEquals(off.google.acknowledge, false);
  assertEquals(off.apple.keyId, "K");
});
