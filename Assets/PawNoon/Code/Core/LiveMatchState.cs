using System;
using System.Collections.Generic;

namespace PawNoon.Core
{
    /// <summary>
    /// Live duel protocol constants (Docs/LIVE_DUELS.md). Same values as LIVE_RULES in
    /// supabase/functions/_shared/live.ts and RULES in Tools/Parity/live_reference.py (live_vectors.json meta checks it).
    /// </summary>
    public static class LiveRules
    {
        public const int Version = 1;
        public const int Rounds = 3;
        public const int MaxAttempts = 9;
        public const long FriendLobbyMs = 600000;
        public const long QuickLobbyMs = 30000;
        /// <summary>Reconnection window N: both players must be ready for the next attempt within this.</summary>
        public const long ReadyTimeoutMs = 20000;
        /// <summary>round_start is sent this long before "3" (show "3" at startAtMs on the server clock).</summary>
        public const long StartLeadMs = 800;
        public const long NetworkGraceMs = 1500;
        public const long EarlyGraceMs = 1000;
        public const long CueEarlyToleranceNs = 200000000L;
        public const long CueLateToleranceNs = 2000000000L;
        public const long NoDeadline = 9007199254740991L;

        /// <summary>Report deadline after the cue: noShotTimeout + reportSlack (ceil ms) + network grace.</summary>
        public static long ReportWindowMs(DuelConfig cfg)
        {
            return CeilMs(cfg.NoShotTimeoutNs + cfg.ReportSlackNs) + NetworkGraceMs;
        }

        public static long CeilMs(long ns) { return (ns + 999999L) / 1000000L; }
    }

    public enum LivePhase { Lobby = 0, Ready, Countdown, Draw, Finished, Cancelled }

    public enum LiveVerdict { Accepted = 0, Duplicate, Stale, WrongRound, Implausible, Inconsistent, Late, NotPlaying, Taken }

    public enum LiveEnd { None = 0, Ko, DoubleKo, Rounds, MaxAttempts, Forfeit, Abandoned, LobbyExpired, HostLeft }

    /// <summary>Broadcast events (wire names: joined, ready, round_start, draw, round_result, match_over, cancelled).</summary>
    public enum LiveEvent { Joined = 0, Ready, RoundStart, Draw, RoundResult, MatchOver, Cancelled }

    /// <summary>A phone's ShotReport for live play: Protocol.ShotReport + countdownStartNs (+ the server's receive time).</summary>
    public struct LiveReport
    {
        public int roundId;
        public ReportKind kind;
        public long reactionNs;
        public long cuePresentedNs;
        public long inputNs;
        /// <summary>When "3" appeared on this phone (its own clock).</summary>
        public long countdownStartNs;
        /// <summary>The phone's ClockSync RTT estimate in ms, NaN when unknown (only feeds server-side soft flags).</summary>
        public double rttMs;
        public long receivedAtMs;
    }

    /// <summary>One resolved attempt as both players see it (live.ts AttemptView + the kinds fed to the rules).</summary>
    public sealed class LiveAttemptView
    {
        public int attempt;
        public int roundId;
        public int roundNumber;
        public long holdNs;
        public long resolvedAtMs;
        public bool isVoid;
        public bool tie;
        public int winner = -1;
        public readonly AttemptKind[] kinds = new AttemptKind[2];
        public readonly SlotOutcome[] outcomes = new SlotOutcome[2];
        /// <summary>Shot / anticipation reactions, -1 otherwise.</summary>
        public readonly long[] reactionsNs = { -1, -1 };
        public readonly double[] damage = new double[2];
        public readonly Grade[] grades = new Grade[2];
        public readonly double[] hp = new double[2];
    }

    /// <summary>The attempt in play (countdown / draw).</summary>
    public sealed class LiveRound
    {
        public int attempt;
        public int roundId;
        public int roundNumber;
        public long holdNs;
        public long cueOffsetNs;
        public long startAtMs;
        /// <summary>Server only: the secret cue time. A client mirror never learns it (it stays 0 there).</summary>
        public long cueDueAtMs;
        /// <summary>-1 until DRAW was sent.</summary>
        public long cueSentAtMs = -1;
        public readonly bool[] reported = new bool[2];
        public readonly LiveReport[] reports = new LiveReport[2];
    }

