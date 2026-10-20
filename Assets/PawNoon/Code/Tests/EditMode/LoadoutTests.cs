using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>
    /// Guns and loadouts (SPEC_V2 §6, M7): the Peacemaker curve from duel_config.json, per-slot guns in the
    /// authority (latched at StartCountdown like the LAST LIGHT multiplier), ranked normalisation, and the
    /// guarantee that a gun never touches reactions, grades or report handling.
    /// </summary>
    public class LoadoutTests
    {
        private const long T0 = 10000000000L;

        private sealed class Rig
        {
            public readonly ManualClock Clock = new ManualClock { Now = T0 };
            public readonly LoopbackTransport Net = new LoopbackTransport();
            public readonly LocalAuthority A;

            public Rig(DuelConfig cfg, string gun0, string gun1, int seed = 1)
            {
                A = new LocalAuthority(cfg, Clock, Net, seed);
                A.SetGun(0, gun0);
                A.SetGun(1, gun1);
                A.StartMatch();
            }

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

            public void Next()
            {
                Clock.Now += 1000000000L;
                Assert.IsTrue(A.NextRound());
            }
        }

        // ------------------------------------------------------------------ curve

        [Test]
        public void PeacemakerCurveComesFromJson()
        {
            var cfg = DuelConfig.FromJson("{\"damageValuePeacemaker\":[50,44,36,28,16,8,4,2]}");
            Assert.AreEqual(new double[] { 50, 44, 36, 28, 16, 8, 4, 2 }, cfg.damageValuePeacemaker);
            Assert.AreEqual(50, DamageCurve.Evaluate(cfg, Guns.Peacemaker, 150), 1e-12);
            Assert.AreEqual(40, DamageCurve.Evaluate(cfg, Guns.Standard, 150), 1e-12, "standard curve untouched");
        }

        [Test]
        public void ShippedConfigHasTheSpecCurve()
        {
            // the default and duel_config.json carry SPEC_V2 §6: [48,42,34,26,16,8,4,2] at the standard anchors
            var cfg = DuelConfig.Default();
            Assert.AreEqual(new double[] { 48, 42, 34, 26, 16, 8, 4, 2 }, cfg.damageValuePeacemaker);
            double[] ms = { 150, 200, 250, 300, 400, 600, 1000, 1500 };
            double[] pm = { 48, 42, 34, 26, 16, 8, 4, 2 };
            for (int i = 0; i < ms.Length; i++)
                Assert.AreEqual(pm[i], DamageCurve.Evaluate(cfg, Guns.Peacemaker, ms[i]), 1e-12, "anchor " + ms[i]);
            Assert.AreEqual(45, DamageCurve.Evaluate(cfg, Guns.Peacemaker, 175), 1e-12, "linear between anchors");
            Assert.AreEqual(48, DamageCurve.Evaluate(cfg, Guns.Peacemaker, 90), 1e-12, "below the first anchor");
            Assert.AreEqual(0, DamageCurve.Evaluate(cfg, Guns.Peacemaker, 1500.001), 1e-12, "past the last anchor");
            // top three bands up, slow bands unchanged
            Assert.Greater(DamageCurve.Evaluate(cfg, Guns.Peacemaker, 240), DamageCurve.Evaluate(cfg, 240));
            for (double x = 400; x <= 1600; x += 50)
                Assert.AreEqual(DamageCurve.Evaluate(cfg, x), DamageCurve.Evaluate(cfg, Guns.Peacemaker, x), 1e-12, "slow band at " + x);
        }

        [Test]
        public void MalformedPeacemakerCurveIsIgnored()
        {
            var shortArr = DuelConfig.FromJson("{\"damageValuePeacemaker\":[99,1]}");
            Assert.AreEqual(48, shortArr.damageValuePeacemaker[0], "wrong length: keep the default");
            // custom anchors of another length without a Peacemaker curve: fall back to the standard values
            var custom = DuelConfig.FromJson("{\"damageMs\":[100,200,300],\"damageValue\":[30,20,10]}");
            Assert.AreEqual(new double[] { 30, 20, 10 }, custom.damageValuePeacemaker);
            Assert.AreEqual(30, DamageCurve.Evaluate(custom, Guns.Peacemaker, 100), 1e-12);
        }

        [Test]
        public void CopyFromCarriesThePeacemakerCurve()
        {
            var a = DuelConfig.Default();
            var b = DuelConfig.FromJson("{\"damageValuePeacemaker\":[50,44,36,28,16,8,4,2]}");
            a.CopyFrom(b);
            Assert.AreEqual(50, a.damageValuePeacemaker[0]);
            Assert.AreNotSame(a.damageValuePeacemaker, b.damageValuePeacemaker, "arrays are copied, not shared");
        }

        [Test]
        public void GunIdsNormalise()
        {
            Assert.AreEqual(Guns.Standard, Guns.Normalize(null));
            Assert.AreEqual(Guns.Standard, Guns.Normalize(""));
            Assert.AreEqual(Guns.Standard, Guns.Normalize("golden_gun"));
            Assert.AreEqual(Guns.Peacemaker, Guns.Normalize(" Peacemaker "));
            Assert.AreEqual(Guns.Standard, Guns.Effective(Guns.Peacemaker, true), "ranked: standard curve for everyone");
            Assert.AreEqual(Guns.Peacemaker, Guns.Effective(Guns.Peacemaker, false));
            Assert.AreEqual(Guns.PeacemakerProductId, Guns.ProductFor(Guns.Peacemaker));
            Assert.IsNull(Guns.ProductFor(Guns.Standard));
            Assert.AreEqual(Guns.Peacemaker, Guns.GunForProduct("com.pawnoon.pixel.peacemaker"));
            Assert.IsNull(Guns.GunForProduct("com.pawnoon.pixel.coins_small"));
        }

        // ------------------------------------------------------------------ authority

        [Test]
        public void SoloPeacemakerHitsHarder()
        {
            var rig = new Rig(DuelConfig.Default(), Guns.Peacemaker, Guns.Standard);
            rig.Exchange(150, -1);
            Assert.AreEqual(48, rig.A.Round.slots[0].damageDealt, 1e-12);
            Assert.AreEqual(52, rig.A.Match.Hp[1], 1e-12);
            Assert.AreEqual(Guns.Peacemaker, rig.A.Round.guns[0]);
            Assert.AreEqual(Guns.Standard, rig.A.Round.guns[1]);
        }

        [Test]
        public void RankedNormalisedGunsUseTheStandardCurve()
        {
            var rig = new Rig(DuelConfig.Default(), Guns.Effective(Guns.Peacemaker, true), Guns.Effective(Guns.Peacemaker, true));
            rig.Exchange(150, 300);
            Assert.AreEqual(40, rig.A.Round.slots[0].damageDealt, 1e-12);
            Assert.AreEqual(24, rig.A.Round.slots[1].damageDealt, 1e-12);
        }

        [Test]
        public void EachSlotUsesItsOwnGunInATie()
        {
            var rig = new Rig(DuelConfig.Default(), Guns.Standard, Guns.Peacemaker);
            rig.Exchange(250, 260);   // tie window: 30 (standard) and 32.4 (Peacemaker, 34 -> 26 over 250..300)
            var r = rig.A.Round;
            Assert.IsTrue(r.isTie);
            Assert.AreEqual(30, r.slots[0].damageDealt, 1e-9);
            Assert.AreEqual(32.4, r.slots[1].damageDealt, 1e-9);
            Assert.AreEqual(67.6, rig.A.Match.Hp[0], 1e-9);
            Assert.AreEqual(70, rig.A.Match.Hp[1], 1e-9);
        }

        [Test]
        public void GunStacksWithLastLightOnce()
        {
            var rig = new Rig(DuelConfig.Default(), Guns.Peacemaker, Guns.Standard);
            rig.A.SetDamageMultiplier(2.0);
            rig.Exchange(150, -1);
            Assert.AreEqual(96, rig.A.Round.slots[0].damageDealt, 1e-12, "48 x 2");
        }

        [Test]
        public void GunIsLatchedAtCountdown()
        {
            var rig = new Rig(DuelConfig.Default(), Guns.Standard, Guns.Standard);
            Assert.IsTrue(rig.A.StartCountdown());
            rig.A.SetGun(0, Guns.Peacemaker);   // too late for this round
            Assert.AreEqual(Guns.Standard, rig.A.Round.guns[0]);
            rig.Clock.Now += rig.A.Round.schedule.CueOffsetNs;
            rig.A.Tick();
            long cue = rig.A.Round.cueDecidedNs;
            rig.Clock.Now = cue + 2000000000L;
            long ns = DuelConfig.MsToNs(150);
            rig.Net.SendReport(new ShotReport { roundId = rig.A.Round.roundId, slot = 0, kind = ReportKind.Shot, reactionNs = ns, cuePresentedNs = cue, inputNs = cue + ns, inputSource = InputSource.Injected });
            rig.A.Tick();
            Assert.AreEqual(40, rig.A.Round.slots[0].damageDealt, 1e-12);
            rig.Next();
            rig.Exchange(150, -1);
            Assert.AreEqual(48, rig.A.Round.slots[0].damageDealt, 1e-12, "from the next countdown on");
        }

        [Test]
        public void GunSurvivesStartMatchAndIgnoresBadSlots()
        {
            var rig = new Rig(DuelConfig.Default(), Guns.Peacemaker, Guns.Standard);
            rig.A.SetGun(-1, Guns.Standard);
            rig.A.SetGun(2, Guns.Standard);
            rig.A.SetGun(0, "nonsense_gun");
            Assert.AreEqual(Guns.Standard, rig.A.GunOf(0), "unknown ids mean standard");
            rig.A.SetGun(0, Guns.Peacemaker);
            rig.A.StartMatch();
            Assert.AreEqual(Guns.Peacemaker, rig.A.GunOf(0), "a loadout is the driver's choice, not reset per match");
        }

        [Test]
        public void GunNeverChangesReactionGradeOrValidation()
        {
            var std = new Rig(DuelConfig.Default(), Guns.Standard, Guns.Standard);
            var pm = new Rig(DuelConfig.Default(), Guns.Peacemaker, Guns.Peacemaker);
            std.Exchange(212.5, 333);
            pm.Exchange(212.5, 333);
            for (int s = 0; s < 2; s++)
            {
                Assert.AreEqual(std.A.Round.slots[s].reactionNs, pm.A.Round.slots[s].reactionNs);
                Assert.AreEqual(std.A.Round.slots[s].grade, pm.A.Round.slots[s].grade);
                Assert.AreEqual(std.A.Round.slots[s].outcome, pm.A.Round.slots[s].outcome);
            }
            Assert.AreEqual(std.A.Round.schedule.HoldNs, pm.A.Round.schedule.HoldNs, "same seed, same hold");
            Assert.AreEqual(std.A.Round.cueDecidedNs, pm.A.Round.cueDecidedNs);
            Assert.AreEqual(0, pm.A.RejectedReports);
        }

        // ------------------------------------------------------------------ recorded match (casual vs ranked)

        [Test]
        public void RecordedSimAppliesGunsLikeTheServer()
        {
            var cfg = DuelConfig.Default();
            var me = new[] { GhostAttempt.Shot(DuelConfig.MsToNs(180), 1), GhostAttempt.Shot(DuelConfig.MsToNs(280), 2), GhostAttempt.Shot(DuelConfig.MsToNs(320), 3) };
            var ghost = new[] { GhostAttempt.Shot(DuelConfig.MsToNs(230), 1), GhostAttempt.Shot(DuelConfig.MsToNs(240), 2), GhostAttempt.Shot(DuelConfig.MsToNs(250), 3) };
            // Tools/Parity/vectors.json "peacemaker_casual" / "peacemaker_ranked_normalised"
            var casual = RecordedMatchSim.Play(cfg, 21, 3, 9, me, ghost, Guns.Effective(Guns.Peacemaker, false), Guns.Standard);
            var ranked = RecordedMatchSim.Play(cfg, 21, 3, 9, me, ghost, Guns.Effective(Guns.Peacemaker, true), Guns.Standard);
            Assert.AreEqual(0, casual.Winner, "the Peacemaker wins this duel in casual");
            Assert.AreEqual(1, ranked.Winner, "the same duel goes to the ghost in ranked");
            var plain = RecordedMatchSim.Play(cfg, 21, 3, 9, me, ghost);
            Assert.AreEqual(ranked.Hp[0], plain.Hp[0], 1e-12, "default = standard guns");
            Assert.AreEqual(ranked.Hp[1], plain.Hp[1], 1e-12);
        }
    }
}
