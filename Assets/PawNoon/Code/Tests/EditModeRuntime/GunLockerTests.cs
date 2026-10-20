using NUnit.Framework;
using PawNoon.Core;
using PawNoon.Runtime.Shop;

namespace PawNoon.Tests
{
    /// <summary>
    /// Shop / loadout (SPEC_V2 §6, M7): the own / equip state machine (GunLocker), its persistence, the
    /// server's authority over ownership, the purchase-validate answer parser, and the mock-store flow through
    /// IapService (what the editor and ReviewDirector run).
    /// </summary>
    public class GunLockerTests
    {
        private const string PM = Guns.Peacemaker;

        [Test]
        public void FreshInstallOwnsOnlyTheStandardGun()
        {
            var l = new GunLocker(new MemoryKeyValueStore());
            Assert.AreEqual(Guns.Standard, l.Equipped);
            Assert.AreEqual(GunState.Locked, l.StateOf(PM));
            Assert.AreEqual(GunState.Owned, l.StateOf(Guns.Standard));
            Assert.IsFalse(l.Owns(PM));
            Assert.IsFalse(l.Equip(PM), "cannot equip what you do not own");
            Assert.AreEqual(Guns.Standard, l.GunFor(false));
            Assert.AreEqual(Guns.Standard, l.GunFor(true));
        }

        [Test]
        public void BuyCancelAndBuyAgain()
        {
            var l = new GunLocker(new MemoryKeyValueStore());
            Assert.IsTrue(l.BeginPurchase(PM));
            Assert.AreEqual(GunState.Buying, l.StateOf(PM));
            Assert.IsFalse(l.BeginPurchase(PM), "one store sheet at a time");
            l.PurchaseCancelled(PM);
            Assert.AreEqual(GunState.Locked, l.StateOf(PM));
            Assert.IsTrue(l.BeginPurchase(PM));
            Assert.IsFalse(l.BeginPurchase(Guns.Standard), "the standard gun is never bought");
        }

        [Test]
        public void VerifiedPurchaseOwnsEquipsAndGoesToPvp()
        {
            var l = new GunLocker(new MemoryKeyValueStore());
            l.BeginPurchase(PM);
            l.StoreSucceeded(PM);
            Assert.AreEqual(GunState.Verifying, l.StateOf(PM));
            l.PurchaseCancelled(PM);
            Assert.AreEqual(GunState.Verifying, l.StateOf(PM), "a charged purchase is not undone by a late cancel");
            l.Granted(PM, true, true);
            Assert.AreEqual(GunState.Owned, l.StateOf(PM));
            Assert.AreEqual(PM, l.Equipped);
            Assert.AreEqual(PM, l.GunFor(false));
            Assert.AreEqual(PM, l.GunFor(true));
            Assert.IsFalse(l.BeginPurchase(PM), "owned: BUY is gone");
        }

        [Test]
        public void UnverifiedGrantIsSoloOnly()
        {
            var l = new GunLocker(new MemoryKeyValueStore());
            l.Granted(PM, false, true);
            Assert.AreEqual(PM, l.GunFor(false));
            Assert.AreEqual(Guns.Standard, l.GunFor(true), "never put an unverified gun in a recording");
            Assert.IsFalse(l.IsVerified(PM));
        }

        [Test]
        public void RejectionReturnsToLockedButNeverTakesAnOwnedGun()
        {
            var l = new GunLocker(new MemoryKeyValueStore());
            l.BeginPurchase(PM);
            l.StoreSucceeded(PM);
            l.Rejected(PM);
            Assert.AreEqual(GunState.Locked, l.StateOf(PM));
            l.Granted(PM, true, false);
            l.StoreSucceeded(PM);              // a restore of something already owned
            Assert.AreEqual(GunState.Owned, l.StateOf(PM));
            l.Rejected(PM);
            Assert.AreEqual(GunState.Owned, l.StateOf(PM), "only the server's profile can take it away");
        }

        [Test]
        public void EquipToggles()
        {
            var l = new GunLocker(new MemoryKeyValueStore());
            l.Granted(PM, true, false);
            Assert.AreEqual(Guns.Standard, l.Equipped, "a restore does not equip");
            Assert.IsTrue(l.Equip(PM));
            Assert.AreEqual(PM, l.Equipped);
            Assert.IsTrue(l.Equip(Guns.Standard));
            Assert.AreEqual(Guns.Standard, l.GunFor(false));
            Assert.IsTrue(l.Equip(" PEACEMAKER "));
            Assert.AreEqual(PM, l.Equipped);
        }

