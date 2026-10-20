using System;
using System.Collections.Generic;
using System.Globalization;

namespace PawNoon.Runtime.Shop
{
    /// <summary>One purchase as the store reports it (new, restored, or left pending by a previous session).</summary>
    public struct StorePurchase
    {
        public string ProductId;
        public string TransactionId;
        /// <summary>Unity IAP's Product.receipt JSON ({"Store","TransactionID","Payload"}), sent to purchase-validate as is.</summary>
        public string Receipt;
        public bool FromRestore;
    }

    /// <summary>
    /// The store seam (SPEC_V2 §6): Unity IAP on devices (UnityIapStore, IapService.cs, compiled only with
    /// PAWNOON_IAP), MockStore in the editor and in review runs. The store never grants anything itself: every
    /// purchase goes to purchase-validate, and is finished (Finish) only after the server answered for good.
    /// </summary>
    public interface IStore
    {
        /// <summary>"app_store", "google_play" or "mock" (purchase-validate's store field).</summary>
        string Name { get; }
        bool IsReady { get; }
        bool IsMock { get; }

        /// <summary>Raised for every purchase the store delivers (main thread).</summary>
        Action<StorePurchase> Purchased { get; set; }
        /// <summary>Raised when a purchase did not happen: (productId, reason). Reason "cancelled" is the user's choice.</summary>
        Action<string, string> Failed { get; set; }

        void Initialize(string[] productIds, Action<bool, string> done);
        /// <summary>Localised price string from the store, or null when unknown.</summary>
        string PriceOf(string productId);
        void Purchase(string productId);
        /// <summary>Asks the store to re-deliver owned non-consumables (they arrive through Purchased, FromRestore).</summary>
        void Restore(Action<bool, string> done);
        /// <summary>Completes a pending transaction after the server answered for good.</summary>
        void Finish(string productId);
        /// <summary>Per frame (the mock store's delays).</summary>
        void Tick(float dt);
        /// <summary>
        /// Binds the next purchases to this account (review pass 2): Apple applicationUsername (copied into the
        /// transaction's appAccountToken), Google obfuscatedAccountId. The server checks it before a purchase may move
        /// between accounts (supabase/functions/_shared/payments/binding.ts). Null / empty = unbound.
        /// </summary>
        void SetAccount(string userId);
    }

    /// <summary>The account tokens the stores carry (must match binding.ts expectedAccountToken).</summary>
    public static class PurchaseBinding
    {
        /// <summary>Apple: the user id itself when it is a UUID (lower case), else null (StoreKit only maps UUIDs).</summary>
        public static string AppleToken(string userId)
        {
            Guid g;
            if (string.IsNullOrEmpty(userId) || !Guid.TryParseExact(userId.Trim(), "D", out g)) return null;
            return g.ToString("D");
        }

        /// <summary>Google: SHA-256 (lower-case hex, 64 chars: Play's limit) of the lower-case user id; null when empty.</summary>
        public static string GoogleToken(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(userId.Trim().ToLowerInvariant()));
                var sb = new System.Text.StringBuilder(64);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// Editor / review store: no network, no money. A purchase succeeds after a short delay (or fails as set
    /// by Outcome), restore re-delivers what it sold before (PlayerPrefs-free: kept in memory and in the
    /// supplied key-value store). Its receipts are {"Store":"fake",...}, which the real server refuses
    /// (422 fake_store), so IapService grants mock purchases locally and never marks them server-verified.
    /// </summary>
    public sealed class MockStore : IStore
    {
        public enum MockOutcome { Succeed, Cancel, Fail }

        private const string KeySold = "pn_mock_store_sold";
        public const string MockPrice = "₹299";

        private readonly IKeyValueStore kv;
        private readonly HashSet<string> sold = new HashSet<string>();
        private readonly List<KeyValuePair<float, StorePurchase>> due = new List<KeyValuePair<float, StorePurchase>>(2);
        private readonly List<KeyValuePair<float, string[]>> failDue = new List<KeyValuePair<float, string[]>>(2);
        private bool ready;
        private int txn = 1000;

        public float DelaySeconds = 0.6f;
        public MockOutcome Outcome = MockOutcome.Succeed;

        public MockStore(IKeyValueStore kv)
        {
            this.kv = kv ?? new MemoryKeyValueStore();
            var s = this.kv.Get(KeySold, "");
            if (!string.IsNullOrEmpty(s))
                foreach (var p in s.Split(',')) if (p.Length > 0) sold.Add(p);
        }

        public string Name { get { return "mock"; } }
        public bool IsReady { get { return ready; } }
        public bool IsMock { get { return true; } }
        public Action<StorePurchase> Purchased { get; set; }
        public Action<string, string> Failed { get; set; }

        public void Initialize(string[] productIds, Action<bool, string> done)
        {
            ready = true;
            if (done != null) done(true, null);
        }

        public string PriceOf(string productId) { return MockPrice; }

        public void Purchase(string productId)
        {
            if (Outcome != MockOutcome.Succeed)
            {
                failDue.Add(new KeyValuePair<float, string[]>(DelaySeconds, new[] { productId, Outcome == MockOutcome.Cancel ? "cancelled" : "mock_failure" }));
                return;
            }
            due.Add(new KeyValuePair<float, StorePurchase>(DelaySeconds, Make(productId, false)));
        }

        /// <summary>Delivers a purchase right now (ReviewDirector MockBuy).</summary>
        public void DeliverNow(string productId)
        {
            Deliver(Make(productId, false));
        }

        public void Restore(Action<bool, string> done)
        {
            // like StoreKit: the restored purchases are delivered before the completion callback
            var list = new List<string>(sold);
            for (int i = 0; i < list.Count; i++) Deliver(Make(list[i], true));
            if (done != null) done(true, null);
        }

        public void Finish(string productId) { }

        /// <summary>The account the next purchase is bound to (tests / Status; the mock has no store to tell).</summary>
        public string Account { get; private set; }

        public void SetAccount(string userId) { Account = userId; }

        /// <summary>QA: forget what the mock store sold.</summary>
        public void Forget()
        {
            sold.Clear();
            kv.Set(KeySold, "");
            kv.Save();
        }

        public void Tick(float dt)
        {
            for (int i = due.Count - 1; i >= 0; i--)
            {
                float left = due[i].Key - dt;
                if (left > 0f) { due[i] = new KeyValuePair<float, StorePurchase>(left, due[i].Value); continue; }
                var p = due[i].Value;
                due.RemoveAt(i);
                Deliver(p);
            }
            for (int i = failDue.Count - 1; i >= 0; i--)
            {
                float left = failDue[i].Key - dt;
                if (left > 0f) { failDue[i] = new KeyValuePair<float, string[]>(left, failDue[i].Value); continue; }
                var f = failDue[i].Value;
                failDue.RemoveAt(i);
                if (Failed != null) Failed(f[0], f[1]);
            }
        }

        private StorePurchase Make(string productId, bool restore)
        {
            string id = "mock-" + (txn++).ToString(CultureInfo.InvariantCulture);
            return new StorePurchase
            {
                ProductId = productId,
                TransactionId = id,
                Receipt = "{\"Store\":\"fake\",\"TransactionID\":\"" + id + "\",\"Payload\":\"ThisIsFakeReceiptData\"}",
                FromRestore = restore
            };
        }

        private void Deliver(StorePurchase p)
        {
            if (sold.Add(p.ProductId))
            {
                kv.Set(KeySold, string.Join(",", new List<string>(sold).ToArray()));
                kv.Save();
            }
            if (Purchased != null) Purchased(p);
        }
    }
}
