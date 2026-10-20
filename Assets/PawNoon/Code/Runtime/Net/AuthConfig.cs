using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// Native sign-in settings: <c>Resources/Data/auth_config.json</c> (Docs/ACCOUNTS.md says where each value comes from).
    ///
    ///   "googleWebClientId": the Google Cloud OAuth client of type "Web application". Android Credential Manager
    ///                        asks for an ID token for THIS client (serverClientId); Supabase's Google provider
    ///                        lists it first under Client IDs.
    ///   "googleIosClientId": the OAuth client of type "iOS" (bundle com.pawnoon.pixel). iOS signs in to Google
    ///                        with it (ASWebAuthenticationSession + PKCE, no Google SDK).
    ///   "apple" / "google":  show the button at all (Apple is iOS only regardless).
    ///
    /// Client IDs are public (they ship inside every app); no secret ever goes here. While a value still contains
    /// "YOUR-" that provider is treated as unavailable on devices (the Editor mock ignores this).
    /// </summary>
    public sealed class AuthConfig
    {
        public const string ResourcePath = "Data/auth_config";

        public string GoogleWebClientId = "";
        public string GoogleIosClientId = "";
        public bool Apple = true;
        public bool Google = true;

        private static bool Set(string v)
        {
            return !string.IsNullOrEmpty(v) && v.IndexOf("YOUR-", System.StringComparison.Ordinal) < 0;
        }

        public bool GoogleAndroidReady { get { return Google && Set(GoogleWebClientId); } }
        public bool GoogleIosReady { get { return Google && Set(GoogleIosClientId); } }

        public static AuthConfig Load()
        {
            var ta = Resources.Load<TextAsset>(ResourcePath);
            return Parse(ta != null ? ta.text : null);
        }

        public static AuthConfig Parse(string json)
        {
            var c = new AuthConfig();
            var o = PnJson.ParseObject(json);
            if (o == null) return c;
            c.GoogleWebClientId = (PnJson.Str(o, "googleWebClientId", "") ?? "").Trim();
            c.GoogleIosClientId = (PnJson.Str(o, "googleIosClientId", "") ?? "").Trim();
            c.Apple = PnJson.Bool(o, "apple", true);
            c.Google = PnJson.Bool(o, "google", true);
            return c;
        }
    }
}
