using System;

namespace PawNoon.Core
{
    /// <summary>Pure sampling helpers for the bot.</summary>
    public static class BotModel
    {
        /// <summary>Normal(mean, sd) via Box-Muller, clamped to [min, max].</summary>
        public static double SampleReactionMs(Random rng, double meanMs, double sdMs, double minMs, double maxMs)
        {
            double u1 = 1.0 - rng.NextDouble(); // (0, 1]
            double u2 = rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            double v = meanMs + sdMs * z;
            if (v < minMs) v = minMs;
            if (v > maxMs) v = maxMs;
            return v;
        }

        public static double SampleReactionMs(DuelConfig cfg, Random rng, int playerRoundWins)
        {
            double mean, sd;
            cfg.BotDistribution(playerRoundWins, out mean, out sd);
            return SampleReactionMs(rng, mean, sd, cfg.botMinMs, cfg.botMaxMs);
        }
    }

    /// <summary>
    /// "Blackpaw": an opponent that goes through exactly the same DuelClient / transport / report path
    /// as a remote player. It never knows the cue time in advance: it reacts to the DrawCue it receives.
    /// Its only pre-cue action is a rare planned foul (jumping the gun), always due before the earliest
    /// possible cue. Feints and the late tell are presentation-only and live in <see cref="BotActing"/>.
    /// </summary>
    public sealed class BotClient : IOpponent
    {
        public readonly DuelClient Client;

        // IOpponent: thin forwards, behaviour unchanged
        DuelClient IOpponent.Client { get { return Client; } }
        void IOpponent.StartMatch(int seed) { Reseed(seed); }
        void IOpponent.SetPlayerRoundWins(int wins) { PlayerRoundWins = wins; }
        void IOpponent.PreAuthorityTick(long frameStartNs) { }
        bool IOpponent.HostActing { get { return true; } }
        private readonly DuelConfig cfg;
        private Random rng;

        /// <summary>Set by the host before each round (adaptive difficulty).</summary>
        public int PlayerRoundWins;

        private bool foulPlanned;
        private long foulAtNs;
        private bool shotPlanned;
        private long shotAtNs;

        public double LastPlannedReactionMs { get; private set; }
        /// <summary>True from round start until the planned foul is pressed (or the cue arrives).</summary>
        public bool FoulPlanned { get { return foulPlanned; } }

        public BotClient(int slot, IDuelTransport transport, DuelConfig cfg, int seed)
        {
            this.cfg = cfg;
            Client = new DuelClient(slot, transport, cfg);
            rng = new Random(seed);
        }

        public void Reseed(int seed) { rng = new Random(seed); }

        /// <summary>Call every frame at frame start (after the authority ticked).</summary>
        public void Tick(long frameStartNs, long presentLatencyNs)
        {
            RoundStartMsg rs;
            if (Client.PollRoundStart(frameStartNs, out rs)) PlanRound(frameStartNs, rs);

            if (Client.PollCue(frameStartNs, presentLatencyNs))
            {
                foulPlanned = false; // he waited it out
                LastPlannedReactionMs = BotModel.SampleReactionMs(cfg, rng, PlayerRoundWins);
                shotAtNs = Client.CuePresentedNs + DuelConfig.MsToNs(LastPlannedReactionMs);
                shotPlanned = true;
            }

            Client.Tick(frameStartNs);

            if (foulPlanned && frameStartNs >= foulAtNs)
            {
                foulPlanned = false;
                Client.Press(foulAtNs, InputSource.Bot, false);
            }
            if (shotPlanned && frameStartNs >= shotAtNs)
            {
                shotPlanned = false;
                Client.Press(shotAtNs, InputSource.Bot, false);
            }
        }

        private void PlanRound(long startNs, RoundStartMsg rs)
        {
            shotPlanned = false;
            foulPlanned = false;
            // always draw, so the rng stream does not depend on the round number
            double roll = rng.NextDouble();
            double at = rng.NextDouble();
            if (rs.roundNumber < cfg.botFoulMinRound || roll >= cfg.botFoulChance) return;

            // He cracks during the tension: from half a beat before "1" disappears to well inside the
            // guaranteed part of the hold, so it always lands before the earliest possible cue.
            long countdown = rs.countdownBeats * rs.beatNs;
            long holdMin = DuelConfig.SecToNs(cfg.holdMinSeconds);
            long lo = Math.Max(rs.beatNs / 2, countdown - rs.beatNs / 2);
            long hi = countdown + (long)(holdMin * 0.85);
            if (hi <= lo) hi = lo + 1;
            foulAtNs = startNs + lo + (long)(at * (hi - lo));
            foulPlanned = true;
        }
    }

    public enum BotActKind
    {
        None = 0,
        /// <summary>Paw flick toward the holster + spur jingle. Bait only.</summary>
        Feint = 1,
        /// <summary>Ears flatten: the learnable read, a fixed lead before the real cue.</summary>
        Tell = 2
    }

    /// <summary>
    /// Blackpaw's body language during the hold: 0-2 feints and the occasional late tell.
    /// PRESENTATION ONLY. It is planned by the local host from the authority's private schedule (the bot
    /// is local, so the host may stage it), and it never feeds into any reaction, report or rule: the bot's
    /// shot still comes only from the DrawCue it receives. Not used for remote opponents.
    /// All times are on the host clock (the authority's clock).
    /// </summary>
    public sealed class BotActing
    {
        public const int MaxFeints = 2;

