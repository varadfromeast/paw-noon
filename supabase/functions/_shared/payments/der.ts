// Minimal DER / X.509 reader for verifying Apple's JWS certificate chains with WebCrypto only (no npm).
// Reads exactly what the chain check needs: tbsCertificate bytes, signature algorithm, signature,
// SubjectPublicKeyInfo (+ its EC curve), validity and the extension OIDs. Anything malformed throws
// DerError; callers turn that into "invalid".

export class DerError extends Error {}

export interface DerNode {
  tag: number;
  /** Offset of the tag byte in the source buffer. */
  start: number;
  /** Offset of the first content byte. */
  contentStart: number;
  /** Offset one past the last content byte. */
  end: number;
}

/** Reads one TLV at `pos`. Only definite lengths (DER), at most 4 length bytes. */
export function readNode(buf: Uint8Array, pos: number): DerNode {
  if (pos + 2 > buf.length) throw new DerError("truncated tag");
  const tag = buf[pos];
  let len = buf[pos + 1];
  let p = pos + 2;
  if (len & 0x80) {
    const n = len & 0x7f;
    if (n === 0 || n > 4) throw new DerError("unsupported length");
    if (p + n > buf.length) throw new DerError("truncated length");
    len = 0;
    for (let i = 0; i < n; i++) len = len * 256 + buf[p + i];
    p += n;
  }
  const end = p + len;
  if (end > buf.length) throw new DerError("truncated content");
  return { tag, start: pos, contentStart: p, end };
}

/** Children of a constructed node. */
export function children(buf: Uint8Array, node: DerNode): DerNode[] {
  const out: DerNode[] = [];
  let p = node.contentStart;
  while (p < node.end) {
    const c = readNode(buf, p);
    out.push(c);
    p = c.end;
  }
  if (p !== node.end) throw new DerError("children overrun");
  return out;
}

export function content(buf: Uint8Array, n: DerNode): Uint8Array {
  return buf.subarray(n.contentStart, n.end);
}

export function whole(buf: Uint8Array, n: DerNode): Uint8Array {
  return buf.subarray(n.start, n.end);
}

/** OBJECT IDENTIFIER content -> dotted string. */
export function oid(bytes: Uint8Array): string {
  if (bytes.length === 0) throw new DerError("empty oid");
  const parts: number[] = [];
  const first = bytes[0];
  parts.push(first < 80 ? Math.floor(first / 40) : 2, first < 80 ? first % 40 : first - 80);
  let v = 0;
  for (let i = 1; i < bytes.length; i++) {
    v = v * 128 + (bytes[i] & 0x7f);
    if (!(bytes[i] & 0x80)) {
      parts.push(v);
      v = 0;
    }
  }
  return parts.join(".");
}

function expect(n: DerNode, tag: number, what: string): DerNode {
  if (n.tag !== tag) throw new DerError(`${what}: tag 0x${n.tag.toString(16)}`);
  return n;
}

/** UTCTime / GeneralizedTime -> epoch ms. */
function time(buf: Uint8Array, n: DerNode): number {
  const s = new TextDecoder().decode(content(buf, n));
  let m: RegExpExecArray | null;
  if (n.tag === 0x17) {
    m = /^(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})Z$/.exec(s);
    if (!m) throw new DerError("bad UTCTime");
    const yy = Number(m[1]);
    return Date.UTC(yy >= 50 ? 1900 + yy : 2000 + yy, Number(m[2]) - 1, Number(m[3]), Number(m[4]), Number(m[5]), Number(m[6]));
  }
  if (n.tag === 0x18) {
    m = /^(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})Z$/.exec(s);
    if (!m) throw new DerError("bad GeneralizedTime");
    return Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3]), Number(m[4]), Number(m[5]), Number(m[6]));
  }
  throw new DerError("bad time tag");
}

export const OID = {
  ecPublicKey: "1.2.840.10045.2.1",
  p256: "1.2.840.10045.3.1.7",
  p384: "1.3.132.0.34",
  ecdsaSha256: "1.2.840.10045.4.3.2",
  ecdsaSha384: "1.2.840.10045.4.3.3",
  basicConstraints: "2.5.29.19",
  /** Apple: leaf certificates that sign App Store data. */
  appleLeaf: "1.2.840.113635.100.6.11.1",
  /** Apple: Apple Worldwide Developer Relations intermediate CA. */
  appleIntermediate: "1.2.840.113635.100.6.2.1",
} as const;

export interface Certificate {
  der: Uint8Array;
  tbs: Uint8Array;
  sigAlg: string;
  /** DER-encoded ECDSA-Sig-Value (SEQUENCE { r, s }). */
  signature: Uint8Array;
  spki: Uint8Array;
  keyAlg: string;
  /** EC curve OID of the public key, "" for non-EC keys. */
  curve: string;
  notBefore: number;
  notAfter: number;
  extensions: string[];
  isCa: boolean;
}

