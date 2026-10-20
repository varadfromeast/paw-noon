using System;
using System.Collections;
using System.Globalization;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// Supabase Auth for the game (SPEC_V2 §5): an anonymous guest session on first online use, persisted in
    /// PlayerPrefs (access token, refresh token, expiry, user id, anonymous flag, linked providers) and refreshed
    /// before it expires. No email or password, no custom auth.
    ///
    /// M6: <see cref="LinkApple"/> / <see cref="LinkGoogle"/> attach a Sign in with Apple / Google identity to this
    /// same guest user (the user id does not change, so profile, matches, recordings and MMR stay). Endpoint (GoTrue,
    /// what supabase-js <c>linkIdentity({provider, token, nonce})</c> sends):
    /// <code>
    /// POST /auth/v1/token?grant_type=id_token
    ///   Authorization: Bearer &lt;guest access token&gt;   apikey: &lt;anon key&gt;
    ///   { "provider": "apple"|"google", "id_token": "...", "nonce": "&lt;raw nonce&gt;", "link_identity": true }
    /// </code>
    /// Needs Auth > "Manual linking" on. When the Apple / Google account already belongs to another user the answer
    /// is 422 identity_already_exists: the player may switch to that account (<see cref="SignInWithIdToken"/>, the
    /// same grant without the bearer and without link_identity). Docs/ACCOUNTS.md has the fallback.
    /// </summary>
    public sealed class SupabaseAuth
    {
        private const string KAccess = "pn_auth_access";
        private const string KRefresh = "pn_auth_refresh";
        private const string KExpires = "pn_auth_expires";
        private const string KUser = "pn_auth_user";
        private const string KAnon = "pn_auth_anon";
        private const string KProviders = "pn_auth_providers";
        private const string KMock = "pn_auth_mock";
        /// <summary>Refresh this long before the access token expires.</summary>
        private const long RefreshMarginSeconds = 60;

        private readonly NetConfig cfg;
        private string[] providers = new string[0];
        // a session for another user that an id-token grant returned (IdentityTaken), kept until the player decides
        private string pendingSwitchBody;

        public string AccessToken { get; private set; }
        public string RefreshToken { get; private set; }
        public string UserId { get; private set; }
        /// <summary>Unix seconds.</summary>
        public long ExpiresAt { get; private set; }
        /// <summary>The session's user is anonymous (true until a link succeeds; also true with no session).</summary>
        public bool IsAnonymous { get; private set; }
        /// <summary>The link was simulated (Editor mock / ReviewDirector MockLink), not done by a server.</summary>
        public bool MockLinked { get; private set; }

        public bool HasSession { get { return !string.IsNullOrEmpty(AccessToken); } }

        /// <summary>Linked providers other than anonymous ("apple", "google").</summary>
        public string[] Providers { get { return providers; } }

        public bool HasProvider(string p)
        {
            for (int i = 0; i < providers.Length; i++) if (providers[i] == p) return true;
            return false;
        }

        /// <summary>
        /// Linked to Sign in with Apple or Google (M5 ranked relies on this; the server checks the same thing with
        /// <c>hasLinkedIdentity</c>). False for guests and when there is no session.
        /// </summary>
        public bool IsLinked
        {
            get { return (HasSession || MockLinked) && !IsAnonymous && (HasProvider("apple") || HasProvider("google")); }
        }

        /// <summary>Not linked (a device guest, or no session yet).</summary>
        public bool IsGuest { get { return !IsLinked; } }

        /// <summary>"apple" / "google" / "" (the first linked one).</summary>
        public string LinkedProvider
        {
            get
            {
                if (!IsLinked) return "";
                return HasProvider("apple") ? "apple" : "google";
            }
        }

        /// <summary>For ReviewDirector Status: none | guest | stale | apple | google | mock-apple | mock-google.</summary>
        public string StatusWord
        {
            get
            {
                if (MockLinked && IsLinked) return "mock-" + LinkedProvider;
                if (!HasSession) return "none";
                if (!AccessValid) return "stale";
                return IsLinked ? LinkedProvider : "guest";
            }
        }

        /// <summary>
        /// The ranked gate on the client (M5): <see cref="IsLinked"/> (M6, from the auth user's identities), or the
        /// access token's <c>is_anonymous</c> claim, unless the review override is set. The server checks again on
        /// every ranked match-start (403 link_required). The override only affects the PVP card, not the account UI.
        /// </summary>
        public bool RankedLinked
        {
            get
            {
                if (LinkedOverride != 0) return LinkedOverride > 0;
                return IsLinked || (HasSession && JwtClaims.IsLinked(AccessToken));
            }
        }

        /// <summary>Review / QA (ReviewDirector.SetLinked): 1 = treat as linked, -1 = guest, 0 = the real token.</summary>
        public static int LinkedOverride;

        public SupabaseAuth(NetConfig cfg)
        {
            this.cfg = cfg;
            Load();
        }

        public static long NowUnix() { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }

        public bool AccessValid { get { return HasSession && ExpiresAt - RefreshMarginSeconds > NowUnix(); } }

        private void Load()
        {
            try
            {
                AccessToken = PlayerPrefs.GetString(KAccess, "");
                RefreshToken = PlayerPrefs.GetString(KRefresh, "");
                UserId = PlayerPrefs.GetString(KUser, "");
                long e;
                ExpiresAt = long.TryParse(PlayerPrefs.GetString(KExpires, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out e) ? e : 0;
                // sessions stored before M6 have no flags: they were guests
                IsAnonymous = PlayerPrefs.GetInt(KAnon, 1) == 1;
                providers = AuthUserInfo.Split(PlayerPrefs.GetString(KProviders, ""));
                MockLinked = PlayerPrefs.GetInt(KMock, 0) == 1;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PawNoon] auth load failed: " + ex.Message);
                AccessToken = RefreshToken = UserId = "";
                ExpiresAt = 0;
                IsAnonymous = true;
                providers = new string[0];
                MockLinked = false;
            }
        }

        private void Save()
        {
            try
            {
                PlayerPrefs.SetString(KAccess, AccessToken ?? "");
                PlayerPrefs.SetString(KRefresh, RefreshToken ?? "");
                PlayerPrefs.SetString(KUser, UserId ?? "");
                PlayerPrefs.SetString(KExpires, ExpiresAt.ToString(CultureInfo.InvariantCulture));
                PlayerPrefs.SetInt(KAnon, IsAnonymous ? 1 : 0);
                PlayerPrefs.SetString(KProviders, AuthUserInfo.Join(providers));
                PlayerPrefs.SetInt(KMock, MockLinked ? 1 : 0);
                PlayerPrefs.Save();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PawNoon] auth save failed: " + ex.Message);
            }
        }

        /// <summary>Forgets the session (the next online call signs in as a new guest).</summary>
        public void SignOut()
        {
            AccessToken = RefreshToken = UserId = "";
            ExpiresAt = 0;
            IsAnonymous = true;
            providers = new string[0];
            MockLinked = false;
            pendingSwitchBody = null;
            Save();
        }

        /// <summary>Marks the access token as stale (after a 401) so the next EnsureSession refreshes it.</summary>
        public void Invalidate() { ExpiresAt = 0; }

        /// <summary>
        /// Valid session or done(false, code). Codes: "offline" (no network / server down), "auth_failed".
        /// Order: keep a valid token; else refresh; else (no refresh token or it was refused) sign in anonymously.
        /// A linked player whose refresh token was refused is NOT silently turned into a new guest: done(false,
        /// "relink") so the UI can ask for Apple / Google again (their account is safe on the server).
        /// </summary>
        public IEnumerator EnsureSession(Action<bool, string> done)
        {
            if (AccessValid) { done(true, null); yield break; }

            bool wasLinked = IsLinked && !MockLinked;
            if (!string.IsNullOrEmpty(RefreshToken))
            {
                HttpResult r = default(HttpResult);
                string body = new JsonOut(96).BeginObject().Prop("refresh_token", RefreshToken).EndObject().ToString();
                yield return NetHost.Run(Http.PostJson(cfg.AuthUrl("token?grant_type=refresh_token"), body, null, cfg.AnonKey, cfg.TimeoutSeconds, x => r = x));
                if (r.Ok && Store(r.Body)) { done(true, null); yield break; }
                if (r.Transient) { done(false, "offline"); yield break; }
                if (wasLinked)
                {
                    Debug.LogWarning("[PawNoon] refresh refused (" + r.Status + ") for a linked account: sign in with " + LinkedProvider + " again");
                    done(false, "relink");
                    yield break;
                }
                Debug.LogWarning("[PawNoon] refresh refused (" + r.Status + "), signing in as a new guest");
            }

            // anonymous sign-in: POST /auth/v1/signup with an empty body (Auth > "Anonymous sign-ins" must be on)
            HttpResult s = default(HttpResult);
            yield return NetHost.Run(Http.PostJson(cfg.AuthUrl("signup"), "{}", null, cfg.AnonKey, cfg.TimeoutSeconds, x => s = x));
            if (s.Ok && Store(s.Body)) { done(true, null); yield break; }
            if (s.Transient) { done(false, "offline"); yield break; }
            Debug.LogWarning("[PawNoon] anonymous sign-in failed (" + s.Status + "): " + Http.Short(s.Body));
            done(false, "auth_failed");
        }

        /// <summary>Reads a GoTrue session ({access_token, refresh_token, expires_in|expires_at, user{...}}).</summary>
        private bool Store(string json)
        {
            var o = PnJson.ParseObject(json);
            string access = PnJson.Str(o, "access_token", "");
            if (string.IsNullOrEmpty(access)) return false;
            AccessToken = access;
            string refresh = PnJson.Str(o, "refresh_token", "");
            if (!string.IsNullOrEmpty(refresh)) RefreshToken = refresh;
            long at = (long)PnJson.Num(o, "expires_at", 0);
            if (at <= 0) at = NowUnix() + (long)PnJson.Num(o, "expires_in", 3600);
            ExpiresAt = at;
            var user = AuthUserInfo.Parse(PnJson.Obj(o, "user"));
            if (user != null)
            {
                UserId = user.Id;
                IsAnonymous = user.IsAnonymous;
                providers = user.Providers;
                MockLinked = false;
            }
            Save();
            return true;
        }

        // ------------------------------------------------------------------ M6: Apple / Google

        /// <summary>Links Sign in with Apple. <paramref name="nonce"/> is the RAW nonce (its SHA-256 went to Apple).</summary>
        public IEnumerator LinkApple(string idToken, string nonce, Action<LinkOutcome, string> done)
        {
            return Link("apple", idToken, nonce, done);
        }

        /// <summary>Links Google Sign-In. <paramref name="nonce"/>: the raw nonce whose SHA-256 went to Google, or null.</summary>
        public IEnumerator LinkGoogle(string idToken, string nonce, Action<LinkOutcome, string> done)
        {
            return Link("google", idToken, nonce, done);
        }

        public IEnumerator LinkGoogle(string idToken, Action<LinkOutcome, string> done)
        {
            return Link("google", idToken, null, done);
        }

        /// <summary>M4 seam kept for callers that only need ok / code.</summary>
        public IEnumerator LinkIdentity(string provider, string idToken, Action<bool, string> done)
        {
            return Link(provider, idToken, null, (o, code) => done(o == LinkOutcome.Linked, o == LinkOutcome.Linked ? null : code));
        }

        /// <summary>
        /// Guest session first (signs up anonymously if there is none), then the id-token grant with
        /// <c>link_identity: true</c> and the guest's bearer. Outcome codes: see <see cref="LinkOutcome"/>; the string
        /// is the server's error code (or "offline", "auth_failed", "relink").
        /// </summary>
        public IEnumerator Link(string provider, string idToken, string nonce, Action<LinkOutcome, string> done)
        {
            pendingSwitchBody = null;
            if (!cfg.IsConfigured) { done(LinkOutcome.Unavailable, "not_configured"); yield break; }
            if (string.IsNullOrEmpty(idToken)) { done(LinkOutcome.Failed, "no_token"); yield break; }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool ok = false;
                string err = null;
                yield return NetHost.Run(EnsureSession((a, b) => { ok = a; err = b; }));
                if (!ok)
                {
                    done(err == "offline" ? LinkOutcome.Offline : LinkOutcome.Failed, err ?? "auth_failed");
                    yield break;
                }
                string guestId = UserId;
                string body = IdTokenBody(provider, idToken, nonce, true);
                HttpResult r = default(HttpResult);
                yield return NetHost.Run(Http.PostJson(cfg.AuthUrl("token?grant_type=id_token"), body, AccessToken, cfg.AnonKey, cfg.TimeoutSeconds, x => r = x));

                if (r.Status == 401 && attempt == 0)
                {
                    Invalidate();   // guest token revoked: refresh / re-sign-in once, then link again
                    continue;
                }
                if (r.Transient) { done(LinkOutcome.Offline, "offline"); yield break; }
                if (r.Ok)
                {
                    var o = PnJson.ParseObject(r.Body);
                    var user = AuthUserInfo.Parse(PnJson.Obj(o, "user"));
                    var outcome = LinkAnswer.Classify(guestId, user, NowUnix());
                    switch (outcome)
                    {
                        case LinkOutcome.Linked:
                            if (!Store(r.Body)) { done(LinkOutcome.Failed, "bad_session"); yield break; }
                            Debug.Log("[PawNoon] linked " + provider + " to " + UserId + " (same user id, guest stats kept)");
                            done(LinkOutcome.Linked, null);
                            yield break;
                        case LinkOutcome.IdentityTaken:
                            pendingSwitchBody = r.Body;
                            done(outcome, "identity_already_exists");
                            yield break;
                        default:
                            Debug.LogWarning("[PawNoon] the id-token grant made a new user instead of linking: is Auth > Manual linking on and Auth up to date? (Docs/ACCOUNTS.md)");
                            done(LinkOutcome.LinkUnsupported, "link_unsupported");
                            yield break;
                    }
                }
                var e = AuthError.Parse(r.Status, r.Body);
                Debug.LogWarning("[PawNoon] link " + provider + " -> " + r.Status + " " + e.Code + ": " + e.Message);
                done(e.IdentityTaken ? LinkOutcome.IdentityTaken : LinkOutcome.Failed, e.Code);
                yield break;
            }
            done(LinkOutcome.Failed, "unauthorized");
        }

        /// <summary>
        /// Signs in as the Apple / Google account's own player (a returning player on a new phone), leaving this
        /// guest behind. Uses the session a link attempt already returned when there is one, else the plain
        /// id-token grant (no bearer, no link_identity).
        /// </summary>
        public IEnumerator SignInWithIdToken(string provider, string idToken, string nonce, Action<bool, string> done)
        {
            if (!string.IsNullOrEmpty(pendingSwitchBody))
            {
                string b = pendingSwitchBody;
                pendingSwitchBody = null;
                if (Store(b)) { done(true, null); yield break; }
            }
            if (!cfg.IsConfigured) { done(false, "not_configured"); yield break; }
            HttpResult r = default(HttpResult);
            yield return NetHost.Run(Http.PostJson(cfg.AuthUrl("token?grant_type=id_token"), IdTokenBody(provider, idToken, nonce, false), null, cfg.AnonKey, cfg.TimeoutSeconds, x => r = x));
            if (r.Ok && Store(r.Body)) { done(true, null); yield break; }
            if (r.Transient) { done(false, "offline"); yield break; }
            var e = AuthError.Parse(r.Status, r.Body);
            Debug.LogWarning("[PawNoon] sign in with " + provider + " -> " + r.Status + " " + e.Code + ": " + e.Message);
            done(false, e.Code);
        }

        /// <summary>Drops a session kept for a possible switch (the player chose to stay a guest).</summary>
        public void ForgetPendingSwitch() { pendingSwitchBody = null; }

        private static string IdTokenBody(string provider, string idToken, string nonce, bool link)
        {
            var j = new JsonOut(idToken.Length + 160).BeginObject().Prop("provider", provider).Prop("id_token", idToken);
            if (!string.IsNullOrEmpty(nonce)) j.Prop("nonce", nonce);
            if (link) j.Prop("link_identity", true);
            return j.EndObject().ToString();
        }

        /// <summary>
        /// Editor mock / ReviewDirector MockLink: pretend the link worked (no network). Keeps the session as it is;
        /// stored so the UI survives a restart. Cleared by SignOut.
        /// </summary>
        public void SimulateLink(string provider)
        {
            IsAnonymous = false;
            providers = new[] { provider == "apple" ? "apple" : "google" };
            MockLinked = true;
            if (string.IsNullOrEmpty(UserId)) UserId = "mock-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            Save();
        }
    }
}
