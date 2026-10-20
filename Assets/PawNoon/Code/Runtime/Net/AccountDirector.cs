using System;
using System.Globalization;
using System.Text;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// Accounts (SPEC_V2 §5, M6): KEEP YOUR BOUNTY once after the first solo match, the same sheet from Settings >
    /// ACCOUNT, the profile card on the title (name, cat, rating, best, duels, wins, LIGHTNING, EDIT NAME with the pixel
    /// keypad), Sign in with Apple / Google linking the guest (same user id), and DELETE ACCOUNT (two-step confirm,
    /// account-delete, local state cleared). Solo play never waits on any of this.
    ///
    /// Mock (no server): net mock on, or the Editor while net_config.json is not set up. The native sheet is then the
    /// Editor mock and the link / name / delete are simulated locally, so the whole UI flow can be exercised.
    /// </summary>
    public sealed class AccountDirector
    {
        private const string KProfileCache = "pn_profile_cache";
        private const string KMockName = "pn_mock_name";
        private const string KGuestNo = "pn_guest_no";
        private const float OfferDelaySeconds = 1.4f;

        private enum Ask { None, Delete1, Delete2, Deleting, Switch, Switching, Info, InfoThenTitle }

        private sealed class PrefsFlags : IFlagStore
        {
            public bool Get(string key)
            {
                try { return PlayerPrefs.GetInt(key, 0) == 1; }
                catch (Exception) { return false; }
            }

            public void Set(string key, bool value)
            {
                try
                {
                    PlayerPrefs.SetInt(key, value ? 1 : 0);
                    PlayerPrefs.Save();
                }
                catch (Exception e) { Debug.LogWarning("[PawNoon] flag save failed: " + e.Message); }
            }
        }

        private PawGame game;
        private OnlineDirector online;
        private SupabaseAuth auth;
        private SupabaseApi api;
        private NetConfig cfg;
        private AuthConfig authCfg;
        private INativeSignIn realNative;
        private MockNativeSignIn mockNative;
        private BountyGate gate;
        private UiKit kit;

        private AccountSheet sheet;
        private ProfileCard card;
        private NameKeypad keypad;
        private ConfirmCard confirm;

        private ProfileInfo profile;
        private bool profileLoading;
        private string profileNote = "";
        private bool sheetBounty;
        private bool sheetOverSettings;
        private float offerT = -1f;
        private Ask ask;
        private int linkToken;
        private int profileToken;
        private string linkProvider, linkIdToken, linkNonce;
        /// <summary>Mock server answer for the next simulated link: "linked" or "taken" (ReviewDirector MockLink).</summary>
        private string mockServer = "linked";
        private string lastEvent = "";

        public SupabaseAuth Auth { get { return auth; } }
        public bool Mock { get { return online.Mock || (Application.isEditor && !cfg.IsConfigured); } }
        public bool Available { get { return Mock || cfg.IsConfigured; } }
        /// <summary>Any account card is up: it owns the input (PawGame ticks it before the screen below).</summary>
        public bool Open { get { return card.IsOpen || sheet.IsOpen || keypad.IsOpen || confirm.IsOpen; } }
        private INativeSignIn Native { get { return Mock ? (INativeSignIn)mockNative : realNative; } }

        public void Init(PawGame g, UiKit uiKit, GameLayout layout, PixelPipeline pipe, Transform uiRoot, OnlineDirector onlineDirector)
        {
            game = g;
            kit = uiKit;
            online = onlineDirector;
            auth = online.Auth;
            api = online.Api;
            cfg = online.Config;
            authCfg = AuthConfig.Load();
            mockNative = new MockNativeSignIn(authCfg);
            realNative = NativeSignIn.Create(authCfg, false);
            gate = new BountyGate(new PrefsFlags());

            card = NewScreen<ProfileCard>("ProfileCard", uiRoot, uiKit, layout, pipe);
            sheet = NewScreen<AccountSheet>("AccountSheet", uiRoot, uiKit, layout, pipe);
            keypad = NewScreen<NameKeypad>("NameKeypad", uiRoot, uiKit, layout, pipe);
            confirm = NewScreen<ConfirmCard>("AccountConfirm", uiRoot, uiKit, layout, pipe);

            string cached = null;
            try { cached = PlayerPrefs.GetString(KProfileCache, ""); } catch (Exception) { }
            if (!string.IsNullOrEmpty(cached))
            {
                string err;
                profile = ProfileInfo.Parse(cached, out err);
            }
        }

        private static T NewScreen<T>(string name, Transform parent, UiKit k, GameLayout l, PixelPipeline p) where T : ScreenBase
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var s = go.AddComponent<T>();
            s.Setup(k, l, p);
            return s;
        }

        // ------------------------------------------------------------------ hooks from PawGame

        /// <summary>A solo match ended (PawGame.OnMatchFinished): offer KEEP YOUR BOUNTY once, after the banner lands.</summary>
        public void OnSoloMatchFinished()
        {
            if (gate.ShouldOffer(true, auth.IsLinked, Available)) offerT = OfferDelaySeconds;
        }

        /// <summary>
        /// Every frame on the title and the match result screen. True while an account card is open (it took the
        /// input; the caller must not act on its own buttons this frame).
        /// </summary>
        public bool Tick()
        {
            if (offerT >= 0f)
            {
                if (game.CurrentMode != PawGame.Mode.MatchResult) offerT = -1f;
                else
                {
                    offerT -= PawTime.UnscaledDt;
                    if (offerT < 0f) { offerT = -1f; OpenBounty(true); }
                }
            }
            if (!Open) return false;
            if (confirm.IsOpen) TickConfirm();
            else if (keypad.IsOpen) TickKeypad();
            else if (sheet.IsOpen) TickSheet();
            else if (card.IsOpen) TickCard();
            return true;
        }

        /// <summary>Title or a new screen: close every account card (late answers are ignored).</summary>
        public void CloseAll()
        {
            offerT = -1f;
            ask = Ask.None;
            linkToken++;
            profileToken++;
            profileLoading = false;
            confirm.Close();
            keypad.Close();
            sheet.Close();
            card.Close();
        }

        // ------------------------------------------------------------------ open

        public void OpenProfile()
        {
            card.Open();
            FillCard();
            RefreshDims();
            LoadProfile();
        }

        /// <summary>Settings > ACCOUNT (or ACCOUNT on the profile card).</summary>
        public void OpenAccount()
        {
            sheetBounty = false;
            sheetOverSettings = game.SettingsOpenState != "closed" && !card.IsOpen;
            ShowSheet();
            if (profile == null) LoadProfile();
        }

        /// <summary><paramref name="spend"/>: the real once-only offer (false for review stills).</summary>
        public void OpenBounty(bool spend)
        {
            if (spend) gate.MarkOffered();
            sheetBounty = true;
            sheetOverSettings = false;
            ShowSheet();
            lastEvent = "bounty_shown";
        }

        private bool Supports(string provider) { return Available && Native.Supports(provider); }

        private void ShowSheet()
        {
            bool linked = auth.IsLinked;
            bool a = !linked && Supports("apple");
            bool g = !linked && Supports("google");
            string l1, l2;
            if (sheetBounty)
            {
                l1 = "Link Apple or Google so your";
                l2 = "bounty follows you to any phone.";
                sheet.Show("KEEP YOUR BOUNTY", BountyLine(), l1, l2, a, g, false, "LATER");
            }
            else
            {
                l1 = linked ? "LINKED WITH " + Upper(auth.LinkedProvider) : "GUEST ON THIS PHONE";
                l2 = linked ? "Your bounty is safe on any phone." : "Link to keep your bounty safe.";
                if (!linked && !Available) l2 = "Online is not set up in this build.";
                else if (!linked && !a && !g) l2 = "Sign-in is not available here.";
                sheet.Show("ACCOUNT", CurrentName(), l1, l2, a, g, true, "CLOSE");
            }
            RefreshDims();
        }

        private static string Upper(string s) { return string.IsNullOrEmpty(s) ? "" : s.ToUpperInvariant(); }

        private string BountyLine()
        {
            long best = ProfileInfo.CombinedBest(profile, PawSettings.BestNs);
            return best > 0 ? "YOUR BEST: " + BestText(best) : "A NEW GUN IN TOWN";
        }

        private static string BestText(long ns)
        {
            if (ns <= 0) return "--";
            string s = NumFmt.Seconds6(ns);
            return (s.Length > 5 ? s.Substring(0, 5) : s) + " s";
        }

        /// <summary>
        /// Every open card dims what is under it (a stacked confirm / keypad darkens the card below, so the eye goes to
        /// the top one), except the sheet opened from Settings, which sits on the settings dim already.
        /// </summary>
        private void RefreshDims()
        {
            card.SetDim(true);
            sheet.SetDim(!(sheetOverSettings && !card.IsOpen));
            keypad.SetDim(true);
            confirm.SetDim(true);
        }

        // ------------------------------------------------------------------ sheet + linking

        private void TickSheet()
        {
            switch (sheet.Tick())
            {
                case SheetAction.Apple: BeginLink("apple"); break;
                case SheetAction.Google: BeginLink("google"); break;
                case SheetAction.Delete: ShowAsk(Ask.Delete1); break;
                case SheetAction.Close:
                    sheet.Close();
                    linkToken++;
                    RefreshDims();
                    if (card.IsOpen) FillCard();
                    break;
            }
        }

        private void BeginLink(string provider)
        {
            int my = ++linkToken;   // a newer request makes any late answer of an older one irrelevant
            linkProvider = provider;
            linkIdToken = null;
            linkNonce = Nonce.NewRaw();
            sheet.SetBusy(provider == "apple" ? "WAITING FOR APPLE" : "WAITING FOR GOOGLE");
            lastEvent = "native:" + provider;
            Native.Begin(provider, Nonce.Sha256Hex(linkNonce), r => { if (my == linkToken) OnNative(r, my); });
        }

        private void OnNative(NativeSignInResult r, int my)
        {
            if (!r.Ok)
            {
                lastEvent = "native:" + (r.Cancelled ? "cancelled" : r.Error);
                if (r.Cancelled) sheet.SetStatus(null, Pal.Sand0);
                else if (r.Error == "unavailable") sheet.SetStatus("NOT AVAILABLE ON THIS PHONE", Pal.SigRed);
                else if (r.Error == "offline") sheet.SetStatus("NO SIGNAL. TRY AGAIN.", Pal.SigRed);
                else sheet.SetStatus("COULD NOT SIGN IN. TRY AGAIN.", Pal.SigRed);
                return;
            }
            linkIdToken = r.IdToken;
            if (Mock)
            {
                string m = mockServer;
                mockServer = "linked";
                OnLinkAnswer(m == "taken" ? LinkOutcome.IdentityTaken : LinkOutcome.Linked, m == "taken" ? "identity_already_exists" : null);
                return;
            }
            sheet.SetBusy("SAVING YOUR BOUNTY");
            string provider = linkProvider;
            NetHost.Run(auth.Link(provider, r.IdToken, linkNonce, (o, code) => { if (my == linkToken) OnLinkAnswer(o, code); }));
        }

        private void OnLinkAnswer(LinkOutcome o, string code)
        {
            lastEvent = "link:" + o + (string.IsNullOrEmpty(code) ? "" : ":" + code);
            switch (o)
            {
                case LinkOutcome.Linked:
                    if (Mock) auth.SimulateLink(linkProvider);
                    ForgetLinkSecrets();
                    OnLinked("LINKED! YOUR BOUNTY IS SAFE.");
                    return;
                case LinkOutcome.IdentityTaken:
                    sheet.SetStatus(null, Pal.Sand0);
                    ShowAsk(Ask.Switch);
                    return;
                case LinkOutcome.LinkUnsupported:
                    ForgetLinkSecrets();
                    sheet.SetStatus("LINKING IS OFF ON THE SERVER", Pal.SigRed);
                    return;
                case LinkOutcome.Offline:
                    ForgetLinkSecrets();
                    sheet.SetStatus("NO SIGNAL. TRY AGAIN.", Pal.SigRed);
                    return;
                case LinkOutcome.Unavailable:
                    ForgetLinkSecrets();
                    sheet.SetStatus("ONLINE IS NOT SET UP", Pal.SigRed);
                    return;
                default:
                    ForgetLinkSecrets();
                    sheet.SetStatus("COULD NOT SIGN IN. TRY AGAIN.", Pal.SigRed);
                    return;
            }
        }

        private void ForgetLinkSecrets()
        {
            linkIdToken = null;
            linkNonce = null;
            auth.ForgetPendingSwitch();
        }

        private void OnLinked(string statusLine)
        {
            gate.MarkOffered();
            if (sheet.IsOpen)
            {
                sheet.SetLinkButtons(false, false);
                if (!sheetBounty) { sheet.SetBig(CurrentName()); sheet.SetLines("LINKED WITH " + Upper(auth.LinkedProvider), "Your bounty is safe on any phone."); }
                sheet.SetStatus(statusLine, Pal.SigGreen);
                sheet.SetCloseLabel("DONE");
            }
            if (kit.Audio != null) kit.Audio.Play("sfx_stamp", 0.8f);
            PawHaptics.Notify(0);
            if (card.IsOpen) FillCard();
            LoadProfile();
        }

        // ------------------------------------------------------------------ confirm cards

        private void ShowAsk(Ask a)
        {
            ask = a;
            switch (a)
            {
                case Ask.Delete1:
                    confirm.Show("DELETE ACCOUNT?", "YOUR BOUNTY GOES TOO", "Name, rating, duels and ghosts", "are removed for good.", "KEEP IT", "DELETE");
                    break;
                case Ask.Delete2:
                    confirm.Show("LAST CHANCE", "THIS CANNOT BE UNDONE", "Store purchases can be restored", "later from the store.", "CANCEL", "YES, DELETE");
                    break;
                case Ask.Deleting:
                    confirm.Show("DELETE ACCOUNT", "RIDING OUT OF TOWN...", "Telling the sheriff.", null, null, null);
                    break;
                case Ask.Switch:
                    confirm.Show("ALREADY WANTED", "THAT ACCOUNT HAS A BOUNTY", "Switch to it? This guest's duels", "stay behind on the server.", "STAY GUEST", "SWITCH");
                    break;
                case Ask.Switching:
                    confirm.Show("SWITCHING", "SADDLING UP...", null, null, null, null);
                    break;
            }
            RefreshDims();
        }

        private void ShowInfo(string head, string big, string l1, string l2, bool thenTitle)
        {
            ask = thenTitle ? Ask.InfoThenTitle : Ask.Info;
            confirm.Show(head, big, l1, l2, null, "OK");
            RefreshDims();
        }

        private void CloseAsk()
        {
            ask = Ask.None;
            confirm.Close();
            RefreshDims();
        }

        private void TickConfirm()
        {
            int c = confirm.Tick();
            if (c == 0) return;
            switch (ask)
            {
                case Ask.Delete1:
                    if (c == 2) ShowAsk(Ask.Delete2); else CloseAsk();
                    break;
                case Ask.Delete2:
                    if (c == 2) DoDelete(); else CloseAsk();
                    break;
                case Ask.Switch:
                    if (c == 2) DoSwitch();
                    else
                    {
                        ForgetLinkSecrets();
                        CloseAsk();
                        sheet.SetStatus("STILL A GUEST. NOTHING CHANGED.", Pal.Sand0);
                    }
                    break;
                case Ask.InfoThenTitle:
                    CloseAll();
                    game.ShowTitle();
                    break;
                default:
                    CloseAsk();
                    break;
            }
        }

        private void DoSwitch()
        {
            ShowAsk(Ask.Switching);
            int my = linkToken;
            string provider = linkProvider;
            if (Mock)
            {
                auth.SimulateLink(provider);
                OnSwitched(true, null);
                return;
            }
            NetHost.Run(auth.SignInWithIdToken(provider, linkIdToken, linkNonce, (ok, code) => { if (my == linkToken) OnSwitched(ok, code); }));
        }

        private void OnSwitched(bool ok, string code)
        {
            ForgetLinkSecrets();
            lastEvent = ok ? "switched:" + auth.LinkedProvider : "switch_failed:" + code;
            if (!ok)
            {
                ShowInfo("NO LUCK", code == "offline" ? "NO SIGNAL" : "COULD NOT SWITCH", "You are still the guest", "on this phone. Try again soon.", false);
                return;
            }
            // a different player now: forget this guest's cached profile, bounty count and gun verification
            profile = null;
            DeleteKey(KProfileCache);
            ForgetAccountState();
            CloseAsk();
            OnLinked("WELCOME BACK, PARTNER.");
        }

        // ------------------------------------------------------------------ delete

        private void DoDelete()
        {
            ShowAsk(Ask.Deleting);
            lastEvent = "deleting";
            // nothing on a server: never went online, or the mock
            if (Mock || !cfg.IsConfigured || !auth.HasSession)
            {
                ClearLocal();
                Deleted();
                return;
            }
            int my = ++profileToken;
            api.Call("account-delete", "{\"confirm\":\"DELETE\"}", r =>
            {
                if (my != profileToken || ask != Ask.Deleting) return;
                if (r.Ok)
                {
                    ClearLocal();
                    Deleted();
                    return;
                }
                lastEvent = "delete_failed:" + r.Error.code;
                if (r.Offline) ShowInfo("NOT DELETED", "NO SIGNAL", "Your account is unchanged.", "Try again with a connection.", false);
                else ShowInfo("NOT DELETED", "TELEGRAPH IS DOWN", "Your account is unchanged.", "Try again soon.", false);
            });
        }

        private void Deleted()
        {
            lastEvent = "deleted";
            ShowInfo("ACCOUNT DELETED", "GONE WITH THE WIND", "A new stranger rides into town.", null, true);
        }

        /// <summary>
        /// State that belongs to one server account (review pass 2): today's PvP bounty count + streak (the server's
        /// numbers for that account) and the shop's server verification of owned guns (a new account must not put a gun
        /// it does not own into a PvP recording: match-submit would reject it as loadout_not_owned and count it towards
        /// the cheat flag). Owned guns stay for solo; RESTORE verifies them for the new account.
        /// </summary>
        private void ForgetAccountState()
        {
            if (online != null && online.Bounty != null) online.Bounty.ResetAll();
            if (game != null && game.Shop != null) game.Shop.OnAccountGone();
        }

        /// <summary>
        /// Everything this install knows about the player: session, cached profile, guest number, the once-only
        /// bounty flag, undelivered match submissions, the all-time best, the daily bounty state and the guns' server
        /// verification. Device settings (sound, haptics, motion, difficulty) and pending store receipts stay.
        /// </summary>
        private void ClearLocal()
        {
            ForgetAccountState();
            auth.SignOut();
            DeleteKey(KProfileCache);
            DeleteKey(KMockName);
            DeleteKey(KGuestNo);
            gate.Reset();
            var q = api.Queue;
            while (q.Count > 0) q.Remove(q[0].matchId);
            PawSettings.BestNs = -1;
            PawSettings.Save();
            profile = null;
        }

        private static void DeleteKey(string k)
        {
            try
            {
                PlayerPrefs.DeleteKey(k);
                PlayerPrefs.Save();
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ profile

        private int GuestNo()
        {
            try
            {
                int n = PlayerPrefs.GetInt(KGuestNo, -1);
                if (n < 0)
                {
                    n = UnityEngine.Random.Range(0, 10000);
                    PlayerPrefs.SetInt(KGuestNo, n);
                    PlayerPrefs.Save();
                }
                return n;
            }
            catch (Exception) { return DisplayName.GuestNumber(auth.UserId); }
        }

        public string CurrentName()
        {
            if (profile != null) return DisplayName.ForDisplay(profile.DisplayName);
            return DisplayName.GuestName(GuestNo());
        }

        private ProfileInfo MockProfile()
        {
            string name = null;
            try { name = PlayerPrefs.GetString(KMockName, ""); } catch (Exception) { }
            var p = profile != null ? profile : new ProfileInfo();
            p.DisplayName = string.IsNullOrEmpty(name) ? DisplayName.GuestName(GuestNo()) : name;
            p.IsGuest = !auth.IsLinked;
            p.Providers = auth.Providers;
            return p;
        }

        private void LoadProfile()
        {
            if (Mock)
            {
                profile = MockProfile();
                profileNote = "MOCK PROFILE (NO SERVER)";
                FillCard();
                return;
            }
            if (!cfg.IsConfigured)
            {
                profileNote = "OFFLINE PROFILE";
                FillCard();
                return;
            }
            if (profileLoading) return;
            profileLoading = true;
            profileNote = "LOADING...";
            FillCard();
            int my = ++profileToken;
            api.Call("profile-read", "{}", r =>
            {
                if (my != profileToken) return;
                profileLoading = false;
                OnProfileAnswer(r);
            });
        }

        private void OnProfileAnswer(ApiResult r)
        {
            if (r.Ok)
            {
                string err;
                var p = ProfileInfo.Parse(r.Body, out err);
                if (p != null)
                {
                    profile = p;
                    profileNote = "";
                    try
                    {
                        PlayerPrefs.SetString(KProfileCache, r.Body);
                        PlayerPrefs.Save();
                    }
                    catch (Exception) { }
                }
                else
                {
                    Debug.LogWarning("[PawNoon] profile-read answer unreadable: " + err);
                    profileNote = "GARBLED WIRE";
                }
            }
            else if (r.Offline) profileNote = profile != null ? "OFFLINE: LAST KNOWN" : "OFFLINE";
            else if (r.Error.code == "relink") profileNote = "SIGN IN AGAIN TO SYNC";
            else profileNote = "COULD NOT LOAD PROFILE";
            FillCard();
            if (sheet.IsOpen && !sheetBounty) sheet.SetBig(CurrentName());
        }

        private void FillCard()
        {
            if (!card.IsOpen) return;
            var p = profile;
            string cat = Upper(p != null ? p.Character : CharacterRoster.Whiskers);
            string who = auth.IsLinked ? "LINKED: " + Upper(auth.LinkedProvider) : "GUEST";
            string rating = p != null ? p.Mmr.ToString(CultureInfo.InvariantCulture) : "1200";
            string best = BestText(ProfileInfo.CombinedBest(p, PawSettings.BestNs));
            string duels = p != null ? p.Matches.ToString(CultureInfo.InvariantCulture) : "0";
            string wins = p != null && p.Matches > 0
                ? p.Wins.ToString(CultureInfo.InvariantCulture) + " (" + p.WinPercent.ToString(CultureInfo.InvariantCulture) + "%)"
                : "0";
            string lightning = p != null ? p.Lightning.ToString(CultureInfo.InvariantCulture) : "0";
            card.Set(CurrentName(), cat + "   " + who, rating, best, duels, wins, lightning);
            bool warn = profileNote.StartsWith("OFFLINE", StringComparison.Ordinal) || profileNote.StartsWith("COULD", StringComparison.Ordinal);
            card.SetStatus(profileNote, warn ? Pal.PaintRed : Pal.Wood3);
            card.SetEditable(Available);
        }

        private void TickCard()
        {
            switch (card.Tick())
            {
                case ProfileAction.Close:
                    card.Close();
                    profileToken++;
                    profileLoading = false;
                    break;
                case ProfileAction.EditName:
                    keypad.Begin(profile != null ? profile.DisplayName : null);
                    RefreshDims();
                    break;
                case ProfileAction.Account:
                    OpenAccount();
                    break;
            }
        }

        // ------------------------------------------------------------------ name

        private void TickKeypad()
        {
            switch (keypad.Tick())
            {
                case KeypadAction.Cancel:
                    keypad.Close();
                    RefreshDims();
                    break;
                case KeypadAction.Save:
                    SaveName();
                    break;
            }
        }

        private void SaveName()
        {
            string name;
            var e = DisplayName.Validate(keypad.Text, out name);
            if (e != DisplayNameError.None)
            {
                keypad.ShowHint(DisplayName.Explain(e));
                return;
            }
            if (Mock)
            {
                try
                {
                    PlayerPrefs.SetString(KMockName, name);
                    PlayerPrefs.Save();
                }
                catch (Exception) { }
                profile = MockProfile();
                lastEvent = "name:" + name;
                keypad.Close();
                FillCard();
                RefreshDims();
                return;
            }
            if (!cfg.IsConfigured)
            {
                keypad.ShowHint("ONLINE IS NOT SET UP");
                return;
            }
            keypad.SetBusy(true);
            int my = ++profileToken;
            string body = new JsonOut(64).BeginObject().Prop("displayName", name).EndObject().ToString();
            api.Call("profile-update", body, r =>
            {
                if (my != profileToken || !keypad.IsOpen) return;
                keypad.SetBusy(false);
                if (r.Ok)
                {
                    string err;
                    var p = ProfileInfo.Parse(r.Body, out err);
                    if (p != null)
                    {
                        profile = p;
                        profileNote = "";
                        try
                        {
                            PlayerPrefs.SetString(KProfileCache, r.Body);
                            PlayerPrefs.Save();
                        }
                        catch (Exception) { }
                    }
                    lastEvent = "name:" + name;
                    keypad.Close();
                    FillCard();
                    RefreshDims();
                    return;
                }
                lastEvent = "name_failed:" + r.Error.code + ":" + r.Error.reason;
                if (r.Error.code == "bad_display_name")
                {
                    var why = DisplayName.FromWire(r.Error.reason);
                    keypad.ShowHint(why != DisplayNameError.None ? DisplayName.Explain(why) : "THE SHERIFF SAYS NO");
                }
                else if (r.Offline) keypad.ShowHint("NO SIGNAL. TRY AGAIN.");
                else keypad.ShowHint("COULD NOT SAVE. TRY AGAIN.");
            });
        }

        // ------------------------------------------------------------------ review / QA

        /// <summary>ReviewDirector Show bounty | profile | account | keypad | delete | switch.</summary>
        public string ShowForReview(string what)
        {
            switch (what)
            {
                case "bounty":
                    game.ShowTitle();
                    OpenBounty(false);
                    return "ok";
                case "profile":
                    game.ShowTitle();
                    OpenProfile();
                    return "ok";
                case "account":
                    game.ShowSettingsSample();
                    OpenAccount();
                    return "ok";
                case "keypad":
                    game.ShowTitle();
                    OpenProfile();
                    keypad.Begin(profile != null ? profile.DisplayName : "BILLY THE CAT");
                    RefreshDims();
                    return "ok";
                case "delete":
                    game.ShowSettingsSample();
                    OpenAccount();
                    ShowAsk(Ask.Delete1);
                    return "ok";
                case "switch":
                    game.ShowTitle();
                    OpenBounty(false);
                    linkProvider = "google";
                    ShowAsk(Ask.Switch);
                    return "ok";
            }
            return "error: unknown account view '" + what + "'";
        }

        /// <summary>
        /// ReviewDirector MockLink: "apple" / "google" = a successful link (through the open sheet's UI when one is up),
        /// "taken" = the next sheet link finds the account already used (switch prompt), "cancel" / "fail" = the next
        /// native sheet is cancelled / fails, "reset" = back to an unlinked guest and the bounty offer re-armed.
        /// </summary>
        public string MockLink(string what)
        {
            switch (what)
            {
                case "apple":
                case "google":
                    if (sheet.IsOpen && !sheet.Busy)
                    {
                        linkProvider = what;
                        auth.SimulateLink(what);
                        lastEvent = "link:Linked(mock)";
                        OnLinked("LINKED! YOUR BOUNTY IS SAFE.");
                    }
                    else
                    {
                        auth.SimulateLink(what);
                        lastEvent = "link:Linked(mock)";
                        if (card.IsOpen) FillCard();
                    }
                    return "ok: linked " + what + " (simulated, user id kept: " + auth.UserId + ")";
                case "taken":
                    mockServer = "taken";
                    if (!sheet.IsOpen) OpenBounty(false);
                    BeginLink(Supports("apple") ? "apple" : "google");
                    return "ok: link started, the mock server will answer identity_already_exists";
                case "cancel":
                case "fail":
                    MockNativeSignIn.NextOutcome = what;
                    return "ok: the next native sign-in will " + (what == "cancel" ? "be cancelled" : "fail");
                case "reset":
                    auth.SignOut();
                    gate.Reset();
                    profile = null;
                    DeleteKey(KProfileCache);
                    DeleteKey(KMockName);
                    if (card.IsOpen) FillCard();
                    return "ok: guest again, KEEP YOUR BOUNTY re-armed";
            }
            return "error: expected apple, google, taken, cancel, fail or reset";
        }

        public string Status()
        {
            var sb = new StringBuilder(96);
            sb.Append("account=");
            if (confirm.IsOpen) sb.Append("confirm:").Append(ask);
            else if (keypad.IsOpen) sb.Append("keypad");
            else if (sheet.IsOpen) sb.Append(sheetBounty ? "bounty" : "sheet");
            else if (card.IsOpen) sb.Append("profile");
            else sb.Append("closed");
            sb.Append(" accounts=").Append(Mock ? "mock" : (cfg.IsConfigured ? "live" : "off"));
            sb.Append(" bounty=").Append(gate.Offered ? "offered" : "fresh");
            if (offerT >= 0f) sb.Append("(due)");
            sb.Append(" name=\"").Append(CurrentName()).Append('"');
            if (lastEvent.Length > 0) sb.Append(" last=").Append(lastEvent);
            return sb.ToString();
        }
    }
}
