using System;
using PawNoon.Core;
using PawNoon.Runtime.Net;
using UnityEngine;

namespace PawNoon.Runtime.Shop
{
    /// <summary>PlayerPrefs behind <see cref="IKeyValueStore"/> (every call guarded: a storage failure never breaks the game).</summary>
    public sealed class PlayerPrefsStore : IKeyValueStore
    {
        public string Get(string key, string def)
        {
            try { return PlayerPrefs.GetString(key, def); }
            catch (Exception) { return def; }
        }

        public void Set(string key, string value)
        {
            try { PlayerPrefs.SetString(key, value ?? ""); }
            catch (Exception e) { Debug.LogWarning("[PawNoon] prefs write failed: " + e.Message); }
        }

        public void Save()
        {
            try { PlayerPrefs.Save(); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// The shop and the loadout (SPEC_V2 §6, M7): SHOP button on the title, the store panel, the IAP service,
    /// and handing the equipped gun to the duel (PawGame.BeginDuel -> <see cref="ApplyLoadout"/>).
    /// Solo never needs the network; PvP only fires a server-verified Peacemaker; ranked scores everyone
    /// with the standard curve (Guns.Effective) while the hero keeps the Peacemaker's look and sound.
    /// </summary>
    public sealed class ShopDirector
    {
        private GunLocker locker;
        private IapService iap;
        private ShopScreen screen;
        private ShopButton button;
        private OnlineDirector online;
        private int shownVersion = -1;
        private int shownMessage = -1;
        private string shownPrice;

        public GunLocker Locker { get { return locker; } }
        public IapService Iap { get { return iap; } }
        public bool IsOpen { get { return screen != null && screen.IsOpen; } }

        public void Init(UiKit kit, GameLayout layout, PixelPipeline pipe, Transform uiRoot, OnlineDirector onlineDirector, bool forceMockStore = false)
        {
            online = onlineDirector;
            var prefs = new PlayerPrefsStore();
            locker = new GunLocker(prefs);
            iap = new IapService(IapService.CreateStore(prefs, forceMockStore), locker, prefs,
                () => online != null ? online.Api : null,
                () => online != null && online.Api != null && online.Config != null && online.Config.IsConfigured && !online.Mock,
                () => online != null && online.Auth != null ? online.Auth.UserId : null);
            var go = new GameObject("Shop");
            go.transform.SetParent(uiRoot, false);
            screen = go.AddComponent<ShopScreen>();
            screen.Setup(kit, layout, pipe);
            button = new ShopButton(kit, uiRoot, pipe);
            iap.Init();
        }

        /// <summary>Every frame: store timers and retries; the SHOP button follows the title's interactivity.</summary>
        public void Tick(float dt, bool titleInteractive)
        {
            // verified guns belong to the signed-in account (string compare when unchanged, no allocation)
            if (online != null && online.Auth != null) locker.SetAccount(online.Auth.UserId);
            iap.Tick(dt);
            button.SetVisible(titleInteractive && !IsOpen);
            if (IsOpen) Refresh();
        }

        /// <summary>Title frame, before the title's own buttons: true when SHOP was tapped (the panel opens).</summary>
        public bool TickButton()
        {
            if (!button.Tick()) return false;
            Open();
            return true;
        }

        /// <summary>Title frame while the panel is up (instead of the title's input).</summary>
        public void TickOpen()
        {
            switch (screen.Tick())
            {
                case ShopAction.Close:
                    if (screen.IsOpen) { PlayUi("sfx_ui_back"); Close(); }
                    break;
                case ShopAction.Buy:
                    iap.Buy(Guns.Peacemaker);
                    break;
                case ShopAction.Equip:
                    bool on = locker.Equipped != Guns.Peacemaker;
                    iap.Equip(on ? Guns.Peacemaker : Guns.Standard);
                    if (PawGame.Instance != null && PawGame.Instance.Audio != null)
                        PawGame.Instance.Audio.Play(on ? "sfx_peacemaker_draw" : "sfx_holster", 0.8f);
                    break;
                case ShopAction.Restore:
                    iap.Restore();
                    break;
            }
        }

        private static void PlayUi(string key)
        {
            if (PawGame.Instance != null && PawGame.Instance.Audio != null) PawGame.Instance.Audio.Play(key, 0.8f);
        }

        public void Open()
        {
            if (!screen.IsOpen) screen.Open();
            shownVersion = -1;
            Refresh();
        }

        public void Close()
        {
            if (screen.IsOpen) screen.Close();
        }

        private void Refresh()
        {
            string p = iap.PriceOf(Guns.Peacemaker);
            if (shownVersion == locker.Version && shownMessage == iap.MessageVersion && shownPrice == p) return;
            shownVersion = locker.Version;
            shownMessage = iap.MessageVersion;
            shownPrice = p;
            screen.SetView(new ShopCardView
            {
                State = locker.StateOf(Guns.Peacemaker),
                Equipped = locker.Equipped == Guns.Peacemaker,
                Price = p,
                Message = iap.Message
            });
        }

        // ------------------------------------------------------------------ duel hook

        /// <summary>
        /// Hands the loadout to the duel before a match (PawGame.BeginDuel). Solo: the equipped gun vs the bot's
        /// standard one. PvP: a server-verified gun only, the ghost's recorded gun; ranked normalises both.
        /// </summary>
        public void ApplyLoadout(DuelDirector director, bool pvp, bool ranked, string opponentGun)
        {
            director.SetGuns(locker.GunFor(pvp), pvp ? opponentGun : Guns.Standard, pvp && ranked);
        }

        /// <summary>The gun to write into a PvP recording (match-submit checks it against owned_items).</summary>
        public string RecordingGun() { return locker.GunFor(true); }

        // ------------------------------------------------------------------ review / QA

        /// <summary>Review: deliver a mock purchase now (granted locally, never server-verified).</summary>
        public void MockBuy(string gun)
        {
            var mock = iap.Store as MockStore;
            string pid = Guns.ProductFor(gun);
            if (pid == null) return;
            if (mock != null) { locker.BeginPurchase(gun); mock.DeliverNow(pid); }
            else locker.Granted(gun, false, true);
        }

        public bool Equip(string gun) { return iap.Equip(gun); }

        /// <summary>DELETE ACCOUNT (or a switch to another account): guns stay for solo, PvP verification is gone until RESTORE.</summary>
        public void OnAccountGone()
        {
            locker.ForgetVerification();
            Close();
        }

        public void ResetGuns()
        {
            locker.ResetAll();
            var mock = iap.Store as MockStore;
            if (mock != null) mock.Forget();
        }

        public string Status()
        {
            return "gun=" + locker.Equipped + " shop=" + (IsOpen ? "open" : "closed") + " " + locker + " store=" + iap.Store.Name +
                   (iap.Ready ? "" : "(not ready)") + " iapPending=" + iap.PendingCount;
        }
    }
}