/** Parses an X.509 v3 certificate (DER). */
export function parseCertificate(der: Uint8Array): Certificate {
  const root = expect(readNode(der, 0), 0x30, "certificate");
  if (root.end !== der.length) throw new DerError("trailing bytes");
  const [tbsN, algN, sigN] = children(der, root);
  if (!tbsN || !algN || !sigN) throw new DerError("certificate fields");
  expect(tbsN, 0x30, "tbs");
  const algSeq = children(der, expect(algN, 0x30, "sigAlg"));
  const sigAlg = oid(content(der, expect(algSeq[0], 0x06, "sigAlg oid")));
  const bits = content(der, expect(sigN, 0x03, "signature"));
  if (bits.length < 2 || bits[0] !== 0) throw new DerError("signature bit string");

  const tbs = children(der, tbsN);
  let i = 0;
  if (tbs[i] && tbs[i].tag === 0xa0) i++; // [0] version
  i++; // serialNumber
  i++; // signature algorithm (repeated)
  i++; // issuer
  const validity = children(der, expect(tbs[i++], 0x30, "validity"));
  i++; // subject
  const spkiN = expect(tbs[i++], 0x30, "spki");
  const spkiSeq = children(der, spkiN);
  const keyAlgSeq = children(der, expect(spkiSeq[0], 0x30, "spki alg"));
  const keyAlg = oid(content(der, expect(keyAlgSeq[0], 0x06, "key oid")));
  const curve = keyAlg === OID.ecPublicKey && keyAlgSeq[1] && keyAlgSeq[1].tag === 0x06 ? oid(content(der, keyAlgSeq[1])) : "";

  const extensions: string[] = [];
  let isCa = false;
  for (; i < tbs.length; i++) {
    if (tbs[i].tag !== 0xa3) continue; // [3] extensions
    const extSeq = children(der, children(der, tbs[i])[0]);
    for (const ext of extSeq) {
      const parts = children(der, ext);
      const id = oid(content(der, expect(parts[0], 0x06, "ext oid")));
      extensions.push(id);
      if (id === OID.basicConstraints) {
        const value = parts[parts.length - 1];
        const inner = readNode(content(der, value), 0);
        const bc = children(content(der, value), inner);
        isCa = bc.length > 0 && bc[0].tag === 0x01 && content(content(der, value), bc[0])[0] !== 0;
      }
    }
  }
  return {
    der,
    tbs: whole(der, tbsN),
    sigAlg,
    signature: bits.subarray(1),
    spki: whole(der, spkiN),
    keyAlg,
    curve,
    notBefore: time(der, validity[0]),
    notAfter: time(der, validity[1]),
    extensions,
    isCa,
  };
}

/** ECDSA-Sig-Value (DER) -> raw r || s, each left-padded to `size` bytes (WebCrypto's format). */
export function ecdsaDerToRaw(sig: Uint8Array, size: number): Uint8Array {
  const seq = expect(readNode(sig, 0), 0x30, "ecdsa sig");
  const [r, s] = children(sig, seq);
  const out = new Uint8Array(size * 2);
  const put = (n: DerNode, off: number) => {
    let b = content(sig, expect(n, 0x02, "ecdsa int"));
    while (b.length > 0 && b[0] === 0) b = b.subarray(1);
    if (b.length > size) throw new DerError("ecdsa int too long");
    out.set(b, off + size - b.length);
  };
  put(r, 0);
  put(s, size);
  return out;
}

// ---------------------------------------------------------------- encodings

export function b64ToBytes(b64: string): Uint8Array {
  const clean = b64.replace(/\s+/g, "");
  if (!/^[A-Za-z0-9+/]*={0,2}$/.test(clean)) throw new DerError("bad base64");
  const bin = atob(clean);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

export function b64urlToBytes(s: string): Uint8Array {
  if (!/^[A-Za-z0-9_-]*$/.test(s)) throw new DerError("bad base64url");
  const b64 = s.replace(/-/g, "+").replace(/_/g, "/");
  return b64ToBytes(b64 + "===".slice((b64.length + 3) % 4));
}

export function bytesToB64url(b: Uint8Array): string {
  let bin = "";
  for (let i = 0; i < b.length; i++) bin += String.fromCharCode(b[i]);
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** A standalone ArrayBuffer copy (WebCrypto's BufferSource; avoids SharedArrayBuffer typing issues). */
export function ab(u: Uint8Array): ArrayBuffer {
  return u.slice().buffer as ArrayBuffer;
}

export function utf8(s: string): Uint8Array {
  return new TextEncoder().encode(s);
}

export async function sha256Hex(data: Uint8Array): Promise<string> {
  const d = new Uint8Array(await crypto.subtle.digest("SHA-256", ab(data)));
  return Array.from(d, (x) => x.toString(16).padStart(2, "0")).join("");
}

/** PEM (any label) -> DER bytes of the first block. */
export function pemToDer(pem: string): Uint8Array {
  const m = /-----BEGIN [^-]+-----([\s\S]+?)-----END [^-]+-----/.exec(pem);
  if (!m) throw new DerError("no PEM block");
  return b64ToBytes(m[1]);
}
