using System;
using System.Collections.Generic;
using PawNoon.Core;
using PawNoon.Runtime.Net;
using UnityEngine;
#if PAWNOON_IAP
using Unity.Services.Core;
using UnityEngine.Purchasing;
using UnityEngine.Purchasing.Extension;
#endif

namespace PawNoon.Runtime.Shop
{
    /// <summary>
    /// In-app purchases (SPEC_V2 §6): store -> receipt -> purchase-validate -> entitlement -> profile.
    ///
    /// The store sits behind <see cref="IStore"/>: <see cref="UnityIapStore"/> (Unity IAP, only compiled with
    /// PAWNOON_IAP, which the Runtime asmdef defines when com.unity.purchasing resolves) on iOS / Android
    /// players, <see cref="MockStore"/> in the editor, in review runs and in builds without the package.
    ///
    /// Rules:
    ///  * the client never grants a store purchase on its own when a server is configured: the store
    ///    transaction stays pending (Unity IAP PurchaseProcessingResult.Pending) until purchase-validate
    ///    answered for good; then it is finished (ConfirmPendingPurchase), granted or not;
    ///  * a transient failure (offline, 5xx, store_unavailable) keeps the receipt in <c>pn_iap_pending</c>
    ///    and retries every <see cref="RetrySeconds"/> and on the next launch / RESTORE;
    ///  * mock purchases and builds without a backend grant locally, never server-verified (solo only).
    /// </summary>
    public sealed class IapService
    {
        public const float RetrySeconds = 45f;
        private const string KeyPending = "pn_iap_pending";
        private static readonly string[] Products = { Guns.PeacemakerProductId };

        private readonly IStore store;
        private readonly GunLocker locker;
        private readonly IKeyValueStore kv;
        private readonly Func<SupabaseApi> api;
        private readonly Func<bool> serverOn;
        private readonly Func<string> accountId;
        private readonly List<StorePurchase> pending = new List<StorePurchase>(2);
        private readonly HashSet<string> inFlight = new HashSet<string>();
        private float retryT = -1f;
        private bool initStarted;

        /// <summary>Short player-facing line for the shop card (pixel font glyphs only), "" when quiet.</summary>
        public string Message { get; private set; }
        /// <summary>Bumps when Message changes.</summary>
        public int MessageVersion { get; private set; }
        public string InitError { get; private set; }

        public IapService(IStore store, GunLocker locker, IKeyValueStore kv, Func<SupabaseApi> api, Func<bool> serverOn,
            Func<string> accountId = null)
        {
            this.accountId = accountId;
            this.store = store;
            this.locker = locker;
            this.kv = kv ?? new MemoryKeyValueStore();
            this.api = api;
            this.serverOn = serverOn ?? (() => false);
            Message = "";
            store.Purchased = OnPurchased;
            store.Failed = OnFailed;
            LoadPending();
        }

        public IStore Store { get { return store; } }
        public bool Ready { get { return store.IsReady; } }
        public int PendingCount { get { return pending.Count; } }
        public bool ServerValidation { get { return !store.IsMock && serverOn(); } }

        /// <summary>Starts the store (once). Pending receipts from earlier sessions are re-validated.</summary>
        public void Init()
        {
            if (initStarted) return;
            initStarted = true;
            store.Initialize(Products, (ok, err) =>
            {
                InitError = ok ? null : err;
                if (!ok) Debug.LogWarning("[PawNoon] store unavailable: " + err);
                for (int i = 0; i < pending.Count; i++) locker.StoreSucceeded(Guns.GunForProduct(pending[i].ProductId));
                Flush();
            });
        }

        /// <summary>Store price for the gun's product (e.g. "₹299.00"), or null until the store answered.</summary>
        public string PriceOf(string gun)
        {
            string pid = Guns.ProductFor(gun);
            return pid != null && store.IsReady ? store.PriceOf(pid) : null;
        }

        public bool Buy(string gun)
        {
            string pid = Guns.ProductFor(gun);
            if (pid == null) return false;
            if (!store.IsReady)
            {
                Say(InitError != null ? "STORE UNAVAILABLE" : "STORE IS WAKING UP");
                if (!initStarted) Init();
                return false;
            }
            if (!locker.BeginPurchase(gun)) return false;
            Say("");
            // review pass 2: the purchase carries the account it is for (appAccountToken / obfuscatedAccountId)
            store.SetAccount(accountId != null ? accountId() : null);
            store.Purchase(pid);
            return true;
        }

