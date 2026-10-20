#!/usr/bin/env python3
"""Regenerates the purchase-validation test fixtures (TEST MATERIAL ONLY, never real keys).

The App Store and Play answers are shaped exactly like the real ones (field names and types from Apple's
JWSTransactionDecodedPayload / verifyReceipt docs and Google's ProductPurchase resource), but a test CA
signs them, because a real Apple-signed transaction cannot be minted here. The chain mirrors Apple's:
P-384 self-signed root -> P-384 intermediate CA (Apple WWDR OID 1.2.840.113635.100.6.2.1) -> P-256 leaf
(App Store OID 1.2.840.113635.100.6.11.1), certificates signed ecdsa-with-SHA384, JWS signed ES256.
payments.test.ts pins this root's SHA-256 instead of Apple Root CA - G3.

    python3 supabase/functions/_shared/payments/fixtures/make_fixtures.py   (needs `cryptography`)
"""
import base64, datetime, hashlib, json, os
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils
from cryptography.x509.oid import NameOID

HERE = os.path.dirname(os.path.abspath(__file__))
BUNDLE = "com.pawnoon.pixel"
PRODUCT = "com.pawnoon.pixel.peacemaker"
NB = datetime.datetime(2024, 1, 1, tzinfo=datetime.timezone.utc)
NA = datetime.datetime(2036, 1, 1, tzinfo=datetime.timezone.utc)


def name(cn):
    return x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, cn), x509.NameAttribute(NameOID.ORGANIZATION_NAME, "Paw Noon TEST")])


def cert(subject_key, subject, issuer_key, issuer, ca, extra_oid=None, nb=NB, na=NA):
    b = (x509.CertificateBuilder().subject_name(subject).issuer_name(issuer).public_key(subject_key.public_key())
         .serial_number(x509.random_serial_number()).not_valid_before(nb).not_valid_after(na)
         .add_extension(x509.BasicConstraints(ca=ca, path_length=None), critical=True))
    if extra_oid:
        b = b.add_extension(x509.UnrecognizedExtension(x509.ObjectIdentifier(extra_oid), b"\x05\x00"), critical=False)
    return b.sign(issuer_key, hashes.SHA384())


def der_b64(c):
    return base64.b64encode(c.public_bytes(serialization.Encoding.DER)).decode()


