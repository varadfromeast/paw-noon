using System;
using System.Globalization;
using System.Text;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// The PVP flow (SPEC_V2 §4.0, §4.6): PVP on the title -> FINDING A RIVAL (cancellable) -> match-start ->
    /// opponent card -> the match runs through DuelDirector exactly like solo, with a GhostOpponent in slot 1,
    /// the server's seed, Hard rules and the recorded-match end rules -> match-submit with the player's
    /// recording (built from the real ShotReports) -> the result screen shows "VS name" and the server's
    /// verdict (SENDING... / SAVED, WILL SEND). Every failure becomes a friendly pixel-text card; solo never
    /// depends on any of this.
    /// </summary>
    public sealed class OnlineDirector
    {
        public enum State { Idle, Finding, Opponent, Message, Playing, Result }

        private const float OpponentAutoStartSeconds = 4.5f;
        private const float MockFindSeconds = 1.6f;
        private const float MockSubmitSeconds = 0.9f;
        private const string Character = "whiskers";   // the player's cat until character select (M2) lands

        private PawGame game;
        private NetConfig cfg;
        private SupabaseAuth auth;
        private SupabaseApi api;
        private TapTransport transport;
        private TitleScreen title;
        private MatchResultScreen matchScreen;
        private OnlineCard card;
        private OnlineResultCard resultCard;
        private GhostOpponent ghost;
        private RecordingBuilder recorder;
        private RecordedMatchRules rules;
        private BountyDirector bounty;   // daily bounty + streak on the title (SPEC_V2 §7, feel wave 2)

        private bool mockOverride;
        private bool mockOverrideSet;
        private State state = State.Idle;
        private int token;              // bumps on cancel: late network answers for an old request are ignored
        private float t;
        private MatchStartInfo info;
        private bool matchIsMock;
        private ApiResult pendingStart;
        private MatchStartInfo pendingInfo;
        private string pendingStartError;
        private bool hasVerdict;
        private MatchVerdict verdict;
        private string verdictLine = "";
        private int localWinner = -2;
        private float mockSubmitT = -1f;
        private string displayName = "";

        public State Current { get { return state; } }
        /// <summary>An online card is up over the title (the title is not interactive).</summary>
        public bool CardOpen { get { return state == State.Finding || state == State.Opponent || state == State.Message; } }
        /// <summary>A PvP match is running or showing its result.</summary>
        public bool InPvp { get { return state == State.Playing || state == State.Result; } }
        public bool Mock { get { return mockOverrideSet ? mockOverride : cfg.Mock; } }
        public NetConfig Config { get { return cfg; } }
        /// <summary>Daily bounty (PvP wins today, streak, daily-claim).</summary>
        public BountyDirector Bounty { get { return bounty; } }

        public void Init(PawGame g, UiKit kit, GameLayout layout, PixelPipeline pipe, Transform uiRoot,
            TitleScreen titleScreen, MatchResultScreen resultScreen, TapTransport tap)
        {
            game = g;
            transport = tap;
            title = titleScreen;
            matchScreen = resultScreen;
            cfg = NetConfig.Load();
            NetHost.Ensure(g.transform);
            auth = new SupabaseAuth(cfg);
            string qpath = null;
            try { qpath = System.IO.Path.Combine(Application.persistentDataPath, "pn_submit_queue.json"); }
            catch (Exception e) { Debug.LogWarning("[PawNoon] no persistent path, submit queue in memory: " + e.Message); }
            api = new SupabaseApi(cfg, auth, new SubmitQueue(qpath));

            var cgo = new GameObject("OnlineCard");
            cgo.transform.SetParent(uiRoot, false);
            card = cgo.AddComponent<OnlineCard>();
            card.Setup(kit, layout, pipe);
            var rgo = new GameObject("OnlineResult");
            rgo.transform.SetParent(uiRoot, false);
            resultCard = rgo.AddComponent<OnlineResultCard>();
            resultCard.Setup(kit, layout, pipe);

            ghost = new GhostOpponent(1, transport, g.Config);
            recorder = new RecordingBuilder(0, g.Config);
            title.SetFriendDuel(cfg.FriendDuel);

            bounty = new BountyDirector(api, cfg, () => Mock, kit, pipe, uiRoot, title);

            // anything a previous session could not send goes out now (silently); a bounty owed from an
            // offline day is claimed now, or once the queue has drained
            if (cfg.IsConfigured && !cfg.Mock && api.Queue.Count > 0) api.Flush();
            else bounty.TryClaim();
        }

        // ------------------------------------------------------------------ title side

        /// <summary>PVP tapped on the title.</summary>
        public void BeginQuickDuel()
        {
            if (state != State.Idle) return;
            if (!Mock && !cfg.IsConfigured)
            {
                ShowMessage("PVP IS OFF", "ONLINE IS NOT SET UP", "This build has no server yet.", "Solo duels work as always.");
                return;
            }
            token++;
            state = State.Finding;
            t = 0f;
            pendingStart = null;
            pendingInfo = null;
            pendingStartError = null;
            matchIsMock = Mock;
            title.SetOnlineBusy(true);
            card.Show("FINDING A RIVAL", null, "Hard rules. 3 rounds.", "Most damage wins.", "CANCEL", true);
            if (!matchIsMock)
            {
                int my = token;
                api.MatchStart(r => { if (my == token && state == State.Finding) OnMatchStartAnswer(r); });
            }
        }

        /// <summary>FRIEND tapped (only shown when net_config.json "friendDuel" is true). Room codes are M5.</summary>
        public void FriendTapped()
        {
            if (state != State.Idle) return;
            ShowMessage("FRIEND DUEL", "ROOM CODES SOON", "Duel a friend with a 4-letter code.", "Coming in the next update.");
        }

        private void ShowMessage(string head, string big, string l1, string l2)
        {
            token++;
            state = State.Message;
            title.SetOnlineBusy(true);
            card.Show(head, big, l1, l2, "OK", false);
        }

        private void OnMatchStartAnswer(ApiResult r)
        {
            pendingStart = r;
            if (!r.Ok) return;
            string err;
            pendingInfo = MatchStartInfo.Parse(r.Body, out err);
            if (pendingInfo == null)
            {
                Debug.LogWarning("[PawNoon] match-start answer unreadable: " + err + " " + Http.Short(r.Body));
                pendingStartError = err ?? "bad answer";
            }
        }

        /// <summary>Drives the online card while it is up (PawGame calls it instead of the title's input).</summary>
        public void TickTitle()
        {
            float dt = PawTime.UnscaledDt;
            t += dt;
            bool clicked = card.IsOpen && card.Tick();
            switch (state)
            {
                case State.Finding:
                    if (clicked) { CancelToTitle(); return; }
                    if (matchIsMock && pendingInfo == null && t >= MockFindSeconds) pendingInfo = NetMock.MatchStart(NetMock.RandomSeed());
                    if (pendingInfo != null && t >= cfg.FindMinSeconds) ShowOpponent(pendingInfo);
                    else if (pendingStart != null && (pendingInfo == null) && t >= 0.6f) ShowStartError(pendingStart, pendingStartError);
                    break;
                case State.Opponent:
                    if (clicked || t >= OpponentAutoStartSeconds) StartMatch(info, matchIsMock);
                    break;
                case State.Message:
                    if (clicked) CancelToTitle();
                    break;
            }
        }

        private void ShowOpponent(MatchStartInfo m)
        {
            info = m;
            state = State.Opponent;
            t = 0f;
            displayName = NameForHud(m.OpponentName);
            string cat = "CAT: " + Upper(m.OpponentCharacter);
            string rating = (m.OpponentIsHouse ? "HOUSE GHOST   " : "") + "RATING " + m.OpponentMmr.ToString(CultureInfo.InvariantCulture);
            card.Show("YOUR RIVAL", displayName, cat, rating, "DUEL", false);
        }

        private void ShowStartError(ApiResult r, string parseError)
        {
            string big, l1, l2;
            Friendly(r, parseError, out big, out l1, out l2);
            ShowMessage("NO DUEL", big, l1, l2);
        }

        /// <summary>Friendly words for a failed match-start (never raw exceptions or codes).</summary>
        private static void Friendly(ApiResult r, string parseError, out string big, out string l1, out string l2)
        {
            string code = r != null ? r.Error.code : "";
            if (parseError != null) { big = "GARBLED WIRE"; l1 = "The server answered in riddles."; l2 = "Try again soon."; return; }
            if (r != null && r.Offline) { big = "NO SIGNAL"; l1 = "Check your connection"; l2 = "and try again."; return; }
            switch (code)
            {
                case "no_opponent": big = "NO RIVALS IN TOWN"; l1 = "Every gun is out riding."; l2 = "Try again in a minute."; return;
                case "ranked_not_allowed": big = "RANKED IS LOCKED"; l1 = "Link your account or update."; l2 = "Casual duels are open."; return;
                case "auth_failed":
                case "unauthorized":
                case "no_profile": big = "COULD NOT SIGN IN"; l1 = "The sheriff lost your papers."; l2 = "Try again soon."; return;
                case "not_configured": big = "ONLINE IS NOT SET UP"; l1 = "This build has no server yet."; l2 = "Solo duels work as always."; return;
                default: big = "TELEGRAPH IS DOWN"; l1 = "The server did not answer right."; l2 = "Try again soon."; return;
            }
        }

        private void CancelToTitle()
        {
            token++;
            state = State.Idle;
            card.Close();
            title.SetOnlineBusy(false);
        }

        // ------------------------------------------------------------------ match

        private void StartMatch(MatchStartInfo m, bool mock)
        {
            info = m;
            matchIsMock = mock;
            displayName = NameForHud(m.OpponentName);
            ghost.Load(m.OpponentAttempts);
            rules = new RecordedMatchRules(m.Rounds, m.MaxAttempts, m.OpponentAttempts.Length);
            hasVerdict = false;
            verdict = null;
            verdictLine = "";
            localWinner = -2;
            mockSubmitT = -1f;
            card.Close();
            resultCard.Close();
            title.SetOnlineBusy(false);
            state = State.Playing;
            game.StartPvpDuel(m.Seed, ghost, rules, displayName);
            // after StartPvpDuel: the shared config is Hard now and the match (and its first countdown) is ahead
            recorder.Begin(m.Seed, game.Director.Player);
            recorder.Difficulty = DuelConfig.DifficultyHard;
            recorder.Character = Character;
            recorder.Gun = "standard";
            recorder.ClientBuild = cfg.Build;
            FillDevice(recorder);
            transport.Tap = recorder;
        }

        private static void FillDevice(RecordingBuilder r)
        {
            try
            {
                r.DeviceModel = SystemInfo.deviceModel;
                r.DeviceOs = SystemInfo.operatingSystem;
                double hz = Screen.currentResolution.refreshRateRatio.value;
                if (double.IsNaN(hz) || hz <= 0) hz = Application.targetFrameRate > 0 ? Application.targetFrameRate : 60;
                r.RefreshHz = Math.Round(hz, 3);
            }
            catch (Exception)
            {
                r.RefreshHz = 60;
            }
        }

        /// <summary>DuelDirector finished the PvP match (PawGame forwards it after showing the result screen).</summary>
        public void OnMatchFinished(bool won, bool draw)
        {
            if (state != State.Playing) return;
            transport.Tap = null;
            recorder.Finish();
            state = State.Result;
            localWinner = won ? 0 : (draw ? -1 : 1);
            bounty.OnPvpFinished(won, matchIsMock);   // counted locally now; the server validates on daily-claim
            matchScreen.SetRematchLabel("NEW RIVAL");
            resultCard.Place(matchScreen.ContentBottom + 4, matchScreen.ButtonsTop - 8);
            resultCard.Show("VS " + displayName, null, Pal.Wood3);

            string body = recorder.SubmitBody(info.MatchId, NowIso());
            if (matchIsMock)
            {
                mockSubmitT = MockSubmitSeconds;
                return;
            }
            int my = token;
            api.SubmitMatch(info.MatchId, body, r => { if (my == token) OnSubmitAnswer(r); });
        }

        private void OnSubmitAnswer(ApiResult r)
        {
            if (state != State.Result) return;
            if (r.Ok)
            {
                string err;
                var v = MatchVerdict.Parse(r.Body, out err);
                bounty.TryClaim();   // this win is on the server now
                if (v != null) { ShowVerdict(v, false); return; }
                Debug.LogWarning("[PawNoon] match-submit answer unreadable: " + err);
                resultCard.SetStatus("SENT", Pal.Wood3);
                return;
            }
            if (r.Queued) { resultCard.SetStatus("SAVED, WILL SEND", Pal.Wood3); verdictLine = "queued"; return; }
            string line;
            switch (r.Error.code)
            {
                case "recording_rejected": line = r.Error.reason == "late" ? "NOT COUNTED: TOO LATE" : "NOT COUNTED: REJECTED"; break;
                case "already_submitted": line = "ALREADY SENT"; break;
                case "match_not_found": line = "MATCH EXPIRED"; break;
                case "bad_recording":
                case "bad_request": line = "NOT COUNTED: BAD RECORDING"; break;
                default: line = "COULD NOT SEND"; break;
            }
            verdictLine = line;
            resultCard.SetStatus(line, Pal.PaintRed);
        }

        private void ShowVerdict(MatchVerdict v, bool mock)
        {
            hasVerdict = true;
            verdict = v;
            int w = v.WinnerSlot;
            string who = w == 0 ? "YOU WIN" : (w == 1 ? "RIVAL WINS" : "DRAW");
            var sb = new StringBuilder(32);
            sb.Append(mock ? "MOCK SERVER: " : "SERVER: ").Append(who);
            if (v.HasRating) sb.Append(v.RatingDelta >= 0 ? "  +" : "  ").Append(v.RatingDelta.ToString(CultureInfo.InvariantCulture));
            verdictLine = sb.ToString();
            resultCard.SetStatus(verdictLine, w == 0 ? Pal.Green2 : (w == 1 ? Pal.PaintRed : Pal.Wood3));
            if (w != localWinner)
                Debug.LogWarning("[PawNoon] server verdict (" + v.Winner + ", " + v.EndReason + ") differs from the local result (" + localWinner + ")");
        }

        /// <summary>While the match result screen is up after a PvP match.</summary>
        public void TickResult()
        {
            if (state != State.Result) return;
            resultCard.Tick();
            if (mockSubmitT > 0f)
            {
                mockSubmitT -= PawTime.UnscaledDt;
                if (mockSubmitT <= 0f)
                {
                    // what the server would answer: its recompute, done locally with the same rules
                    var sim = RecordedMatchSim.Play(game.Config, info.Seed, info.Rounds, info.MaxAttempts, recorder.ToGhostAttempts(), info.OpponentAttempts);
                    ShowVerdict(MatchVerdict.FromSim(info.MatchId, sim), true);
                }
            }
        }

        /// <summary>NEW RIVAL on the result screen.</summary>
        public void Rematch()
        {
            bool mock = matchIsMock;
            game.ShowTitle();
            if (mock && !Mock) { mockOverride = true; mockOverrideSet = true; }
            BeginQuickDuel();
        }

        /// <summary>The game went back to the title (PawGame.ShowTitle): close everything online, keep the queue.</summary>
        public void OnTitle()
        {
            if (state == State.Playing) recorder.Cancel();
            transport.Tap = null;
            token++;
            state = State.Idle;
            mockSubmitT = -1f;
            card.Close();
            resultCard.Close();
            title.SetOnlineBusy(false);
            title.SetFriendDuel(cfg.FriendDuel);
            bounty.OnTitle();
        }

        // ------------------------------------------------------------------ review / QA

        /// <summary>Forgets the guest session: the next online call signs in as a brand-new guest (QA: play your own ghost).</summary>
        public void SignOut() { auth.SignOut(); }

        public void SetMock(bool on)
        {
            mockOverride = on;
            mockOverrideSet = true;
        }

        /// <summary>Plays a ghost match right now on <paramref name="seed"/> against the built-in fake recording (no network).</summary>
        public void StartGhostDuel(int seed)
        {
            game.ShowTitle();
            StartMatch(NetMock.MatchStart(seed), true);
        }

        public string Status()
        {
            var sb = new StringBuilder(160);
            sb.Append("online=").Append(Mock ? "mock" : (cfg.IsConfigured ? "live" : "off"));
            sb.Append(" net=").Append(state);
            sb.Append(" auth=").Append(auth.HasSession ? (auth.AccessValid ? "guest" : "stale") : "none");
            sb.Append(" queued=").Append(api.Queue.Count);
            if (info != null && (state == State.Playing || state == State.Result || state == State.Opponent))
            {
                sb.Append(" match=").Append(info.MatchId).Append(" seed=").Append(info.Seed);
                sb.Append(" rival=\"").Append(displayName).Append('"');
                sb.Append(" ghost=").Append(ghost.AttemptsStarted).Append('/').Append(ghost.AttemptCount);
                sb.Append(" recorded=").Append(recorder.Count);
            }
            if (state == State.Result)
            {
                sb.Append(" local=").Append(localWinner == 0 ? "you" : (localWinner == 1 ? "opponent" : "draw"));
                sb.Append(" verdict=\"").Append(hasVerdict ? verdictLine : (verdictLine.Length > 0 ? verdictLine : "sending")).Append('"');
                if (hasVerdict) sb.Append(" serverEnd=").Append(verdict.EndReason).Append(" agree=").Append(verdict.WinnerSlot == localWinner ? "yes" : "NO");
            }
            return sb.ToString();
        }

        /// <summary>The last recording as JSON (review / debugging).</summary>
        public string LastRecordingJson() { return recorder.Count > 0 ? recorder.ToJson(NowIso()) : "{}"; }

        // ------------------------------------------------------------------ helpers

        public static string NowIso()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        private static string Upper(string s) { return string.IsNullOrEmpty(s) ? "" : s.ToUpperInvariant(); }

        /// <summary>Upper case, only glyphs the bitmap fonts have, at most 14 characters.</summary>
        public static string NameForHud(string s)
        {
            if (string.IsNullOrEmpty(s)) return "STRANGER";
            var sb = new StringBuilder(14);
            string u = s.Trim().ToUpperInvariant();
            for (int i = 0; i < u.Length && sb.Length < 14; i++)
            {
                char c = u[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ' || c == '#' || c == '-' || c == '\'' || c == '.' || c == '!' || c == '?' || c == '&';
                if (ok) sb.Append(c);
            }
            string r = sb.ToString().Trim();
            return r.Length == 0 ? "STRANGER" : r;
        }
    }

    /// <summary>
    /// Offline stand-in for the server (net_config "mock", ReviewDirector SetNetMock / StartGhostDuel): a fixed
    /// house-ghost recording and match-start answer. The verdict is RecordedMatchSim (the server's recompute).
    /// </summary>
    public static class NetMock
    {
        /// <summary>
        /// Marshal Mittens (the README's house ghost) plus spare attempts for void replays: a clean shot, an
        /// early foul 2.6 s after "3" (during the hold: countdown 2.1 s + hold >= 1.0 s), shots, one no-shot.
        /// </summary>
        public static GhostAttempt[] FakeRecording()
        {
            return new[]
            {
                GhostAttempt.Shot(268400000L, 1),
                GhostAttempt.Early(2600000000L, 2),
                GhostAttempt.Shot(301100000L, 3),
                GhostAttempt.Shot(247900000L, 4),
                GhostAttempt.NoShot(5),
                GhostAttempt.Shot(290000000L, 6),
                GhostAttempt.Shot(233000000L, 7),
                GhostAttempt.Shot(275000000L, 8),
                GhostAttempt.Shot(260000000L, 9)
            };
        }

        public static int RandomSeed() { return Environment.TickCount & 0x7fffffff; }

        public static MatchStartInfo MatchStart(int seed)
        {
            var m = new MatchStartInfo();
            m.MatchId = "mock-" + seed.ToString(CultureInfo.InvariantCulture);
            m.Mode = "casual";
            m.Seed = seed;
            m.Rounds = RecordedMatchRules.DefaultRounds;
            m.MaxAttempts = RecordedMatchRules.DefaultMaxAttempts;
            m.RecordingId = "00000000-0000-4000-8000-000000000002";
            m.OpponentName = "Marshal Mittens";
            m.OpponentCharacter = "blackpaw";
            m.OpponentGun = "standard";
            m.OpponentMmr = 1200;
            m.OpponentIsHouse = true;
            m.OpponentAttempts = FakeRecording();
            return m;
        }
    }
}