        public void Restore()
        {
            Say("RESTORING...");
            if (!store.IsReady)
            {
                if (!initStarted) Init();
                Say(InitError != null ? "STORE UNAVAILABLE" : "STORE IS WAKING UP");
                return;
            }
            store.Restore((ok, err) =>
            {
                if (!ok) Say("RESTORE FAILED");
                else if (Message == "RESTORING...") Say(locker.Owns(Guns.Peacemaker) ? "PURCHASES RESTORED" : "NOTHING TO RESTORE");
            });
            Flush();
        }

        /// <summary>EQUIP / UNEQUIP. The profile follows (loadout-equip) when the gun is server-verified.</summary>
        public bool Equip(string gun)
        {
            if (!locker.Equip(gun)) return false;
            string g = Guns.Normalize(gun);
            if (ServerValidation && locker.IsVerified(g))
            {
                var a = api();
                if (a != null)
                {
                    string body = new JsonOut(32).BeginObject().Prop("gun", g).EndObject().ToString();
                    a.Call("loadout-equip", body, r =>
                    {
                        if (!r.Ok) Debug.Log("[PawNoon] loadout-equip -> " + r.Status + " " + r.Error.code);
                    });
                }
            }
            return true;
        }

        public void Tick(float dt)
        {
            store.Tick(dt);
            if (retryT > 0f)
            {
                retryT -= dt;
                if (retryT <= 0f) Flush();
            }
        }

        // ------------------------------------------------------------------ store callbacks

        private void OnPurchased(StorePurchase p)
        {
            string gun = Guns.GunForProduct(p.ProductId);
            if (gun == null)
            {
                store.Finish(p.ProductId);   // not ours (a future consumable): never leave it hanging
                return;
            }
            locker.StoreSucceeded(gun);
            if (!ServerValidation)
            {
                // editor mock store, or a build without a backend: solo-only grant, never server-verified
                locker.Granted(gun, false, !p.FromRestore);
                store.Finish(p.ProductId);
                Say(store.IsMock ? "MOCK STORE: GUN GRANTED" : (p.FromRestore ? "PURCHASES RESTORED" : "THANK YOU, PARTNER"));
                return;
            }
            AddPending(p);
            Validate(p);
        }

        private void OnFailed(string productId, string reason)
        {
            string gun = Guns.GunForProduct(productId);
            if (gun != null) locker.PurchaseCancelled(gun);
            Say(reason == "cancelled" ? "" : (reason == "pending" ? "PAYMENT PENDING" : "PURCHASE FAILED"));
        }

        // ------------------------------------------------------------------ validation

        private void Flush()
        {
            retryT = -1f;
            if (!ServerValidation) return;
            for (int i = 0; i < pending.Count; i++) Validate(pending[i]);
        }

        private void Validate(StorePurchase p)
        {
            var a = api();
            if (a == null) { Retry(); return; }
            string key = p.ProductId + "|" + p.TransactionId;
            if (!inFlight.Add(key)) return;
            Say("CHECKING PURCHASE...");
            var body = new JsonOut(512).BeginObject()
                .Prop("store", store.Name)
                .Prop("productId", p.ProductId)
                .Prop("restore", p.FromRestore)
                .Prop("receipt", p.Receipt ?? "")
                .EndObject().ToString();
            a.Call("purchase-validate", body, r =>
            {
                inFlight.Remove(key);
                OnAnswer(p, r);
            });
        }

        private void OnAnswer(StorePurchase p, ApiResult r)
        {
            string gun = Guns.GunForProduct(p.ProductId);
            if (r.Ok)
            {
                var ans = PurchaseAnswer.Parse(r.Body);
                if (ans == null) { Retry(); return; }
                if (ans.HasProfile) locker.ApplyServerProfile(ans.OwnedItems, ans.EquippedGun);
                switch (ans.Status)
                {
                    case "valid":
                        locker.Granted(gun, true, false);   // the server equipped it on a fresh purchase (profile above)
                        Done(p);
                        Say(p.FromRestore || ans.Outcome != "granted" ? "PURCHASES RESTORED" : "THANK YOU, PARTNER");
                        return;
                    case "pending":
                        Say("PAYMENT PENDING");             // keep the store transaction open; Google finishes it later
                        Retry();
                        return;
                    default:                                // refunded / revoked
                        locker.Rejected(gun);
                        Done(p);
                        Say("PURCHASE WAS REFUNDED");
                        return;
                }
            }
            string code = r.Error.code ?? "";
            if (!r.Offline && PurchaseAnswer.IsPermanentError(code))
            {
                Debug.LogWarning("[PawNoon] purchase rejected by the server: " + code + " " + r.Error.message);
                locker.Rejected(gun);
                Done(p);
                // bought for another account (review pass 2): restore it on the phone / account that bought it
                Say(code == "owned_elsewhere" || code == "account_mismatch" ? "OWNED BY ANOTHER ACCOUNT" : "PURCHASE NOT VALID");
                return;
            }
            Debug.Log("[PawNoon] purchase-validate will retry: " + r.Status + " " + code);
            Say("WILL CHECK LATER");
            Retry();
        }