    public sealed class LiveResult
    {
        /// <summary>0, 1, or -1 (draw / abandoned / cancelled).</summary>
        public int winner = -1;
        public LiveEnd end;
        public int forfeitedBy = -1;
        public readonly double[] hp = new double[2];
        public readonly int[] roundWins = new int[2];
    }

    /// <summary>
    /// Engine-free C# mirror of the live duel state machine (supabase/functions/_shared/live.ts), so client and server
    /// share the rules: lobby -> ready -> countdown -> draw -> resolve (the ghost-duel rules: every attempt so far is
    /// replayed through <see cref="LocalAuthority"/>, exactly like playRecordedMatch / RecordedMatchSim) -> next attempt
    /// or finished; ready / lobby / report deadlines; forfeits. Times: server ms (<c>nowMs</c>); report fields on the
    /// reporting phone's clock (ns). Reports are ordered by the reaction they carry, never by arrival.
    ///
    /// The client (LiveOpponent, Docs/LIVE_DUELS.md "Client") does not own the authority: it can run this mirror on the
    /// server's broadcasts to check every round_result (parity, like RecordedMatchSim for ghost duels) and to know the
    /// deadlines it must meet. Checked against Tools/Parity/live_vectors.json by LiveMatchStateTests. The server-only
    /// soft reaction-vs-RTT flags are not mirrored.
    /// </summary>
    public sealed class LiveMatchState
    {
        public const int Slots = 2;
        private const long ConsistencyToleranceNs = 1000; // 1 us, as LocalAuthority
        private const long Ms = 1000000L;

        private readonly DuelConfig cfg;
        public readonly bool Friend;
        public readonly bool Ranked;
        public readonly int Seed;
        public readonly string[] SlotGuns = { Guns.Standard, Guns.Standard };
        public readonly string[] Users = { null, null };
        /// <summary>Attempt index each player is ready for, -1 = not ready.</summary>
        public readonly int[] ReadyFor = { -1, -1 };
        public readonly double[] Hp = new double[2];
        public readonly int[] RoundWins = new int[2];
        public readonly List<LiveAttemptView> History = new List<LiveAttemptView>(9);
        /// <summary>Events caused by the last call (cleared at the start of each call).</summary>
        public readonly List<LiveEvent> Out = new List<LiveEvent>(4);

        public LivePhase Phase { get; private set; }
        public long DeadlineMs { get; private set; }
        public int Attempt { get; private set; }
        public LiveRound Round { get; private set; }
        public LiveResult Result { get; private set; }

        public LiveMatchState(DuelConfig cfg, bool friend, bool ranked, int seed, string hostId, string hostGun, long nowMs)
        {
            this.cfg = cfg ?? new DuelConfig();
            Friend = friend;
            Ranked = ranked;
            Seed = seed;
            Users[0] = hostId;
            SlotGuns[0] = Guns.Normalize(hostGun);
            Phase = LivePhase.Lobby;
            DeadlineMs = nowMs + (friend ? LiveRules.FriendLobbyMs : LiveRules.QuickLobbyMs);
        }

        public bool IsOver { get { return Phase == LivePhase.Finished || Phase == LivePhase.Cancelled; } }

        public int SlotOf(string userId)
        {
            if (userId == null) return -1;
            if (Users[0] == userId) return 0;
            if (Users[1] == userId) return 1;
            return -1;
        }

        // ------------------------------------------------------------------------------------ transitions

        /// <summary>Applies every deadline that has passed (lobby expiry, ready timeout, the cue, the report deadline).</summary>
        public void Advance(long nowMs)
        {
            Out.Clear();
            ApplyDeadlines(nowMs);
        }

        public LiveVerdict Join(string userId, string gun, long nowMs)
        {
            Out.Clear();
            ApplyDeadlines(nowMs);
            if (userId != null && Users[1] == userId) return LiveVerdict.Duplicate;
            if (Phase != LivePhase.Lobby) return IsOver ? LiveVerdict.NotPlaying : LiveVerdict.Taken;
            if (Users[0] == userId) return LiveVerdict.Taken;
            Users[1] = userId;
            SlotGuns[1] = Guns.Normalize(gun);
            Hp[0] = cfg.maxHp;
            Hp[1] = cfg.maxHp;
            EnterReady(nowMs);
            Out.Add(LiveEvent.Joined);
            return LiveVerdict.Accepted;
        }

