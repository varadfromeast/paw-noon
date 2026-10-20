using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// Where the online backend lives: <c>Resources/Data/net_config.json</c>.
    ///
    /// SET THESE before online play works:
    ///   "supabaseUrl": "https://&lt;project-ref&gt;.supabase.co"   (Dashboard > Project Settings > API > Project URL)
    ///   "anonKey":     "&lt;anon public key&gt;"                     (same page; the anon key is public by design)
    /// Local stack: the API URL and anon key printed by `supabase status` (a phone needs the Mac's LAN IP,
    /// not 127.0.0.1, and Player Settings > "Allow downloads over HTTP" for plain http).
    ///
    /// While either value is a "YOUR-..." placeholder the game stays fully offline: PVP explains that online
    /// play is not set up (or plays the built-in mock when "mock" is true). Never put the service-role key here.
    /// </summary>
    public sealed class NetConfig
    {
        public const string ResourcePath = "Data/net_config";

        public string SupabaseUrl = "";
        public string AnonKey = "";
        /// <summary>Sent as clientBuild (ranked allow-list). Empty = Application.version.</summary>
        public string ClientBuild = "";
        /// <summary>Default pick of the PVP card's CASUAL / RANKED toggle until the player chooses (then PlayerPrefs pawnoon.pvpMode).</summary>
        public string Mode = "casual";
        public int TimeoutSeconds = 10;
        /// <summary>The FINDING A RIVAL card stays up at least this long (feel; 1-3 s).</summary>
        public float FindMinSeconds = 1.2f;
        /// <summary>Shows the FRIEND button: friend duels by room code (M5). On in the shipped net_config.json.</summary>
        public bool FriendDuel;
        /// <summary>Play against the built-in fake recording and a local recompute instead of the server.</summary>
        public bool Mock;
        /// <summary>false = no sign-in anywhere (beta builds): no KEEP YOUR BOUNTY sheet, no account UI.</summary>
        public bool Accounts = true;

        public bool IsConfigured
        {
            get
            {
                return !string.IsNullOrEmpty(SupabaseUrl) && !string.IsNullOrEmpty(AnonKey) &&
                       SupabaseUrl.IndexOf("YOUR-", System.StringComparison.Ordinal) < 0 &&
                       AnonKey.IndexOf("YOUR-", System.StringComparison.Ordinal) < 0 &&
                       (SupabaseUrl.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase) ||
                        SupabaseUrl.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase));
            }
        }

        private string Base { get { return (SupabaseUrl ?? "").TrimEnd('/'); } }
        public string FunctionUrl(string name) { return Base + "/functions/v1/" + name; }
        public string AuthUrl(string path) { return Base + "/auth/v1/" + path; }

        public string Build
        {
            get { return string.IsNullOrEmpty(ClientBuild) ? (string.IsNullOrEmpty(Application.version) ? "0.0.0" : Application.version) : ClientBuild; }
        }

        public static NetConfig Load()
        {
            var ta = Resources.Load<TextAsset>(ResourcePath);
            if (ta == null)
            {
                Debug.Log("[PawNoon] no Resources/" + ResourcePath + ".json: online play off");
                return new NetConfig();
            }
            return Parse(ta.text);
        }

        public static NetConfig Parse(string json)
        {
            var c = new NetConfig();
            var o = PnJson.ParseObject(json);
            if (o == null) return c;
            c.SupabaseUrl = (PnJson.Str(o, "supabaseUrl", "") ?? "").Trim();
            c.AnonKey = (PnJson.Str(o, "anonKey", "") ?? "").Trim();
            c.ClientBuild = (PnJson.Str(o, "clientBuild", "") ?? "").Trim();
            c.Mode = PnJson.Str(o, "mode", "casual") == "ranked" ? "ranked" : "casual";
            c.TimeoutSeconds = Mathf.Clamp(PnJson.Int(o, "timeoutSeconds", 10), 2, 60);
            c.FindMinSeconds = Mathf.Clamp((float)PnJson.Num(o, "findMinSeconds", 1.2), 0f, 3f);
            c.FriendDuel = PnJson.Bool(o, "friendDuel", false);
            c.Mock = PnJson.Bool(o, "mock", false);
            c.Accounts = PnJson.Bool(o, "accounts", true);
            return c;
        }
    }
}
