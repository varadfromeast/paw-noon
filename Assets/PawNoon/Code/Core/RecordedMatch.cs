using System.Collections.Generic;

namespace PawNoon.Core
{
    /// <summary>Why a recorded (ghost) match ended. Wire names as the server's <c>endReason</c>.</summary>
    public enum RecordedEnd
    {
        None = 0,
        Ko,
        DoubleKo,
        Rounds,
        MaxAttempts,
        OpponentExhausted,
        PlayerExhausted
    }

    /// <summary>
    /// The recorded-match driver's end rules (supabase/functions/_shared/duel/recorded_match.ts), shared
    /// by the live client (DuelDirector in PvP) and <see cref="RecordedMatchSim"/>. Checked before each
    /// attempt, in this order: KO / double KO, <c>Rounds</c> resolved rounds (3), <c>MaxAttempts</c>
    /// countdowns (9), the opponent recording running out of attempts. Winner: KO winner, else more HP.
    /// </summary>
    public sealed class RecordedMatchRules
    {
        public const int DefaultRounds = 3;
        public const int DefaultMaxAttempts = 9;

        public int Rounds = DefaultRounds;
        public int MaxAttempts = DefaultMaxAttempts;
        /// <summary>Attempts in the opponent recording (int.MaxValue = unlimited).</summary>
        public int OpponentAttempts = int.MaxValue;

        /// <summary>Countdowns started this match (void replays included).</summary>
        public int Attempts { get; private set; }
        /// <summary>Non-void attempts that resolved.</summary>
        public int Resolved { get; private set; }
        public RecordedEnd EndReason { get; private set; }

        public RecordedMatchRules() { }

        public RecordedMatchRules(int rounds, int maxAttempts, int opponentAttempts)
        {
            Rounds = rounds > 0 ? rounds : DefaultRounds;
            MaxAttempts = maxAttempts > 0 ? maxAttempts : DefaultMaxAttempts;
            OpponentAttempts = opponentAttempts >= 0 ? opponentAttempts : int.MaxValue;
        }

        public void Reset()
        {
            Attempts = 0;
            Resolved = 0;
            EndReason = RecordedEnd.None;
        }

        /// <summary>A countdown started (LocalAuthority.StartCountdown returned true).</summary>
        public void AttemptStarted() { Attempts++; }

        /// <summary>The attempt resolved (RoundResolved event; a void attempt never resolves).</summary>
        public void AttemptResolved() { Resolved++; }

        /// <summary>None = play another attempt; otherwise the match ends now (and EndReason is set).</summary>
        public RecordedEnd Check(MatchState m)
        {
            RecordedEnd e = RecordedEnd.None;
            if (m.Over) e = m.Winner >= 0 ? RecordedEnd.Ko : RecordedEnd.DoubleKo;
            else if (Resolved >= Rounds) e = RecordedEnd.Rounds;
            else if (Attempts >= MaxAttempts) e = RecordedEnd.MaxAttempts;
            else if (Attempts >= OpponentAttempts) e = RecordedEnd.OpponentExhausted;
            if (e != RecordedEnd.None) EndReason = e;
            return e;
        }

        /// <summary>KO winner, else more HP left (= more total damage dealt), equal = -1 (draw).</summary>
        public static int Winner(MatchState m)
        {
            if (m.Over) return m.Winner;
            return m.Hp[0] > m.Hp[1] ? 0 : (m.Hp[1] > m.Hp[0] ? 1 : -1);
        }

        public static string ToWire(RecordedEnd e)
        {
            switch (e)
            {
                case RecordedEnd.Ko: return "ko";
                case RecordedEnd.DoubleKo: return "double_ko";
                case RecordedEnd.Rounds: return "rounds";
                case RecordedEnd.MaxAttempts: return "max_attempts";
                case RecordedEnd.OpponentExhausted: return "opponent_exhausted";
                case RecordedEnd.PlayerExhausted: return "player_exhausted";
                default: return "none";
            }
        }
    }