        public LiveVerdict Ready(int slot, int attempt, long nowMs)
        {
            Out.Clear();
            ApplyDeadlines(nowMs);
            if (slot < 0 || slot >= Slots || Phase == LivePhase.Lobby || IsOver) return LiveVerdict.NotPlaying;
            if (Phase == LivePhase.Countdown || Phase == LivePhase.Draw)
            {
                if (attempt == Attempt) return LiveVerdict.Duplicate;
                return attempt < Attempt ? LiveVerdict.Stale : LiveVerdict.WrongRound;
            }
            if (attempt < Attempt) return LiveVerdict.Stale;
            if (attempt > Attempt) return LiveVerdict.WrongRound;
            if (ReadyFor[slot] == attempt) return LiveVerdict.Duplicate;
            ReadyFor[slot] = attempt;
            Out.Add(LiveEvent.Ready);
            if (ReadyFor[1 - slot] == attempt) StartRound(nowMs);
            return LiveVerdict.Accepted;
        }

        public LiveVerdict Report(int slot, LiveReport rep, long nowMs)
        {
            Out.Clear();
            ApplyDeadlines(nowMs);
            LiveVerdict v = Validate(slot, rep, nowMs);
            if (v != LiveVerdict.Accepted) return v;
            rep.receivedAtMs = nowMs;
            Round.reports[slot] = rep;
            Round.reported[slot] = true;
            if (Round.reported[0] && Round.reported[1]) Resolve(nowMs);
            return LiveVerdict.Accepted;
        }

        public LiveVerdict Forfeit(int slot, long nowMs)
        {
            Out.Clear();
            ApplyDeadlines(nowMs);
            if (IsOver || slot < 0 || slot >= Slots) return LiveVerdict.NotPlaying;
            if (Phase == LivePhase.Lobby) Cancel(LiveEnd.HostLeft);
            else Finish(1 - slot, LiveEnd.Forfeit, slot);
            return LiveVerdict.Accepted;
        }

        // ------------------------------------------------------------------------------------ internals

        private void EnterReady(long nowMs)
        {
            Phase = LivePhase.Ready;
            Round = null;
            ReadyFor[0] = -1;
            ReadyFor[1] = -1;
            DeadlineMs = nowMs + LiveRules.ReadyTimeoutMs;
        }

        private void StartRound(long nowMs)
        {
            int k = Attempt;
            var rng = new Random(Seed);
            RoundSchedule sched = null;
            for (int i = 0; i <= k; i++) sched = RoundSchedule.Create(cfg, rng);
            int resolved = 0;
            for (int i = 0; i < History.Count; i++) if (!History[i].isVoid) resolved++;
            var r = new LiveRound();
            r.attempt = k;
            r.roundId = k + 1;
            r.roundNumber = resolved + 1;
            r.holdNs = sched.HoldNs;
            r.cueOffsetNs = sched.CueOffsetNs;
            r.startAtMs = nowMs + LiveRules.StartLeadMs;
            r.cueDueAtMs = r.startAtMs + LiveRules.CeilMs(sched.CueOffsetNs);
            Round = r;
            Phase = LivePhase.Countdown;
            DeadlineMs = r.cueDueAtMs;
            Out.Add(LiveEvent.RoundStart);
        }

        private void ApplyDeadlines(long nowMs)
        {
            for (int guard = 0; guard < 8; guard++)
            {
                if (IsOver || nowMs < DeadlineMs) return;
                switch (Phase)
                {
                    case LivePhase.Lobby:
                        Cancel(LiveEnd.LobbyExpired);
                        break;
                    case LivePhase.Ready:
                    {
                        bool r0 = ReadyFor[0] == Attempt;
                        bool r1 = ReadyFor[1] == Attempt;
                        if (r0 && !r1) Finish(0, LiveEnd.Forfeit, 1);
                        else if (r1 && !r0) Finish(1, LiveEnd.Forfeit, 0);
                        else Finish(-1, LiveEnd.Abandoned, -1);
                        break;
                    }
                    case LivePhase.Countdown:
                        Round.cueSentAtMs = nowMs;
                        Phase = LivePhase.Draw;
                        DeadlineMs = nowMs + LiveRules.ReportWindowMs(cfg);
                        Out.Add(LiveEvent.Draw);
                        break;
                    case LivePhase.Draw:
                        Resolve(nowMs);
                        break;
                }
            }
        }

