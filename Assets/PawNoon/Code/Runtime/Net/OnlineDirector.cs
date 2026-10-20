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
        /// <summary>Menu (M5): the PVP card, a friend-duel card or the leaderboard is up over the title.</summary>
        public enum State { Idle, Finding, Opponent, Message, Playing, Result, Menu }

        private const string PvpModeKey = "pawnoon.pvpMode";

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
        // M5: PVP card (casual / ranked), friend duels by room code, leaderboards
        private PvpCard pvpCard;
        private FriendCard friendCard;
        private FriendDuel friend;
        private LeaderboardScreen board;
        private string matchMode = "casual";
        private string lastMode = "casual";
        private string hostRoom;          // mock: the room the host's next recording goes to
        private bool boardRanked = true;

        public State Current { get { return state; } }
        /// <summary>An online card is up over the title (the title is not interactive).</summary>
        public bool CardOpen { get { return state == State.Finding || state == State.Opponent || state == State.Message || state == State.Menu; } }
        /// <summary>A PvP match is running or showing its result.</summary>
        public bool InPvp { get { return state == State.Playing || state == State.Result; } }
        public bool Mock { get { return mockOverrideSet ? mockOverride : cfg.Mock; } }
        public NetConfig Config { get { return cfg; } }
        /// <summary>Daily bounty (PvP wins today, streak, daily-claim).</summary>
        public BountyDirector Bounty { get { return bounty; } }
        /// <summary>The guest / linked session (M6 accounts share it).</summary>
        public SupabaseAuth Auth { get { return auth; } }
        /// <summary>The shared function client (one auth session): the shop's purchase-validate / loadout-equip go through it.</summary>
        public SupabaseApi Api { get { return api; } }

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

            pvpCard = NewCard<PvpCard>("PvpCard", uiRoot, kit, layout, pipe);
            friendCard = NewCard<FriendCard>("FriendCard", uiRoot, kit, layout, pipe);
            friend = new FriendDuel(this, api, friendCard);
            board = NewCard<LeaderboardScreen>("Leaderboard", uiRoot, kit, layout, pipe);
            board.Init(api);

            ghost = new GhostOpponent(1, transport, g.Config);
            recorder = new RecordingBuilder(0, g.Config);
            title.SetFriendDuel(cfg.FriendDuel);

            bounty = new BountyDirector(api, cfg, () => Mock, kit, pipe, uiRoot, title);

            // anything a previous session could not send goes out now (silently); a bounty owed from an
            // offline day is claimed now, or once the queue has drained
            if (cfg.IsConfigured && !cfg.Mock && api.Queue.Count > 0) api.Flush();
            else bounty.TryClaim();
        }

        private static T NewCard<T>(string name, Transform uiRoot, UiKit kit, GameLayout layout, PixelPipeline pipe) where T : ScreenBase
        {
            var go = new GameObject(name);
            go.transform.SetParent(uiRoot, false);
            var c = go.AddComponent<T>();
            c.Setup(kit, layout, pipe);
            return c;
        }

        // ------------------------------------------------------------------ title side

        /// <summary>The player's casual / ranked pick on the PVP card (PlayerPrefs, default net_config "mode").</summary>
        public string PvpMode
        {
            get
            {
                string m = cfg.Mode;
                try { m = PlayerPrefs.GetString(PvpModeKey, cfg.Mode); } catch (Exception) { }
                return m == "ranked" ? "ranked" : "casual";
            }
            set
            {
                try { PlayerPrefs.SetString(PvpModeKey, value == "ranked" ? "ranked" : "casual"); PlayerPrefs.Save(); } catch (Exception) { }
            }
        }

        /// <summary>Account linked to Apple / Google (ranked): SupabaseAuth.RankedLinked (M6 IsLinked + review override).</summary>
        public bool Linked { get { return auth.RankedLinked; } }

        /// <summary>PVP tapped on the title: the PVP card (CASUAL | RANKED, FIND RIVAL).</summary>
        public void BeginQuickDuel()
        {
            if (state != State.Idle) return;
            if (!Mock && !cfg.IsConfigured)
            {
                ShowMessage("PVP IS OFF", "ONLINE IS NOT SET UP", "This build has no server yet.", "Solo duels work as always.");
                return;
            }
            token++;
            state = State.Menu;
            title.SetOnlineBusy(true);
            pvpCard.Show(PvpMode == "ranked", Linked);
        }

        /// <summary>FINDING A RIVAL in <paramref name="mode"/> ("casual" / "ranked"): match-start, then the rival card.</summary>
        public void FindRival(string mode)
        {
            if (state != State.Idle && state != State.Menu) return;
            CloseMenus();
            if (!Mock && !cfg.IsConfigured)
            {
                ShowMessage("PVP IS OFF", "ONLINE IS NOT SET UP", "This build has no server yet.", "Solo duels work as always.");
                return;
            }
            matchMode = mode == "ranked" ? "ranked" : "casual";
            lastMode = matchMode;
            token++;
            state = State.Finding;
            t = 0f;
            pendingStart = null;
            pendingInfo = null;
            pendingStartError = null;
            matchIsMock = Mock;
            title.SetOnlineBusy(true);
            card.Show("FINDING A RIVAL", null, matchMode == "ranked" ? "RANKED. Hard rules. 3 rounds." : "Hard rules. 3 rounds.", "Most damage wins.", "CANCEL", true);
            if (!matchIsMock)
            {
                int my = token;
                api.MatchStart(matchMode, cfg.Build, r => { if (my == token && state == State.Finding) OnMatchStartAnswer(r); });
            }
        }

        /// <summary>FRIEND tapped (shown when net_config.json "friendDuel" is true): room codes (M5).</summary>
        public void FriendTapped()
        {
            if (state != State.Idle) return;
            if (!Mock && !cfg.IsConfigured)
            {
                ShowMessage("FRIEND DUEL", "ONLINE IS NOT SET UP", "This build has no server yet.", "Solo duels work as always.");
                return;
            }
            token++;
            state = State.Menu;
            title.SetOnlineBusy(true);
            friend.OpenMenu(Mock);
        }

        /// <summary>The trophy on the title: leaderboards (weekly ranked / fastest draw).</summary>
        public void OpenLeaderboard()
        {
            if (state != State.Idle) return;
            token++;
            state = State.Menu;
            title.SetOnlineBusy(true);
            board.Show(boardRanked, Mock);
        }

        /// <summary>A room-join answered ready: the host's ghost is the rival (same card and match path as a quick duel).</summary>
        public void ShowRival(MatchStartInfo m, bool mock)
        {
            CloseMenus();
            token++;
            matchIsMock = mock;
            matchMode = "casual";
            lastMode = "casual";   // NEW RIVAL after a friend duel is a casual quick duel
            title.SetOnlineBusy(true);
            ShowOpponent(m);
        }

        private void CloseMenus()
        {
            if (pvpCard.IsOpen) pvpCard.Close();
            if (friend.Open) friend.Close();
            if (board.IsOpen) { boardRanked = board.Ranked; board.Close(); }
        }

        /// <summary>The M5 cards over the title.</summary>
        private void TickMenu()
        {
            if (pvpCard.IsOpen)
            {
                var a = pvpCard.Tick();
                if (a == PvpCard.Action.Back) { CancelToTitle(); return; }
                if (a == PvpCard.Action.Find)
                {
                    PvpMode = pvpCard.Ranked ? "ranked" : "casual";
                    hostRoom = null;
                    FindRival(PvpMode);
                }
                return;
            }
            if (friend.Open)
            {
                string room = friend.Code;
                bool closed = friend.Tick();
                if (closed && state == State.Finding && Mock) hostRoom = room;   // DUEL from the room card (mock)
                else if (closed && state == State.Finding) hostRoom = null;
                if (closed && state == State.Menu) CancelToTitle();
                return;
            }
            if (board.IsOpen)
            {
                if (board.Tick()) { boardRanked = board.Ranked; CancelToTitle(); }
                return;
            }
            CancelToTitle();
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
            if (state == State.Menu) { TickMenu(); return; }
            bool clicked = card.IsOpen && card.Tick();
            switch (state)
            {
                case State.Finding:
                    if (clicked) { CancelToTitle(); return; }
                    if (matchIsMock && pendingInfo == null && t >= MockFindSeconds) pendingInfo = NetMock.MatchStart(NetMock.RandomSeed(), matchMode);
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
            string mmr = m.OpponentMmr.ToString(CultureInfo.InvariantCulture);
            string cat = "CAT: " + Upper(m.OpponentCharacter) + "   RATING " + mmr;
            string line2;
            if (m.Room != null) line2 = "FRIEND DUEL   ROOM " + m.Room;
            else if (m.Mode == "ranked") line2 = "RANKED   YOUR RATING " + m.YouMmr.ToString(CultureInfo.InvariantCulture);
            else line2 = (m.OpponentIsHouse ? "HOUSE GHOST   " : "") + "CASUAL";
            card.Show(m.Room != null ? "YOUR FRIEND" : "YOUR RIVAL", displayName, cat, line2, "DUEL", false);
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
                case "link_required": big = "SIGN IN TO RANK"; l1 = "Ranked needs Apple or Google."; l2 = "Casual duels are open."; return;
                case "ranked_not_allowed": big = "RANKED IS LOCKED"; l1 = "Link your account or update."; l2 = "Casual duels are open."; return;
                case "rate_limited": big = "EASY, PARTNER"; l1 = "Too many duels in a minute."; l2 = "Try again shortly."; return;
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
            CloseMenus();
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
            game.SetPvpLoadout(m.Mode == "ranked", m.OpponentGun);   // M7: guns for this match (ranked = standard curve)
            game.StartPvpDuel(m.Seed, ghost, rules, displayName);
            // after StartPvpDuel: the shared config is Hard now and the match (and its first countdown) is ahead
            recorder.Begin(m.Seed, game.Director.Player);
            recorder.Difficulty = DuelConfig.DifficultyHard;
            recorder.Character = Character;
            recorder.Gun = game.Shop != null ? game.Shop.RecordingGun() : Guns.Standard;   // server-verified guns only
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
            verdictLine = sb.ToString();
            resultCard.SetStatus(verdictLine, w == 0 ? Pal.Green2 : (w == 1 ? Pal.PaintRed : Pal.Wood3));
            // M5: the rating line ("RATING 1214 (+12)") in ranked; a host's room note in a friend duel
            if (v.HasRating) resultCard.SetExtra(RatingLine(v.RatingAfter, v.RatingDelta), v.RatingDelta >= 0 ? Pal.Green2 : Pal.PaintRed);
            else if (v.Room != null) resultCard.SetExtra("GHOST SENT TO ROOM " + v.Room, Pal.Wood3);
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
                    bool ranked = info.Mode == "ranked";
                    var sim = RecordedMatchSim.Play(game.Config, info.Seed, info.Rounds, info.MaxAttempts, recorder.ToGhostAttempts(), info.OpponentAttempts,
                        Guns.Effective(recorder.Gun, ranked), Guns.Effective(info.OpponentGun, ranked));
                    var v = MatchVerdict.FromSim(info.MatchId, sim);
                    NetMock.Rate(v, info);
                    if (hostRoom != null) { v.Room = hostRoom; hostRoom = null; }
                    ShowVerdict(v, true);
                }
            }
        }

        /// <summary>NEW RIVAL on the result screen.</summary>
        public void Rematch()
        {
            bool mock = matchIsMock;
            string mode = lastMode;
            game.ShowTitle();
            if (mock && !Mock) { mockOverride = true; mockOverrideSet = true; }
            FindRival(mode);
        }

        /// <summary>The game went back to the title (PawGame.ShowTitle): close everything online, keep the queue.</summary>
        public void OnTitle()
        {
            if (state == State.Playing) recorder.Cancel();
            transport.Tap = null;
            token++;
            state = State.Idle;
            mockSubmitT = -1f;
            CloseMenus();
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
            sb.Append(" auth=").Append(auth.StatusWord);   // none | guest | stale | apple | google | mock-apple | mock-google
            sb.Append(" queued=").Append(api.Queue.Count);
            sb.Append(" linked=").Append(Linked ? "yes" : "no").Append(" pvpMode=").Append(PvpMode);
            if (state == State.Menu)
            {
                if (pvpCard.IsOpen) sb.Append(" card=pvp(").Append(pvpCard.Ranked ? "ranked" : "casual").Append(')');
                if (friend.Open) sb.Append(' ').Append(friend.Status());
                if (board.IsOpen) sb.Append(' ').Append(board.Status());
            }
            if (info != null && (state == State.Playing || state == State.Result || state == State.Opponent))
            {
                sb.Append(" match=").Append(info.MatchId).Append(" mode=").Append(info.Mode).Append(" seed=").Append(info.Seed);
                if (info.Room != null) sb.Append(" room=").Append(info.Room);
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

        /// <summary>"RATING 1214 (+12)" / "RATING 1190 (-11)".</summary>
        public static string RatingLine(int after, int delta)
        {
            return "RATING " + after.ToString(CultureInfo.InvariantCulture) + " (" + (delta >= 0 ? "+" : "") + delta.ToString(CultureInfo.InvariantCulture) + ")";
        }

        // ------------------------------------------------------------------ review (M5)

        /// <summary>ReviewDirector.Show: "pvp", "friend", "room", "join", "waiting", "leaderboard", "fastest" (all mock).</summary>
        public string ReviewShow(string what)
        {
            game.ShowTitle();
            token++;
            state = State.Menu;
            title.SetOnlineBusy(true);
            switch (what)
            {
                case "pvp": pvpCard.Show(PvpMode == "ranked", Linked); break;
                case "friend": friend.OpenMenu(true); break;
                case "room":
                case "join":
                case "waiting": friend.ReviewShow(what); break;
                case "leaderboard":
                case "fastest": board.Show(what == "leaderboard", true); break;
                default: CancelToTitle(); return "error: unknown online screen " + what;
            }
            return "ok";
        }

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
    public static partial class NetMock
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

        /// <summary>The mock's ranked answer: your rating 1202, and after the match +12 for a win, -11 for a loss.</summary>
        public static MatchStartInfo MatchStart(int seed, string mode)
        {
            var m = MatchStart(seed);
            if (mode == "ranked")
            {
                m.Mode = "ranked";
                m.YouMmr = 1202;
                m.OpponentMmr = 1214;
            }
            return m;
        }

        /// <summary>Adds the ranked rating change the mock server would answer.</summary>
        public static void Rate(MatchVerdict v, MatchStartInfo m)
        {
            if (m == null || m.Mode != "ranked") return;
            v.HasRating = true;
            v.RatingBefore = m.YouMmr;
            v.RatingDelta = v.WinnerSlot == 0 ? 12 : (v.WinnerSlot == 1 ? -11 : 0);
            v.RatingAfter = v.RatingBefore + v.RatingDelta;
        }

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
