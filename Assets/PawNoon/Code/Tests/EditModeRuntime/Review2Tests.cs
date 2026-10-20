using NUnit.Framework;
using PawNoon.Core;
using PawNoon.Runtime.Net;
using PawNoon.Runtime.Shop;

namespace PawNoon.Tests
{
    /// <summary>
    /// Review pass 2 (Docs/REVIEW_2026-10-20_pass2.md), client side: guns verified per account, purchases bound to the
    /// account, which failed calls keep a match-submit queued, the pawtruth id.
    /// </summary>
    public class Review2Tests
    {
        private const string PM = Guns.Peacemaker;
        private const string UserA = "5d0b1c9e-1f7e-4d53-a1a6-6f3f8b9d2c44";
        private const string UserB = "11111111-2222-4333-8444-555555555555";

        // ------------------------------------------------------------------ GunLocker: verification belongs to an account

        [Test]
        public void VerifiedGunsOnlyCountForTheAccountThatVerifiedThem()
        {
            var kv = new MemoryKeyValueStore();
            var l = new GunLocker(kv);
            l.SetAccount(UserA);
            l.Granted(PM, true, true);
            Assert.AreEqual(UserA, l.VerifiedFor);
            Assert.AreEqual(PM, l.GunFor(true));

            // switch to another account (or a new guest after a refused refresh): solo keeps the gun, PvP does not
            l.SetAccount(UserB);
            Assert.IsTrue(l.Owns(PM));
            Assert.AreEqual(PM, l.GunFor(false), "solo still fires it");
            Assert.IsFalse(l.IsVerified(PM));
            Assert.AreEqual(Guns.Standard, l.GunFor(true), "never a gun the new account may not own in a recording");

            // back to the first account: verified again (nothing was lost)
            l.SetAccount(UserA);
            Assert.AreEqual(PM, l.GunFor(true));

            // signed out
            l.SetAccount(null);
            Assert.AreEqual(Guns.Standard, l.GunFor(true));
        }

        [Test]
        public void VerificationOwnerIsPersistedAndLegacyInstallsAdoptTheFirstAccount()
        {
            var kv = new MemoryKeyValueStore();
            var a = new GunLocker(kv);
            a.SetAccount(UserA);
            a.Granted(PM, true, true);
            var b = new GunLocker(kv);
            b.SetAccount(UserA);
            Assert.IsTrue(b.IsVerified(PM), "persisted with its owner");
            var c = new GunLocker(kv);
            c.SetAccount(UserB);
            Assert.IsFalse(c.IsVerified(PM), "persisted owner is another account");

            // an install from before review pass 2: verified, but no owner stored
            var old = new MemoryKeyValueStore();
            old.Set(GunLocker.KeyOwned, PM);
            old.Set(GunLocker.KeyVerified, PM);
            var d = new GunLocker(old);
            d.SetAccount(UserA);
            Assert.IsTrue(d.IsVerified(PM), "the first account seen adopts the old verification");
            Assert.AreEqual(UserA, old.Get(GunLocker.KeyVerifiedFor, ""));
            d.SetAccount(UserB);
            Assert.IsFalse(d.IsVerified(PM));
        }

        [Test]
        public void DeletedAccountForgetsVerificationButKeepsTheGunForSolo()
        {
            var l = new GunLocker(new MemoryKeyValueStore());
            l.SetAccount(UserA);
            l.Granted(PM, true, true);
            l.ForgetVerification();
            Assert.IsTrue(l.Owns(PM));
            Assert.AreEqual(GunState.Owned, l.StateOf(PM));
            Assert.IsFalse(l.IsVerified(PM));
            Assert.AreEqual(PM, l.GunFor(false));
            Assert.AreEqual(Guns.Standard, l.GunFor(true));
            // RESTORE on the new account verifies it again
            l.SetAccount(UserB);
            l.ApplyServerProfile(new[] { PM }, null);
            Assert.IsTrue(l.IsVerified(PM));
            Assert.AreEqual(UserB, l.VerifiedFor);
        }

