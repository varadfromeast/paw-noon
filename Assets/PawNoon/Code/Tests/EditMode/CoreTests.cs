using System;
using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    public class DamageAndGradeTests
    {
        private readonly DuelConfig cfg = DuelConfig.Default();

        [Test]
        public void DamageAnchorsMatchSpec()
        {
            double[] ms = { 150, 200, 250, 300, 400, 600, 1000, 1500 };
            double[] dmg = { 40, 36, 30, 24, 16, 8, 4, 2 };
            for (int i = 0; i < ms.Length; i++)
                Assert.AreEqual(dmg[i], DamageCurve.Evaluate(cfg, ms[i]), 1e-9, "at " + ms[i]);
            Assert.AreEqual(40, DamageCurve.Evaluate(cfg, 100), 1e-9);
            Assert.AreEqual(0, DamageCurve.Evaluate(cfg, 1500.001), 1e-9);
            Assert.AreEqual(33, DamageCurve.Evaluate(cfg, 225), 1e-9);
        }

        [Test]
        public void DamageIsMonotonicNonIncreasing()
        {
            double prev = double.MaxValue;
            for (double ms = 0; ms <= 2000; ms += 0.25)
            {
                double d = DamageCurve.Evaluate(cfg, ms);
                Assert.LessOrEqual(d, prev + 1e-12, "at " + ms);
                prev = d;
            }
        }

        [Test]
        public void GradeBands()
        {
            Assert.AreEqual(Grade.Lightning, Grading.FromMs(cfg, 200));
            Assert.AreEqual(Grade.Perfect, Grading.FromMs(cfg, 200.001));
            Assert.AreEqual(Grade.Perfect, Grading.FromMs(cfg, 250));
            Assert.AreEqual(Grade.Great, Grading.FromMs(cfg, 320));
            Assert.AreEqual(Grade.Good, Grading.FromMs(cfg, 420));
            Assert.AreEqual(Grade.Slow, Grading.FromMs(cfg, 700));
            Assert.AreEqual(Grade.Slowpoke, Grading.FromMs(cfg, 700.5));
        }

        [Test]
        public void ConfigParsesOverridesAndKeepsDefaults()
        {
            var c = DuelConfig.FromJson("{\"beatSeconds\":0.5, \"botName\":\"X\", \"unknown\": [1,2,{\"a\":null}]}");
            Assert.AreEqual(0.5, c.beatSeconds, 1e-12);
            Assert.AreEqual("X", c.botName);
            Assert.AreEqual(1.5, c.noShotTimeoutSeconds, 1e-12);
            var bad = DuelConfig.FromJson("{ this is not json");
            Assert.AreEqual(0.70, bad.beatSeconds, 1e-12);
        }

        [Test]
        public void JsonUnicodeEscapes()
        {
            var o = PnJson.ParseObject("{\"\\u00d7\":1,\"\\u0394\":2}");
            Assert.IsTrue(o.ContainsKey("\u00d7"));
            Assert.IsTrue(o.ContainsKey("\u0394"));
        }
    }

    public class TimingTests
    {
        [Test]
        public void TicksToNsIsExact()
        {
            Assert.AreEqual(1000000000L, TimeConv.TicksToNs(10000000L, 10000000L));
            Assert.AreEqual(41L, TimeConv.TicksToNs(1L, 24000000L)); // 41.666 -> floor
            long big = 24000000L * 86400L * 30L; // 30 days of 24 MHz ticks: no overflow
            Assert.AreEqual(86400L * 30L * 1000000000L, TimeConv.TicksToNs(big, 24000000L));
            Assert.AreEqual(123456789L, TimeConv.TicksToNs(123456789L, 1000000000L));
            Assert.AreEqual(1500000L, DuelConfig.MsToNs(1.5));
            Assert.AreEqual(700000000L, DuelConfig.SecToNs(0.70));
            Assert.AreEqual(2500000000L, TimeConv.SecondsToNs(2.5));
        }

        [Test]
        public void StopwatchClockIsMonotonic()
        {
            var c = new StopwatchClock();
            long a = c.NowNs;
            long b = c.NowNs;
            Assert.GreaterOrEqual(b, a);
        }

        [Test]
        public void ScheduleBounds()
        {
            var cfg = DuelConfig.Default();
            var rng = new Random(1234);
            long min = long.MaxValue, max = long.MinValue;
            for (int i = 0; i < 5000; i++)
            {
                var s = RoundSchedule.Create(cfg, rng);
                Assert.AreEqual(3, s.Beats);
                Assert.AreEqual(700000000L, s.BeatNs);
                Assert.GreaterOrEqual(s.HoldNs, 1000000000L);
                Assert.LessOrEqual(s.HoldNs, 4000000000L);
                min = Math.Min(min, s.HoldNs);
                max = Math.Max(max, s.HoldNs);
            }
            Assert.Less(min, 1100000000L, "hold should cover the low end");
            Assert.Greater(max, 3900000000L, "hold should cover the high end");
            // same seed -> same schedule
            var a = RoundSchedule.Create(cfg, new Random(7));
            var b = RoundSchedule.Create(cfg, new Random(7));
            Assert.AreEqual(a.HoldNs, b.HoldNs);
        }

        [Test]
        public void CountdownNumerals()
        {
            long beat = 700000000L;
            Assert.AreEqual(3, RoundSchedule.NumeralAt(3, beat, 0));
            Assert.AreEqual(2, RoundSchedule.NumeralAt(3, beat, beat));
            Assert.AreEqual(1, RoundSchedule.NumeralAt(3, beat, 2 * beat + 1));
            Assert.AreEqual(0, RoundSchedule.NumeralAt(3, beat, 3 * beat));
        }

        [Test]
        public void BotClampAndAdaptation()
        {
            var cfg = DuelConfig.Default();
            var rng = new Random(99);
            double sumEarly = 0, sumLate = 0;
            const int n = 20000;
            for (int i = 0; i < n; i++)
            {
                double v = BotModel.SampleReactionMs(cfg, rng, 0);
                Assert.GreaterOrEqual(v, cfg.botMinMs);
                Assert.LessOrEqual(v, cfg.botMaxMs);
                sumEarly += v;
                double w = BotModel.SampleReactionMs(cfg, rng, 99);
                Assert.GreaterOrEqual(w, cfg.botMinMs);
                Assert.LessOrEqual(w, cfg.botMaxMs);
                sumLate += w;
            }
            Assert.AreEqual(cfg.botMeanStartMs, sumEarly / n, 3.0);
            Assert.AreEqual(cfg.botMeanEndMs, sumLate / n, 3.0);
            // extreme distribution still clamps
            for (int i = 0; i < 2000; i++)
            {
                double v = BotModel.SampleReactionMs(rng, 300, 5000, 170, 900);
                Assert.That(v >= 170 && v <= 900);
            }
        }

        [Test]
        public void ClockSyncWithAsymmetricDelay()
        {
            // server = client + 5 s; up = 30 ms, down = 10 ms (+ jitter on a few samples)
            long trueOffset = 5000000000L;
            long up = 30000000L, down = 10000000L, proc = 1000000L;
            var cs = new ClockSync(16);
            var rng = new Random(3);
            long t = 0;
            for (int i = 0; i < 16; i++)
            {
                long jitter = (i % 4 == 0) ? (long)(rng.NextDouble() * 80000000L) : 0;
                long t0 = t;
                long t1 = t0 + up + jitter + trueOffset;
                long t2 = t1 + proc;
                long t3 = (t2 - trueOffset) + down;
                cs.AddSample(t0, t1, t2, t3);
                t += 100000000L;
            }
            long off, rtt;
            Assert.IsTrue(cs.TryEstimate(out off, out rtt));
            Assert.AreEqual(up + down, rtt);
            // asymmetry bias = (up - down) / 2 = 10 ms; jittered samples are filtered out
            Assert.AreEqual(trueOffset + (up - down) / 2, off);
            Assert.LessOrEqual(Math.Abs(off - trueOffset), (up - down) / 2);
            Assert.IsFalse(new ClockSync().TryEstimate(out off, out rtt));
        }
    }

    public class AuthorityTests
    {
        private DuelConfig cfg;
        private ManualClock clock;
        private LoopbackTransport net;
        private LocalAuthority auth;
        private DuelClient p0;
        private DuelClient p1;

        [SetUp]
        public void SetUp()
        {
            cfg = DuelConfig.Default();
            clock = new ManualClock();
            clock.Now = 10000000000L;
            net = new LoopbackTransport();
            auth = new LocalAuthority(cfg, clock, net, 42);
            p0 = new DuelClient(0, net, cfg);
            p1 = new DuelClient(1, net, cfg);
            auth.StartMatch();
        }

        private void Frame()
        {
            auth.Tick();
            RoundStartMsg m;
            p0.PollRoundStart(clock.Now, out m);
            p1.PollRoundStart(clock.Now, out m);
            p0.PollCue(clock.Now, 0);
            p1.PollCue(clock.Now, 0);
            p0.Tick(clock.Now);
            p1.Tick(clock.Now);
        }

        private void StartAndReachCue()
        {
            Assert.IsTrue(auth.StartCountdown());
            Frame();
            auth.ForceCueNow();
            clock.AdvanceMs(1);
            Frame();
            Assert.AreEqual(RoundPhase.Draw, auth.Phase);
            Assert.IsTrue(p0.HasCue);
            Assert.IsTrue(p1.HasCue);
        }

        private void Run(double ms)
        {
            for (double t = 0; t < ms; t += 1) { clock.AdvanceMs(1); Frame(); }
        }

        private int Count(DuelEventKind kind, int slot)
        {
            int n = 0;
            DuelEvent e;
            while (auth.TryDequeueEvent(out e))
                if (e.kind == kind && (slot < -1 || e.slot == slot)) n++;
            return n;
        }

        [Test]
        public void CueIsNotRevealedBeforeHold()
        {
            Assert.IsTrue(auth.StartCountdown());
            Frame();
            Assert.IsFalse(p0.HasCue);
            Run(2000); // still inside countdown (2.1 s) + min hold
            Assert.IsFalse(p0.HasCue);
            Run(4200); // countdown 2.1 s + hold <= 4.0 s
            Assert.IsTrue(p0.HasCue);
        }

        [Test]
        public void FasterShotLandsFirstAndReactionsAreClientMeasured()
        {
            StartAndReachCue();
            Run(200);
            Assert.AreEqual(PressResult.Shot, p0.Press(clock.Now, InputSource.Touch, false));
            Run(90);
            Assert.AreEqual(PressResult.Shot, p1.Press(clock.Now, InputSource.Bot, false));
            Run(100);
            Assert.AreEqual(RoundPhase.Result, auth.Phase);
            Assert.AreEqual(SlotOutcome.Shot, auth.Round.slots[0].outcome);
            Assert.AreEqual(SlotOutcome.Shot, auth.Round.slots[1].outcome);
            Assert.Less(auth.Round.slots[0].reactionNs, auth.Round.slots[1].reactionNs);
            Assert.AreEqual(0, auth.Round.winnerSlot);
            Assert.AreEqual(cfg.maxHp - auth.Round.slots[0].damageDealt, auth.Match.Hp[1], 1e-9);

            DuelEvent e;
            Assert.IsTrue(auth.TryDequeueEvent(out e)); // RoundStarted
            Assert.IsTrue(auth.TryDequeueEvent(out e)); // Cue
            Assert.IsTrue(auth.TryDequeueEvent(out e));
            Assert.AreEqual(DuelEventKind.Shot, e.kind);
            Assert.AreEqual(0, e.slot);
        }

        [Test]
        public void KillStopsSecondShot()
        {
            auth.Match.Hp[1] = 10;
            StartAndReachCue();
            Run(180);
            p0.Press(clock.Now, InputSource.Touch, false);
            Run(60); // past reaction + tie + grace: committed before the bot reports
            Assert.AreEqual(SlotOutcome.Shot, auth.Round.slots[0].outcome);
            Assert.AreEqual(SlotOutcome.Stopped, auth.Round.slots[1].outcome);
            Assert.IsTrue(auth.Match.Over);
            Assert.AreEqual(0, auth.Match.Winner);
            Assert.AreEqual(cfg.maxHp, auth.Match.Hp[0], 1e-9);
        }

        [Test]
        public void KillStopsSecondShotWhenBothReportedInSameFrame()
        {
            auth.Match.Hp[0] = 5;
            StartAndReachCue();
            Run(300);
            long now = clock.Now;
            p0.Press(now - DuelConfig.MsToNs(10), InputSource.Touch, false); // 290 ms
            p1.Press(now - DuelConfig.MsToNs(50), InputSource.Bot, false);   // 250 ms (faster)
            Frame();
            Assert.AreEqual(SlotOutcome.Shot, auth.Round.slots[1].outcome);
            Assert.AreEqual(SlotOutcome.Stopped, auth.Round.slots[0].outcome);
            Assert.AreEqual(1, auth.Match.Winner);
        }

        [Test]
        public void TieWindowDefaultIs15Ms()
        {
            Assert.AreEqual(15.0, DuelConfig.Default().tieWindowMs, 1e-12);
            Assert.AreEqual(15.0, DuelConfig.FromJson("{}").tieWindowMs, 1e-12);
            Assert.AreEqual(4.0, DuelConfig.FromJson("{\"tieWindowMs\": 4}").tieWindowMs, 1e-12);
        }

        [Test]
        public void TieWindowFiresBoth()
        {
            auth.Match.Hp[0] = 5;
            auth.Match.Hp[1] = 5;
            StartAndReachCue();
            Run(250);
            long now = clock.Now;
            p0.Press(now, InputSource.Touch, false);
            p1.Press(now + DuelConfig.MsToNs(0.5), InputSource.Bot, false);
            clock.AdvanceMs(1);
            Frame();
            Assert.AreEqual(SlotOutcome.Shot, auth.Round.slots[0].outcome);
            Assert.AreEqual(SlotOutcome.Shot, auth.Round.slots[1].outcome);
            Assert.IsTrue(auth.Round.isTie);
            Assert.IsTrue(auth.Match.Over);
            Assert.AreEqual(-1, auth.Match.Winner);
            Assert.IsTrue(auth.Match.IsDraw);
        }

        /// <summary>Reports land in the same frame, slot 1 reporting first; gap given in µs (slot 0 slower by gap).</summary>
        private void ShootPairUs(long reaction0Us, long reaction1Us)
        {
            StartAndReachCue();
            Run(400);
            long cue = auth.Round.cueDecidedNs;
            var r1 = new ShotReport { roundId = auth.Round.roundId, slot = 1, kind = ReportKind.Shot, cuePresentedNs = cue, reactionNs = reaction1Us * 1000, inputSource = InputSource.Remote };
            r1.inputNs = cue + r1.reactionNs;
            var r0 = new ShotReport { roundId = auth.Round.roundId, slot = 0, kind = ReportKind.Shot, cuePresentedNs = cue, reactionNs = reaction0Us * 1000, inputSource = InputSource.Remote };
            r0.inputNs = cue + r0.reactionNs;
            net.SendReport(r1);
            net.SendReport(r0);
            Frame();
        }

        [Test]
        public void TieWindowIsInclusiveAndBothShotsLand()
        {
            ShootPairUs(265000, 250000); // exactly 15.000 ms apart
            Assert.AreEqual(RoundPhase.Result, auth.Phase);
            Assert.IsTrue(auth.Round.isTie);
            Assert.AreEqual(-1, auth.Round.winnerSlot);
            Assert.AreEqual(0, auth.Match.RoundWins[0]);
            Assert.AreEqual(0, auth.Match.RoundWins[1]);
            double d0 = DamageCurve.Evaluate(cfg, 265.0), d1 = DamageCurve.Evaluate(cfg, 250.0);
            Assert.AreEqual(d0, auth.Round.slots[0].damageDealt, 1e-9);
            Assert.AreEqual(d1, auth.Round.slots[1].damageDealt, 1e-9);
            Assert.AreEqual(cfg.maxHp - d0, auth.Match.Hp[1], 1e-9);
            Assert.AreEqual(cfg.maxHp - d1, auth.Match.Hp[0], 1e-9);
            Assert.IsFalse(auth.Match.Over);

            int shots = 0;
            bool resolvedTie = false;
            DuelEvent e;
            while (auth.TryDequeueEvent(out e))
            {
                if (e.kind == DuelEventKind.Shot) { shots++; Assert.IsTrue(e.simultaneous); }
                if (e.kind == DuelEventKind.RoundResolved) { resolvedTie = e.simultaneous; Assert.AreEqual(-1, e.slot); }
            }
            Assert.AreEqual(2, shots);
            Assert.IsTrue(resolvedTie);
        }

        [Test]
        public void GapJustOverTieWindowIsOrdered()
        {
            auth.Match.Hp[0] = 5;
            ShootPairUs(265001, 250000); // 15.001 ms: Blackpaw's faster shot kills, the slower one never fires
            Assert.IsFalse(auth.Round.isTie);
            Assert.AreEqual(SlotOutcome.Shot, auth.Round.slots[1].outcome);
            Assert.AreEqual(SlotOutcome.Stopped, auth.Round.slots[0].outcome);
            Assert.AreEqual(1, auth.Round.winnerSlot);
            Assert.AreEqual(1, auth.Match.Winner);
            Assert.IsFalse(auth.Match.IsDraw);
        }

        [Test]
        public void TieDoubleKoIsADraw()
        {
            auth.Match.Hp[0] = 8;
            auth.Match.Hp[1] = 8;
            ShootPairUs(240000, 231000); // 9 ms apart: both lethal
            Assert.IsTrue(auth.Round.isTie);
            Assert.AreEqual(0, auth.Match.Hp[0], 1e-12);
            Assert.AreEqual(0, auth.Match.Hp[1], 1e-12);
            Assert.IsTrue(auth.Match.Over);
            Assert.AreEqual(-1, auth.Match.Winner);
            Assert.IsTrue(auth.Match.IsDraw);
            Assert.IsFalse(auth.NextRound());

            int kills = 0, overSlot = 99;
            DuelEvent e;
            while (auth.TryDequeueEvent(out e))
            {
                if (e.kind == DuelEventKind.Shot && e.killed) kills++;
                if (e.kind == DuelEventKind.MatchOver) overSlot = e.slot;
            }
            Assert.AreEqual(2, kills);
            Assert.AreEqual(-1, overSlot);
        }

        [Test]
        public void TieWithOneSurvivorIsNotADraw()
        {
            auth.Match.Hp[0] = 5;
            ShootPairUs(250000, 244000);
            Assert.IsTrue(auth.Round.isTie);
            Assert.AreEqual(SlotOutcome.Shot, auth.Round.slots[0].outcome); // the dying duelist's shot still lands
            Assert.Less(auth.Match.Hp[1], cfg.maxHp);
            Assert.IsTrue(auth.Match.Over);
            Assert.AreEqual(1, auth.Match.Winner);
            Assert.IsFalse(auth.Match.IsDraw);
        }

        [Test]
        public void PvpLateFasterReportStillOrdersFirst()
        {
            // the slower report arrives first; the faster one arrives later but inside reaction + tie + grace
            StartAndReachCue();
            Run(262);
            Assert.AreEqual(ReportVerdict.Accepted, auth.Submit(ValidShot(1, 260), clock.Now));
            Run(10);  // cue + ~273 ms < 260 + 15 + 25: slot 1 not committed yet
            Assert.AreEqual(SlotOutcome.Pending, auth.Round.slots[1].outcome);
            Assert.AreEqual(ReportVerdict.Accepted, auth.Submit(ValidShot(0, 230), clock.Now));
            Frame();
            Assert.IsFalse(auth.Round.isTie);
            Assert.AreEqual(0, auth.Round.winnerSlot);

            DuelEvent e;
            int firstShot = -1;
            while (auth.TryDequeueEvent(out e))
                if (e.kind == DuelEventKind.Shot) { if (firstShot < 0) firstShot = e.slot; Assert.IsFalse(e.simultaneous); }
            Assert.AreEqual(0, firstShot);
        }

        [Test]
        public void PvpReportAfterCommitWindowFiresSecond()
        {
            // past reaction + tie window + grace the earlier report is committed; a later faster claim fires after it
            StartAndReachCue();
            Run(262);
            auth.Submit(ValidShot(1, 260), clock.Now);
            Run(60);  // cue + ~323 ms > 260 + 15 + 25
            Assert.AreEqual(SlotOutcome.Shot, auth.Round.slots[1].outcome);
            auth.Submit(ValidShot(0, 250), clock.Now);
            Frame();
            Assert.AreEqual(SlotOutcome.Shot, auth.Round.slots[0].outcome);
            Assert.IsFalse(auth.Round.isTie);
            DuelEvent e;
            int firstShot = -1;
            while (auth.TryDequeueEvent(out e))
                if (e.kind == DuelEventKind.Shot && firstShot < 0) firstShot = e.slot;
            Assert.AreEqual(1, firstShot);
        }

        [Test]
        public void FoulDealsZeroButOpponentStillShoots()
        {
            Assert.IsTrue(auth.StartCountdown());
            Frame();
            Run(300);
            Assert.AreEqual(PressResult.Early, p0.Press(clock.Now, InputSource.Touch, false));
            Frame();
            Assert.AreEqual(SlotOutcome.Foul, auth.Round.slots[0].outcome);
            auth.ForceCueNow();
            Frame();
            Run(250);
            p1.Press(clock.Now, InputSource.Bot, false);
            Run(5);
            Assert.AreEqual(RoundPhase.Result, auth.Phase);
            Assert.AreEqual(0, auth.Round.slots[0].damageDealt, 1e-12);
            Assert.Less(auth.Match.Hp[0], cfg.maxHp);
            Assert.AreEqual(cfg.maxHp, auth.Match.Hp[1], 1e-12);
        }

        [Test]
        public void BothFoulVoidsRound()
        {
            Assert.IsTrue(auth.StartCountdown());
            Frame();
            int round = auth.Match.RoundNumber;
            Run(100);
            p0.Press(clock.Now, InputSource.Touch, false);
            Run(100);
            p1.Press(clock.Now, InputSource.Bot, false);
            Frame();
            Assert.AreEqual(RoundPhase.Standoff, auth.Phase);
            Assert.IsTrue(auth.Round.isVoid);
            Assert.AreEqual(round, auth.Match.RoundNumber);
            Assert.AreEqual(cfg.maxHp, auth.Match.Hp[0], 1e-12);
            Assert.AreEqual(cfg.maxHp, auth.Match.Hp[1], 1e-12);
            Assert.AreEqual(1, Count(DuelEventKind.RoundVoid, -2));
        }

        [Test]
        public void AnticipationIsAFoul()
        {
            StartAndReachCue();
            Run(60);
            Assert.AreEqual(PressResult.Shot, p0.Press(clock.Now, InputSource.Touch, false));
            Frame();
            Assert.AreEqual(SlotOutcome.Anticipation, auth.Round.slots[0].outcome);
            Run(200);
            p1.Press(clock.Now, InputSource.Bot, false);
            Run(5);
            Assert.AreEqual(0, auth.Round.slots[0].damageDealt, 1e-12);
        }

        [Test]
        public void NoShotTimeout()
        {
            StartAndReachCue();
            Run(300);
            p1.Press(clock.Now, InputSource.Bot, false);
            Run(1200);
            Assert.AreNotEqual(RoundPhase.Result, auth.Phase);
            Run(100);
            Assert.AreEqual(PressResult.TooLate, p0.Press(clock.Now, InputSource.Touch, false));
            Run(100);
            Assert.AreEqual(RoundPhase.Result, auth.Phase);
            Assert.AreEqual(SlotOutcome.NoShot, auth.Round.slots[0].outcome);
            Assert.AreEqual(0, auth.Round.slots[0].damageDealt, 1e-12);
        }

        private ShotReport ValidShot(int slot, long reactionMs)
        {
            var r = new ShotReport();
            r.roundId = auth.Round.roundId;
            r.slot = slot;
            r.kind = ReportKind.Shot;
            r.cuePresentedNs = auth.Round.cueDecidedNs;
            r.reactionNs = DuelConfig.MsToNs(reactionMs);
            r.inputNs = r.cuePresentedNs + r.reactionNs;
            r.inputSource = InputSource.Remote;
            return r;
        }

        [Test]
        public void ReportValidation()
        {
            StartAndReachCue();
            Run(400);
            long now = clock.Now;

            var wrong = ValidShot(0, 250); wrong.roundId += 5;
            Assert.AreEqual(ReportVerdict.WrongRound, auth.Submit(wrong, now));

            var stale = ValidShot(0, 250); stale.roundId -= 1;
            Assert.AreEqual(ReportVerdict.Stale, auth.Submit(stale, now));

            var badSlot = ValidShot(0, 250); badSlot.slot = 7;
            Assert.AreEqual(ReportVerdict.BadSlot, auth.Submit(badSlot, now));

            var future = ValidShot(0, 1000); // claims 1000 ms but only ~400 ms passed since the cue
            Assert.AreEqual(ReportVerdict.Implausible, auth.Submit(future, now));

            var tooLong = ValidShot(0, 5000);
            Assert.AreEqual(ReportVerdict.Implausible, auth.Submit(tooLong, now));

            var negative = ValidShot(0, 250); negative.reactionNs = -5; negative.inputNs = negative.cuePresentedNs - 5;
            Assert.AreEqual(ReportVerdict.Implausible, auth.Submit(negative, now));

            var inconsistent = ValidShot(0, 250); inconsistent.inputNs += DuelConfig.MsToNs(30);
            Assert.AreEqual(ReportVerdict.Inconsistent, auth.Submit(inconsistent, now));

            var ok = ValidShot(0, 250);
            Assert.AreEqual(ReportVerdict.Accepted, auth.Submit(ok, now));
            Assert.AreEqual(ReportVerdict.Duplicate, auth.Submit(ok, now));

            var earlyAfterCue = ValidShot(1, 250); earlyAfterCue.kind = ReportKind.Early;
            Assert.AreEqual(ReportVerdict.Inconsistent, auth.Submit(earlyAfterCue, now));
        }

        [Test]
        public void StaleReportAfterResultIsRejected()
        {
            StartAndReachCue();
            Run(250);
            p0.Press(clock.Now, InputSource.Touch, false);
            p1.Press(clock.Now - DuelConfig.MsToNs(20), InputSource.Bot, false);
            Run(5);
            Assert.AreEqual(RoundPhase.Result, auth.Phase);
            Assert.AreEqual(ReportVerdict.Stale, auth.Submit(ValidShot(0, 240), clock.Now));
        }

        [Test]
        public void ShotBeforeCueIsImplausible()
        {
            Assert.IsTrue(auth.StartCountdown());
            Frame();
            var r = new ShotReport();
            r.roundId = auth.Round.roundId;
            r.slot = 0;
            r.kind = ReportKind.Shot;
            r.reactionNs = DuelConfig.MsToNs(200);
            r.cuePresentedNs = clock.Now;
            r.inputNs = r.cuePresentedNs + r.reactionNs;
            Assert.AreEqual(ReportVerdict.Implausible, auth.Submit(r, clock.Now));
        }

        [Test]
        public void BotGoesThroughTransportAndNeverFiresBeforeCue()
        {
            var botNet = new LoopbackTransport();
            var a = new LocalAuthority(cfg, clock, botNet, 5);
            var bot = new BotClient(1, botNet, cfg, 77);
            var me = new DuelClient(0, botNet, cfg);
            a.StartMatch();
            for (int round = 0; round < 40 && !a.Match.Over; round++)
            {
                if (a.Phase == RoundPhase.Result) a.NextRound();
                Assert.IsTrue(a.StartCountdown());
                for (int f = 0; f < 8000 && a.Phase != RoundPhase.Result && a.Phase != RoundPhase.Standoff; f++)
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
                var s1 = a.Round.slots[1];
                if (s1.outcome == SlotOutcome.Shot)
                {
                    Assert.GreaterOrEqual(s1.reactionNs, DuelConfig.MsToNs(cfg.botMinMs) - 1);
                    Assert.LessOrEqual(s1.reactionNs, DuelConfig.MsToNs(cfg.botMaxMs) + 1);
                }
            }
            Assert.IsTrue(a.Match.Over, "bot alone must eventually win against a no-show player");
            Assert.AreEqual(1, a.Match.Winner);
        }
    }
}
