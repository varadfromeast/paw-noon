using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>
    /// The JSON the client sends to match-submit, checked by hand against
    /// supabase/functions/_shared/schemas/match-submit.request.schema.json + recording.schema.json (structure,
    /// types, enums, ranges) and the README's field rules. The recording is built from real DuelClient
    /// ShotReports seen on a TapTransport, exactly like in the game.
    /// </summary>
    public class RecordingBuilderTests
    {
        private const string MatchId = "9b2f6c1e-3a57-4d2e-9f0a-2c4b8e7d1a10";
        private const string SubmittedAt = "2026-10-20T10:04:12.000Z";
        private const long Lat = 8333333L;

        private DuelConfig cfg;
        private TapTransport net;
        private DuelClient me;
        private RecordingBuilder rec;
        private int roundId;

        [SetUp]
        public void SetUp()
        {
            cfg = DuelConfig.Default();
            net = new TapTransport(new LoopbackTransport());
            me = new DuelClient(0, net, cfg);
            rec = new RecordingBuilder(0, cfg);
            rec.Character = "whiskers";
            rec.Gun = "standard";
            rec.DeviceModel = "iPhone15,2";
            rec.DeviceOs = "iOS 18.1";
            rec.RefreshHz = 120;
            rec.ClientBuild = "1.0.0+42";
            rec.Begin(1, me);
            net.Tap = rec;
            roundId = 0;
        }

        // ------------------------------------------------------------------ drive the real client path

        /// <summary>The authority's RoundStart, received by our client at <paramref name="startNs"/> ("3" appears).</summary>
        private void Countdown(int roundNumber, long startNs)
        {
            var m = new RoundStartMsg { roundId = ++roundId, roundNumber = roundNumber, countdownBeats = cfg.countdownBeats, beatNs = cfg.BeatNs };
            net.BroadcastRoundStart(m);
            RoundStartMsg got;
            Assert.IsTrue(me.PollRoundStart(startNs, out got));
        }

        private void Cue(long frameNs)
        {
            net.BroadcastCue(new DrawCue { roundId = roundId, cueSeq = 1 });
            Assert.IsTrue(me.PollCue(frameNs, Lat));
        }

        private void Drain()
        {
            ShotReport r;
            while (net.TryReceiveReport(out r)) { }
        }

        /// <summary>shot, early (void, round repeats), shot, no-shot, anticipation: one attempt of each kind.</summary>
        private void PlayFiveAttempts()
        {
            Countdown(1, 5000000000L);
            Cue(8846005752L);
            Assert.AreEqual(PressResult.Shot, me.Press(me.CuePresentedNs + 231456789L, InputSource.Touch, false));

            Countdown(2, 15854339085L);
            Assert.AreEqual(PressResult.Early, me.Press(18804339085L, InputSource.Touch, false));
            me.Reset();   // both fouled: the director resets the clients, round 2 replays

            Countdown(2, 21000000000L);
            Cue(24400000000L);
            Assert.AreEqual(PressResult.Shot, me.Press(me.CuePresentedNs + 264000000L, InputSource.Mouse, true));

            Countdown(3, 30000000000L);
            Cue(33500000000L);
            me.Tick(33500000000L + Lat + cfg.NoShotTimeoutNs + 1);
            Assert.IsTrue(me.Closed);

            Countdown(4, 40000000000L);
            Cue(42500000000L);
            Assert.AreEqual(PressResult.Shot, me.Press(me.CuePresentedNs + 80000000L, InputSource.Touch, false));
            rec.Finish();
            Drain();
        }

        private static Dictionary<string, object> O(object node, string path)
        {
            var d = node as Dictionary<string, object>;
            Assert.IsNotNull(d, path + " must be an object");
            return d;
        }

        private static List<object> A(object node, string path)
        {
            var l = node as List<object>;
            Assert.IsNotNull(l, path + " must be an array");
            return l;
        }

        private static void Keys(Dictionary<string, object> o, string path, string[] required, string[] optional)
        {
            foreach (var k in required) Assert.IsTrue(o.ContainsKey(k), path + "." + k + " is required");
            var allowed = new HashSet<string>(required);
            if (optional != null) foreach (var k in optional) allowed.Add(k);
            foreach (var k in o.Keys) Assert.IsTrue(allowed.Contains(k), path + "." + k + " is not in the schema");
        }

        private static long Int(Dictionary<string, object> o, string k, string path)
        {
            Assert.IsInstanceOf<double>(o[k], path + "." + k + " must be a number");
            double d = (double)o[k];
            Assert.AreEqual(Math.Floor(d), d, path + "." + k + " must be an integer");
            Assert.LessOrEqual(Math.Abs(d), 9007199254740991.0, path + "." + k + " must be a safe integer");
            return (long)d;
        }

        private static string Str(Dictionary<string, object> o, string k, string path, int min, int max)
        {
            Assert.IsInstanceOf<string>(o[k], path + "." + k + " must be a string");
            var s = (string)o[k];
            Assert.GreaterOrEqual(s.Length, min, path + "." + k + " too short");
            Assert.LessOrEqual(s.Length, max, path + "." + k + " too long");
            return s;
        }

        private static void OneOf(Dictionary<string, object> o, string k, string path, params string[] values)
        {
            Assert.IsInstanceOf<string>(o[k], path + "." + k + " must be a string");
            CollectionAssert.Contains(values, (string)o[k], path + "." + k + " not in the enum");
        }

        // ------------------------------------------------------------------ tests

        [Test]
        public void SubmitBodyMatchesTheSchemas()
        {
            PlayFiveAttempts();
            string json = rec.SubmitBody(MatchId, SubmittedAt);

            // integers are written as plain integers (the server requires safe integers, not 2.31E+08)
            Assert.IsFalse(Regex.IsMatch(json, "Ns\":-?[0-9]+[.eE]"), "ns fields are written as integers: " + json);

            var root = O(PnJson.Parse(json), "body");
            // match-submit.request.schema.json
            Keys(root, "body", new[] { "matchId", "recording" }, null);
            Assert.AreEqual(MatchId, Str(root, "matchId", "body", 36, 36));

            // recording.schema.json
            var r = O(root["recording"], "recording");
            Keys(r, "recording", new[] { "version", "seed", "difficulty", "character", "loadout", "rounds", "device", "clientBuild", "submittedAt" }, null);
            Assert.AreEqual(1L, Int(r, "version", "recording"), "version const 1");
            long seed = Int(r, "seed", "recording");
            Assert.That(seed, Is.InRange(-2147483648L, 2147483647L));
            Assert.AreEqual(1L, seed);
            OneOf(r, "difficulty", "recording", "hard");
            OneOf(r, "character", "recording", "whiskers", "pawtruth", "blackpaw");   // blackpaw: legacy alias (review pass 2)
            var loadout = O(r["loadout"], "recording.loadout");
            Keys(loadout, "recording.loadout", new[] { "gun" }, null);
            OneOf(loadout, "gun", "recording.loadout", "standard", "peacemaker");
            var device = O(r["device"], "recording.device");
            Keys(device, "recording.device", new[] { "model", "os", "refreshHz" }, null);
            Assert.AreEqual("iPhone15,2", Str(device, "model", "recording.device", 1, 128));
            Str(device, "os", "recording.device", 1, 128);
            Assert.IsInstanceOf<double>(device["refreshHz"]);
            Assert.AreEqual(120.0, (double)device["refreshHz"]);
            Assert.AreEqual("1.0.0+42", Str(r, "clientBuild", "recording", 1, 64));
            string at = Str(r, "submittedAt", "recording", 1, 64);
            DateTime parsed;
            Assert.IsTrue(DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed), "submittedAt is ISO-8601");

            var rounds = A(r["rounds"], "recording.rounds");
            Assert.That(rounds.Count, Is.InRange(1, 32));
            Assert.AreEqual(5, rounds.Count, "one entry per countdown attempt, the void one included");
            string[] kinds = { "shot", "early", "shot", "noshot", "anticipation" };
            int[] numbers = { 1, 2, 2, 3, 4 };
            for (int i = 0; i < rounds.Count; i++)
            {
                string p = "recording.rounds[" + i + "]";
                var o = O(rounds[i], p);
                Keys(o, p, new[] { "roundNumber", "kind", "reactionNs", "cuePresentedNs", "inputNs", "inputSource", "frameQuantized" },
                    new[] { "countdownStartNs", "samples" });
                Assert.GreaterOrEqual(Int(o, "roundNumber", p), 1L);
                Assert.AreEqual((long)numbers[i], Int(o, "roundNumber", p), p + ".roundNumber");
                OneOf(o, "kind", p, "shot", "anticipation", "early", "noshot");
                Assert.AreEqual(kinds[i], (string)o["kind"], p + ".kind");
                long reaction = Int(o, "reactionNs", p);
                long cue = Int(o, "cuePresentedNs", p);
                long input = Int(o, "inputNs", p);
                Assert.IsTrue(o.ContainsKey("countdownStartNs"), p + ".countdownStartNs is recommended and always sent");
                long start = Int(o, "countdownStartNs", p);
                OneOf(o, "inputSource", p, "unknown", "touch", "mouse", "keyboard", "bot", "injected", "remote");
                Assert.IsInstanceOf<bool>(o["frameQuantized"], p + ".frameQuantized must be a boolean");

                // README field rules (validate_recording.ts)
                switch (kinds[i])
                {
                    case "shot":
                    case "anticipation":
                        Assert.Greater(cue, 0L);
                        Assert.AreEqual(input - cue, reaction, p + ": reactionNs = inputNs - cuePresentedNs");
                        Assert.That(reaction, Is.InRange(0L, cfg.NoShotTimeoutNs));
                        Assert.AreEqual(kinds[i] == "anticipation", reaction < cfg.AnticipationNs, p + ": anticipation exactly under 100 ms");
                        break;
                    case "early":
                        Assert.Greater(input, 0L);
                        Assert.AreEqual(0L, cue, p + ": no cue was shown yet");
                        Assert.AreEqual(0L, reaction);
                        break;
                    case "noshot":
                        Assert.Greater(cue, 0L, p + ": a no-shot carries the presented cue");
                        Assert.AreEqual(0L, input);
                        Assert.AreEqual(0L, reaction);
                        break;
                }
                // timing: "3" before the cue and the press
                if (cue > 0) Assert.Greater(cue, start);
                if (input > 0) Assert.GreaterOrEqual(input, start);
            }

            // the values are the real reports
            var r0 = O(rounds[0], "r0");
            Assert.AreEqual(231456789L, Int(r0, "reactionNs", "r0"));
            Assert.AreEqual(8846005752L + Lat, Int(r0, "cuePresentedNs", "r0"));
            Assert.AreEqual(5000000000L, Int(r0, "countdownStartNs", "r0"));
            Assert.AreEqual("touch", (string)r0["inputSource"]);
            var r2 = O(rounds[2], "r2");
            Assert.AreEqual("mouse", (string)r2["inputSource"]);
            Assert.AreEqual(true, (bool)r2["frameQuantized"]);
            var r1 = O(rounds[1], "r1");
            Assert.AreEqual(18804339085L, Int(r1, "inputNs", "r1"));
            Assert.AreEqual(15854339085L, Int(r1, "countdownStartNs", "r1"));
        }

        [Test]
        public void TimestampsAreMonotonicAcrossAttempts()
        {
            PlayFiveAttempts();
            long last = long.MinValue;
            for (int i = 0; i < rec.Count; i++)
            {
                var a = rec[i];
                long first = a.countdownStartNs;
                Assert.Greater(first, last, "attempt " + i + " starts after everything in the previous one");
                last = Math.Max(first, Math.Max(a.cuePresentedNs, a.inputNs));
            }
        }

        [Test]
        public void GhostAttemptsCarryTheEarlyInputOffset()
        {
            PlayFiveAttempts();
            var g = rec.ToGhostAttempts();
            Assert.AreEqual(5, g.Length);
            Assert.AreEqual(AttemptKind.Shot, g[0].kind);
            Assert.AreEqual(231456789L, g[0].reactionNs);
            Assert.AreEqual(AttemptKind.Early, g[1].kind);
            Assert.AreEqual(18804339085L - 15854339085L, g[1].inputOffsetNs, "inputNs - countdownStartNs (server playableAttempts)");
            Assert.AreEqual(0L, g[1].reactionNs);
            Assert.AreEqual(AttemptKind.NoShot, g[3].kind);
            Assert.AreEqual(0L, g[3].reactionNs);
            Assert.AreEqual(AttemptKind.Anticipation, g[4].kind);
            Assert.AreEqual(80000000L, g[4].reactionNs);
        }

        [Test]
        public void OnlyTheLocalSlotIsRecorded()
        {
            Countdown(1, 5000000000L);
            Cue(8000000000L);
            // the opponent's report on the same transport must not leak into our recording
            var other = new ShotReport { roundId = roundId, slot = 1, kind = ReportKind.Shot, reactionNs = 200000000L, cuePresentedNs = 8000000000L + Lat, inputNs = 8200000000L + Lat, inputSource = InputSource.Remote };
            net.SendReport(other);
            me.Press(me.CuePresentedNs + 300000000L, InputSource.Touch, false);
            rec.Finish();
            Assert.AreEqual(1, rec.Count);
            Assert.AreEqual(300000000L, rec[0].reactionNs);
            Assert.AreEqual(InputSource.Touch, rec[0].inputSource);
        }

        [Test]
        public void NothingIsRecordedOutsideBeginFinish()
        {
            rec.Finish();
            int before = rec.Count;
            Countdown(1, 5000000000L);
            Cue(8000000000L);
            me.Press(me.CuePresentedNs + 300000000L, InputSource.Touch, false);
            Assert.AreEqual(before, rec.Count);
            Assert.IsFalse(rec.Recording);
        }

        [Test]
        public void StringsAreEscaped()
        {
            rec.DeviceModel = "Pixel \"8\" \\ Pro\n";
            PlayFiveAttempts();
            var root = PnJson.ParseObject(rec.ToJson(SubmittedAt));
            Assert.IsNotNull(root, "still valid JSON");
            Assert.AreEqual("Pixel \"8\" \\ Pro\n", PnJson.Str(PnJson.Obj(root, "device"), "model", null));
        }

        [Test]
        public void ParsesTheReadmeMatchStartAnswer()
        {
            const string json = "{\"matchId\":\"9b2f6c1e-3a57-4d2e-9f0a-2c4b8e7d1a10\",\"mode\":\"casual\",\"season\":null,\"difficulty\":\"hard\"," +
                "\"seed\":1,\"rounds\":3,\"maxAttempts\":9,\"gunsNormalised\":false,\"issuedAt\":\"2026-10-20T10:00:00.000Z\"," +
                "\"deadlineAt\":\"2026-10-20T10:04:33.000Z\",\"you\":{\"mmr\":1200,\"rd\":350},\"opponent\":{" +
                "\"recordingId\":\"00000000-0000-4000-8000-000000000002\",\"displayName\":\"Marshal Mittens\",\"character\":\"blackpaw\"," +
                "\"loadout\":{\"gun\":\"standard\"},\"mmr\":1200,\"isHouse\":true,\"rounds\":[" +
                "{\"roundNumber\":1,\"kind\":\"shot\",\"reactionNs\":268400000}," +
                "{\"roundNumber\":2,\"kind\":\"early\",\"reactionNs\":0,\"inputOffsetNs\":2950000000}," +
                "{\"roundNumber\":2,\"kind\":\"noshot\",\"reactionNs\":0}," +
                "{\"roundNumber\":3,\"kind\":\"anticipation\",\"reactionNs\":91000000}]}}";
            string err;
            var m = MatchStartInfo.Parse(json, out err);
            Assert.IsNotNull(m, err);
            Assert.AreEqual("9b2f6c1e-3a57-4d2e-9f0a-2c4b8e7d1a10", m.MatchId);
            Assert.AreEqual(1, m.Seed);
            Assert.AreEqual(3, m.Rounds);
            Assert.AreEqual(9, m.MaxAttempts);
            Assert.AreEqual("Marshal Mittens", m.OpponentName);
            Assert.AreEqual("pawtruth", m.OpponentCharacter, "the legacy server id blackpaw is normalised (review pass 2)");
            Assert.AreEqual(1200, m.OpponentMmr);
            Assert.IsTrue(m.OpponentIsHouse);
            Assert.AreEqual(4, m.OpponentAttempts.Length);
            Assert.AreEqual(AttemptKind.Shot, m.OpponentAttempts[0].kind);
            Assert.AreEqual(268400000L, m.OpponentAttempts[0].reactionNs);
            Assert.AreEqual(AttemptKind.Early, m.OpponentAttempts[1].kind);
            Assert.AreEqual(2950000000L, m.OpponentAttempts[1].inputOffsetNs);
            Assert.AreEqual(AttemptKind.NoShot, m.OpponentAttempts[2].kind);
            Assert.AreEqual(-1L, m.OpponentAttempts[2].inputOffsetNs);
            Assert.AreEqual(AttemptKind.Anticipation, m.OpponentAttempts[3].kind);

            Assert.IsNull(MatchStartInfo.Parse("{\"matchId\":\"x\",\"seed\":1.5,\"opponent\":{\"rounds\":[]}}", out err));
            Assert.IsNull(MatchStartInfo.Parse("not json", out err));
            Assert.IsNull(MatchStartInfo.Parse("{\"matchId\":\"x\",\"seed\":1,\"opponent\":{\"rounds\":[{\"kind\":\"dance\"}]}}", out err));
        }

        [Test]
        public void ParsesTheReadmeVerdict()
        {
            const string json = "{\"matchId\":\"9b2f\",\"status\":\"completed\",\"mode\":\"ranked\",\"winner\":\"opponent\",\"endReason\":\"rounds\"," +
                "\"hp\":{\"you\":18.044,\"opponent\":39.45481467999999},\"roundWins\":{\"you\":1,\"opponent\":2},\"rounds\":[]," +
                "\"rating\":{\"season\":1,\"before\":1200,\"after\":1162,\"delta\":-38,\"rd\":290},\"recordingId\":null}";
            string err;
            var v = MatchVerdict.Parse(json, out err);
            Assert.IsNotNull(v, err);
            Assert.AreEqual(1, v.WinnerSlot);
            Assert.AreEqual("rounds", v.EndReason);
            Assert.AreEqual(18.044, v.HpYou, 1e-12);
            Assert.AreEqual(2, v.RoundWinsOpponent);
            Assert.IsTrue(v.HasRating);
            Assert.AreEqual(-38, v.RatingDelta);
            Assert.AreEqual(1162, v.RatingAfter);

            var e = ApiError.Parse("{\"error\":\"recording_rejected\",\"message\":\"late\",\"reason\":\"late\",\"matchStatus\":\"void\",\"flagged\":false}");
            Assert.AreEqual("recording_rejected", e.code);
            Assert.AreEqual("late", e.reason);
        }
    }
}