        private void Done(StorePurchase p)
        {
            store.Finish(p.ProductId);
            for (int i = pending.Count - 1; i >= 0; i--)
                if (pending[i].ProductId == p.ProductId && pending[i].TransactionId == p.TransactionId) pending.RemoveAt(i);
            SavePending();
        }

        private void Retry()
        {
            if (retryT <= 0f) retryT = RetrySeconds;
        }

        private void Say(string s)
        {
            s = s ?? "";
            if (s == Message) return;
            Message = s;
            MessageVersion++;
        }

        // ------------------------------------------------------------------ pending receipts (persisted)

        private void AddPending(StorePurchase p)
        {
            for (int i = 0; i < pending.Count; i++)
                if (pending[i].ProductId == p.ProductId && pending[i].TransactionId == p.TransactionId) return;
            pending.Add(p);
            SavePending();
        }

        private void LoadPending()
        {
            pending.Clear();
            try
            {
                var arr = PnJson.Arr(PnJson.Parse(kv.Get(KeyPending, "")), "items");
                if (arr == null) return;
                for (int i = 0; i < arr.Count; i++)
                {
                    var p = new StorePurchase
                    {
                        ProductId = PnJson.Str(arr[i], "productId", null),
                        TransactionId = PnJson.Str(arr[i], "transactionId", ""),
                        Receipt = PnJson.Str(arr[i], "receipt", null),
                        FromRestore = PnJson.Bool(arr[i], "restore", false)
                    };
                    if (!string.IsNullOrEmpty(p.ProductId) && !string.IsNullOrEmpty(p.Receipt)) pending.Add(p);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawNoon] pending purchases unreadable: " + e.Message);
                pending.Clear();
            }
        }

        private void SavePending()
        {
            var j = new JsonOut(256).BeginObject().Key("items").BeginArray();
            for (int i = 0; i < pending.Count; i++)
                j.BeginObject().Prop("productId", pending[i].ProductId).Prop("transactionId", pending[i].TransactionId ?? "")
                    .Prop("receipt", pending[i].Receipt ?? "").Prop("restore", pending[i].FromRestore).EndObject();
            kv.Set(KeyPending, j.EndArray().EndObject().ToString());
            kv.Save();
        }

        /// <summary>The store this build uses: Unity IAP on an iOS / Android player with the package, else the mock.</summary>
        public static IStore CreateStore(IKeyValueStore kv, bool forceMock)
        {
#if PAWNOON_IAP && !UNITY_EDITOR
            if (!forceMock && (Application.platform == RuntimePlatform.IPhonePlayer || Application.platform == RuntimePlatform.Android))
                return new UnityIapStore();
#endif
            return new MockStore(kv);
        }
    }

#if PAWNOON_IAP
    /// <summary>
    /// Unity IAP 4.x (com.unity.purchasing, IDetailedStoreListener). Purchases are returned as Pending from
    /// ProcessPurchase and confirmed only through <see cref="Finish"/>, after purchase-validate answered.
    /// </summary>
    public sealed class UnityIapStore : IStore, IDetailedStoreListener
    {
        private IStoreController controller;
        private IExtensionProvider extensions;
        private IGooglePlayConfiguration googleConfig;
        private string account;
        private Action<bool, string> initDone;
        private bool restoring;

        public string Name
        {
            get { return Application.platform == RuntimePlatform.Android ? "google_play" : "app_store"; }
        }

        public bool IsReady { get { return controller != null; } }
        public bool IsMock { get { return false; } }
        public Action<StorePurchase> Purchased { get; set; }
        public Action<string, string> Failed { get; set; }

        public async void Initialize(string[] productIds, Action<bool, string> done)
        {
            initDone = done;
            try
            {
                if (UnityServices.State == ServicesInitializationState.Uninitialized) await UnityServices.InitializeAsync();
            }
            catch (Exception e)
            {
                // IAP still works without Unity Gaming Services (it only logs a warning)
                Debug.LogWarning("[PawNoon] Unity Services init failed: " + e.Message);
            }
            try
            {
                var builder = ConfigurationBuilder.Instance(StandardPurchasingModule.Instance());
                for (int i = 0; i < productIds.Length; i++) builder.AddProduct(productIds[i], ProductType.NonConsumable);
                // kept to set the obfuscated account id per purchase (the Play store reads it when the flow starts)
                if (Application.platform == RuntimePlatform.Android) googleConfig = builder.Configure<IGooglePlayConfiguration>();
                UnityPurchasing.Initialize(this, builder);
            }
            catch (Exception e)
            {
                InitFinished(false, e.Message);
            }
        }