        private LiveVerdict Validate(int slot, LiveReport rep, long nowMs)
        {
            if (slot < 0 || slot >= Slots || Phase == LivePhase.Lobby || IsOver) return LiveVerdict.NotPlaying;
            if (Phase == LivePhase.Ready) return rep.roundId <= Attempt ? LiveVerdict.Stale : LiveVerdict.WrongRound;
            var r = Round;
            if (rep.roundId < r.roundId) return LiveVerdict.Stale;
            if (rep.roundId != r.roundId) return LiveVerdict.WrongRound;
            if (r.reported[slot]) return LiveVerdict.Duplicate;
            if (rep.countdownStartNs <= 0) return LiveVerdict.Inconsistent;
            long sent = r.cueSentAtMs >= 0 ? r.cueSentAtMs : r.cueDueAtMs;
            long expectedOffset = r.cueOffsetNs + Math.Max(0L, sent - r.cueDueAtMs) * Ms;

            if (rep.kind == ReportKind.Early)
            {
                if (r.cueSentAtMs >= 0 && nowMs > r.cueSentAtMs + LiveRules.EarlyGraceMs) return LiveVerdict.Late;
                if (rep.inputNs < rep.countdownStartNs) return LiveVerdict.Inconsistent;
                if (rep.cuePresentedNs != 0 && rep.inputNs >= rep.cuePresentedNs) return LiveVerdict.Inconsistent;
                if (rep.inputNs - rep.countdownStartNs > expectedOffset + LiveRules.CueLateToleranceNs) return LiveVerdict.Inconsistent;
                return LiveVerdict.Accepted;
            }
            if (rep.kind != ReportKind.Shot) return LiveVerdict.Implausible;
            if (r.cueSentAtMs < 0) return LiveVerdict.Implausible;
            if (nowMs > DeadlineMs) return LiveVerdict.Late;
            if (rep.reactionNs < 0 || rep.reactionNs > cfg.NoShotTimeoutNs) return LiveVerdict.Implausible;
            long diff = (rep.inputNs - rep.cuePresentedNs) - rep.reactionNs;
            if (diff > ConsistencyToleranceNs || diff < -ConsistencyToleranceNs) return LiveVerdict.Inconsistent;
            if (rep.cuePresentedNs <= rep.countdownStartNs) return LiveVerdict.Inconsistent;
            long off = rep.cuePresentedNs - rep.countdownStartNs;
            if (off < expectedOffset - LiveRules.CueEarlyToleranceNs || off > expectedOffset + LiveRules.CueLateToleranceNs)
                return LiveVerdict.Inconsistent;
            if ((nowMs - r.cueSentAtMs) * Ms + cfg.ReportSlackNs < rep.reactionNs) return LiveVerdict.Implausible;
            return LiveVerdict.Accepted;
        }

        private GhostAttempt InputOf(LiveRound r, int slot)
        {
            var a = new GhostAttempt();
            a.roundNumber = r.roundNumber;
            a.inputOffsetNs = -1;
            if (!r.reported[slot]) { a.kind = AttemptKind.NoShot; return a; }
            LiveReport rep = r.reports[slot];
            if (rep.kind == ReportKind.Early) { a.kind = AttemptKind.Early; return a; }
            a.kind = rep.reactionNs < cfg.AnticipationNs ? AttemptKind.Anticipation : AttemptKind.Shot;
            a.reactionNs = rep.reactionNs;
            return a;
        }

        private readonly List<GhostAttempt> inputs0 = new List<GhostAttempt>(9);
        private readonly List<GhostAttempt> inputs1 = new List<GhostAttempt>(9);

