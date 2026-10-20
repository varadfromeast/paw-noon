using System;
using System.Globalization;
using PawNoon.Core;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// M5 parts of the offline stand-in (net_config "mock", ReviewDirector SetNetMock / Show room|join|leaderboard):
    /// rooms that answer like room-create / room-join, and two fake leaderboards, so every screen can be seen
    /// without a server.
    /// </summary>
    public static partial class NetMock
    {
        /// <summary>Codes the mock hands out (generator alphabet: no I / O).</summary>
        private static readonly string[] Codes = { "HOWL", "DUST", "SPUR", "COLT", "MESA", "LASS" };
        private static int nextCode;

        public static RoomCreated CreateRoom()
        {
            var r = new RoomCreated();
            r.Code = Codes[nextCode++ % Codes.Length];
            r.TtlSec = 600;
            r.ExpiresAt = DateTime.UtcNow.AddSeconds(600).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            return r;
        }

        /// <summary>The mock's room-join errors: "XXXX" is unknown, "OLDY" closed, "MINE" your own; anything else works.</summary>
        public static string JoinError(string code)
        {
            switch (code)
            {
                case "XXXX": return "NO ROOM WITH THAT CODE.";
                case "OLDY": return "ROOM CLOSED. ASK FOR A NEW CODE.";
                case "MINE": return "THAT'S YOUR OWN ROOM. SHARE IT!";
                default: return null;
            }
        }

        /// <summary>room-join: waiting (the host has not dueled yet) or ready (the host's ghost, seeded from the code).</summary>
        public static RoomJoinAnswer JoinRoom(string code, bool ready)
        {
            var a = new RoomJoinAnswer();
            a.Code = code;
            a.HostName = "Pardner Pete";
            if (!ready)
            {
                a.Waiting = true;
                a.PollAfterSec = 3;
                a.ExpiresAt = DateTime.UtcNow.AddSeconds(541).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                return a;
            }
            int seed = 0;
            for (int i = 0; i < code.Length; i++) seed = seed * 31 + code[i];
            var m = MatchStart(seed & 0x7fffffff);
            m.MatchId = "mock-room-" + code;
            m.OpponentName = a.HostName;
            m.OpponentIsHouse = false;
            m.OpponentMmr = 1187;
            m.Room = code;
            a.Match = m;
            return a;
        }

        private static readonly string[] Names =
        {
            "Marshal Mittens", "Kit Carson", "Dusty", "Old Tom", "Calamity Paws", "Whisker Wyatt", "Doc Purrliday",
            "Belle Starr", "Tabby Twofeet", "Sundance", "Black Bart", "Annie Oakpaw", "Jesse Mews", "Stranger #4471",
            "Pecos Puss", "Billie the Kit", "Ginger Ike", "Rawhide", "Mesa Mae", "Sagebrush", "Ruby Ridge", "Tumble",
            "Copperhead", "Lefty", "Smokey Jo", "Stranger #0912", "Cactus Kate", "Long Tom", "Midnight", "Dutch",
        };

        /// <summary>A believable 50-row board. Ranked: you are 87th (pinned below the list). Fastest: you are 12th.</summary>
        public static LeaderboardBoard Leaderboard(bool ranked)
        {
            var b = new LeaderboardBoard();
            b.Kind = ranked ? LeaderboardBoard.Ranked : LeaderboardBoard.Fastest;
            b.Board = ranked ? "ranked_2026_43" : LeaderboardBoard.Fastest;
            b.Season = ranked ? 202643 : 0;
            b.MinMatches = ranked ? 5 : 0;
            b.Total = ranked ? 412 : 1873;
            b.Top = new LeaderboardEntry[50];
            var rng = new Random(ranked ? 43 : 7);
            long v = ranked ? 1702 : 151_300_000L;
            int rank = 1;
            for (int i = 0; i < 50; i++)
            {
                if (i > 0)
                {
                    long step = ranked ? rng.Next(0, 9) : rng.Next(0, 1_400_000);
                    if (step > 0) rank = i + 1;      // equal values share a rank
                    v += ranked ? -step : step;
                }
                bool you = !ranked && i == 11;
                b.Top[i] = new LeaderboardEntry
                {
                    Rank = rank,
                    Name = you ? "Stranger #2026" : Names[(i * 7 + (ranked ? 3 : 0)) % Names.Length] + (i >= Names.Length ? " II" : ""),
                    Character = i % 3 == 0 ? "blackpaw" : "whiskers",
                    Value = v,
                    Matches = 5 + rng.Next(0, 40),
                    You = you
                };
            }
            if (ranked)
            {
                b.You = new LeaderboardEntry { Rank = 87, Value = 1214, Matches = 9, You = true };
                b.YouEligible = true;
            }
            else
            {
                b.You = new LeaderboardEntry { Rank = 12, Value = b.Top[11].Value, Matches = 23, You = true };
                b.YouEligible = true;
            }
            return b;
        }
    }
}