        private void InitFinished(bool ok, string err)
        {
            var d = initDone;
            initDone = null;
            if (d != null) d(ok, err);
        }

        public string PriceOf(string productId)
        {
            if (controller == null) return null;
            var p = controller.products.WithID(productId);
            if (p == null || p.metadata == null || string.IsNullOrEmpty(p.metadata.localizedPriceString)) return null;
            return p.metadata.localizedPriceString;
        }

        public void Purchase(string productId)
        {
            if (controller == null) { if (Failed != null) Failed(productId, "not_ready"); return; }
            var p = controller.products.WithID(productId);
            if (p == null || !p.availableToPurchase) { if (Failed != null) Failed(productId, "unavailable"); return; }
            BindAccount();
            controller.InitiatePurchase(p);
        }

        public void Restore(Action<bool, string> done)
        {
            if (extensions == null) { if (done != null) done(false, "not_ready"); return; }
            restoring = true;
            Action<bool, string> finish = (ok, err) =>
            {
                restoring = false;
                // products the store already shows as owned (Google keeps acknowledged ones here) are re-validated too
                if (ok) ReplayOwned();
                if (done != null) done(ok, err);
            };
            if (Application.platform == RuntimePlatform.Android)
                extensions.GetExtension<IGooglePlayStoreExtensions>().RestoreTransactions(finish);
            else
                extensions.GetExtension<IAppleExtensions>().RestoreTransactions(finish);
        }

        private void ReplayOwned()
        {
            if (controller == null || Purchased == null) return;
            var all = controller.products.all;
            for (int i = 0; i < all.Length; i++)
            {
                var p = all[i];
                if (p.definition.type == ProductType.NonConsumable && p.hasReceipt)
                    Purchased(new StorePurchase { ProductId = p.definition.id, TransactionId = p.transactionID, Receipt = p.receipt, FromRestore = true });
            }
        }

        public void Finish(string productId)
        {
            if (controller == null) return;
            var p = controller.products.WithID(productId);
            if (p != null) controller.ConfirmPendingPurchase(p);
        }

        public void Tick(float dt) { }

        public void SetAccount(string userId) { account = userId; }

        /// <summary>
        /// Review pass 2: Apple SKPayment.applicationUsername (a UUID, so StoreKit copies it into appAccountToken) and
        /// Google's obfuscated account id, checked by purchase-validate before a purchase may change accounts.
        /// </summary>
        private void BindAccount()
        {
            try
            {
                if (Application.platform == RuntimePlatform.IPhonePlayer)
                {
                    string t = PurchaseBinding.AppleToken(account);
                    if (t != null && extensions != null) extensions.GetExtension<IAppleExtensions>().SetApplicationUsername(t);
                }
                else if (Application.platform == RuntimePlatform.Android && googleConfig != null)
                {
                    string t = PurchaseBinding.GoogleToken(account);
                    if (t != null) googleConfig.SetObfuscatedAccountId(t);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawNoon] purchase not bound to the account: " + e.Message);
            }
        }

        // --- IDetailedStoreListener

        public void OnInitialized(IStoreController c, IExtensionProvider e)
        {
            controller = c;
            extensions = e;
            InitFinished(true, null);
        }

        public void OnInitializeFailed(InitializationFailureReason error)
        {
            InitFinished(false, error.ToString());
        }

        public void OnInitializeFailed(InitializationFailureReason error, string message)
        {
            InitFinished(false, error + ": " + message);
        }

        public PurchaseProcessingResult ProcessPurchase(PurchaseEventArgs args)
        {
            var p = args.purchasedProduct;
            if (Purchased != null)
                Purchased(new StorePurchase { ProductId = p.definition.id, TransactionId = p.transactionID, Receipt = p.receipt, FromRestore = restoring });
            // never Complete here: the server decides, then Finish() confirms
            return PurchaseProcessingResult.Pending;
        }

        public void OnPurchaseFailed(Product product, PurchaseFailureReason reason)
        {
            if (Failed != null) Failed(product != null ? product.definition.id : null, Reason(reason));
        }

        public void OnPurchaseFailed(Product product, PurchaseFailureDescription description)
        {
            if (Failed != null) Failed(product != null ? product.definition.id : description.productId, Reason(description.reason));
        }

        private static string Reason(PurchaseFailureReason r)
        {
            switch (r)
            {
                case PurchaseFailureReason.UserCancelled: return "cancelled";
                case PurchaseFailureReason.ExistingPurchasePending: return "pending";
                case PurchaseFailureReason.DuplicateTransaction: return "duplicate";
                default: return r.ToString();
            }
        }
    }
#endif
}