        private void Resolve(long nowMs)
        {
            var r = Round;
            inputs0.Add(InputOf(r, 0));
            inputs1.Add(InputOf(r, 1));
            var replay = LiveReplay.Play(cfg, Seed, LiveRules.Rounds, LiveRules.MaxAttempts, inputs0, inputs1,
                Guns.Effective(SlotGuns[0], Ranked), Guns.Effective(SlotGuns[1], Ranked));
            if (replay.Attempts.Count != inputs0.Count)
                throw new InvalidOperationException("live replay played " + replay.Attempts.Count + " of " + inputs0.Count + " attempts");
            LiveAttemptView v = replay.Attempts[replay.Attempts.Count - 1];
            if (v.roundNumber != r.roundNumber)
                throw new InvalidOperationException("live round number " + r.roundNumber + " != replay " + v.roundNumber);
            v.attempt = r.attempt;
            v.roundId = r.roundId;
            v.resolvedAtMs = nowMs;
            History.Add(v);
            Hp[0] = replay.Hp[0];
            Hp[1] = replay.Hp[1];
            RoundWins[0] = replay.RoundWins[0];
            RoundWins[1] = replay.RoundWins[1];
            Round = null;
            Out.Add(LiveEvent.RoundResult);
            Attempt = History.Count;
            if (replay.Terminal) Finish(replay.Winner, replay.End, -1);
            else EnterReady(nowMs);
        }

        private void Finish(int winner, LiveEnd end, int forfeitedBy)
        {
            Phase = LivePhase.Finished;
            Round = null;
            DeadlineMs = LiveRules.NoDeadline;
            var res = new LiveResult();
            res.winner = winner;
            res.end = end;
            res.forfeitedBy = forfeitedBy;
            res.hp[0] = Hp[0];
            res.hp[1] = Hp[1];
            res.roundWins[0] = RoundWins[0];
            res.roundWins[1] = RoundWins[1];
            Result = res;
            Out.Add(LiveEvent.MatchOver);
        }

        private void Cancel(LiveEnd end)
        {
            Phase = LivePhase.Cancelled;
            Round = null;
            DeadlineMs = LiveRules.NoDeadline;
            var res = new LiveResult();
            res.end = end;
            res.hp[0] = Hp[0];
            res.hp[1] = Hp[1];
            Result = res;
            Out.Add(LiveEvent.Cancelled);
        }

        // ------------------------------------------------------------------------------------ wire names

        public static string ToWire(LivePhase p)
        {
            switch (p)
            {
                case LivePhase.Lobby: return "lobby";
                case LivePhase.Ready: return "ready";
                case LivePhase.Countdown: return "countdown";
                case LivePhase.Draw: return "draw";
                case LivePhase.Finished: return "finished";
                default: return "cancelled";
            }
        }

        public static string ToWire(LiveEvent e)
        {
            switch (e)
            {
                case LiveEvent.Joined: return "joined";
                case LiveEvent.Ready: return "ready";
                case LiveEvent.RoundStart: return "round_start";
                case LiveEvent.Draw: return "draw";
                case LiveEvent.RoundResult: return "round_result";
                case LiveEvent.MatchOver: return "match_over";
                default: return "cancelled";
            }
        }

        public static string ToWire(LiveEnd e)
        {
            switch (e)
            {
                case LiveEnd.Ko: return "ko";
                case LiveEnd.DoubleKo: return "double_ko";
                case LiveEnd.Rounds: return "rounds";
                case LiveEnd.MaxAttempts: return "max_attempts";
                case LiveEnd.Forfeit: return "forfeit";
                case LiveEnd.Abandoned: return "abandoned";
                case LiveEnd.LobbyExpired: return "lobby_expired";
                case LiveEnd.HostLeft: return "host_left";
                default: return "";
            }
        }
    }

    /// <summary>Result of <see cref="LiveReplay.Play"/>: every attempt's view plus the match status after the last one.</summary>
    public sealed class LiveReplayResult
    {
        public readonly List<LiveAttemptView> Attempts = new List<LiveAttemptView>(9);
        public readonly double[] Hp = new double[2];
        public readonly int[] RoundWins = new int[2];
        /// <summary>The match is over (KO, double KO, rounds, max attempts); otherwise another attempt follows.</summary>
        public bool Terminal;
        public LiveEnd End;
        public int Winner = -1;
    }