    /// <summary>Result of <see cref="RecordedMatchSim.Play"/> (server <c>MatchResult</c>).</summary>
    public sealed class RecordedMatchResult
    {
        public RecordedEnd EndReason;
        /// <summary>0 = player, 1 = opponent, -1 = draw.</summary>
        public int Winner = -1;
        public readonly double[] Hp = new double[2];
        public readonly int[] RoundWins = new int[2];
        public int AttemptsUsed;
        public int ResolvedRounds;
        /// <summary>Round number of each played attempt (void attempts repeat it).</summary>
        public readonly List<int> RoundNumbers = new List<int>(9);
    }

    /// <summary>
    /// C# twin of the server's authoritative recompute (playRecordedMatch in recorded_match.ts): plays two
    /// recordings attempt by attempt through the real LocalAuthority on a synthetic clock. Used by the
    /// offline mock (it is what the server would answer) and by the tests that check the live client
    /// (GhostOpponent + DuelDirector rules) against it.
    /// </summary>
    public static class RecordedMatchSim
    {
        // recorded_match.ts HARNESS
        public const long T0Ns = 10000000000L;
        public const long AfterCueNs = 2000000000L;
        public const long BetweenNs = 1000000000L;
        public const long EarlyAfterStartNs = 1000000000L;

        /// <param name="playerGun">Gun in effect for the player (Guns.Effective: Standard in ranked).</param>
        /// <param name="opponentGun">Gun in effect for the opponent recording (same rule).</param>
        public static RecordedMatchResult Play(DuelConfig cfg, int seed, int rounds, int maxAttempts,
            IList<GhostAttempt> player, IList<GhostAttempt> opponent,
            string playerGun = Guns.Standard, string opponentGun = Guns.Standard)
        {
            var clock = new ManualClock { Now = T0Ns };
            var net = new LoopbackTransport();
            var a = new LocalAuthority(cfg, clock, net, seed);
            a.SetGun(0, playerGun);
            a.SetGun(1, opponentGun);
            a.StartMatch();
            var res = new RecordedMatchResult();
            int resolved = 0, k = 0;
            RecordedEnd end;
            for (;;)
            {
                if (a.Match.Over) { end = a.Match.Winner >= 0 ? RecordedEnd.Ko : RecordedEnd.DoubleKo; break; }
                if (resolved >= rounds) { end = RecordedEnd.Rounds; break; }
                if (k >= maxAttempts) { end = RecordedEnd.MaxAttempts; break; }
                if (k >= opponent.Count) { end = RecordedEnd.OpponentExhausted; break; }
                if (k >= player.Count) { end = RecordedEnd.PlayerExhausted; break; }
                a.SetDamageMultiplier(SuddenDeath.MultiplierFor(cfg, a.Match));   // LAST LIGHT, as recorded_match.ts
                Exchange(a, clock, net, player[k], opponent[k]);
                res.RoundNumbers.Add(a.Round.roundNumber);
                if (!a.Round.isVoid) resolved++;
                k++;
                clock.Now += BetweenNs;
                if (a.Phase == RoundPhase.Result && !a.Match.Over) a.NextRound();
            }
            res.EndReason = end;
            res.Winner = RecordedMatchRules.Winner(a.Match);
            res.Hp[0] = a.Match.Hp[0];
            res.Hp[1] = a.Match.Hp[1];
            res.RoundWins[0] = a.Match.RoundWins[0];
            res.RoundWins[1] = a.Match.RoundWins[1];
            res.AttemptsUsed = k;
            res.ResolvedRounds = resolved;
            return res;
        }

        private static void Exchange(LocalAuthority a, ManualClock clock, LoopbackTransport net, GhostAttempt s0, GhostAttempt s1)
        {
            a.StartCountdown();
            long start = clock.Now;
            clock.Now = start + a.Round.schedule.CueOffsetNs;
            a.Tick();
            long cue = a.Round.cueDecidedNs;
            clock.Now = cue + AfterCueNs;
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
            r.inputSource = InputSource.Injected;
            if (s.kind == AttemptKind.Early)
            {
                r.kind = ReportKind.Early;
                r.inputNs = start + EarlyAfterStartNs;
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
