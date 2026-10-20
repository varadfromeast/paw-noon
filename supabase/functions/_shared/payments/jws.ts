// Verifies App Store signed data (StoreKit 2 / App Store Server API JWS: alg ES256 with an x5c chain
// leaf -> Apple WWDR intermediate -> Apple Root CA - G3) with WebCrypto only.
//
// Checks, in the order Apple's own app-store-server-library does them:
//   1. header alg = ES256 and an x5c chain of exactly three certificates;
//   2. the last certificate is the pinned root (SHA-256 fingerprint of its DER);
//   3. each certificate is signed by the next one, and all three are valid at `now`;
//   4. the intermediate is a CA carrying Apple's WWDR OID, the leaf carries Apple's App Store signing OID;
//   5. the JWS signature verifies with the leaf key.
import {
  ab,
  b64ToBytes,
  b64urlToBytes,
  type Certificate,
  DerError,
  ecdsaDerToRaw,
  OID,
  parseCertificate,
  sha256Hex,
  utf8,
} from "./der.ts";

/** SHA-256 of Apple Root CA - G3 (https://www.apple.com/certificateauthority/AppleRootCA-G3.cer). */
export const APPLE_ROOT_CA_G3_SHA256 = "63343abfb89a6a03ebb57e9b3f5fa7be7c4f5c756f3017b3a8c488c3653e9179";

export class JwsError extends Error {
  readonly reason: string;
  constructor(reason: string, message: string) {
    super(message);
    this.reason = reason;
  }
}

export interface JwsVerifyOptions {
  /** Accepted root fingerprints (lower-case hex SHA-256). Default: Apple Root CA - G3 only. */
  rootSha256?: string[];
  /** Verification time (epoch ms). Default: Date.now(). */
  now?: number;
  /** Require Apple's leaf / intermediate OIDs (default true; test chains may omit them). */
  requireAppleOids?: boolean;
}

function curveOf(c: Certificate): { name: string; size: number } {
  if (c.keyAlg !== OID.ecPublicKey) throw new JwsError("bad_chain", "certificate key is not EC");
  if (c.curve === OID.p256) return { name: "P-256", size: 32 };
  if (c.curve === OID.p384) return { name: "P-384", size: 48 };
  throw new JwsError("bad_chain", "unsupported curve");
}

function hashOf(sigAlg: string): string {
  if (sigAlg === OID.ecdsaSha256) return "SHA-256";
  if (sigAlg === OID.ecdsaSha384) return "SHA-384";
  throw new JwsError("bad_chain", "unsupported certificate signature algorithm");
}

async function importEcKey(c: Certificate): Promise<{ key: CryptoKey; size: number }> {
  const curve = curveOf(c);
  const key = await crypto.subtle.importKey("spki", ab(c.spki), { name: "ECDSA", namedCurve: curve.name }, false, ["verify"]);
  return { key, size: curve.size };
}

/** True when `child` carries a valid signature by `issuer`'s key. */
async function signedBy(child: Certificate, issuer: Certificate): Promise<boolean> {
  const { key, size } = await importEcKey(issuer);
  let raw: Uint8Array;
  try {
    raw = ecdsaDerToRaw(child.signature, size);
  } catch {
    return false;
  }
  return await crypto.subtle.verify({ name: "ECDSA", hash: hashOf(child.sigAlg) }, key, ab(raw), ab(child.tbs));
}

/** Decodes a compact JWS without verifying it (header + payload), for diagnostics and tests. */
export function decodeJwsUnverified(token: string): { header: Record<string, unknown>; payload: Record<string, unknown> } {
  const parts = token.split(".");
  if (parts.length !== 3) throw new JwsError("malformed", "not a compact JWS");
  try {
    const dec = new TextDecoder();
    return {
      header: JSON.parse(dec.decode(b64urlToBytes(parts[0]))),
      payload: JSON.parse(dec.decode(b64urlToBytes(parts[1]))),
    };
  } catch {
    throw new JwsError("malformed", "JWS header or payload is not base64url JSON");
  }
}

/** Verifies an App Store JWS and returns its payload. Throws JwsError(reason) when anything is off. */
export async function verifyAppleJws(token: string, opts: JwsVerifyOptions = {}): Promise<Record<string, unknown>> {
  const roots = (opts.rootSha256 ?? [APPLE_ROOT_CA_G3_SHA256]).map((s) => s.toLowerCase().replace(/:/g, ""));
  const now = opts.now ?? Date.now();
  const requireOids = opts.requireAppleOids ?? true;
  const { header, payload } = decodeJwsUnverified(token);
  if (header.alg !== "ES256") throw new JwsError("bad_alg", "JWS alg must be ES256");
  const x5c = header.x5c;
  if (!Array.isArray(x5c) || x5c.length !== 3 || !x5c.every((c) => typeof c === "string")) {
    throw new JwsError("bad_chain", "x5c must hold three certificates");
  }
  let certs: Certificate[];
  try {
    certs = (x5c as string[]).map((c) => parseCertificate(b64ToBytes(c)));
  } catch (e) {
    throw new JwsError("bad_chain", `unreadable certificate: ${e instanceof DerError ? e.message : String(e)}`);
  }
  const [leaf, intermediate, root] = certs;
  if (!roots.includes(await sha256Hex(root.der))) throw new JwsError("untrusted_root", "chain does not end in the pinned root");
  for (const c of certs) {
    if (now < c.notBefore || now > c.notAfter) throw new JwsError("cert_expired", "a certificate is outside its validity");
  }
  if (!intermediate.isCa) throw new JwsError("bad_chain", "intermediate is not a CA");
  if (requireOids) {
    if (!intermediate.extensions.includes(OID.appleIntermediate)) throw new JwsError("bad_chain", "intermediate lacks Apple's WWDR OID");
    if (!leaf.extensions.includes(OID.appleLeaf)) throw new JwsError("bad_chain", "leaf lacks Apple's App Store OID");
  }
  if (!(await signedBy(root, root))) throw new JwsError("bad_chain", "root is not self-signed");
  if (!(await signedBy(intermediate, root))) throw new JwsError("bad_chain", "intermediate not signed by the root");
  if (!(await signedBy(leaf, intermediate))) throw new JwsError("bad_chain", "leaf not signed by the intermediate");

  const parts = token.split(".");
  const { key, size } = await importEcKey(leaf);
  if (size !== 32) throw new JwsError("bad_chain", "ES256 needs a P-256 leaf");
  const sig = b64urlToBytes(parts[2]);
  if (sig.length !== 64) throw new JwsError("bad_signature", "ES256 signature must be 64 bytes");
  const ok = await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, ab(sig), ab(utf8(`${parts[0]}.${parts[1]}`)));
  if (!ok) throw new JwsError("bad_signature", "JWS signature does not verify");
  return payload;
}