        [Test]
        public void ServerProfileOfANewAccountReplacesTheOldVerification()
        {
            var l = new GunLocker(new MemoryKeyValueStore());
            l.SetAccount(UserA);
            l.Granted(PM, true, true);
            l.SetAccount(UserB);
            l.ApplyServerProfile(new string[0], null);   // B owns nothing
            Assert.IsFalse(l.Owns(PM), "the server is authoritative for the signed-in account");
            l.SetAccount(UserA);
            Assert.IsFalse(l.IsVerified(PM), "A's verification was dropped when B's answer came in (RESTORE brings it back)");
        }

        // ------------------------------------------------------------------ purchase binding

        [Test]
        public void PurchaseBindingTokensMatchTheServer()
        {
            Assert.AreEqual(UserA, PurchaseBinding.AppleToken(UserA.ToUpperInvariant()), "Apple: the UUID, lower case");
            Assert.IsNull(PurchaseBinding.AppleToken("mock-1234"), "StoreKit only maps UUIDs");
            Assert.IsNull(PurchaseBinding.AppleToken(null));
            // same vector as supabase/functions/_shared/review2.test.ts (expectedAccountToken google_play)
            Assert.AreEqual("807ff2d8af672f5908d1ffd07db62014e5f627b991a4fd4243de256265592ca8", PurchaseBinding.GoogleToken(UserA.ToUpperInvariant()));
            Assert.AreEqual(64, PurchaseBinding.GoogleToken(UserB).Length, "Play's limit");
            Assert.IsNull(PurchaseBinding.GoogleToken(""));
        }

        [Test]
        public void TheStoreIsToldTheAccountBeforeEveryPurchase()
        {
            var kv = new MemoryKeyValueStore();
            var l = new GunLocker(kv);
            var store = new MockStore(kv) { DelaySeconds = 0.1f };
            string who = UserA;
            var s = new IapService(store, l, kv, null, null, () => who);
            s.Init();
            Assert.IsTrue(s.Buy(PM));
            Assert.AreEqual(UserA, store.Account);
        }

        [Test]
        public void ForeignAccountAnswersFinishThePurchase()
        {
            Assert.IsTrue(PurchaseAnswer.IsPermanentError("owned_elsewhere"));
            Assert.IsTrue(PurchaseAnswer.IsPermanentError("account_mismatch"));
            Assert.IsFalse(PurchaseAnswer.IsPermanentError("rate_limited"), "429 is retried");
        }

        // ------------------------------------------------------------------ submit queue: what keeps a match queued

        private static ApiResult R(long status, bool offline = false, string code = null)
        {
            var r = new ApiResult { Status = status, Offline = offline, Ok = status >= 200 && status < 300 && !offline };
            r.Error.code = code;
            return r;
        }

        [Test]
        public void OnlyDefinitiveAnswersDropAQueuedSubmit()
        {
            Assert.IsTrue(R(0, true, "offline").RetryLater, "offline");
            Assert.IsTrue(R(0, false, "auth_failed").RetryLater, "no session right now");
            Assert.IsTrue(R(0, false, "relink").RetryLater, "linked player must sign in again: keep the match");
            Assert.IsTrue(R(503, false, "busy").RetryLater, "503 busy: match back to issued, resend");
            Assert.IsTrue(R(500, false, "internal").RetryLater);
            Assert.IsTrue(R(429, false, "rate_limited").RetryLater);
            Assert.IsTrue(R(401, false, "unauthorized").RetryLater);
            Assert.IsTrue(R(408).RetryLater);
            Assert.IsFalse(R(200).RetryLater);
            Assert.IsFalse(R(409, false, "already_submitted").RetryLater);
            Assert.IsFalse(R(422, false, "recording_rejected").RetryLater);
            Assert.IsFalse(R(404, false, "match_not_found").RetryLater);
            Assert.IsFalse(R(400, false, "bad_recording").RetryLater);
        }

        // ------------------------------------------------------------------ character ids

        [Test]
        public void LegacyBlackpawIdIsPawtruthOnTheClient()
        {
            Assert.AreEqual(CharacterRoster.Pawtruth, CharacterRoster.Normalize("blackpaw"));
            Assert.AreEqual(CharacterRoster.Pawtruth, new MatchStartInfo().OpponentCharacter, "default persona id");
        }
    }
}
