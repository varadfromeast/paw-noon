using System;
using System.Collections.Generic;
using System.Text;
using PawNoon.Core;

namespace PawNoon.Runtime.Shop
{
    /// <summary>Where a paid gun stands for this player (SPEC_V2 §6).</summary>
    public enum GunState
    {
        /// <summary>Not owned: the card shows BUY.</summary>
        Locked,
        /// <summary>The store sheet is up (BUY tapped).</summary>
        Buying,
        /// <summary>The store charged; the server has not confirmed yet (offline / pending card). Retried later.</summary>
        Verifying,
        /// <summary>Owned: the card shows EQUIP / EQUIPPED.</summary>
        Owned
    }

    /// <summary>Key-value persistence seam (PlayerPrefs in the game, a dictionary in tests).</summary>
    public interface IKeyValueStore
    {
        string Get(string key, string def);
        void Set(string key, string value);
        void Save();
    }

    /// <summary>In-memory store (tests, and the fallback when PlayerPrefs throws).</summary>
    public sealed class MemoryKeyValueStore : IKeyValueStore
    {
        public readonly Dictionary<string, string> Data = new Dictionary<string, string>();
        public string Get(string key, string def) { string v; return Data.TryGetValue(key, out v) ? v : def; }
        public void Set(string key, string value) { Data[key] = value; }
        public void Save() { }
    }

    /// <summary>
    /// The player's guns: what is owned, what is equipped, and the purchase state machine of each paid gun.
    /// Engine-free (tested in PawNoon.Tests.Runtime). Persisted under <c>pn_owned_items</c>,
    /// <c>pn_owned_verified</c>, <c>pn_owned_verified_for</c> and <c>pn_equipped_gun</c>.
    ///
    /// Ownership comes from the server (purchase-validate's profile) whenever the game is online: the server's
    /// list replaces the local one, so a refund takes the gun away. "Verified" marks guns the server confirmed;
    /// only those are ever put into a PvP recording (match-submit rejects an unowned loadout). A gun granted
    /// without the server (editor mock store, a build without a backend) works in solo only.
    /// Verification belongs to the account that got it (review pass 2): <see cref="SetAccount"/> follows the signed-in
    /// user, and after a delete, a switch to another account or a new guest the guns stay owned for solo but are no
    /// longer verified, so a PvP recording never carries a gun the new account does not own (match-submit would
    /// reject it as loadout_not_owned, which counts towards the cheat flag). RESTORE verifies them again.
    ///
    /// Transitions: Locked -BeginPurchase-> Buying -StoreSucceeded-> Verifying -Granted-> Owned;
    /// Buying -PurchaseCancelled-> Locked; Verifying -Rejected-> Locked; Verifying stays Verifying on a
    /// transient server error. A restore can go Locked -> Verifying -> Owned directly.
    /// </summary>
    public sealed class GunLocker
    {
        public const string KeyOwned = "pn_owned_items";
        public const string KeyVerified = "pn_owned_verified";
        public const string KeyEquipped = "pn_equipped_gun";
        public const string KeyVerifiedFor = "pn_owned_verified_for";

        private static readonly string[] PaidGuns = { Guns.Peacemaker };

        private readonly IKeyValueStore store;
        private readonly HashSet<string> owned = new HashSet<string>();
        private readonly HashSet<string> verified = new HashSet<string>();
        private readonly Dictionary<string, GunState> states = new Dictionary<string, GunState>();
        private string equipped = Guns.Standard;
        private string account = "";       // the signed-in user (SetAccount), "" = none / unknown
        private string verifiedFor = "";   // the user the verified set belongs to

        /// <summary>Bumps on every change (screens refresh when it moves).</summary>
        public int Version { get; private set; }

        public GunLocker(IKeyValueStore store)
        {
            this.store = store ?? new MemoryKeyValueStore();
            Load();
        }

        public string Equipped { get { return equipped; } }

        public bool Owns(string gun)
        {
            string g = Guns.Normalize(gun);
            return g == Guns.Standard || owned.Contains(g);
        }

        /// <summary>Owned and confirmed by the server (safe to put in a PvP recording).</summary>
        public bool IsVerified(string gun)
        {
            string g = Guns.Normalize(gun);
            return g == Guns.Standard || (verified.Contains(g) && verifiedFor == account);
        }

        /// <summary>The account verification currently applies to ("" before any server confirmed a gun).</summary>
        public string VerifiedFor { get { return verifiedFor; } }

