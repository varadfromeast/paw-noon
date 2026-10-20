using System.Collections.Generic;
using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>
    /// GhostOpponent replays a recording through the same DuelClient / transport / authority path as the bot,
    /// attempt k against attempt k. The harness runs frames in DuelDirector's order (opponent pre-tick,
    /// authority, opponent, StartCountdown, player, authority) and drives the match with RecordedMatchRules,
    /// then checks the outcome against RecordedMatchSim (the C# twin of the server's recompute).
    /// </summary>
    public class GhostOpponentTests
    {
        private const long FrameNs = 8333333L;      // 120 Hz
        private const long LatencyNs = 8333333L;    // one frame from frame start to photons
        private const long StandoffNs = 1000000000L;

        private static long Ms(double ms) { return DuelConfig.MsToNs(ms); }

        /// <summary>What the local player does in one attempt.</summary>
        private struct Plan
        {
            public AttemptKind kind;
            public long reactionNs;   // shot / anticipation
            public long earlyNs;      // early: after "3"
        }

        private static Plan PShot(double ms) { return new Plan { kind = AttemptKind.Shot, reactionNs = Ms(ms) }; }
        private static Plan PEarly(double afterThreeMs) { return new Plan { kind = AttemptKind.Early, earlyNs = Ms(afterThreeMs) }; }
        private static Plan PNone() { return new Plan { kind = AttemptKind.NoShot }; }

        /// <summary>Records every report the ghost (slot 1) sends, with the attempt it belongs to.</summary>
        private sealed class SlotTap : IDuelTap
        {
            public readonly List<ShotReport> Ghost = new List<ShotReport>();
            public readonly List<RoundStartMsg> Starts = new List<RoundStartMsg>();
            public IDuelTap Next;
            public void OnRoundStart(RoundStartMsg m) { Starts.Add(m); if (Next != null) Next.OnRoundStart(m); }
            public void OnReport(ShotReport r) { if (r.slot == 1) Ghost.Add(r); if (Next != null) Next.OnReport(r); }
        }

        private sealed class Harness
        {
            public readonly DuelConfig Cfg = DuelConfig.Default();
            public readonly ManualClock Clock = new ManualClock { Now = 5000000000L };
            public readonly TapTransport Net = new TapTransport(new LoopbackTransport());
            public readonly LocalAuthority Auth;
            public readonly DuelClient Me;
            public readonly GhostOpponent Ghost;
            public readonly RecordingBuilder Rec;
            public readonly RecordedMatchRules Rules;
            public readonly SlotTap Tap = new SlotTap();
            public readonly int Seed;
            public readonly GhostAttempt[] Recording;

            // per attempt (index = countdown k)
            public readonly List<long> CueDecided = new List<long>();
            public readonly List<long> GhostCuePresented = new List<long>();
            public readonly List<long> GhostCountdownStart = new List<long>();
            public readonly List<SlotOutcome> GhostOutcome = new List<SlotOutcome>();
            public readonly List<SlotOutcome> MeOutcome = new List<SlotOutcome>();
            public readonly List<int> RoundNumbers = new List<int>();
            public readonly List<bool> Void = new List<bool>();

            /// <summary>Optional: one long frame (app in background) of this length right after the cue of attempt LongFrameAttempt.</summary>
            public long LongFrameNs;
            public int LongFrameAttempt = -1;

            public Harness(int seed, GhostAttempt[] recording)
            {
                Seed = seed;
                Recording = recording;
                Auth = new LocalAuthority(Cfg, Clock, Net, seed);
                Me = new DuelClient(0, Net, Cfg);
                Ghost = new GhostOpponent(1, Net, Cfg);
                Ghost.Load(recording);
                Rec = new RecordingBuilder(0, Cfg);
                Rules = new RecordedMatchRules(3, 9, recording.Length);
                Tap.Next = Rec;
                Net.Tap = Tap;
            }

            public void Play(Plan[] plans)
            {
                Auth.StartMatch();
                Me.Reset();
                Ghost.StartMatch(Seed);
                Rules.Reset();
                Rec.Begin(Seed, Me);
                int k = 0;
                while (true)
                {
                    if (Rules.Check(Auth.Match) != RecordedEnd.None) break;
                    Assert.Less(k, plans.Length, "the player plan ran out: the match should have ended");
                    PlayAttempt(plans[k], k);
                    k++;
                    if (Auth.Phase == RoundPhase.Result && !Auth.Match.Over) Auth.NextRound();
                    Clock.Now += StandoffNs;
                }
                if (!Auth.Match.Over) Auth.Match.Finish(RecordedMatchRules.Winner(Auth.Match));
                Rec.Finish();
            }

            private void PlayAttempt(Plan plan, int k)
            {
                bool started = false;
                bool longDone = false;
                long pressAt = -1;
                for (int guard = 0; guard < 4000; guard++)
                {
                    long f = Clock.Now;
                    // --- DuelDirector.Tick order
                    Ghost.PreAuthorityTick(f);
                    Auth.Tick();
                    Ghost.SetPlayerRoundWins(Auth.Match.RoundWins[0]);
                    Ghost.Tick(f, LatencyNs);
                    if (!started)
                    {
                        Assert.IsTrue(Auth.StartCountdown(), "StartCountdown");
                        Rules.AttemptStarted();
                        started = true;
                        RoundNumbers.Add(Auth.Round.roundNumber);
                    }
                    RoundStartMsg rs;
                    if (Me.PollRoundStart(f, out rs) && plan.kind == AttemptKind.Early) pressAt = Me.CountdownStartNs + plan.earlyNs;
                    if (Me.PollCue(f, LatencyNs) && (plan.kind == AttemptKind.Shot || plan.kind == AttemptKind.Anticipation))
                        pressAt = Me.CuePresentedNs + plan.reactionNs;
                    if (pressAt >= 0 && f >= pressAt)
                    {
                        Me.Press(pressAt, InputSource.Touch, false);
                        pressAt = -1;
                    }
                    Me.Tick(f);
                    Auth.Tick();
                    DuelEvent e;
                    bool over = false;
                    while (Auth.TryDequeueEvent(out e))
                    {
                        if (e.kind == DuelEventKind.Cue)
                        {
                            CueDecided.Add(Auth.Round.cueDecidedNs);
                        }
                        else if (e.kind == DuelEventKind.RoundResolved) { Rules.AttemptResolved(); over = true; }
                        else if (e.kind == DuelEventKind.RoundVoid)
                        {
                            Me.Reset();
                            Ghost.Client.Reset();
                            over = true;
                        }
                    }
                    // the ghost polls a countdown one frame after the player (DuelDirector order): match it by round id
                    bool ghostHere = Ghost.Client.RoundId == Auth.Round.roundId;
                    if (ghostHere && Ghost.Client.HasCue && GhostCuePresented.Count == k) GhostCuePresented.Add(Ghost.Client.CuePresentedNs);
                    if (ghostHere && GhostCountdownStart.Count == k) GhostCountdownStart.Add(Ghost.Client.CountdownStartNs);
                    if (over)
                    {
                        Void.Add(Auth.Round.isVoid);
                        GhostOutcome.Add(Auth.Round.slots[1].outcome);
                        MeOutcome.Add(Auth.Round.slots[0].outcome);
                        while (GhostCuePresented.Count <= k) GhostCuePresented.Add(0);
                        while (GhostCountdownStart.Count <= k) GhostCountdownStart.Add(-1);
                        return;
                    }
                    if (k == LongFrameAttempt && !longDone && ghostHere && Ghost.Client.HasCue)
                    {
                        longDone = true;
                        Clock.Now += LongFrameNs;
                    }
                    else Clock.Now += FrameNs;
                }
                Assert.Fail("attempt " + k + " never resolved");
            }

            public RecordedMatchResult Server()
            {
                return RecordedMatchSim.Play(Cfg, Seed, 3, 9, Rec.ToGhostAttempts(), Recording);
            }

            /// <summary>The live match (this harness) must end exactly like the server's recompute.</summary>
            public void AssertMatchesServer()
            {
                var s = Server();
                Assert.AreEqual(s.EndReason, Rules.EndReason, "end reason");
                Assert.AreEqual(s.AttemptsUsed, Rules.Attempts, "attempts");
                Assert.AreEqual(s.AttemptsUsed, Rec.Count, "recording stops exactly where the match ended");
                Assert.AreEqual(s.ResolvedRounds, Rules.Resolved, "resolved rounds");
                Assert.AreEqual(s.Winner, Auth.Match.Winner, "winner");
                Assert.AreEqual(s.Hp[0], Auth.Match.Hp[0], 1e-9, "hp you");
                Assert.AreEqual(s.Hp[1], Auth.Match.Hp[1], 1e-9, "hp ghost");
                Assert.AreEqual(s.RoundWins[0], Auth.Match.RoundWins[0], "round wins you");
                Assert.AreEqual(s.RoundWins[1], Auth.Match.RoundWins[1], "round wins ghost");
                for (int i = 0; i < s.RoundNumbers.Count; i++)
                {
                    Assert.AreEqual(s.RoundNumbers[i], RoundNumbers[i], "round number of attempt " + i);
                    Assert.AreEqual(s.RoundNumbers[i], Rec[i].roundNumber, "recorded round number of attempt " + i);
                }
                Assert.AreEqual(0, Auth.RejectedReports, "no report rejected");
            }
        }

        // ------------------------------------------------------------------ tests

        [Test]
        public void ReplaysEachShotAtTheRecordedReactionAfterOurOwnCue()
        {
            var recording = new[]
            {
                GhostAttempt.Shot(Ms(250), 1),
                GhostAttempt.Shot(Ms(300.5), 2),
                GhostAttempt.Shot(231456789L, 3)
            };
            var h = new Harness(1, recording);
            h.Play(new[] { PShot(280), PShot(260), PShot(240) });

            Assert.AreEqual(3, h.Tap.Ghost.Count, "one report per attempt");
            for (int k = 0; k < 3; k++)
            {
                var r = h.Tap.Ghost[k];
                Assert.AreEqual(1, r.slot);
                Assert.AreEqual(ReportKind.Shot, r.kind);
                Assert.AreEqual(InputSource.Remote, r.inputSource);
                Assert.IsFalse(r.frameQuantized);
                Assert.AreEqual(h.Tap.Starts[k].roundId, r.roundId, "report belongs to countdown " + k);
                Assert.AreEqual(recording[k].reactionNs, r.reactionNs, "exact recorded reaction");
                // the ghost saw OUR cue: decided at the authority's frame, presented one frame later
                Assert.AreEqual(h.CueDecided[k] + LatencyNs, r.cuePresentedNs, "cue presented on our clock");
                Assert.AreEqual(r.cuePresentedNs + r.reactionNs, r.inputNs, "input = cue + reaction");
                Assert.AreEqual(SlotOutcome.Shot, h.GhostOutcome[k]);
            }
            Assert.AreEqual(RecordedEnd.Rounds, h.Rules.EndReason);
            h.AssertMatchesServer();
        }

        [Test]
        public void AttemptsAreMatchedByCountdownNotByRoundNumber()
        {
            // the ghost jumped the gun on its first countdown; so do we: void, round 1 replays and the ghost
            // plays its SECOND recorded attempt there
            var recording = new[]
            {
                GhostAttempt.Early(Ms(2500), 1),
                GhostAttempt.Shot(Ms(260), 1),
                GhostAttempt.Shot(Ms(280), 2),
                GhostAttempt.Shot(Ms(240), 3),
            };
            var h = new Harness(7, recording);
            h.Play(new[] { PEarly(2400), PShot(250), PShot(300), PShot(230) });

            Assert.IsTrue(h.Void[0], "both fouled: void");
            Assert.AreEqual(1, h.RoundNumbers[0]);
            Assert.AreEqual(1, h.RoundNumbers[1], "void replays the same round number");
            Assert.AreEqual(2, h.RoundNumbers[2]);
            Assert.AreEqual(ReportKind.Early, h.Tap.Ghost[0].kind);
            Assert.AreEqual(h.Tap.Starts[1].roundId, h.Tap.Ghost[1].roundId);
            Assert.AreEqual(Ms(260), h.Tap.Ghost[1].reactionNs, "attempt 2 (round 1 again) plays recorded attempt 2");
            Assert.AreEqual(Ms(280), h.Tap.Ghost[2].reactionNs);
            Assert.AreEqual(Ms(240), h.Tap.Ghost[3].reactionNs);
            Assert.AreEqual(4, h.Rules.Attempts);
            Assert.AreEqual(3, h.Rules.Resolved);
            Assert.AreEqual(RecordedEnd.Rounds, h.Rules.EndReason);
            Assert.AreEqual(4, h.Rec.Count, "the void attempt is in our recording too");
            Assert.AreEqual(AttemptKind.Early, h.Rec[0].kind);
            h.AssertMatchesServer();
        }

        [Test]
        public void EarlyFoulIsReplayedAtItsInputOffsetAfterThree()
        {
            long offset = Ms(2600);   // during the hold: countdown 2.1 s, hold >= 1.0 s
            var recording = new[] { GhostAttempt.Early(offset, 1), GhostAttempt.Shot(Ms(270), 2), GhostAttempt.Shot(Ms(290), 3) };
            var h = new Harness(42, recording);
            h.Play(new[] { PShot(300), PShot(250), PShot(260) });

            var r = h.Tap.Ghost[0];
            Assert.AreEqual(ReportKind.Early, r.kind);
            Assert.AreEqual(h.GhostCountdownStart[0] + offset, r.inputNs, "pressed exactly inputOffsetNs after its own \"3\"");
            Assert.AreEqual(0L, r.cuePresentedNs, "before any cue");
            Assert.AreEqual(0L, r.reactionNs);
            Assert.AreEqual(SlotOutcome.Foul, h.GhostOutcome[0]);
            Assert.AreEqual(SlotOutcome.Shot, h.MeOutcome[0], "our clean shot still lands on a fouled ghost");
            h.AssertMatchesServer();
        }

        [Test]
        public void EarlyWithoutOffsetUsesTheServerDefault()
        {
            var recording = new[] { GhostAttempt.Early(-1, 1), GhostAttempt.Shot(Ms(270), 2), GhostAttempt.Shot(Ms(290), 3) };
            var h = new Harness(3, recording);
            h.Play(new[] { PShot(300), PShot(250), PShot(260) });
            Assert.AreEqual(ReportKind.Early, h.Tap.Ghost[0].kind);
            Assert.AreEqual(h.GhostCountdownStart[0] + GhostOpponent.DefaultEarlyOffsetNs, h.Tap.Ghost[0].inputNs);
            Assert.AreEqual(GhostOpponent.DefaultEarlyOffsetNs, 1000000000L, "recorded_match.ts HARNESS.earlyAfterStartNs");
            h.AssertMatchesServer();
        }

        [Test]
        public void EarlyThatOurCueOvertakesIsStillAFoul()
        {
            // an offset past our cue (their frame was late): the press is put 1 ns before our presented cue
            var recording = new[] { GhostAttempt.Early(Ms(9000), 1), GhostAttempt.Shot(Ms(270), 2), GhostAttempt.Shot(Ms(290), 3) };
            var h = new Harness(5, recording);
            h.Play(new[] { PShot(300), PShot(250), PShot(260) });
            var r = h.Tap.Ghost[0];
            Assert.AreEqual(ReportKind.Early, r.kind);
            Assert.AreEqual(h.GhostCuePresented[0], r.cuePresentedNs);
            Assert.AreEqual(r.cuePresentedNs - 1, r.inputNs);
            Assert.AreEqual(-1L, r.reactionNs);
            Assert.AreEqual(SlotOutcome.Foul, h.GhostOutcome[0]);
            h.AssertMatchesServer();
        }

        [Test]
        public void NoShotStaysSilentAndTimesOut()
        {
            var recording = new[] { GhostAttempt.NoShot(1), GhostAttempt.Shot(Ms(270), 2), GhostAttempt.Shot(Ms(290), 3) };
            var h = new Harness(11, recording);
            h.Play(new[] { PNone(), PShot(250), PShot(400) });

            Assert.AreEqual(2, h.Tap.Ghost.Count, "no report for the no-shot attempt");
            Assert.AreEqual(h.Tap.Starts[1].roundId, h.Tap.Ghost[0].roundId);
            Assert.AreEqual(SlotOutcome.NoShot, h.GhostOutcome[0]);
            Assert.AreEqual(SlotOutcome.NoShot, h.MeOutcome[0]);
            Assert.AreEqual(AttemptKind.NoShot, h.Rec[0].kind);
            Assert.Greater(h.Rec[0].cuePresentedNs, 0L, "our no-shot carries the cue we saw");
            h.AssertMatchesServer();
        }

        [Test]
        public void AnticipationIsReplayedAsAShotTheAuthorityFouls()
        {
            var recording = new[] { GhostAttempt.Anticipation(Ms(80), 1), GhostAttempt.Shot(Ms(270), 2), GhostAttempt.Shot(Ms(290), 3) };
            var h = new Harness(13, recording);
            h.Play(new[] { PShot(300), PShot(250), PShot(260) });
            Assert.AreEqual(ReportKind.Shot, h.Tap.Ghost[0].kind);
            Assert.AreEqual(Ms(80), h.Tap.Ghost[0].reactionNs);
            Assert.AreEqual(SlotOutcome.Anticipation, h.GhostOutcome[0]);
            h.AssertMatchesServer();
        }

        [Test]
        public void RecordingRunsOutWhenWeVoidAnAttemptTheGhostDidNot()
        {
            // in the ghost's own match its first-attempt foul was not voided, so it needed only 3 attempts
            var recording = new[] { GhostAttempt.Early(Ms(2500), 1), GhostAttempt.Shot(Ms(260), 2), GhostAttempt.Shot(Ms(280), 3) };
            var h = new Harness(99, recording);
            h.Play(new[] { PEarly(2300), PShot(250), PShot(300), PShot(230) });
            Assert.IsTrue(h.Void[0]);
            Assert.AreEqual(3, h.Rules.Attempts);
            Assert.AreEqual(2, h.Rules.Resolved);
            Assert.AreEqual(RecordedEnd.OpponentExhausted, h.Rules.EndReason);
            Assert.IsTrue(h.Auth.Match.Over);
            h.AssertMatchesServer();
        }

        [Test]
        public void KnockoutEndsTheMatchEarly()
        {
            var recording = new[] { GhostAttempt.Shot(Ms(150), 1), GhostAttempt.Shot(Ms(150), 2), GhostAttempt.Shot(Ms(150), 3) };
            var h = new Harness(2, recording);
            var cfg = h.Cfg;
            cfg.maxHp = 60;   // two 40-damage hits
            h.Auth.Match.Reset(cfg.maxHp);
            h.Play(new[] { PNone(), PNone(), PNone() });
            Assert.AreEqual(RecordedEnd.Ko, h.Rules.EndReason);
            Assert.AreEqual(2, h.Rules.Attempts);
            Assert.AreEqual(1, h.Auth.Match.Winner);
            h.AssertMatchesServer();
        }

        [Test]
        public void ShotDueDuringALongFrameIsDeliveredBeforeTheAuthorityTimesOut()
        {
            // app backgrounded right after the cue (PvP never voids): on resume the ghost's shot must still count
            var recording = new[] { GhostAttempt.Shot(Ms(260), 1), GhostAttempt.Shot(Ms(270), 2), GhostAttempt.Shot(Ms(280), 3) };
            var h = new Harness(21, recording);
            h.LongFrameAttempt = 0;
            h.LongFrameNs = 5000000000L;
            h.Play(new[] { PNone(), PShot(250), PShot(250) });
            Assert.AreEqual(SlotOutcome.Shot, h.GhostOutcome[0], "not a no-shot");
            Assert.AreEqual(SlotOutcome.NoShot, h.MeOutcome[0]);
            Assert.AreEqual(Ms(260), h.Tap.Ghost[0].reactionNs);
            h.AssertMatchesServer();
        }

        [Test]
        public void BotStillWorksThroughTheOpponentSeam()
        {
            var cfg = DuelConfig.Default();
            IOpponent bot = new BotClient(1, new LoopbackTransport(), cfg, 3);
            Assert.IsTrue(bot.HostActing);
            Assert.IsNotNull(bot.Client);
            Assert.AreEqual(1, bot.Client.Slot);
            IOpponent ghost = new GhostOpponent(1, new LoopbackTransport(), cfg);
            Assert.IsFalse(ghost.HostActing, "no host-staged feints, tells or banter for a recorded player");
        }

        [Test]
        public void RulesCheckOrderMatchesTheServer()
        {
            var m = new MatchState(100);
            var r = new RecordedMatchRules(3, 9, 2);
            Assert.AreEqual(RecordedEnd.None, r.Check(m));
            r.AttemptStarted(); r.AttemptResolved();
            r.AttemptStarted(); r.AttemptResolved();
            Assert.AreEqual(RecordedEnd.OpponentExhausted, r.Check(m));
            r.AttemptStarted(); r.AttemptResolved();
            Assert.AreEqual(RecordedEnd.Rounds, r.Check(m), "rounds is checked before exhaustion");
            m.Hp[1] = 0;
            m.Finish(0);
            Assert.AreEqual(RecordedEnd.Ko, r.Check(m), "KO first");
            var m2 = new MatchState(100);
            m2.Hp[0] = 40; m2.Hp[1] = 40;
            Assert.AreEqual(-1, RecordedMatchRules.Winner(m2), "equal HP = draw");
            m2.Hp[1] = 39.5;
            Assert.AreEqual(0, RecordedMatchRules.Winner(m2));
        }
    }
}
