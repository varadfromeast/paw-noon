using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>
    /// C# -> TypeScript parity (SPEC_V2 §4.5). Runs the real Core rules (System.Random, RoundSchedule,
    /// DamageCurve, Grading, MatchState, LocalAuthority) over a fixed case list and compares the result
    /// with the committed Tools/Parity/vectors.json, which supabase/functions/_shared/duel/duel.test.ts
    /// replays with `deno test`.
    ///
    /// When the committed file is missing or differs (semantically: numbers to 1e-9), the test rewrites
    /// vectors.json with the C# output and fails, so `git diff Tools/Parity/vectors.json` shows exactly what
    /// changed. Commit the new file only if the change in the rules is intended, then make deno test pass.
    /// Set the environment variable PN_PARITY_REWRITE=1 to rewrite the file even when it matches.
    ///
    /// Keep the case list, the harness and the writer layout identical to Tools/Parity/reference_vectors.py.
    /// </summary>
    public class ParityVectorsTests
    {
        // ------------------------------------------------------------------ harness constants
        private const long T0 = 10000000000L;
        private const long AfterCueNs = 2000000000L;   // reports arrive 2 s after the cue (past timeout + slack)
        private const long BetweenNs = 1000000000L;
        private const long EarlyAfterStartNs = 1000000000L;
        private const int MatchRounds = 3;
        private const int MatchMaxAttempts = 9;        // rounds * 3, same as the server default

        private static readonly int[] Seeds =
        {
            0, 1, 2, 3, 7, 42, 99, 1234, 31337, 161803398, 161803399, 987654321, int.MaxValue, -1, -42, int.MinValue
        };
        private const int HoldsPerSeed = 8;
        private const int NextPerSeed = 5;

        private static readonly double[] DamageMs =
        {
            -1.0, 0.0, 99.9, 100.0, 149.999, 150.0, 175.0, 200.0, 200.001, 225.0, 250.0, 263.5, 300.0,
            333.333333, 400.0, 420.0, 500.0, 600.0, 700.0, 700.5, 999.0, 1000.0, 1250.0, 1500.0, 1500.001, 2000.0
        };

        private struct Spec
        {
            public string kind; // shot | early | noshot (anticipation = shot under anticipationMs)
            public long reactionNs;
        }

        private static Spec S(double ms) { return new Spec { kind = "shot", reactionNs = DuelConfig.MsToNs(ms) }; }
        private static Spec SN(long ns) { return new Spec { kind = "shot", reactionNs = ns }; }
        private static readonly Spec Early = new Spec { kind = "early", reactionNs = 0 };
        private static readonly Spec NoShot = new Spec { kind = "noshot", reactionNs = 0 };

        private struct RoundCase
        {
            public string name;
            public double hp0, hp1;
            public Spec s0, s1;
        }

        private static RoundCase R(string name, double hp0, double hp1, Spec s0, Spec s1)
        {
            return new RoundCase { name = name, hp0 = hp0, hp1 = hp1, s0 = s0, s1 = s1 };
        }

        private static RoundCase[] RoundCases()
        {
            return new[]
            {
                R("p0_faster", 100, 100, S(200), S(290)),
                R("p1_faster", 100, 100, S(310), S(240)),
                R("tie_exact_window", 100, 100, S(250), S(265)),
                R("tie_reverse_order", 100, 100, S(265), S(250)),
                R("just_outside_tie", 100, 100, SN(250000000L), SN(265000001L)),
                R("tie_double_ko", 5, 5, S(250), S(250.5)),
                R("tie_one_ko", 5, 50, S(250), S(260)),
                R("kill_stops_second", 100, 10, S(180), S(240)),
                R("kill_stops_second_slot0", 5, 100, S(290), S(250)),
                R("same_damage_no_tie", 100, 100, S(120), S(140)),
                R("anticipation_vs_shot", 100, 100, S(90), S(300)),
                R("anticipation_boundary", 100, 100, SN(99999999L), SN(100000000L)),
                R("both_100ms_tie", 100, 100, S(100), S(100)),
                R("anticipation_vs_early_void", 100, 100, S(50), Early),
                R("early_vs_early_void", 100, 100, Early, Early),
                R("anticipation_both_void", 100, 100, S(20), S(99)),
                R("early_vs_shot", 100, 100, Early, S(420)),
                R("early_vs_noshot", 100, 100, Early, NoShot),
                R("noshot_both", 100, 100, NoShot, NoShot),
                R("shot_vs_noshot", 100, 100, S(333.333333), NoShot),
                R("shot_1500_vs_noshot", 100, 100, S(1500), NoShot),
                R("shot_over_curve_vs_noshot", 100, 100, SN(1500000001L), NoShot),
                R("shot_in_slack_vs_shot", 100, 100, S(1520), S(700)),
                R("shot_implausible_becomes_noshot", 100, 100, S(1600), S(700)),
                R("fractional", 100, 100, SN(263456789L), SN(333333333L)),
                R("low_hp_noshot_kill", 100, 16, S(400), NoShot),
                R("both_low_slower_kills_after_survive", 30, 30, S(260), S(500)),
            };
        }

        private struct MatchCase
        {
            public string name;
            public int seed;
            public int maxAttempts;
            public Spec[] a, b;
            public string gunA, gunB;   // equipped guns (SPEC_V2 §6)
            public bool ranked;         // ranked: both scored with the standard curve
        }

        private static MatchCase M(string name, int seed, Spec[] a, Spec[] b, int maxAttempts = MatchMaxAttempts)
        {
            return new MatchCase { name = name, seed = seed, a = a, b = b, maxAttempts = maxAttempts, gunA = Guns.Standard, gunB = Guns.Standard };
        }

        /// <summary>A match with loadouts (M7): <paramref name="gunA"/> / <paramref name="gunB"/>, ranked or casual.</summary>
        private static MatchCase MG(string name, int seed, string gunA, string gunB, bool ranked, Spec[] a, Spec[] b)
        {
            return new MatchCase { name = name, seed = seed, a = a, b = b, maxAttempts = MatchMaxAttempts, gunA = gunA, gunB = gunB, ranked = ranked };
        }

        private static MatchCase[] MatchCases()
        {
            return new[]
            {
                M("three_clean_rounds", 1, new[] { S(210), S(250), S(230) }, new[] { S(300), S(240), S(260) }),
                M("void_replays_round", 7, new[] { Early, S(200), S(220), S(210) }, new[] { Early, S(300), S(330), S(260) }),
                M("ko_in_round_three", 42, new[] { S(140), S(140), S(140) }, new[] { S(300), S(300), S(300) }),
                M("double_ko_draw", 3, new[] { S(140), S(140), S(140) }, new[] { S(145), S(140), S(150) }),
                M("opponent_exhausted", 1234, new[] { Early, S(200), S(200) }, new[] { Early, S(250) }),
                M("hp_equal_draw", 99, new[] { S(200), S(300), NoShot }, new[] { S(300), S(200), NoShot }),
                M("mixed_fouls", 31337, new[] { S(80), Early, NoShot, S(450) }, new[] { S(260), S(500), S(1200), S(380) }),
                M("three_ties_hp_decides", 987654321, new[] { S(231), S(265), S(244) }, new[] { S(230), S(266), S(250) }),
                M("ghost_wins", 5, new[] { S(300), S(280), S(260) }, new[] { S(240), S(250), S(230) }),
                M("max_attempts", 2, new[] { Early, Early, Early, Early, Early }, new[] { Early, Early, Early, Early, Early }, 4),
                // LAST LIGHT (SPEC_V2 §8.4): 100 -> 60 -> 20 each, round 3 doubles a 400 ms shot (16 -> 32) into a KO
                M("last_light_ko", 11, new[] { S(110), S(110), S(400) }, new[] { S(140), S(140), NoShot }),
                // void at 20/20 replays as LAST LIGHT too; x2 tie 250 vs 260 (60 / 57.6) is a double KO
                M("last_light_void_then_tie", 12, new[] { S(110), S(110), Early, S(250) }, new[] { S(140), S(140), Early, S(260) }),
                // only one cat at or below 20 HP: no LAST LIGHT, x1
                M("one_low_no_last_light", 13, new[] { S(110), S(300), S(400) }, new[] { S(140), S(140), S(600) }),
                // Peacemaker (SPEC_V2 §6): casual uses each side's gun; ranked scores both with the standard curve
                // (the same duel: the Peacemaker wins it in casual, the ghost wins it in ranked)
                MG("peacemaker_casual", 21, Guns.Peacemaker, Guns.Standard, false,
                    new[] { S(180), S(280), S(320) }, new[] { S(230), S(240), S(250) }),
                MG("peacemaker_ranked_normalised", 21, Guns.Peacemaker, Guns.Standard, true,
                    new[] { S(180), S(280), S(320) }, new[] { S(230), S(240), S(250) }),
                // the ghost carries the Peacemaker; a tie lands both shots with their own curves
                MG("peacemaker_ghost_tie", 22, Guns.Standard, Guns.Peacemaker, false,
                    new[] { S(200), S(260), S(500) }, new[] { S(210), S(250), S(450) }),
                // both Peacemakers into LAST LIGHT: you 100 -> 55 -> 7, ghost 100 -> 52 -> 4 (tie), then x2 (48 -> 96) KOs
                MG("peacemaker_last_light", 23, Guns.Peacemaker, Guns.Peacemaker, false,
                    new[] { S(150), S(150), S(150) }, new[] { S(175), S(150), NoShot }),
            };
        }

        // ------------------------------------------------------------------ the test

        [Test]
        public void VectorsMatchCommittedFile()
        {
            string dataPath = UnityEngine.Application.dataPath;
            string cfgPath = Path.Combine(dataPath, "PawNoon", "Resources", "Data", "duel_config.json");
            string outPath = Path.GetFullPath(Path.Combine(dataPath, "..", "Tools", "Parity", "vectors.json"));
            var cfg = DuelConfig.FromJson(File.ReadAllText(cfgPath));

            string generated = Write(Build(cfg));
            Assert.IsNotNull(PnJson.Parse(generated), "generated vectors are not valid JSON");

            string committed = File.Exists(outPath) ? File.ReadAllText(outPath) : null;
            var diffs = new List<string>();
            if (committed == null) diffs.Add("no committed vectors.json");
            else Compare("$", PnJson.Parse(committed), PnJson.Parse(generated), diffs);

            bool force = Environment.GetEnvironmentVariable("PN_PARITY_REWRITE") == "1";
            if (diffs.Count > 0 || force)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllText(outPath, generated, new UTF8Encoding(false));
            }
            if (diffs.Count > 0)
            {
                var sb = new StringBuilder();
                sb.Append(diffs.Count).Append(" difference(s) between C# and the committed vectors; rewrote ")
                  .Append(outPath).Append(" (git diff it, then make `deno test` pass):\n");
                for (int i = 0; i < diffs.Count && i < 40; i++) sb.Append("  ").Append(diffs[i]).Append('\n');
                Assert.Fail(sb.ToString());
            }
        }

        // ------------------------------------------------------------------ build

        private static List<object> Build_Section(IEnumerable<J> items)
        {
            var l = new List<object>();
            foreach (var i in items) l.Add(i);
            return l;
        }

        private static List<KeyValuePair<string, List<object>>> Build(DuelConfig cfg)
        {
            var sections = new List<KeyValuePair<string, List<object>>>();

            var meta = new J()
                .Add("version", 1)
                .Add("source", "Assets/PawNoon/Resources/Data/duel_config.json")
                .Add("generator", "ParityVectorsTests.cs (Unity) / Tools/Parity/reference_vectors.py")
                .Add("tieWindowMs", cfg.tieWindowMs).Add("anticipationMs", cfg.anticipationMs)
                .Add("noShotTimeoutSeconds", cfg.noShotTimeoutSeconds).Add("reportSlackMs", cfg.reportSlackMs)
                .Add("commitGraceMs", cfg.commitGraceMs).Add("maxHp", cfg.maxHp)
                .Add("holdMinSeconds", cfg.holdMinSeconds).Add("holdMaxSeconds", cfg.holdMaxSeconds)
                .Add("countdownBeats", cfg.countdownBeats).Add("beatNs", cfg.BeatNs)
                .Add("suddenDeathEnabled", cfg.suddenDeathEnabled).Add("suddenDeathHp", cfg.suddenDeathHp)
                .Add("suddenDeathDamageMultiplier", cfg.suddenDeathDamageMultiplier)
                .Add("damageValuePeacemaker", cfg.damageValuePeacemaker);
            sections.Add(Section("meta", new[] { meta }));

            var sch = new List<J>();
            foreach (int seed in Seeds)
            {
                var r = new System.Random(seed);
                var next = new List<object>();
                for (int i = 0; i < NextPerSeed; i++) next.Add(r.Next());
                r = new System.Random(seed);
                var holds = new List<object>();
                var offs = new List<object>();
                for (int i = 0; i < HoldsPerSeed; i++)
                {
                    var s = RoundSchedule.Create(cfg, r);
                    holds.Add(s.HoldNs);
                    offs.Add(s.CueOffsetNs);
                }
                sch.Add(new J().Add("seed", seed).Add("next", next).Add("holdNs", holds).Add("cueOffsetNs", offs));
            }
            sections.Add(Section("schedules", sch));

            var dmg = new List<J>();
            foreach (double ms in DamageMs)
                dmg.Add(new J().Add("ms", ms).Add("damage", DamageCurve.Evaluate(cfg, ms))
                    .Add("damagePeacemaker", DamageCurve.Evaluate(cfg, Guns.Peacemaker, ms)).Add("grade", Grading.FromMs(cfg, ms).ToString()));
            sections.Add(Section("damage", dmg));

            var rounds = new List<J>();
            foreach (var c in RoundCases())
            {
                var expect = PlayRound(cfg, c);
                rounds.Add(new J().Add("name", c.name).Add("hp", L(c.hp0, c.hp1))
                    .Add("slots", L(SpecJ(c.s0), SpecJ(c.s1))).Add("expect", expect));
            }
            sections.Add(Section("rounds", rounds));

            var matches = new List<J>();
            foreach (var c in MatchCases())
            {
                var a = new List<object>();
                foreach (var s in c.a) a.Add(SpecJ(s));
                var b = new List<object>();
                foreach (var s in c.b) b.Add(SpecJ(s));
                matches.Add(new J().Add("name", c.name).Add("seed", c.seed).Add("rounds", MatchRounds)
                    .Add("maxAttempts", c.maxAttempts).Add("guns", L(c.gunA, c.gunB)).Add("ranked", c.ranked)
                    .Add("a", a).Add("b", b).Add("expect", PlayMatch(cfg, c)));
            }
            sections.Add(Section("matches", matches));
            return sections;
        }

        private static KeyValuePair<string, List<object>> Section(string name, IEnumerable<J> items)
        {
            return new KeyValuePair<string, List<object>>(name, Build_Section(items));
        }

        private static List<object> L(params object[] items) { return new List<object>(items); }

        private static J SpecJ(Spec s) { return new J().Add("kind", s.kind).Add("reactionNs", s.reactionNs); }

        // ------------------------------------------------------------------ harness (mirrors recorded_match.ts)

        private static bool MakeReport(LocalAuthority a, int slot, Spec s, long cue, long start, out ShotReport r)
        {
            r = default(ShotReport);
            if (s.kind == "noshot") return false;
            r.roundId = a.Round.roundId;
            r.slot = slot;
            r.inputSource = InputSource.Injected;
            if (s.kind == "early")
            {
                r.kind = ReportKind.Early;
                r.reactionNs = 0;
                r.cuePresentedNs = 0;
                r.inputNs = start + EarlyAfterStartNs;
                return true;
            }
            r.kind = ReportKind.Shot;
            r.reactionNs = s.reactionNs;
            r.cuePresentedNs = cue;
            r.inputNs = cue + s.reactionNs;
            return true;
        }

        private static void RunExchange(LocalAuthority a, ManualClock clock, LoopbackTransport net, Spec s0, Spec s1)
        {
            Assert.IsTrue(a.StartCountdown(), "StartCountdown");
            long start = clock.Now;
            clock.Now = start + a.Round.schedule.CueOffsetNs;
            a.Tick();
            Assert.AreEqual(RoundPhase.Draw, a.Phase, "cue at the scheduled offset");
            long cue = a.Round.cueDecidedNs;
            clock.Now = cue + AfterCueNs;
            ShotReport r;
            if (MakeReport(a, 0, s0, cue, start, out r)) net.SendReport(r);
            if (MakeReport(a, 1, s1, cue, start, out r)) net.SendReport(r);
            a.Tick();
        }

        private static J AttemptView(LocalAuthority a, bool withSchedule)
        {
            var round = a.Round;
            var j = new J();
            if (withSchedule)
                j.Add("roundNumber", round.roundNumber).Add("holdNs", round.schedule.HoldNs).Add("cueOffsetNs", round.schedule.CueOffsetNs);
            return j.Add("void", round.isVoid).Add("tie", round.isTie).Add("winner", round.winnerSlot)
                .Add("outcomes", L(round.slots[0].outcome.ToString(), round.slots[1].outcome.ToString()))
                .Add("damage", L(round.slots[0].damageDealt, round.slots[1].damageDealt))
                .Add("grades", L(round.slots[0].grade.ToString(), round.slots[1].grade.ToString()))
                .Add("hp", L(a.Match.Hp[0], a.Match.Hp[1]))
                .Add("matchOver", a.Match.Over).Add("matchWinner", a.Match.Winner);
        }

        private static J PlayRound(DuelConfig cfg, RoundCase c)
        {
            var clock = new ManualClock { Now = T0 };
            var net = new LoopbackTransport();
            var a = new LocalAuthority(cfg, clock, net, 1);
            a.StartMatch();
            a.Match.Hp[0] = c.hp0;
            a.Match.Hp[1] = c.hp1;
            RunExchange(a, clock, net, c.s0, c.s1);
            return AttemptView(a, false);
        }

        /// <summary>The recorded-match driver (3 resolved rounds, end reasons, HP decides) shared with the server.</summary>
        private static J PlayMatch(DuelConfig cfg, MatchCase c)
        {
            var clock = new ManualClock { Now = T0 };
            var net = new LoopbackTransport();
            var a = new LocalAuthority(cfg, clock, net, c.seed);
            a.SetGun(0, Guns.Effective(c.gunA, c.ranked));
            a.SetGun(1, Guns.Effective(c.gunB, c.ranked));
            a.StartMatch();
            var attempts = new List<object>();
            int resolved = 0, k = 0;
            string end;
            while (true)
            {
                if (a.Match.Over) { end = a.Match.Winner >= 0 ? "ko" : "double_ko"; break; }
                if (resolved >= MatchRounds) { end = "rounds"; break; }
                if (k >= c.maxAttempts) { end = "max_attempts"; break; }
                if (k >= c.b.Length) { end = "opponent_exhausted"; break; }
                if (k >= c.a.Length) { end = "player_exhausted"; break; }
                a.SetDamageMultiplier(SuddenDeath.MultiplierFor(cfg, a.Match));   // LAST LIGHT, as DuelDirector.BeginStandoff
                RunExchange(a, clock, net, c.a[k], c.b[k]);
                attempts.Add(AttemptView(a, true));
                if (!a.Round.isVoid) resolved++;
                k++;
                clock.Now += BetweenNs;
                if (a.Phase == RoundPhase.Result && !a.Match.Over) a.NextRound();
            }
            var hp = a.Match.Hp;
            int winner = a.Match.Over ? a.Match.Winner : (hp[0] > hp[1] ? 0 : (hp[1] > hp[0] ? 1 : -1));
            return new J().Add("attempts", attempts).Add("attemptsUsed", k).Add("resolvedRounds", resolved)
                .Add("endReason", end).Add("winner", winner).Add("hp", L(hp[0], hp[1]))
                .Add("roundWins", L(a.Match.RoundWins[0], a.Match.RoundWins[1]));
        }

        // ------------------------------------------------------------------ JSON writer (same layout as the Python)

        /// <summary>Ordered JSON object.</summary>
        private sealed class J
        {
            public readonly List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();
            public J Add(string k, object v) { Items.Add(new KeyValuePair<string, object>(k, v)); return this; }
        }

        private static string Write(List<KeyValuePair<string, List<object>>> sections)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            for (int s = 0; s < sections.Count; s++)
            {
                sb.Append("  ").Append(Str(sections[s].Key)).Append(": [\n");
                var items = sections[s].Value;
                for (int i = 0; i < items.Count; i++)
                {
                    sb.Append("    ");
                    Fmt(sb, items[i]);
                    if (i < items.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append("  ]");
                if (s < sections.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string Str(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void Fmt(StringBuilder sb, object v)
        {
            var ci = CultureInfo.InvariantCulture;
            if (v == null) { sb.Append("null"); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is int) { sb.Append(((int)v).ToString(ci)); return; }
            if (v is long) { sb.Append(((long)v).ToString(ci)); return; }
            if (v is double)
            {
                double d = (double)v;
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(ci));
                else sb.Append(d.ToString("R", ci));
                return;
            }
            if (v is string) { sb.Append(Str((string)v)); return; }
            var j = v as J;
            if (j != null)
            {
                sb.Append('{');
                for (int i = 0; i < j.Items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Str(j.Items[i].Key)).Append(':');
                    Fmt(sb, j.Items[i].Value);
                }
                sb.Append('}');
                return;
            }
            var list = v as IList;
            if (list != null)
            {
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Fmt(sb, list[i]);
                }
                sb.Append(']');
                return;
            }
            throw new ArgumentException("cannot write " + v.GetType());
        }

        // ------------------------------------------------------------------ semantic comparison

        private static void Compare(string path, object expected, object actual, List<string> diffs)
        {
            if (expected is double || actual is double)
            {
                if (!(expected is double) || !(actual is double) || Math.Abs((double)expected - (double)actual) > 1e-9)
                    diffs.Add(path + ": committed " + Show(expected) + ", C# " + Show(actual));
                return;
            }
            var eo = expected as Dictionary<string, object>;
            var ao = actual as Dictionary<string, object>;
            if (eo != null || ao != null)
            {
                if (eo == null || ao == null) { diffs.Add(path + ": object vs non-object"); return; }
                foreach (var kv in eo)
                {
                    object av;
                    if (!ao.TryGetValue(kv.Key, out av)) { diffs.Add(path + "." + kv.Key + ": missing in C# output"); continue; }
                    Compare(path + "." + kv.Key, kv.Value, av, diffs);
                }
                foreach (var kv in ao)
                    if (!eo.ContainsKey(kv.Key)) diffs.Add(path + "." + kv.Key + ": not in committed file");
                return;
            }
            var el = expected as List<object>;
            var al = actual as List<object>;
            if (el != null || al != null)
            {
                if (el == null || al == null) { diffs.Add(path + ": array vs non-array"); return; }
                if (el.Count != al.Count) { diffs.Add(path + ": committed " + el.Count + " items, C# " + al.Count); return; }
                for (int i = 0; i < el.Count; i++)
                {
                    string label = path + "[" + i + "]";
                    var named = el[i] as Dictionary<string, object>;
                    object n;
                    if (named != null && (named.TryGetValue("name", out n) || named.TryGetValue("seed", out n)))
                        label = path + "[" + Show(n) + "]";
                    Compare(label, el[i], al[i], diffs);
                }
                return;
            }
            if (!Equals(expected, actual)) diffs.Add(path + ": committed " + Show(expected) + ", C# " + Show(actual));
        }

        private static string Show(object v)
        {
            if (v == null) return "null";
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            return v.ToString();
        }
    }
}