        /// <summary>
        /// The signed-in user id (ShopDirector, every frame; cheap when unchanged). Verified guns only count for the account
        /// that verified them. Installs from before review pass 2 have no owner recorded: the first account seen adopts them.
        /// </summary>
        public void SetAccount(string userId)
        {
            string a = userId ?? "";
            if (a == account) return;
            account = a;
            if (a.Length > 0 && verifiedFor.Length == 0 && verified.Count > 0)
            {
                verifiedFor = a;
                Save();
            }
            else Version++;
        }

        /// <summary>The account was deleted (or left): guns stay owned for solo, nothing is verified until RESTORE.</summary>
        public void ForgetVerification()
        {
            if (verified.Count == 0 && verifiedFor.Length == 0) return;
            verified.Clear();
            verifiedFor = "";
            Save();
        }

        public GunState StateOf(string gun)
        {
            string g = Guns.Normalize(gun);
            if (g == Guns.Standard) return GunState.Owned;
            GunState s;
            return states.TryGetValue(g, out s) ? s : GunState.Locked;
        }

        /// <summary>
        /// The gun this player fires with: the equipped one in solo; in PvP only a server-verified gun (else
        /// Standard); ranked scores everyone with the standard curve anyway (the look and sound still apply).
        /// </summary>
        public string GunFor(bool pvp)
        {
            if (!pvp) return Owns(equipped) ? equipped : Guns.Standard;
            return IsVerified(equipped) ? equipped : Guns.Standard;
        }

        // ------------------------------------------------------------------ purchase flow

        public bool BeginPurchase(string gun)
        {
            string g = Guns.Normalize(gun);
            if (g == Guns.Standard || StateOf(g) != GunState.Locked) return false;
            SetState(g, GunState.Buying);
            return true;
        }

        /// <summary>The store sheet closed without a charge (cancelled, failed, unavailable).</summary>
        public void PurchaseCancelled(string gun)
        {
            string g = Guns.Normalize(gun);
            if (StateOf(g) == GunState.Buying) SetState(g, GunState.Locked);
        }

        /// <summary>The store reports a purchase (new, restored or a leftover pending one): now verify it.</summary>
        public void StoreSucceeded(string gun)
        {
            string g = Guns.Normalize(gun);
            if (g == Guns.Standard) return;
            if (StateOf(g) != GunState.Owned) SetState(g, GunState.Verifying);
        }

        /// <summary>The purchase is good. <paramref name="serverVerified"/>: confirmed by purchase-validate.</summary>
        public void Granted(string gun, bool serverVerified, bool equip)
        {
            string g = Guns.Normalize(gun);
            if (g == Guns.Standard) return;
            owned.Add(g);
            if (serverVerified) AdoptVerified().Add(g);
            SetState(g, GunState.Owned);
            if (equip) equipped = g;
            Save();
        }

        /// <summary>The server refused the purchase for good (invalid receipt, refunded...).</summary>
        public void Rejected(string gun)
        {
            string g = Guns.Normalize(gun);
            if (StateOf(g) != GunState.Owned) SetState(g, GunState.Locked);
        }

        /// <summary>
        /// The server's profile (authoritative): owned items replace the local list (refunds and transfers
        /// take a gun away), the equipped gun follows when it is owned.
        /// </summary>
        public void ApplyServerProfile(IList<string> ownedItems, string equippedGun)
        {
            if (ownedItems == null) return;
            AdoptVerified();   // the answer is about the signed-in account
            var server = new HashSet<string>();
            for (int i = 0; i < ownedItems.Count; i++)
            {
                string g = Guns.Normalize(ownedItems[i]);
                if (g != Guns.Standard) server.Add(g);
            }
            for (int i = 0; i < PaidGuns.Length; i++)
            {
                string g = PaidGuns[i];
                if (server.Contains(g))
                {
                    owned.Add(g);
                    verified.Add(g);
                    SetState(g, GunState.Owned);
                }
                else
                {
                    owned.Remove(g);
                    verified.Remove(g);
                    if (StateOf(g) == GunState.Owned) SetState(g, GunState.Locked);
                }
            }
            if (!string.IsNullOrEmpty(equippedGun) && Owns(equippedGun)) equipped = Guns.Normalize(equippedGun);
            if (!Owns(equipped)) equipped = Guns.Standard;
            Save();
        }

        /// <summary>Equips an owned gun (EQUIP / UNEQUIP on the card). False when it is not owned.</summary>
        public bool Equip(string gun)
        {
            string g = Guns.Normalize(gun);
            if (!Owns(g)) return false;
            if (equipped == g) return true;
            equipped = g;
            Save();
            return true;
        }

