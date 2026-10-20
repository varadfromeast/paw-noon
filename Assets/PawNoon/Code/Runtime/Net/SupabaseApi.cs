using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PawNoon.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace PawNoon.Runtime.Net
{
    /// <summary>Hidden MonoBehaviour that runs the network coroutines (child of the PawNoon root).</summary>
    public sealed class NetHost : MonoBehaviour
    {
        private static NetHost instance;

        public static NetHost Ensure(Transform parent)
        {
            if (instance != null) return instance;
            var go = new GameObject("Net");
            if (parent != null) go.transform.SetParent(parent, false);
            instance = go.AddComponent<NetHost>();
            return instance;
        }

        /// <summary>Starts a coroutine on the host (yield the returned Coroutine to wait for it).</summary>
        public static Coroutine Run(IEnumerator routine)
        {
            if (instance == null) Ensure(null);
            return instance.StartCoroutine(routine);
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }

    /// <summary>One HTTP exchange.</summary>
    public struct HttpResult
    {
        /// <summary>HTTP status, 0 when the request never got an answer.</summary>
        public long Status;
        public string Body;
        /// <summary>No connection / DNS / TLS / timeout.</summary>
        public bool NetworkError;
        public string Error;

        public bool Ok { get { return !NetworkError && Status >= 200 && Status < 300; } }

        /// <summary>Worth retrying / queueing: no answer, timeout, rate limit or a server-side failure.</summary>
        public bool Transient { get { return NetworkError || Status == 0 || Status == 408 || Status == 429 || Status >= 500; } }
    }

    public static class Http
    {
        /// <summary>POST a JSON body; headers apikey (+ Authorization: Bearer when <paramref name="bearer"/> is set).</summary>
        public static IEnumerator PostJson(string url, string json, string bearer, string apiKey, int timeoutSeconds, Action<HttpResult> done)
        {
            var res = new HttpResult();
            UnityWebRequest req = null;
            try
            {
                req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json ?? "{}"));
                req.uploadHandler.contentType = "application/json";
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", "application/json");
                if (!string.IsNullOrEmpty(apiKey)) req.SetRequestHeader("apikey", apiKey);
                if (!string.IsNullOrEmpty(bearer)) req.SetRequestHeader("Authorization", "Bearer " + bearer);
                req.timeout = Mathf.Max(1, timeoutSeconds);
            }
            catch (Exception e)
            {
                // malformed URL etc.: report as a network failure, never throw into the game
                if (req != null) req.Dispose();
                req = null;
                res.NetworkError = true;
                res.Error = e.Message;
            }
            if (req == null)
            {
                done(res);
                yield break;
            }

            // SendWebRequest throws when plain http is blocked (Player Settings > Allow downloads over HTTP)
            UnityWebRequestAsyncOperation op = null;
            try { op = req.SendWebRequest(); }
            catch (Exception e)
            {
                req.Dispose();
                res.NetworkError = true;
                res.Error = e.Message;
                Debug.LogWarning("[PawNoon] request not sent: " + e.Message);
            }
            if (op == null)
            {
                done(res);
                yield break;
            }
            yield return op;

            try
            {
                res.Status = req.responseCode;
                res.Body = req.downloadHandler != null ? req.downloadHandler.text : null;
                res.NetworkError = req.result == UnityWebRequest.Result.ConnectionError;
                res.Error = req.error;
                if (req.result == UnityWebRequest.Result.DataProcessingError && res.Status == 0) res.NetworkError = true;
            }
            catch (Exception e)
            {
                res.NetworkError = true;
                res.Error = e.Message;
            }
            finally
            {
                req.Dispose();
            }
            done(res);
        }

        public static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 200 ? s : s.Substring(0, 200) + "...";
        }
    }

    /// <summary>Outcome of a function call.</summary>
    public sealed class ApiResult
    {
        public bool Ok;
        public long Status;
        public string Body;
        /// <summary>Could not reach the server (or it kept failing): try later.</summary>
        public bool Offline;
        /// <summary>Error body (schemas/error.schema.json) or a local code ("offline", "auth_failed").</summary>
        public ApiError Error;
        /// <summary>match-submit only: saved to the offline queue, will be sent later.</summary>
        public bool Queued;

        /// <summary>
        /// Not a definitive answer, worth sending again later (review pass 2): no answer, no session right now (status 0:
        /// offline, auth_failed, relink), 401 after the re-auth, 408, 429 rate_limited, any 5xx (500 internal / db_error
        /// and 503 busy put a match back to `issued`, supabase/README.md). Only these keep a match-submit queued; a
        /// definitive 4xx (409 already_submitted, 422 rejected / late, 404, 400) drops it.
        /// </summary>
        public bool RetryLater
        {
            get { return !Ok && (Offline || Status == 0 || Status == 401 || Status == 408 || Status == 429 || Status >= 500); }
        }
    }

    /// <summary>
    /// match-submit requests that could not be delivered, persisted to
    /// <c>Application.persistentDataPath/pn_submit_queue.json</c> and sent on the next launch or the next
    /// successful call. The server accepts a recording until the match deadline (match duration + 60 s); a
    /// later one is answered 422 "late" (voided, not counted against the player) and dropped.
    /// </summary>
    public sealed class SubmitQueue
    {
        public const int MaxItems = 20;

        public struct Entry
        {
            public string matchId;
            public string body;
            public string queuedAt;
        }

        private readonly List<Entry> items = new List<Entry>(4);
        private readonly string path;

        public SubmitQueue(string path)
        {
            this.path = path;
            Load();
        }

        public int Count { get { return items.Count; } }
        public Entry this[int i] { get { return items[i]; } }

        public void Add(string matchId, string body)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].matchId == matchId) { items.RemoveAt(i); break; }
            if (items.Count >= MaxItems) items.RemoveAt(0);
            items.Add(new Entry { matchId = matchId, body = body, queuedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
            Save();
        }

        public void Remove(string matchId)
        {
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i].matchId == matchId) items.RemoveAt(i);
            Save();
        }

        private void Load()
        {
            items.Clear();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                var arr = PnJson.Arr(PnJson.Parse(File.ReadAllText(path, Encoding.UTF8)), "items");
                if (arr == null) return;
                for (int i = 0; i < arr.Count; i++)
                {
                    var it = new Entry
                    {
                        matchId = PnJson.Str(arr[i], "matchId", null),
                        body = PnJson.Str(arr[i], "body", null),
                        queuedAt = PnJson.Str(arr[i], "queuedAt", "")
                    };
                    if (!string.IsNullOrEmpty(it.matchId) && !string.IsNullOrEmpty(it.body)) items.Add(it);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawNoon] submit queue unreadable, starting empty: " + e.Message);
                items.Clear();
            }
        }

        private void Save()
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var j = new JsonOut(256);
                j.BeginObject().Key("items").BeginArray();
                for (int i = 0; i < items.Count; i++)
                    j.BeginObject().Prop("matchId", items[i].matchId).Prop("body", items[i].body).Prop("queuedAt", items[i].queuedAt).EndObject();
                j.EndArray().EndObject();
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, j.ToString(), Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawNoon] submit queue not saved: " + e.Message);
            }
        }
    }

    /// <summary>
    /// The game's door to the Supabase Edge Functions (supabase/README.md "HTTP contract"): JSON POST to
    /// <c>/functions/v1/&lt;name&gt;</c> with the guest's bearer JWT and the anon key, a timeout, one retry
    /// on transient failures (and one re-auth on 401), and an offline queue for match-submit.
    /// Callbacks always run (never an exception) on the main thread.
    /// </summary>
    public sealed class SupabaseApi
    {
        private const float RetryDelaySeconds = 0.6f;

        private readonly NetConfig cfg;
        private readonly SupabaseAuth auth;
        private readonly SubmitQueue queue;
        private bool flushing;

        public SupabaseApi(NetConfig cfg, SupabaseAuth auth, SubmitQueue queue)
        {
            this.cfg = cfg;
            this.auth = auth;
            this.queue = queue;
        }

        public SupabaseAuth Auth { get { return auth; } }
        public SubmitQueue Queue { get { return queue; } }
        public bool Flushing { get { return flushing; } }

        public void Call(string function, string body, Action<ApiResult> done)
        {
            NetHost.Run(CallRoutine(function, body, done));
        }

        /// <summary>POST /match-start {mode, clientBuild}.</summary>
        public void MatchStart(Action<ApiResult> done)
        {
            string body = new JsonOut(96).BeginObject().Prop("mode", cfg.Mode).Prop("clientBuild", cfg.Build).EndObject().ToString();
            Call("match-start", body, done);
        }

        /// <summary>POST /match-submit; on a transient failure the request is queued (result.Queued) and sent later.</summary>
        public void SubmitMatch(string matchId, string body, Action<ApiResult> done)
        {
            Call("match-submit", body, r =>
            {
                if (r.RetryLater)
                {
                    queue.Add(matchId, body);
                    r.Queued = true;
                }
                else if (r.Ok) Flush();
                done(r);
            });
        }

        /// <summary>POST /daily-claim {} (SPEC_V2 §7): pays the daily bounty once per server day when enough PvP wins are validated.</summary>
        public void DailyClaim(Action<ApiResult> done)
        {
            Call("daily-claim", "{}", done);
        }

        /// <summary>Raised on the main thread when a flush emptied the submit queue (queued wins reached the server).</summary>
        public Action QueueDrained;

        /// <summary>Sends queued submissions in order; stops at the first one that still cannot be delivered.</summary>
        public void Flush()
        {
            if (flushing || queue.Count == 0 || !cfg.IsConfigured) return;
            NetHost.Run(FlushRoutine());
        }

        private IEnumerator FlushRoutine()
        {
            flushing = true;
            while (queue.Count > 0)
            {
                var it = queue[0];
                ApiResult r = null;
                yield return NetHost.Run(CallRoutine("match-submit", it.body, x => r = x));
                if (r == null || r.RetryLater) break;   // offline, busy, rate limited, no session: keep it for later
                // delivered, or answered for good (409 already_submitted, 422 late / rejected, 404, 400): drop it
                if (!r.Ok) Debug.Log("[PawNoon] queued submit " + it.matchId + " answered " + r.Status + " " + r.Error.code + " " + r.Error.reason);
                queue.Remove(it.matchId);
            }
            flushing = false;
            if (queue.Count == 0 && QueueDrained != null) QueueDrained();
        }

        private IEnumerator CallRoutine(string function, string body, Action<ApiResult> done)
        {
            var result = new ApiResult();
            if (!cfg.IsConfigured)
            {
                result.Error.code = "not_configured";
                done(result);
                yield break;
            }

            bool reauthed = false;
            int attempt = 0;
            while (true)
            {
                bool authOk = false;
                string authErr = null;
                yield return NetHost.Run(auth.EnsureSession((ok, err) => { authOk = ok; authErr = err; }));
                if (!authOk)
                {
                    result.Offline = authErr == "offline";
                    result.Error.code = authErr ?? "auth_failed";
                    done(result);
                    yield break;
                }

                HttpResult h = default(HttpResult);
                yield return NetHost.Run(Http.PostJson(cfg.FunctionUrl(function), body, auth.AccessToken, cfg.AnonKey, cfg.TimeoutSeconds, x => h = x));

                if (h.Status == 401 && !reauthed)
                {
                    // token revoked or expired early: refresh (or re-sign-in) once, then try again
                    reauthed = true;
                    auth.Invalidate();
                    continue;
                }
                if (h.Transient && attempt == 0)
                {
                    attempt++;
                    yield return new WaitForSecondsRealtime(RetryDelaySeconds);
                    continue;
                }

                result.Status = h.Status;
                result.Body = h.Body;
                result.Ok = h.Ok;
                if (!h.Ok)
                {
                    result.Error = h.NetworkError ? new ApiError { code = "offline", message = h.Error } : ApiError.Parse(h.Body);
                    string code = result.Error.code;
                    // a 5xx with a real answer (503 no_opponent) is not "offline"; a bare 5xx / gateway error is
                    result.Offline = h.NetworkError || h.Status == 0 ||
                                     (h.Transient && (string.IsNullOrEmpty(code) || code == "internal" || code == "db_error"));
                    if (string.IsNullOrEmpty(code)) result.Error.code = result.Offline ? "offline" : "http_" + h.Status;
                    Debug.Log("[PawNoon] " + function + " -> " + h.Status + " " + result.Error.code + " " + result.Error.reason + " " + Http.Short(h.Error));
                }
                done(result);
                yield break;
            }
        }
    }

    /// <summary>The daily-claim answer (supabase/functions/daily-claim).</summary>
    public sealed class DailyClaimInfo
    {
        public bool Claimed;
        public bool AlreadyClaimed;
        public int WinsToday;
        public int WinsRequired;
        public int Reward;
        public int Coins;
        public int Streak;
        public string Day;

        public static DailyClaimInfo Parse(string json)
        {
            var root = PnJson.Parse(json) as Dictionary<string, object>;
            if (root == null || !PnJson.Has(root, "claimed")) return null;
            return new DailyClaimInfo
            {
                Claimed = PnJson.Bool(root, "claimed", false),
                AlreadyClaimed = PnJson.Bool(root, "alreadyClaimed", false),
                WinsToday = PnJson.Int(root, "winsToday", -1),
                WinsRequired = PnJson.Int(root, "winsRequired", -1),
                Reward = PnJson.Int(root, "reward", -1),
                Coins = PnJson.Int(root, "coins", -1),
                Streak = PnJson.Int(root, "streak", -1),
                Day = PnJson.Str(root, "day", "")
            };
        }
    }
}
