using System;
using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    public class BotTests
    {
        private static long Ms(double ms) { return DuelConfig.MsToNs(ms); }

        [Test]
        public void AdaptiveSkillCurve()
        {
            var cfg = DuelConfig.Default();
            double mean, sd;
            cfg.BotDistribution(0, out mean, out sd);
            Assert.AreEqual(300, mean, 1e-9);
            Assert.AreEqual(50, sd, 1e-9);
            cfg.BotDistribution(cfg.botAdaptRounds, out mean, out sd);
            Assert.AreEqual(220, mean, 1e-9);
            Assert.AreEqual(30, sd, 1e-9);
            double prev = 1e9;
            for (int w = 0; w <= cfg.botAdaptRounds + 2; w++)
            {
                cfg.BotDistribution(w, out mean, out sd);
                Assert.LessOrEqual(mean, prev);
                Assert.GreaterOrEqual(mean, 220 - 1e-9);
                prev = mean;
            }
        }

        [Test]
        public void ConfigParsesBotActingKeysAndSanitizes()
        {
            var c = DuelConfig.FromJson("{\"botTellChance\": 2, \"botFeintGapMinMs\": 600, \"botFeintGapMaxMs\": 100, \"botFoulMinRound\": 3, \"botTellLeadMs\": 180, \"botItchDecay\": -1}");
            Assert.AreEqual(1.0, c.botTellChance, 1e-12);
            Assert.AreEqual(600, c.botFeintGapMinMs, 1e-12);
            Assert.AreEqual(600, c.botFeintGapMaxMs, 1e-12);
            Assert.AreEqual(3, c.botFoulMinRound);
            Assert.AreEqual(180, c.botTellLeadMs, 1e-12);
            Assert.AreEqual(0, c.botItchDecay, 1e-12);
            Assert.AreEqual(350, c.botFeintMinBeforeCueMs, 1e-12);
        }

        [Test]
        public void FeintsAndTellRespectTheirWindows()
        {
            var cfg = DuelConfig.Default();
            var acting = new BotActing(cfg, 1234);
            var rng = new Random(5);
            int twos = 0;
            for (int i = 0; i < 20000; i++)
            {
                long holdStart = 5000000000L + (long)(rng.NextDouble() * 1e9);
                double holdS = cfg.holdMinSeconds + rng.NextDouble() * (cfg.holdMaxSeconds - cfg.holdMinSeconds);
                long cue = holdStart + DuelConfig.SecToNs(holdS);
                acting.Plan(holdStart, cue, rng.NextDouble() * cfg.botItchMax);
                Assert.LessOrEqual(acting.FeintCount, BotActing.MaxFeints);
                for (int f = 0; f < acting.FeintCount; f++)
                {
                    long t = acting.FeintAtNs(f);
                    Assert.GreaterOrEqual(t, holdStart + Ms(cfg.botFeintMinAfterHoldMs));
                    Assert.LessOrEqual(t, cue - Ms(cfg.botFeintMinBeforeCueMs), "feint within 350 ms of the cue");
                }
                if (acting.FeintCount == 2)
                {
                    twos++;
                    long gap = acting.FeintAtNs(1) - acting.FeintAtNs(0);
                    Assert.GreaterOrEqual(gap, Ms(cfg.botFeintGapMinMs) - 1);
                    Assert.LessOrEqual(gap, Ms(cfg.botFeintGapMaxMs) + 1);
                }
                if (acting.TellPlanned)
                {
                    Assert.AreEqual(cue - Ms(cfg.botTellLeadMs), acting.TellAtNs);
                    for (int f = 0; f < acting.FeintCount; f++) Assert.Less(acting.FeintAtNs(f), acting.TellAtNs);
                }
            }
            Assert.Greater(twos, 0);
        }

        [Test]
        public void TellRateAndFeintRateFollowConfigAndItch()
        {
            var cfg = DuelConfig.Default();
            var acting = new BotActing(cfg, 99);
            const int n = 20000;
            long holdStart = 1000000000L;
            long cue = holdStart + DuelConfig.SecToNs(3.0); // roomy window: rates are pure chance
            int tells = 0, feintCalm = 0, feintJumpy = 0, twoCalm = 0, twoJumpy = 0;
            for (int i = 0; i < n; i++)
            {
                acting.Plan(holdStart, cue, 0);
                if (acting.TellPlanned) tells++;
                if (acting.FeintCount > 0) feintCalm++;
                if (acting.FeintCount == 2) twoCalm++;
                acting.Plan(holdStart, cue, 2.0);
                if (acting.FeintCount > 0) feintJumpy++;
                if (acting.FeintCount == 2) twoJumpy++;
            }
            Assert.AreEqual(cfg.botTellChance, tells / (double)n, 0.015);
            Assert.AreEqual(BotActing.FeintChance(cfg, 0), feintCalm / (double)n, 0.015);
            Assert.AreEqual(BotActing.FeintChance(cfg, 2.0), feintJumpy / (double)n, 0.015);
            Assert.Greater(feintJumpy, feintCalm);
            Assert.Greater(twoJumpy, twoCalm);
            Assert.LessOrEqual(BotActing.FeintChance(cfg, 100), cfg.botFeintMaxChance + 1e-12);
        }

        [Test]
        public void ItchGrowsOnEarlyDrawsDecaysAndCaps()
        {
            var cfg = DuelConfig.Default();
            double itch = 0;
            itch = BotActing.NextItch(cfg, itch, true);
            Assert.AreEqual(cfg.botItchPerEarlyDraw, itch, 1e-12);
            itch = BotActing.NextItch(cfg, itch, false);
            Assert.AreEqual(cfg.botItchPerEarlyDraw * cfg.botItchDecay, itch, 1e-12);
            for (int i = 0; i < 20; i++) itch = BotActing.NextItch(cfg, itch, true);
            Assert.AreEqual(cfg.botItchMax, itch, 1e-12);
            for (int i = 0; i < 60; i++) itch = BotActing.NextItch(cfg, itch, false);
            Assert.Less(itch, 0.001);
        }

        [Test]
        public void PollDeliversActsInOrderAndNeverNearTheCue()
        {
            var cfg = DuelConfig.Default();
            cfg.botFeintChance = 1; cfg.botFeintMaxChance = 1;
            cfg.botSecondFeintChance = 1; cfg.botSecondFeintMaxChance = 1;
            cfg.botTellChance = 1;
            var acting = new BotActing(cfg, 7);
            long holdStart = 0, cue = DuelConfig.SecToNs(3.5);
            acting.Plan(holdStart, cue, 0);
            Assert.AreEqual(2, acting.FeintCount);
            Assert.IsTrue(acting.TellPlanned);

            int feints = 0, tells = 0;
            bool tellSeen = false;
            for (long t = 0; t < cue + Ms(500); t += Ms(8))
            {
                BotActKind k;
                while ((k = acting.Poll(t)) != BotActKind.None)
                {
                    if (k == BotActKind.Feint)
                    {
                        Assert.IsFalse(tellSeen);
                        Assert.LessOrEqual(t, cue - Ms(cfg.botFeintMinBeforeCueMs));
                        feints++;
                    }
                    else
                    {
                        tellSeen = true;
                        Assert.Less(t, cue);
                        Assert.GreaterOrEqual(cue - t, Ms(cfg.botTellLeadMs) - Ms(8));
                        tells++;
                    }
                }
            }
            Assert.AreEqual(2, feints);
            Assert.AreEqual(1, tells);

            // a frame hitch that jumps past the feint window drops the feints instead of showing them late
            acting.Plan(holdStart, cue, 0);
            Assert.AreEqual(BotActKind.Tell, acting.Poll(cue - Ms(100)));
            Assert.AreEqual(BotActKind.None, acting.Poll(cue - Ms(90)));
            // after the cue nothing is shown at all
            acting.Plan(holdStart, cue, 0);
            Assert.AreEqual(BotActKind.None, acting.Poll(cue + Ms(1)));
            // Clear cancels
            acting.Plan(holdStart, cue, 0);
            acting.Clear();
            Assert.AreEqual(BotActKind.None, acting.Poll(cue - Ms(160)));
        }

        [Test]
        public void BotFoulIsRareSkipsRoundOneAndAlwaysPrecedesTheCue()
        {
            var cfg = DuelConfig.Default();
            cfg.botFoulChance = 1.0; // force it to observe the rules
            var clock = new ManualClock();
            clock.Now = 10000000000L;
            var net = new LoopbackTransport();
            var a = new LocalAuthority(cfg, clock, net, 11);
            var bot = new BotClient(1, net, cfg, 3);
            var me = new DuelClient(0, net, cfg);
            a.StartMatch();
            for (int round = 1; round <= 4; round++)
            {
                if (a.Phase == RoundPhase.Result) Assert.IsTrue(a.NextRound());
                Assert.IsTrue(a.StartCountdown());
                for (int f = 0; f < 8000 && a.Phase != RoundPhase.Result; f++)
                {
                    clock.AdvanceMs(1);
                    a.Tick();
                    RoundStartMsg m;
                    me.PollRoundStart(clock.Now, out m);
                    me.PollCue(clock.Now, 0);
                    me.Tick(clock.Now);
                    bot.Tick(clock.Now, 0);
                    a.Tick();
                }
                Assert.AreEqual(RoundPhase.Result, a.Phase);
                var s1 = a.Round.slots[1];
                if (a.Round.roundNumber < cfg.botFoulMinRound)
                {
                    Assert.AreEqual(SlotOutcome.Shot, s1.outcome, "no foul in round " + a.Round.roundNumber);
                }
                else
                {
                    Assert.AreEqual(SlotOutcome.Foul, s1.outcome);
                    Assert.AreEqual(ReportKind.Early, s1.report.kind);
                    Assert.AreEqual(0, s1.damageDealt, 1e-12);
                }
            }

            // at the shipped rate he fouls rarely (~4 %)
            var shipped = DuelConfig.Default();
            Assert.LessOrEqual(shipped.botFoulChance, 0.06);
            Assert.GreaterOrEqual(shipped.botFoulMinRound, 2);
        }
    }
}