        /// <summary>Review / QA: forget every gun (back to a fresh install).</summary>
        public void ResetAll()
        {
            owned.Clear();
            verified.Clear();
            verifiedFor = "";
            states.Clear();
            equipped = Guns.Standard;
            Save();
        }

        // ------------------------------------------------------------------ persistence

        /// <summary>A server answer for the current account: verification switches to it (another account's is dropped).</summary>
        private HashSet<string> AdoptVerified()
        {
            if (verifiedFor != account)
            {
                verified.Clear();
                verifiedFor = account;
            }
            return verified;
        }

        private void SetState(string g, GunState s)
        {
            GunState old;
            if (states.TryGetValue(g, out old) && old == s) return;
            states[g] = s;
            Version++;
        }

        private void Load()
        {
            Split(store.Get(KeyOwned, ""), owned);
            Split(store.Get(KeyVerified, ""), verified);
            verified.IntersectWith(owned);
            verifiedFor = store.Get(KeyVerifiedFor, "") ?? "";
            foreach (var g in owned) states[g] = GunState.Owned;
            string e = Guns.Normalize(store.Get(KeyEquipped, Guns.Standard));
            equipped = Owns(e) ? e : Guns.Standard;
        }

        private void Save()
        {
            store.Set(KeyOwned, Join(owned));
            store.Set(KeyVerified, Join(verified));
            store.Set(KeyVerifiedFor, verifiedFor);
            store.Set(KeyEquipped, equipped);
            store.Save();
            Version++;
        }

        private static void Split(string s, HashSet<string> into)
        {
            into.Clear();
            if (string.IsNullOrEmpty(s)) return;
            var parts = s.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string g = Guns.Normalize(parts[i]);
                if (g != Guns.Standard) into.Add(g);
            }
        }

        private static string Join(HashSet<string> set)
        {
            var list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            return string.Join(",", list.ToArray());
        }

        public override string ToString()
        {
            var sb = new StringBuilder(48);
            sb.Append(equipped);
            for (int i = 0; i < PaidGuns.Length; i++)
                sb.Append(' ').Append(PaidGuns[i]).Append(':').Append(StateOf(PaidGuns[i])).Append(IsVerified(PaidGuns[i]) && Owns(PaidGuns[i]) ? "(verified)" : "");
            return sb.ToString();
        }
    }

    /// <summary>purchase-validate's 200 body (supabase/README.md), parsed engine-free.</summary>
    public sealed class PurchaseAnswer
    {
        /// <summary>valid | pending | refunded | revoked</summary>
        public string Status = "";
        /// <summary>granted | already_owned | transferred | pending | refunded</summary>
        public string Outcome = "";
        public string Item = "";
        public string Environment = "";
        public readonly List<string> OwnedItems = new List<string>(2);
        public string EquippedGun;
        public bool HasProfile;

        public static PurchaseAnswer Parse(string json)
        {
            var root = PnJson.ParseObject(json);
            if (root == null) return null;
            var a = new PurchaseAnswer();
            var ents = PnJson.Arr(root, "entitlements");
            if (ents != null && ents.Count > 0)
            {
                a.Status = PnJson.Str(ents[0], "status", "");
                a.Item = PnJson.Str(ents[0], "item", "");
                a.Environment = PnJson.Str(ents[0], "environment", "");
            }
            a.Outcome = PnJson.Str(root, "outcome", a.Status);
            var prof = PnJson.Obj(root, "profile");
            if (prof != null)
            {
                a.HasProfile = true;
                var items = PnJson.Arr(prof, "ownedItems");
                if (items != null)
                    for (int i = 0; i < items.Count; i++)
                    {
                        var s = items[i] as string;
                        if (!string.IsNullOrEmpty(s)) a.OwnedItems.Add(s);
                    }
                a.EquippedGun = PnJson.Str(prof, "equippedGun", null);
            }
            return a;
        }

        /// <summary>Error codes after which the store transaction is finished without the item (never retried).</summary>
        public static bool IsPermanentError(string code)
        {
            switch (code)
            {
                case "invalid_receipt":
                case "wrong_app":
                case "product_mismatch":
                case "not_in_receipt":
                case "not_found":
                case "fake_store":
                case "sandbox_not_allowed":
                case "bad_receipt":
                case "unknown_product":
                case "bad_request":
                case "account_mismatch":   // review pass 2: bought for another account, no device proof
                case "owned_elsewhere":
                    return true;
                default:
                    return false;
            }
        }
    }
}
