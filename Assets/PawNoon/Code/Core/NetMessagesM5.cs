using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PawNoon.Core
{
    /// <summary>200 from <c>POST /room-create</c> (schemas/room-create.response.schema.json).</summary>
    public sealed class RoomCreated
    {
        public string Code;
        public string ExpiresAt;
        public int TtlSec = 600;

        public static RoomCreated Parse(string json, out string error)
        {
            error = null;
            var root = PnJson.ParseObject(json);
            if (root == null) { error = "not a JSON object"; return null; }
            var r = new RoomCreated();
            r.Code = RoomCode.Normalize(PnJson.Str(root, "code", null));
            if (r.Code == null) { error = "missing or bad code"; return null; }
            r.ExpiresAt = PnJson.Str(root, "expiresAt", null);
            r.TtlSec = PnJson.Int(root, "ttlSec", 600);
            return r;
        }
    }

    /// <summary>
    /// <c>POST /room-join</c>: 202 waiting (the host has not recorded yet: poll again) or 200 ready (a match-start
    /// answer with the host's recording as the opponent).
    /// </summary>
    public sealed class RoomJoinAnswer
    {
        public bool Waiting;
        public string Code;
        public string HostName = "Stranger";
        public string ExpiresAt;
        public int PollAfterSec = 3;
        /// <summary>Ready: the match against the host's recording.</summary>
        public MatchStartInfo Match;

        public static RoomJoinAnswer Parse(string json, out string error)
        {
            error = null;
            var root = PnJson.ParseObject(json);
            if (root == null) { error = "not a JSON object"; return null; }
            var a = new RoomJoinAnswer();
            if (PnJson.Str(root, "status", "") == "waiting")
            {
                a.Waiting = true;
                a.Code = RoomCode.Normalize(PnJson.Str(root, "code", null));
                a.HostName = PnJson.Str(root, "hostName", "Stranger");
                a.ExpiresAt = PnJson.Str(root, "expiresAt", null);
                a.PollAfterSec = Math.Max(1, Math.Min(30, PnJson.Int(root, "pollAfterSec", 3)));
                return a;
            }
            a.Match = MatchStartInfo.Parse(json, out error);
            if (a.Match == null) return null;
            a.Code = a.Match.Room;
            a.HostName = a.Match.OpponentName;
            return a;
        }
    }

    /// <summary>One row of a leaderboard.</summary>
    public sealed class LeaderboardEntry
    {
        /// <summary>1-based; ties share a rank. 0 = not ranked yet (the caller's own row before minMatches).</summary>
        public int Rank;
        public string Name = "";
        public string Character = "";
        /// <summary>Ranked: MMR. Fastest draw: best shot reaction in ns.</summary>
        public long Value;
        public int Matches;
        public bool You;
    }

    /// <summary>200 from <c>POST /leaderboard-read</c> (schemas/leaderboard-read.response.schema.json).</summary>
    public sealed class LeaderboardBoard
    {
        public const string Ranked = "ranked";
        public const string Fastest = "best_reaction_hard";

        public string Board = "";
        public string Kind = Ranked;
        public int Season;
        public string ClosesAt;
        public int MinMatches;
        public int Total;
        public LeaderboardEntry[] Top = new LeaderboardEntry[0];
        /// <summary>The caller's row (null: never played this board).</summary>
        public LeaderboardEntry You;
        public bool YouEligible;

        public bool IsRanked { get { return Kind == Ranked; } }

        public static LeaderboardBoard Parse(string json, out string error)
        {
            error = null;
            var root = PnJson.ParseObject(json);
            if (root == null) { error = "not a JSON object"; return null; }
            var b = new LeaderboardBoard();
            b.Kind = PnJson.Str(root, "kind", "");
            if (b.Kind != Ranked && b.Kind != Fastest) { error = "unknown kind"; return null; }
            b.Board = PnJson.Str(root, "board", "");
            b.Season = PnJson.Int(root, "season", 0);
            b.ClosesAt = PnJson.Str(root, "closesAt", null);
            b.MinMatches = PnJson.Int(root, "minMatches", 0);
            b.Total = PnJson.Int(root, "total", 0);
            var top = PnJson.Arr(root, "top");
            int n = top == null ? 0 : Math.Min(top.Count, 50);
            b.Top = new LeaderboardEntry[n];
            for (int i = 0; i < n; i++)
            {
                var e = Entry(top[i]);
                if (e == null) { error = "top[" + i + "] is malformed"; return null; }
                b.Top[i] = e;
            }
            var you = PnJson.Obj(root, "you");
            if (you != null)
            {
                b.You = new LeaderboardEntry
                {
                    Rank = Math.Max(0, PnJson.Int(you, "rank", 0)),
                    Value = MatchStartInfo.Ns(you, "value", 0),
                    Matches = PnJson.Int(you, "matches", 0),
                    You = true
                };
                b.YouEligible = PnJson.Bool(you, "eligible", b.You.Rank > 0);
            }
            return b;
        }

        private static LeaderboardEntry Entry(object node)
        {
            if (!(node is Dictionary<string, object>)) return null;
            return new LeaderboardEntry
            {
                Rank = Math.Max(1, PnJson.Int(node, "rank", 1)),
                Name = PnJson.Str(node, "displayName", "Stranger"),
                Character = PnJson.Str(node, "character", ""),
                Value = MatchStartInfo.Ns(node, "value", 0),
                Matches = PnJson.Int(node, "matches", 0),
                You = PnJson.Bool(node, "you", false)
            };
        }

        /// <summary>"ranked_2026_43" -> "WEEK 43"; anything else -> "".</summary>
        public static string WeekLabel(string board)
        {
            if (string.IsNullOrEmpty(board) || !board.StartsWith("ranked_", StringComparison.Ordinal)) return "";
            int us = board.LastIndexOf('_');
            int w;
            if (us < 0 || !int.TryParse(board.Substring(us + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out w)) return "";
            return board.StartsWith("ranked_season_", StringComparison.Ordinal) ? "SEASON " + w : "WEEK " + w;
        }
    }

    /// <summary>
    /// Reads claims from a Supabase access token (JWT) without verifying it: the client only uses them for
    /// presentation (e.g. "is this session still a guest?"). The server always re-checks on its side.
    /// </summary>
    public static class JwtClaims
    {
        public static Dictionary<string, object> Payload(string jwt)
        {
            if (string.IsNullOrEmpty(jwt)) return null;
            int a = jwt.IndexOf('.');
            if (a < 0) return null;
            int b = jwt.IndexOf('.', a + 1);
            if (b < 0) return null;
            string part = jwt.Substring(a + 1, b - a - 1).Replace('-', '+').Replace('_', '/');
            switch (part.Length % 4)
            {
                case 2: part += "=="; break;
                case 3: part += "="; break;
                case 1: return null;
            }
            try
            {
                return PnJson.ParseObject(Encoding.UTF8.GetString(Convert.FromBase64String(part)));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// True only for a session that is positively NOT anonymous (Supabase puts <c>is_anonymous</c> in every
        /// access token). Unreadable or missing claims count as a guest.
        /// </summary>
        public static bool IsLinked(string jwt)
        {
            var p = Payload(jwt);
            if (p == null || !p.ContainsKey("is_anonymous")) return false;
            return !PnJson.Bool(p, "is_anonymous", true);
        }
    }
}