    /// <summary>
    /// Plays the attempts of a live match so far through the real <see cref="LocalAuthority"/> on a synthetic clock,
    /// exactly like <see cref="RecordedMatchSim"/> (and the server's playRecordedMatch), keeping each attempt's view.
    /// Both lists have one entry per attempt (void replays included).
    /// </summary>
    public static class LiveReplay
    {
        public static LiveReplayResult Play(DuelConfig cfg, int seed, int rounds, int maxAttempts,
            IList<GhostAttempt> slot0, IList<GhostAttempt> slot1, string gun0, string gun1)
        {
            var clock = new ManualClock { Now = RecordedMatchSim.T0Ns };
            var net = new LoopbackTransport();
            var a = new LocalAuthority(cfg, clock, net, seed);
            a.SetGun(0, gun0);
            a.SetGun(1, gun1);
            a.StartMatch();
            var res = new LiveReplayResult();
            int resolved = 0, k = 0;
            for (;;)
            {
                if (a.Match.Over) { res.Terminal = true; res.End = a.Match.Winner >= 0 ? LiveEnd.Ko : LiveEnd.DoubleKo; break; }
                if (resolved >= rounds) { res.Terminal = true; res.End = LiveEnd.Rounds; break; }
                if (k >= maxAttempts) { res.Terminal = true; res.End = LiveEnd.MaxAttempts; break; }
                if (k >= slot0.Count || k >= slot1.Count) break; // more attempts to come
                a.SetDamageMultiplier(SuddenDeath.MultiplierFor(cfg, a.Match));
                Exchange(a, clock, net, slot0[k], slot1[k]);
                res.Attempts.Add(View(a, slot0[k], slot1[k]));
                if (!a.Round.isVoid) resolved++;
                k++;
                clock.Now += RecordedMatchSim.BetweenNs;
                if (a.Phase == RoundPhase.Result && !a.Match.Over) a.NextRound();
            }
            res.Hp[0] = a.Match.Hp[0];
            res.Hp[1] = a.Match.Hp[1];
            res.RoundWins[0] = a.Match.RoundWins[0];
            res.RoundWins[1] = a.Match.RoundWins[1];
            res.Winner = RecordedMatchRules.Winner(a.Match);
            return res;
        }

        private static LiveAttemptView View(LocalAuthority a, GhostAttempt s0, GhostAttempt s1)
        {
            var v = new LiveAttemptView();
            v.roundNumber = a.Round.roundNumber;
            v.holdNs = a.Round.schedule.HoldNs;
            v.isVoid = a.Round.isVoid;
            v.tie = a.Round.isTie;
            v.winner = a.Round.winnerSlot;
            v.kinds[0] = s0.kind;
            v.kinds[1] = s1.kind;
            for (int i = 0; i < 2; i++)
            {
                var slot = a.Round.slots[i];
                v.outcomes[i] = slot.outcome;
                bool shotLike = slot.outcome == SlotOutcome.Shot || slot.outcome == SlotOutcome.Anticipation;
                v.reactionsNs[i] = shotLike ? (i == 0 ? s0.reactionNs : s1.reactionNs) : -1;
                v.damage[i] = slot.damageDealt;
                v.grades[i] = slot.grade;
                v.hp[i] = a.Match.Hp[i];
            }
            return v;
        }

        private static void Exchange(LocalAuthority a, ManualClock clock, LoopbackTransport net, GhostAttempt s0, GhostAttempt s1)
        {
            a.StartCountdown();
            long start = clock.Now;
            clock.Now = start + a.Round.schedule.CueOffsetNs;
            a.Tick();
            long cue = a.Round.cueDecidedNs;
            clock.Now = cue + RecordedMatchSim.AfterCueNs;
            ShotReport r;
            if (Report(a, 0, s0, cue, start, out r)) net.SendReport(r);
            if (Report(a, 1, s1, cue, start, out r)) net.SendReport(r);
            a.Tick();
        }

        private static bool Report(LocalAuthority a, int slot, GhostAttempt s, long cue, long start, out ShotReport r)
        {
            r = new ShotReport();
            if (s.kind == AttemptKind.NoShot) return false;
            r.roundId = a.Round.roundId;
            r.slot = slot;
            r.inputSource = InputSource.Remote;
            if (s.kind == AttemptKind.Early)
            {
                r.kind = ReportKind.Early;
                r.inputNs = start + RecordedMatchSim.EarlyAfterStartNs;
                return true;
            }
            r.kind = ReportKind.Shot;
            r.reactionNs = s.reactionNs;
            r.cuePresentedNs = cue;
            r.inputNs = cue + s.reactionNs;
            return true;
        }
    }
}
