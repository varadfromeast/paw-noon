using System;
using PawNoon.Core;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// M5 calls (supabase/README.md "HTTP contract"), on top of <see cref="SupabaseApi.Call"/> so they share its
    /// timeout, retry, re-auth and offline detection.
    /// </summary>
    public static class SupabaseApiM5
    {
        /// <summary>POST /match-start {mode, clientBuild}: "casual" or "ranked" (ranked: 403 link_required for guests).</summary>
        public static void MatchStart(this SupabaseApi api, string mode, string clientBuild, Action<ApiResult> done)
        {
            string body = new JsonOut(96).BeginObject().Prop("mode", mode == "ranked" ? "ranked" : "casual").Prop("clientBuild", clientBuild).EndObject().ToString();
            api.Call("match-start", body, done);
        }

        /// <summary>POST /room-create {}: a 4-letter code that reserves the caller's next recording for one friend.</summary>
        public static void RoomCreate(this SupabaseApi api, Action<ApiResult> done)
        {
            api.Call("room-create", "{}", done);
        }

        /// <summary>POST /room-join {code}: 202 waiting (poll again) or 200 ready (a match-start answer).</summary>
        public static void RoomJoin(this SupabaseApi api, string code, Action<ApiResult> done)
        {
            api.Call("room-join", new JsonOut(32).BeginObject().Prop("code", code).EndObject().ToString(), done);
        }

        /// <summary>POST /leaderboard-read {board}: "ranked" (this week) or "best_reaction_hard".</summary>
        public static void LeaderboardRead(this SupabaseApi api, string board, Action<ApiResult> done)
        {
            api.Call("leaderboard-read", new JsonOut(48).BeginObject().Prop("board", board).EndObject().ToString(), done);
        }
    }
}