def b64url(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def jws(payload, key, x5c):
    header = {"alg": "ES256", "x5c": x5c}
    signing = b64url(json.dumps(header, separators=(",", ":")).encode()) + "." + b64url(json.dumps(payload, separators=(",", ":")).encode())
    der = key.sign(signing.encode(), ec.ECDSA(hashes.SHA256()))
    r, s = utils.decode_dss_signature(der)
    return signing + "." + b64url(r.to_bytes(32, "big") + s.to_bytes(32, "big"))


def transaction(**over):
    t = {
        "transactionId": "2000000812345678", "originalTransactionId": "2000000812345678",
        "bundleId": BUNDLE, "productId": PRODUCT, "purchaseDate": 1760950000000, "originalPurchaseDate": 1760950000000,
        "quantity": 1, "type": "Non-Consumable", "inAppOwnershipType": "PURCHASED", "signedDate": 1760950001000,
        "environment": "Sandbox", "transactionReason": "PURCHASE", "storefront": "IND", "storefrontId": "143467",
        "price": 299000, "currency": "INR",
    }
    t.update(over)
    return t


def main():
    root_k = ec.generate_private_key(ec.SECP384R1())
    int_k = ec.generate_private_key(ec.SECP384R1())
    leaf_k = ec.generate_private_key(ec.SECP256R1())
    root = cert(root_k, name("TEST Root CA - G3"), root_k, name("TEST Root CA - G3"), True)
    inter = cert(int_k, name("TEST WWDR CA - G6"), root_k, root.subject, True, "1.2.840.113635.100.6.2.1")
    leaf = cert(leaf_k, name("TEST StoreKit Signing"), int_k, inter.subject, False, "1.2.840.113635.100.6.11.1")
    plain_leaf = cert(leaf_k, name("TEST leaf without OID"), int_k, inter.subject, False)
    x5c = [der_b64(leaf), der_b64(inter), der_b64(root)]

    # an impostor chain: same shape, different root
    evil_k = ec.generate_private_key(ec.SECP384R1())
    evil_root = cert(evil_k, name("TEST Root CA - G3"), evil_k, name("TEST Root CA - G3"), True)
    evil_int = cert(int_k, name("TEST WWDR CA - G6"), evil_k, evil_root.subject, True, "1.2.840.113635.100.6.2.1")

    root_sha = hashlib.sha256(root.public_bytes(serialization.Encoding.DER)).hexdigest()
    out = {
        "_note": "TEST MATERIAL ONLY (make_fixtures.py). A test CA in Apple's chain shape; never trust it in production.",
        "rootSha256": root_sha,
        "now": 1761000000000,
        "valid": jws(transaction(), leaf_k, x5c),
        "restored": jws(transaction(transactionId="2000000899999999", transactionReason="PURCHASE"), leaf_k, x5c),
        "production": jws(transaction(environment="Production"), leaf_k, x5c),
        "revoked": jws(transaction(revocationDate=1761000000000, revocationReason=0), leaf_k, x5c),
        "otherBundle": jws(transaction(bundleId="com.example.other"), leaf_k, x5c),
        "otherProduct": jws(transaction(productId="com.pawnoon.pixel.coins_small", type="Consumable"), leaf_k, x5c),
        "xcode": jws(transaction(environment="Xcode"), leaf_k, x5c),
        "noOid": jws(transaction(), leaf_k, [der_b64(plain_leaf), der_b64(inter), der_b64(root)]),
        "untrustedRoot": jws(transaction(), leaf_k, [der_b64(leaf), der_b64(evil_int), der_b64(evil_root)]),
        "shortChain": jws(transaction(), leaf_k, [der_b64(leaf), der_b64(inter)]),
    }
    # tamper: valid header + signature, different payload
    h, p, s = out["valid"].split(".")
    out["tampered"] = h + "." + b64url(json.dumps(transaction(productId=PRODUCT, transactionId="2000000800000000"), separators=(",", ":")).encode()) + "." + s
    json.dump(out, open(os.path.join(HERE, "apple_jws.json"), "w"), indent=1)

    # App Store Server API answer for GET /inApps/v1/transactions/{id}. The ES256 API key itself is generated
    # by payments.test.ts at run time (no private key is committed, so secret scanners stay quiet).
    json.dump({
        "_note": "TEST MATERIAL ONLY.",
        "issuerId": "57246542-96fe-1a63-e053-0824d011072a", "keyId": "TESTKEY123",
        "transactionResponse": {"signedTransactionInfo": out["valid"]},
        "notFoundResponse": {"errorCode": 4040010, "errorMessage": "Transaction id not found."},
    }, open(os.path.join(HERE, "apple_server_api.json"), "w"), indent=1)

    # verifyReceipt answers (legacy fallback)
    in_app = {"quantity": "1", "product_id": PRODUCT, "transaction_id": "2000000812345678", "original_transaction_id": "2000000812345678",
              "purchase_date_ms": "1760950000000", "original_purchase_date_ms": "1760950000000", "in_app_ownership_type": "PURCHASED",
              "is_trial_period": "false"}
    json.dump({
        "production21007": {"status": 21007},
        "sandboxOk": {"status": 0, "environment": "Sandbox",
                      "receipt": {"receipt_type": "ProductionSandbox", "bundle_id": BUNDLE, "application_version": "42",
                                  "in_app": [{**in_app, "product_id": "com.pawnoon.pixel.other", "transaction_id": "2000000811111111",
                                              "original_transaction_id": "2000000811111111"}, in_app]}},
        "productionRefunded": {"status": 0, "environment": "Production",
                               "receipt": {"bundle_id": BUNDLE, "in_app": [{**in_app, "cancellation_date_ms": "1761000000000", "cancellation_reason": "0"}]}},
        "otherBundle": {"status": 0, "environment": "Production", "receipt": {"bundle_id": "com.example.other", "in_app": [in_app]}},
        "noProduct": {"status": 0, "environment": "Production", "receipt": {"bundle_id": BUNDLE, "in_app": []}},
        "malformed": {"status": 21002},
        "badSecret": {"status": 21004},
        "unity": json.dumps({"Store": "AppleAppStore", "TransactionID": "2000000812345678", "Payload": "MIITtgYJKoZIhvcNAQcCoIITpzCCE6MCAQExCzAJBgUrDgMCGgUAMIIDVwYJKoZIhvcNAQcBoIIDSASCA0QxggNAMAoCAQgCAQEEAhYAMAoCARQCAQEEAgwAMAsCAQECAQEEAwIBADALAgELAgEBBAMCAQAwCwIBDwIBAQQDAgEAMAsCARACAQEEAwIBADALAgEZAgEBBAMCAQMwDAIBCgIBAQQEFgI0KzAMAgEOAgEBBAQCAgDmMA0CAQMCAQEEBQwDNDIwDQIBDQIBAQQFAgMCJKEwDQIBEwIBAQQFDAMxLjAwDgIBCQIBAQQGAgRQMjU2MBgCAQQCAQIEEPSTEST"}),
    }, open(os.path.join(HERE, "apple_verify_receipt.json"), "w"), indent=1)

    # Google: service account (throwaway RSA key), OAuth answer, ProductPurchase answers, Unity receipts
    token = "opaque-token.AO-J1OxTEST_purchase_token_for_the_peacemaker_0123456789"
    purchase_json = {"orderId": "GPA.3301-1234-5678-90123", "packageName": BUNDLE, "productId": PRODUCT,
                     "purchaseTime": 1760950000000, "purchaseState": 0, "purchaseToken": token, "quantity": 1, "acknowledged": False}
    payload = json.dumps({"json": json.dumps(purchase_json), "signature": "TESTSIGNATURE==",
                          "skuDetails": [json.dumps({"productId": PRODUCT, "type": "inapp", "price": "₹299.00",
                                                     "price_amount_micros": 299000000, "price_currency_code": "INR"})]})
    pp = {"kind": "androidpublisher#productPurchase", "purchaseTimeMillis": "1760950000000", "purchaseState": 0,
          "consumptionState": 0, "developerPayload": "", "orderId": "GPA.3301-1234-5678-90123", "purchaseType": 0,
          "acknowledgementState": 0, "regionCode": "IN"}
    json.dump({
        "_note": "TEST MATERIAL ONLY. The service-account RSA key is generated by payments.test.ts at run time.",
        "clientEmail": "iap-validator@pawnoon-test.iam.gserviceaccount.com",
        "tokenResponse": {"access_token": "ya29.TEST-access-token", "expires_in": 3599, "token_type": "Bearer"},
        "purchaseToken": token,
        "testPurchase": pp,
        "realPurchaseAcked": {**pp, "purchaseType": None, "acknowledgementState": 1},
        "pending": {**pp, "purchaseState": 2, "orderId": ""},
        "canceled": {**pp, "purchaseState": 1},
        "unity": json.dumps({"Store": "GooglePlay", "TransactionID": token, "Payload": payload}),
        "unityOtherApp": json.dumps({"Store": "GooglePlay", "TransactionID": token,
                                     "Payload": json.dumps({"json": json.dumps({**purchase_json, "packageName": "com.example.other"}), "signature": "x"})}),
        "unityFake": json.dumps({"Store": "fake", "TransactionID": "fake-1", "Payload": "ThisIsFakeReceiptData"}),
    }, open(os.path.join(HERE, "google_play.json"), "w"), indent=1)
    print("fixtures written; root", root_sha)


if __name__ == "__main__":
    main()
