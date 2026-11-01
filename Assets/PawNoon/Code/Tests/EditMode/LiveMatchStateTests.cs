using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>
    /// Live duels (Docs/LIVE_DUELS.md): the C# mirror <see cref="LiveMatchState"/> against
    /// Tools/Parity/live_vectors.json (written by Tools/Parity/live_reference.py, replayed by the server's live.test.ts
    /// too), plus a few readable cases. The server-only soft flags in the vectors are not mirrored and not compared.
    /// </summary>
    public class LiveMatchStateTests
    {
        private const long T0 = 1700000000000L;
        private const long Ms = 1000000L;

        private static DuelConfig Hard()
        {
            string path = Path.Combine(UnityEngine.Application.dataPath, "PawNoon", "Resources", "Data", "duel_config.json");
            return DuelConfig.FromJson(File.ReadAllText(path));
        }

        // ------------------------------------------------------------------------------ a simulated phone
        /// <summary>A phone whose uptime clock reads offsetNs at server T0; DRAW reaches it downMs after it was sent.</summary>
        private static LiveReport Shot(LiveMatchState s, long offsetNs, long downMs, double reactionMs)
        {
            var r = s.Round;
            long cue = (r.cueSentAtMs + downMs - T0) * Ms + offsetNs + 8 * Ms;
            long reaction = (long)Math.Round(reactionMs * Ms);
            var rep = new LiveReport();
            rep.roundId = r.roundId;
            rep.kind = ReportKind.Shot;
            rep.reactionNs = reaction;
            rep.cuePresentedNs = cue;
            rep.inputNs = cue + reaction;
            rep.countdownStartNs = (r.startAtMs - T0) * Ms + offsetNs;
            rep.rttMs = double.NaN;
            return rep;
        }

        private static LiveReport Early(LiveMatchState s, long offsetNs, long afterStartMs)
        {
            var r = s.Round;
            var rep = new LiveReport();
            rep.roundId = r.roundId;
            rep.kind = ReportKind.Early;
            rep.countdownStartNs = (r.startAtMs - T0) * Ms + offsetNs;
            rep.inputNs = rep.countdownStartNs + afterStartMs * Ms;
            rep.rttMs = double.NaN;
            return rep;
        }

        private static LiveMatchState Joined(DuelConfig cfg, int seed)
        {
            var s = new LiveMatchState(cfg, true, false, seed, "A", Guns.Standard, T0);
            Assert.AreEqual(LiveVerdict.Accepted, s.Join("B", Guns.Standard, T0 + 1000));
            return s;
        }

        private static void ToDraw(LiveMatchState s, long t)
        {
            Assert.AreEqual(LiveVerdict.Accepted, s.Ready(0, s.Attempt, t));
            Assert.AreEqual(LiveVerdict.Accepted, s.Ready(1, s.Attempt, t + 5));
            Assert.AreEqual(LivePhase.Countdown, s.Phase);
            s.Advance(s.Round.cueDueAtMs);
            Assert.AreEqual(LivePhase.Draw, s.Phase);
        }

        [Test]
        public void FasterReactionWinsWhateverTheArrivalOrder()
        {
            var cfg = Hard();
            var s = Joined(cfg, 3);
            ToDraw(s, T0 + 2000);
            long cue = s.Round.cueSentAtMs;
            // slot 1 (fast phone, 260 ms) arrives first; slot 0 (slow phone, 240 ms) arrives later and still wins
            Assert.AreEqual(LiveVerdict.Accepted, s.Report(1, Shot(s, 5000000000L, 20, 260), cue + 310));
            Assert.AreEqual(0, s.History.Count, "nothing decided on the first arrival");
            Assert.AreEqual(LiveVerdict.Accepted, s.Report(0, Shot(s, 77000000000L, 150, 240), cue + 560));
            Assert.AreEqual(1, s.History.Count);
            Assert.AreEqual(0, s.History[0].winner);
            Assert.AreEqual(LivePhase.Ready, s.Phase);
            Assert.AreEqual(1, s.Attempt);
        }

        [Test]
        public void BothEarlyVoidsBeforeTheCueAndReplaysTheSameRound()
        {
            var cfg = Hard();
            var s = Joined(cfg, 9);
            s.Ready(0, 0, T0 + 2000);
            s.Ready(1, 0, T0 + 2005);
            long start = s.Round.startAtMs;
            Assert.AreEqual(LiveVerdict.Accepted, s.Report(0, Early(s, 5000000000L, 900), start + 920));
            Assert.AreEqual(LiveVerdict.Accepted, s.Report(1, Early(s, 77000000000L, 1000), start + 1140));
            Assert.IsTrue(s.History[0].isVoid);
            Assert.AreEqual(1, s.History[0].roundNumber);
            ToDraw(s, start + 5000);
            Assert.AreEqual(1, s.Round.roundNumber, "the void round replays with the same number");
            Assert.AreEqual(2, s.Round.roundId);
        }

        [Test]
        public void MissedReadyDeadlineForfeitsAndShotBeforeTheCueIsImplausible()
        {
            var cfg = Hard();
            var s = Joined(cfg, 21);
            s.Ready(0, 0, T0 + 2000);
            s.Ready(1, 0, T0 + 2001);
            var fake = Shot(s, 0, 0, 200);
            Assert.AreEqual(LiveVerdict.Implausible, s.Report(0, fake, T0 + 2500), "no DRAW yet");
            s.Advance(s.Round.cueDueAtMs);
            s.Advance(s.DeadlineMs);
            Assert.AreEqual(SlotOutcome.NoShot, s.History[0].outcomes[0]);
            Assert.AreEqual(LiveVerdict.Accepted, s.Ready(0, 1, s.History[0].resolvedAtMs + 1000));
            s.Advance(s.DeadlineMs);
            Assert.AreEqual(LivePhase.Finished, s.Phase);
            Assert.AreEqual(LiveEnd.Forfeit, s.Result.end);
            Assert.AreEqual(0, s.Result.winner);
            Assert.AreEqual(1, s.Result.forfeitedBy);
        }

        [Test]
        public void ReplayAgreesWithRecordedMatchSim()
        {
            var cfg = Hard();
            var a = new List<GhostAttempt> { GhostAttempt.Shot(231000000L), GhostAttempt.Shot(301000000L), GhostAttempt.Shot(255000000L) };
            var b = new List<GhostAttempt> { GhostAttempt.Shot(268000000L), GhostAttempt.Shot(247000000L), GhostAttempt.Shot(290000000L) };
            var live = LiveReplay.Play(cfg, 1, 3, 9, a, b, Guns.Standard, Guns.Standard);
            var sim = RecordedMatchSim.Play(cfg, 1, 3, 9, a, b);
            Assert.IsTrue(live.Terminal);
            Assert.AreEqual(LiveEnd.Rounds, live.End);
            Assert.AreEqual(sim.Winner, live.Winner);
            Assert.AreEqual(sim.Hp[0], live.Hp[0], 1e-9);
            Assert.AreEqual(sim.Hp[1], live.Hp[1], 1e-9);
        }

        // ------------------------------------------------------------------------------ parity vectors

        [Test]
        public void LiveVectorsReplayIdentically()
        {
            var cfg = Hard();
            string path = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "Tools", "Parity", "live_vectors.json"));
            var root = PnJson.Parse(File.ReadAllText(path));
            Assert.IsNotNull(root, "live_vectors.json is not valid JSON");
            var rules = PnJson.Obj(PnJson.Obj(root, "meta"), "rules");
            Assert.AreEqual(LiveRules.ReadyTimeoutMs, (long)PnJson.Num(rules, "readyTimeoutMs", -1));
            Assert.AreEqual(LiveRules.StartLeadMs, (long)PnJson.Num(rules, "startLeadMs", -1));
            Assert.AreEqual(LiveRules.NetworkGraceMs, (long)PnJson.Num(rules, "networkGraceMs", -1));
            Assert.AreEqual(LiveRules.EarlyGraceMs, (long)PnJson.Num(rules, "earlyGraceMs", -1));
            Assert.AreEqual(LiveRules.QuickLobbyMs, (long)PnJson.Num(rules, "quickLobbyMs", -1));
            Assert.AreEqual(LiveRules.FriendLobbyMs, (long)PnJson.Num(rules, "friendLobbyMs", -1));
            Assert.AreEqual(LiveRules.CueEarlyToleranceNs, (long)PnJson.Num(rules, "cueEarlyToleranceNs", -1));
            Assert.AreEqual(LiveRules.CueLateToleranceNs, (long)PnJson.Num(rules, "cueLateToleranceNs", -1));
            Assert.AreEqual(LiveRules.ReportWindowMs(cfg), (long)PnJson.Num(PnJson.Obj(root, "meta"), "reportWindowMs", -1));

            var diffs = new List<string>();
            int ops = 0;
            foreach (var c in PnJson.Arr(root, "cases"))
            {
                string name = PnJson.Str(c, "name", "?");
                var guns = PnJson.Arr(c, "guns");
                var s = new LiveMatchState(cfg, PnJson.Str(c, "kind", "") == "friend", PnJson.Str(c, "mode", "") == "ranked",
                    (int)PnJson.Num(c, "seed", 0), "A", guns[0] as string, (long)PnJson.Num(c, "createdAtMs", 0));
                var opList = PnJson.Arr(c, "ops");
                var expect = PnJson.Arr(c, "expect");
                for (int i = 0; i < opList.Count && diffs.Count < 20; i++)
                {
                    var op = opList[i];
                    long t = (long)PnJson.Num(op, "t", 0);
                    int slot = PnJson.Int(op, "slot", -1);
                    LiveVerdict v = LiveVerdict.Accepted;
                    switch (PnJson.Str(op, "op", ""))
                    {
                        case "join": v = s.Join(PnJson.Str(op, "user", null), PnJson.Str(op, "gun", null), t); break;
                        case "ready": v = s.Ready(slot, PnJson.Int(op, "attempt", -1), t); break;
                        case "report": v = s.Report(slot, ReportOf(PnJson.Obj(op, "report")), t); break;
                        case "forfeit": v = s.Forfeit(slot, t); break;
                        default: s.Advance(t); break;
                    }
                    ops++;
                    Compare(name + "#" + i + "(" + PnJson.Str(op, "op", "") + ")", expect[i], Snapshot(s, v), diffs);
                }
            }
            Assert.IsTrue(ops >= 100, "enough ops: " + ops);
            if (diffs.Count > 0) Assert.Fail(diffs.Count + " live parity mismatch(es):\n  " + string.Join("\n  ", diffs));
        }

        private static LiveReport ReportOf(object o)
        {
            var r = new LiveReport();
            r.roundId = PnJson.Int(o, "roundId", 0);
            r.kind = PnJson.Str(o, "kind", "") == "Early" ? ReportKind.Early : ReportKind.Shot;
            r.reactionNs = (long)PnJson.Num(o, "reactionNs", 0);
            r.cuePresentedNs = (long)PnJson.Num(o, "cuePresentedNs", 0);
            r.inputNs = (long)PnJson.Num(o, "inputNs", 0);
            r.countdownStartNs = (long)PnJson.Num(o, "countdownStartNs", 0);
            r.rttMs = PnJson.Num(o, "rttMs", double.NaN);
            return r;
        }

        /// <summary>The snapshot of live_reference.py `snapshot` / live.test.ts `liveSnapshot` (without the soft flags).</summary>
        private static Dictionary<string, object> Snapshot(LiveMatchState s, LiveVerdict v)
        {
            var d = new Dictionary<string, object>();
            d["verdict"] = v.ToString();
            var ev = new List<object>();
            foreach (var e in s.Out) ev.Add(LiveMatchState.ToWire(e));
            d["events"] = ev;
            d["phase"] = LiveMatchState.ToWire(s.Phase);
            d["attempt"] = (double)s.Attempt;
            d["deadlineMs"] = (double)s.DeadlineMs;
            d["ready"] = new List<object> { (double)s.ReadyFor[0], (double)s.ReadyFor[1] };
            var r = s.Round;
            d["roundId"] = (double)(r != null ? r.roundId : 0);
            d["cueDueAtMs"] = (double)(r != null ? r.cueDueAtMs : 0);
            d["cueSentAtMs"] = (double)(r != null ? r.cueSentAtMs : 0);
            d["hp"] = new List<object> { s.Hp[0], s.Hp[1] };
            d["roundWins"] = new List<object> { (double)s.RoundWins[0], (double)s.RoundWins[1] };
            var hist = new List<object>();
            foreach (var h in s.History)
            {
                var x = new Dictionary<string, object>();
                x["roundNumber"] = (double)h.roundNumber;
                x["kinds"] = new List<object> { AttemptKinds.ToWire(h.kinds[0]), AttemptKinds.ToWire(h.kinds[1]) };
                x["void"] = h.isVoid;
                x["tie"] = h.tie;
                x["winner"] = (double)h.winner;
                x["outcomes"] = new List<object> { h.outcomes[0].ToString(), h.outcomes[1].ToString() };
                x["reactionsNs"] = new List<object>
                {
                    h.reactionsNs[0] < 0 ? null : (object)(double)h.reactionsNs[0],
                    h.reactionsNs[1] < 0 ? null : (object)(double)h.reactionsNs[1]
                };
                x["damage"] = new List<object> { h.damage[0], h.damage[1] };
                x["grades"] = new List<object> { h.grades[0].ToString(), h.grades[1].ToString() };
                x["hp"] = new List<object> { h.hp[0], h.hp[1] };
                hist.Add(x);
            }
            d["history"] = hist;
            if (s.Result == null) d["result"] = null;
            else
            {
                var res = new Dictionary<string, object>();
                res["winner"] = (double)s.Result.winner;
                res["endReason"] = LiveMatchState.ToWire(s.Result.end);
                res["forfeitedBy"] = (double)s.Result.forfeitedBy;
                res["hp"] = new List<object> { s.Result.hp[0], s.Result.hp[1] };
                res["roundWins"] = new List<object> { (double)s.Result.roundWins[0], (double)s.Result.roundWins[1] };
                d["result"] = res;
            }
            return d;
        }

        /// <summary>Every key of the expected object (except the server-only "flags"), numbers to 1e-9 relative.</summary>
        private static void Compare(string path, object expected, object actual, List<string> diffs)
        {
            if (expected == null)
            {
                if (actual != null) diffs.Add(path + ": expected null, got " + actual);
                return;
            }
            if (expected is double)
            {
                double e = (double)expected;
                if (!(actual is double)) { diffs.Add(path + ": expected " + e + ", got " + (actual ?? "null")); return; }
                double a = (double)actual;
                if (!(Math.Abs(a - e) <= 1e-9 * Math.Max(1.0, Math.Abs(e)))) diffs.Add(path + ": expected " + e + ", got " + a);
                return;
            }
            var el = expected as List<object>;
            if (el != null)
            {
                var al = actual as List<object>;
                if (al == null || al.Count != el.Count) { diffs.Add(path + ": list length differs"); return; }
                for (int i = 0; i < el.Count; i++) Compare(path + "[" + i + "]", el[i], al[i], diffs);
                return;
            }
            var ed = expected as Dictionary<string, object>;
            if (ed != null)
            {
                var ad = actual as Dictionary<string, object>;
                if (ad == null) { diffs.Add(path + ": expected an object"); return; }
                foreach (var kv in ed)
                {
                    if (kv.Key == "flags") continue;
                    object av;
                    ad.TryGetValue(kv.Key, out av);
                    Compare(path + "." + kv.Key, kv.Value, av, diffs);
                }
                return;
            }
            if (!expected.Equals(actual)) diffs.Add(path + ": expected " + expected + ", got " + (actual ?? "null"));
        }
    }
}