        private readonly DuelConfig cfg;
        private Random rng;
        private readonly long[] feintAt = new long[MaxFeints];
        private int feintCount;
        private int feintNext;
        private bool tellPlanned;
        private bool tellDone;
        private long tellAt;
        private long cueAt;

        public BotActing(DuelConfig cfg, int seed)
        {
            this.cfg = cfg ?? new DuelConfig();
            rng = new Random(seed);
        }

        public void Reseed(int seed) { rng = new Random(seed); }

        public int FeintCount { get { return feintCount; } }
        public long FeintAtNs(int i) { return i >= 0 && i < feintCount ? feintAt[i] : -1; }
        public bool TellPlanned { get { return tellPlanned; } }
        public long TellAtNs { get { return tellPlanned ? tellAt : -1; } }
        public long CueAtNs { get { return cueAt; } }

        /// <summary>Cancels everything still pending (bot fouled, round void, cue arrived).</summary>
        public void Clear()
        {
            feintCount = 0;
            feintNext = 0;
            tellPlanned = false;
            tellDone = true;
        }

        public static double FeintChance(DuelConfig cfg, double itch)
        {
            return Math.Min(cfg.botFeintMaxChance, cfg.botFeintChance + cfg.botFeintChancePerItch * Math.Max(0, itch));
        }

        public static double SecondFeintChance(DuelConfig cfg, double itch)
        {
            return Math.Min(cfg.botSecondFeintMaxChance, cfg.botSecondFeintChance + cfg.botSecondFeintPerItch * Math.Max(0, itch));
        }

        /// <summary>
        /// "Itch" = how jumpy the player has been lately. +botItchPerEarlyDraw on a round with an early draw,
        /// otherwise decays by botItchDecay; capped at botItchMax. Start a match at 0.
        /// </summary>
        public static double NextItch(DuelConfig cfg, double itch, bool playerDrewEarly)
        {
            double v = playerDrewEarly ? itch + cfg.botItchPerEarlyDraw : itch * cfg.botItchDecay;
            if (v < 0) v = 0;
            if (v > cfg.botItchMax) v = cfg.botItchMax;
            return v;
        }

        /// <summary>
        /// Plans the hold. holdStartNs = when the countdown ends ("1" disappears), cueAtNs = when the
        /// authority will send the cue. Feints start in [holdStart + minAfterHold, cue - minBeforeCue];
        /// the tell (if any) is at cue - tellLead.
        /// </summary>
        public void Plan(long holdStartNs, long cueAtNs, double itch)
        {
            feintCount = 0;
            feintNext = 0;
            tellPlanned = false;
            tellDone = false;
            cueAt = cueAtNs;

            // fixed number of draws per round keeps the stream stable whatever the outcome
            double r1 = rng.NextDouble(), r2 = rng.NextDouble(), r3 = rng.NextDouble(), r4 = rng.NextDouble(), r5 = rng.NextDouble();

            long lo = holdStartNs + DuelConfig.MsToNs(cfg.botFeintMinAfterHoldMs);
            long hi = cueAtNs - DuelConfig.MsToNs(cfg.botFeintMinBeforeCueMs);
            if (hi >= lo && r1 < FeintChance(cfg, itch))
            {
                long gapMin = DuelConfig.MsToNs(cfg.botFeintGapMinMs);
                long gapMax = DuelConfig.MsToNs(cfg.botFeintGapMaxMs);
                bool two = r2 < SecondFeintChance(cfg, itch) && hi - lo >= gapMin;
                if (two)
                {
                    long firstHi = hi - gapMin;
                    feintAt[0] = lo + (long)(r3 * (firstHi - lo));
                    long gapHi = Math.Min(gapMax, hi - feintAt[0]);
                    feintAt[1] = feintAt[0] + gapMin + (long)(r4 * Math.Max(0L, gapHi - gapMin));
                    feintCount = 2;
                }
                else
                {
                    feintAt[0] = lo + (long)(r3 * (hi - lo));
                    feintCount = 1;
                }
            }

            if (r5 < cfg.botTellChance)
            {
                tellAt = cueAtNs - DuelConfig.MsToNs(cfg.botTellLeadMs);
                if (tellAt >= holdStartNs) tellPlanned = true;
            }
        }

        /// <summary>
        /// Call each frame with the frame-start time; returns at most one due act per call (call in a loop).
        /// A feint that comes due too late (frame hitch) is dropped rather than shown near the cue.
        /// </summary>
        public BotActKind Poll(long nowNs)
        {
            long minBefore = DuelConfig.MsToNs(cfg.botFeintMinBeforeCueMs);
            while (feintNext < feintCount && nowNs >= feintAt[feintNext])
            {
                feintNext++;
                if (nowNs <= cueAt - minBefore) return BotActKind.Feint;
            }
            if (tellPlanned && !tellDone && nowNs >= tellAt)
            {
                tellDone = true;
                if (nowNs < cueAt) return BotActKind.Tell;
            }
            return BotActKind.None;
        }
    }
}