        [Test]
        public void StatePersistsAcrossLaunches()
        {
            var kv = new MemoryKeyValueStore();
            var a = new GunLocker(kv);
            a.Granted(PM, true, true);
            var b = new GunLocker(kv);
            Assert.AreEqual(GunState.Owned, b.StateOf(PM));
            Assert.AreEqual(PM, b.Equipped);
            Assert.IsTrue(b.IsVerified(PM));
            Assert.AreEqual("peacemaker", kv.Data[GunLocker.KeyOwned]);
            Assert.AreEqual("peacemaker", kv.Data[GunLocker.KeyEquipped]);
        }

        [Test]
        public void CorruptPrefsAreSanitised()
        {
            var kv = new MemoryKeyValueStore();
            kv.Set(GunLocker.KeyOwned, "golden_gun,,standard");
            kv.Set(GunLocker.KeyVerified, "peacemaker");          // verified but not owned: ignored
            kv.Set(GunLocker.KeyEquipped, "peacemaker");          // equipped but not owned: back to standard
            var l = new GunLocker(kv);
            Assert.IsFalse(l.Owns(PM));
            Assert.IsFalse(l.IsVerified(PM));
            Assert.AreEqual(Guns.Standard, l.Equipped);
        }

        [Test]
        public void ServerProfileIsAuthoritative()
        {
            var l = new GunLocker(new MemoryKeyValueStore());
            l.Granted(PM, false, true);
            l.ApplyServerProfile(new[] { "peacemaker" }, "peacemaker");
            Assert.IsTrue(l.IsVerified(PM), "the server confirmed it");
            Assert.AreEqual(PM, l.GunFor(true));
            int v = l.Version;
            l.ApplyServerProfile(new string[0], "standard");    // refunded / moved to another account
            Assert.Greater(l.Version, v);
            Assert.IsFalse(l.Owns(PM));
            Assert.AreEqual(GunState.Locked, l.StateOf(PM));
            Assert.AreEqual(Guns.Standard, l.Equipped, "unequipped with the refund");
            l.ApplyServerProfile(null, null);                    // no profile in the answer: nothing changes
            Assert.IsFalse(l.Owns(PM));
            l.ApplyServerProfile(new[] { "peacemaker", "coins_badge" }, "golden_gun");
            Assert.IsTrue(l.Owns(PM));
            Assert.AreEqual(Guns.Standard, l.Equipped, "an unknown equipped id means standard");
        }

        [Test]
        public void ResetForgetsEverything()
        {
            var kv = new MemoryKeyValueStore();
            var l = new GunLocker(kv);
            l.Granted(PM, true, true);
            l.ResetAll();
            Assert.AreEqual(GunState.Locked, new GunLocker(kv).StateOf(PM));
        }

        // ------------------------------------------------------------------ purchase-validate answer

        [Test]
        public void ParsesThePurchaseValidateAnswer()
        {
            const string body = "{\"entitlements\":[{\"item\":\"peacemaker\",\"productId\":\"com.pawnoon.pixel.peacemaker\",\"store\":\"app_store\"," +
                                "\"transactionId\":\"2000000812345678\",\"environment\":\"sandbox\",\"status\":\"valid\"}],\"outcome\":\"granted\"," +
                                "\"profile\":{\"ownedItems\":[\"peacemaker\"],\"coins\":0,\"equippedGun\":\"peacemaker\"}}";
            var a = PurchaseAnswer.Parse(body);
            Assert.AreEqual("valid", a.Status);
            Assert.AreEqual("granted", a.Outcome);
            Assert.AreEqual("peacemaker", a.Item);
            Assert.AreEqual("sandbox", a.Environment);
            Assert.IsTrue(a.HasProfile);
            CollectionAssert.AreEqual(new[] { "peacemaker" }, a.OwnedItems);
            Assert.AreEqual("peacemaker", a.EquippedGun);
            Assert.IsNull(PurchaseAnswer.Parse("not json"));
            var bare = PurchaseAnswer.Parse("{\"entitlements\":[{\"status\":\"pending\"}]}");
            Assert.AreEqual("pending", bare.Outcome, "outcome falls back to the status");
            Assert.IsFalse(bare.HasProfile);
        }

