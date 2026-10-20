using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>LAST LIGHT (SPEC_V2 §8.4): trigger condition and the authority's per-round damage multiplier.</summary>
    public class SuddenDeathTests
    {
        private const long T0 = 10000000000L;

        private sealed class Rig
        {
            public readonly ManualClock Clock = new ManualClock { Now = T0 };
            public readonly LoopbackTransport Net = new LoopbackTransport();
            public readonly LocalAuthority A;

            public Rig(DuelConfig cfg, int seed = 1)
            {
                A = new LocalAuthority(cfg, Clock, Net, seed);
                A.StartMatch();
            }

            /// <summary>Countdown, cue, then both reports 2 s later (ms &lt; 0 = no shot). Drains events.</summary>
            public void Exchange(double ms0, double ms1)
            {
                Assert.IsTrue(A.StartCountdown(), "StartCountdown");
                Clock.Now += A.Round.schedule.CueOffsetNs;
                A.Tick();
                Assert.AreEqual(RoundPhase.Draw, A.Phase);
                long cue = A.Round.cueDecidedNs;
                Clock.Now = cue + 2000000000L;
                Send(0, ms0, cue);
                Send(1, ms1, cue);
                A.Tick();
                Assert.AreEqual(RoundPhase.Result, A.Phase);
            }

            private void Send(int slot, double ms, long cue)
            {
                if (ms < 0) return;
                long r = DuelConfig.MsToNs(ms);
                Net.SendReport(new ShotReport
                {
                    roundId = A.Round.roundId, slot = slot, kind = ReportKind.Shot,
                    reactionNs = r, cuePresentedNs = cue, inputNs = cue + r, inputSource = InputSource.Injected
                });
            }

            public double ShotEventDamage(int slot)
            {
                DuelEvent e;
                double d = double.NaN;
                while (A.TryDequeueEvent(out e))
                    if (e.kind == DuelEventKind.Shot && e.slot == slot) d = e.damage;
                return d;
            }

            public void Next()
            {
                Clock.Now += 1000000000L;
                Assert.IsTrue(A.NextRound());
            }
        }

        // ------------------------------------------------------------------ trigger condition

        [Test]
        public void TriggersOnlyWhenBothAliveAndAtOrBelowThreshold()
        {
            var cfg = DuelConfig.Default();
            Assert.AreEqual(20, cfg.suddenDeathHp, 1e-12);
            Assert.IsTrue(cfg.suddenDeathEnabled);
            Assert.IsTrue(SuddenDeath.Triggers(cfg, 20, 20), "threshold is inclusive");
            Assert.IsTrue(SuddenDeath.Triggers(cfg, 0.5, 19));
            Assert.IsFalse(SuddenDeath.Triggers(cfg, 20.000001, 20));
            Assert.IsFalse(SuddenDeath.Triggers(cfg, 20, 21));
            Assert.IsFalse(SuddenDeath.Triggers(cfg, 100, 5), "only one cat low");
            Assert.IsFalse(SuddenDeath.Triggers(cfg, 0, 10), "a dead cat never triggers it");
            cfg.suddenDeathEnabled = false;
            Assert.IsFalse(SuddenDeath.Triggers(cfg, 10, 10), "master switch");
            Assert.IsFalse(SuddenDeath.Triggers(null, 10, 10));
        }

        [Test]
        public void MultiplierForReadsTheMatch()
        {
            var cfg = DuelConfig.Default();
            var m = new MatchState(100);
            Assert.AreEqual(1.0, SuddenDeath.MultiplierFor(cfg, m));
            m.Hp[0] = 20; m.Hp[1] = 12;
            Assert.AreEqual(2.0, SuddenDeath.MultiplierFor(cfg, m));
            m.Finish(0);
            Assert.AreEqual(1.0, SuddenDeath.MultiplierFor(cfg, m), "a finished match never doubles");
        }

        [Test]
        public void ConfigKeysParseAndSanitize()
        {
            var c = DuelConfig.FromJson("{\"suddenDeathEnabled\":false,\"suddenDeathHp\":25,\"suddenDeathDamageMultiplier\":3,\"suddenDeathFlatLineSeconds\":0.8}");
            Assert.IsFalse(c.suddenDeathEnabled);
            Assert.AreEqual(25, c.suddenDeathHp, 1e-12);
            Assert.AreEqual(3, c.suddenDeathDamageMultiplier, 1e-12);
            Assert.AreEqual(0.8, c.suddenDeathFlatLineSeconds, 1e-12);
            var bad = DuelConfig.FromJson("{\"suddenDeathDamageMultiplier\":-2,\"suddenDeathHp\":-5}");
            Assert.AreEqual(1.0, bad.suddenDeathDamageMultiplier, 1e-12);
            Assert.AreEqual(0, bad.suddenDeathHp, 1e-12);
            var def = DuelConfig.FromJson("{}");
            Assert.IsTrue(def.suddenDeathEnabled);
            Assert.AreEqual(2.0, def.suddenDeathDamageMultiplier, 1e-12);
        }

        // ------------------------------------------------------------------ authority multiplier

        [Test]
        public void DefaultMultiplierIsOneAndChangesNothing()
        {
            var rig = new Rig(DuelConfig.Default());
            rig.Exchange(150, -1);
            Assert.AreEqual(1.0, rig.A.Round.damageMultiplier);
            Assert.AreEqual(40, rig.A.Round.slots[0].damageDealt, 1e-12);
            Assert.AreEqual(60, rig.A.Match.Hp[1], 1e-12);
        }

        [Test]
        public void MultiplierAppliedExactlyOncePerShot()
        {
            var cfg = DuelConfig.Default();
            var rig = new Rig(cfg);
            rig.A.SetDamageMultiplier(2.0);
            rig.Exchange(150, 300);   // 40 and 24 on the curve
            var r = rig.A.Round;
            Assert.AreEqual(2.0, r.damageMultiplier);
            Assert.AreEqual(80, r.slots[0].damageDealt, 1e-12, "x2, not x4");
            Assert.AreEqual(80, rig.ShotEventDamage(0), 1e-12, "event carries the same (single) multiply");
            Assert.AreEqual(20, rig.A.Match.Hp[1], 1e-12, "HP drops by the doubled damage once");
            Assert.AreEqual(48, r.slots[1].damageDealt, 1e-12);
            Assert.AreEqual(52, rig.A.Match.Hp[0], 1e-12);
            Assert.AreEqual(0, r.winnerSlot);
            // reaction measurement and grades are untouched
            Assert.AreEqual(DuelConfig.MsToNs(150), r.slots[0].reactionNs);
            Assert.AreEqual(Grade.Lightning, r.slots[0].grade);
        }

        [Test]
        public void TieShotsAreBothMultipliedOnce()
        {
            var rig = new Rig(DuelConfig.Default());
            rig.A.Match.Hp[0] = 100; rig.A.Match.Hp[1] = 100;
            rig.A.SetDamageMultiplier(2.0);
            rig.Exchange(250, 260);   // 30 and 28.8, inside the tie window
            var r = rig.A.Round;
            Assert.IsTrue(r.isTie);
            Assert.AreEqual(60, r.slots[0].damageDealt, 1e-9);
            Assert.AreEqual(57.6, r.slots[1].damageDealt, 1e-9);
            Assert.AreEqual(40, rig.A.Match.Hp[1], 1e-9);
            Assert.AreEqual(42.4, rig.A.Match.Hp[0], 1e-9);
        }

        [Test]
        public void MultiplierIsLatchedAtCountdownNotMidRound()
        {
            var rig = new Rig(DuelConfig.Default());
            Assert.IsTrue(rig.A.StartCountdown());
            rig.A.SetDamageMultiplier(2.0);       // too late for this round
            Assert.AreEqual(1.0, rig.A.Round.damageMultiplier);
            rig.Clock.Now += rig.A.Round.schedule.CueOffsetNs;
            rig.A.Tick();
            long cue = rig.A.Round.cueDecidedNs;
            rig.Clock.Now = cue + 2000000000L;
            long ns = DuelConfig.MsToNs(150);
            rig.Net.SendReport(new ShotReport { roundId = rig.A.Round.roundId, slot = 0, kind = ReportKind.Shot, reactionNs = ns, cuePresentedNs = cue, inputNs = cue + ns });
            rig.A.Tick();
            Assert.AreEqual(40, rig.A.Round.slots[0].damageDealt, 1e-12);
            rig.Next();
            rig.Exchange(150, -1);                 // the pending x2 applies from the next countdown
            Assert.AreEqual(80, rig.A.Round.slots[0].damageDealt, 1e-12);
        }

        [Test]
        public void ResetsToOne()
        {
            var rig = new Rig(DuelConfig.Default());
            rig.A.SetDamageMultiplier(2.0);
            rig.Exchange(400, -1);                  // 16 x2
            Assert.AreEqual(32, rig.A.Round.slots[0].damageDealt, 1e-12);
            rig.Next();
            rig.A.SetDamageMultiplier(1.0);         // the driver's "not sudden death" standoff
            rig.Exchange(400, -1);
            Assert.AreEqual(16, rig.A.Round.slots[0].damageDealt, 1e-12);

            rig.A.SetDamageMultiplier(2.0);
            rig.A.StartMatch();                     // a new match always starts at x1
            Assert.AreEqual(1.0, rig.A.PendingDamageMultiplier);
            rig.Exchange(400, -1);
            Assert.AreEqual(16, rig.A.Round.slots[0].damageDealt, 1e-12);

            rig.A.SetDamageMultiplier(double.NaN);
            Assert.AreEqual(1.0, rig.A.PendingDamageMultiplier);
            rig.A.SetDamageMultiplier(-3);
            Assert.AreEqual(1.0, rig.A.PendingDamageMultiplier);
        }

        [Test]
        public void DriverLoopDoublesOnlyTheLastLightRound()
        {
            // Both cats trade 40-damage shots (110 vs 140 ms, outside the tie window) for two rounds:
            // 100 -> 60 -> 20 each, so round 3 is LAST LIGHT and a 400 ms shot (16) kills with x2 (32).
            var cfg = DuelConfig.Default();
            var rig = new Rig(cfg, 42);
            double[] p0 = { 110, 110, 400 };
            double[] p1 = { 140, 140, -1 };
            double[] expectMult = { 1, 1, 2 };
            for (int k = 0; k < 3; k++)
            {
                rig.A.SetDamageMultiplier(SuddenDeath.MultiplierFor(cfg, rig.A.Match));
                rig.Exchange(p0[k], p1[k]);
                Assert.AreEqual(expectMult[k], rig.A.Round.damageMultiplier, "round " + (k + 1));
                if (k < 2) rig.Next();
            }
            Assert.AreEqual(32, rig.A.Round.slots[0].damageDealt, 1e-12);
            Assert.IsTrue(rig.A.Match.Over);
            Assert.AreEqual(0, rig.A.Match.Winner);
            Assert.AreEqual(20, rig.A.Match.Hp[0], 1e-12);
        }
    }
}
