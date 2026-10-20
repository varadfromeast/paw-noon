using System;
using System.Collections;
using System.Globalization;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// Supabase Auth for the game (SPEC_V2 §5): an anonymous guest session on first online use, persisted in
    /// PlayerPrefs (access token, refresh token, expiry, user id) and refreshed before it expires. No email or
    /// password, no custom auth.
    ///
    /// M6 seam: <see cref="LinkIdentity"/> will link Sign in with Apple / Google to this same user
    /// (supabase.auth.linkIdentity / id-token grant); until then it reports "not_implemented".
    /// </summary>
    public sealed class SupabaseAuth
    {
        private const string KAccess = "pn_auth_access";
        private const string KRefresh = "pn_auth_refresh";
        private const string KExpires = "pn_auth_expires";
        private const string KUser = "pn_auth_user";
        /// <summary>Refresh this long before the access token expires.</summary>
        private const long RefreshMarginSeconds = 60;

        private readonly NetConfig cfg;

        public string AccessToken { get; private set; }
        public string RefreshToken { get; private set; }
        public string UserId { get; private set; }
        /// <summary>Unix seconds.</summary>
        public long ExpiresAt { get; private set; }

        public bool HasSession { get { return !string.IsNullOrEmpty(AccessToken); } }
        /// <summary>Anonymous until M6 links Apple / Google.</summary>
        public bool IsGuest { get { return true; } }

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
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PawNoon] auth load failed: " + ex.Message);
                AccessToken = RefreshToken = UserId = "";
                ExpiresAt = 0;
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
            Save();
        }

        /// <summary>Marks the access token as stale (after a 401) so the next EnsureSession refreshes it.</summary>
        public void Invalidate() { ExpiresAt = 0; }

        /// <summary>
        /// Valid session or done(false, code). Codes: "offline" (no network / server down), "auth_failed".
        /// Order: keep a valid token; else refresh; else (no refresh token or it was refused) sign in anonymously.
        /// </summary>
        public IEnumerator EnsureSession(Action<bool, string> done)
        {
            if (AccessValid) { done(true, null); yield break; }

            if (!string.IsNullOrEmpty(RefreshToken))
            {
                HttpResult r = default(HttpResult);
                string body = new JsonOut(96).BeginObject().Prop("refresh_token", RefreshToken).EndObject().ToString();
                yield return NetHost.Run(Http.PostJson(cfg.AuthUrl("token?grant_type=refresh_token"), body, null, cfg.AnonKey, cfg.TimeoutSeconds, x => r = x));
                if (r.Ok && Store(r.Body)) { done(true, null); yield break; }
                if (r.Transient) { done(false, "offline"); yield break; }
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

        /// <summary>Reads a GoTrue session ({access_token, refresh_token, expires_in|expires_at, user{id}}).</summary>
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
            string uid = PnJson.Str(PnJson.Obj(o, "user"), "id", "");
            if (!string.IsNullOrEmpty(uid)) UserId = uid;
            Save();
            return true;
        }

        /// <summary>
        /// M6: link Sign in with Apple / Google to this guest (same user id, keeps bounty and MMR).
        /// <paramref name="provider"/> "apple" | "google", <paramref name="idToken"/> from the native sign-in sheet.
        /// Not implemented in M4.
        /// </summary>
        public IEnumerator LinkIdentity(string provider, string idToken, Action<bool, string> done)
        {
            done(false, "not_implemented");
            yield break;
        }
    }
}