        [Test]
        public void PermanentAndTransientErrorsAreTellable()
        {
            foreach (var c in new[] { "invalid_receipt", "wrong_app", "product_mismatch", "not_in_receipt", "not_found", "fake_store", "sandbox_not_allowed" })
                Assert.IsTrue(PurchaseAnswer.IsPermanentError(c), c);
            foreach (var c in new[] { "store_unavailable", "store_not_configured", "offline", "internal", "db_error", "unauthorized", "" })
                Assert.IsFalse(PurchaseAnswer.IsPermanentError(c), c);
        }

        // ------------------------------------------------------------------ mock store through IapService

        private static IapService MockService(MemoryKeyValueStore kv, GunLocker l, out MockStore store)
        {
            store = new MockStore(kv) { DelaySeconds = 0.5f };
            var s = new IapService(store, l, kv, null, null);
            s.Init();
            return s;
        }

        [Test]
        public void MockStorePurchaseGrantsLocallyAndEquips()
        {
            var kv = new MemoryKeyValueStore();
            var l = new GunLocker(kv);
            MockStore store;
            var s = MockService(kv, l, out store);
            Assert.IsTrue(s.Ready);
            Assert.IsFalse(s.ServerValidation, "a mock purchase never reaches the server");
            Assert.AreEqual("₹299", s.PriceOf(PM));
            Assert.IsTrue(s.Buy(PM));
            Assert.AreEqual(GunState.Buying, l.StateOf(PM));
            s.Tick(0.3f);
            Assert.AreEqual(GunState.Buying, l.StateOf(PM));
            s.Tick(0.3f);
            Assert.AreEqual(GunState.Owned, l.StateOf(PM));
            Assert.AreEqual(PM, l.Equipped, "a fresh purchase equips");
            Assert.IsFalse(l.IsVerified(PM));
            Assert.AreEqual(0, s.PendingCount);
            Assert.AreEqual("MOCK STORE: GUN GRANTED", s.Message);
        }

        [Test]
        public void MockStoreCancelAndRestore()
        {
            var kv = new MemoryKeyValueStore();
            var l = new GunLocker(kv);
            MockStore store;
            var s = MockService(kv, l, out store);
            store.Outcome = MockStore.MockOutcome.Cancel;
            s.Buy(PM);
            s.Tick(1f);
            Assert.AreEqual(GunState.Locked, l.StateOf(PM));
            Assert.AreEqual("", s.Message, "cancelling is not an error");
            store.Outcome = MockStore.MockOutcome.Fail;
            s.Buy(PM);
            s.Tick(1f);
            Assert.AreEqual("PURCHASE FAILED", s.Message);

            // bought on "another install", then restored on a fresh one
            store.Outcome = MockStore.MockOutcome.Succeed;
            s.Buy(PM);
            s.Tick(1f);
            var fresh = new GunLocker(new MemoryKeyValueStore());
            MockStore again;
            var s2 = MockService(kv, fresh, out again);     // same mock store memory (kv)
            s2.Restore();
            Assert.AreEqual(GunState.Owned, fresh.StateOf(PM));
            Assert.AreEqual("MOCK STORE: GUN GRANTED", s2.Message);
            Assert.AreEqual(Guns.Standard, fresh.Equipped, "a restore does not change the loadout");
        }

        [Test]
        public void EquipThroughTheServiceNeedsOwnership()
        {
            var kv = new MemoryKeyValueStore();
            var l = new GunLocker(kv);
            MockStore store;
            var s = MockService(kv, l, out store);
            Assert.IsFalse(s.Equip(PM));
            store.DeliverNow(Guns.PeacemakerProductId);
            Assert.IsTrue(s.Equip(Guns.Standard));
            Assert.AreEqual(Guns.Standard, l.Equipped);
            Assert.IsTrue(s.Equip(PM));
        }

        [Test]
        public void PriceTextKeepsOnlyFontGlyphs()
        {
            Assert.AreEqual("Rs 299.00", ShopScreen.PriceText("₹299.00"));
            Assert.AreEqual("$2.99", ShopScreen.PriceText("$2.99"));
            Assert.AreEqual("EUR 2,99", ShopScreen.PriceText("€2,99"));
            Assert.AreEqual("2,99 EUR", ShopScreen.PriceText("2,99 €"));
            Assert.AreEqual("", ShopScreen.PriceText(null));
        }
    }
}
