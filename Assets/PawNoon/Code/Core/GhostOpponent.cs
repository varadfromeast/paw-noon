using System;
using System.Collections.Generic;

namespace PawNoon.Core
{
    /// <summary>One countdown attempt as recorded (SPEC_V2 §4.2 <c>kind</c>). Wire names: shot, anticipation, early, noshot.</summary>
    public enum AttemptKind
    {
        Shot = 0,
        /// <summary>Pressed after the cue but under anticipationMs (a foul the authority derives from the reaction).</summary>
        Anticipation = 1,
        /// <summary>Pressed after "3" and before the cue was presented (TOO EARLY).</summary>
        Early = 2,
        /// <summary>No press within the no-shot timeout.</summary>
        NoShot = 3
    }

    public static class AttemptKinds
    {
        public static string ToWire(AttemptKind k)
        {
            switch (k)
            {
                case AttemptKind.Anticipation: return "anticipation";
                case AttemptKind.Early: return "early";
                case AttemptKind.NoShot: return "noshot";
                default: return "shot";
            }
        }

        public static bool TryParse(string s, out AttemptKind k)
        {
            switch (s)
            {
                case "shot": k = AttemptKind.Shot; return true;
                case "anticipation": k = AttemptKind.Anticipation; return true;
                case "early": k = AttemptKind.Early; return true;
                case "noshot": k = AttemptKind.NoShot; return true;
                default: k = AttemptKind.NoShot; return false;
            }
        }
    }

    /// <summary>
    /// What an opponent needs to replay one attempt of a recording (server <c>PlayableAttempt</c>,
    /// schemas/attempt.schema.json).
    /// </summary>
    public struct GhostAttempt
    {
        /// <summary>The recording owner's own round number (informational; attempts are matched by index).</summary>
        public int roundNumber;
        public AttemptKind kind;
        /// <summary>Shot / anticipation: the reaction to replay after OUR presented cue. 0 otherwise.</summary>
        public long reactionNs;
        /// <summary>Early only: when they jumped the gun, after "3". -1 when unknown.</summary>
        public long inputOffsetNs;

        public static GhostAttempt Shot(long reactionNs, int roundNumber = 0)
        {
            var a = new GhostAttempt();
            a.roundNumber = roundNumber;
            a.kind = AttemptKind.Shot;
            a.reactionNs = reactionNs;
            a.inputOffsetNs = -1;
            return a;
        }

        public static GhostAttempt Anticipation(long reactionNs, int roundNumber = 0)
        {
            var a = Shot(reactionNs, roundNumber);
            a.kind = AttemptKind.Anticipation;
            return a;
        }

        /// <summary>Early foul; <paramref name="inputOffsetNs"/> &lt; 0 = unknown (replayed at the server default).</summary>
        public static GhostAttempt Early(long inputOffsetNs, int roundNumber = 0)
        {
            var a = new GhostAttempt();
            a.roundNumber = roundNumber;
            a.kind = AttemptKind.Early;
            a.inputOffsetNs = inputOffsetNs;
            return a;
        }

        public static GhostAttempt NoShot(int roundNumber = 0)
        {
            var a = new GhostAttempt();
            a.roundNumber = roundNumber;
            a.kind = AttemptKind.NoShot;
            a.inputOffsetNs = -1;
            return a;
        }
    }

    /// <summary>
    /// Replays a recorded player ("ghost", SPEC_V2 §4.0) through exactly the transport path the bot uses:
    /// its own DuelClient receives RoundStart / DrawCue and presses, the authority validates the ShotReport.
    /// Attempt k of the match (the k-th countdown, void replays included) plays recording attempt k:
    ///  - shot / anticipation: press at OUR presented cue + recorded reaction (never before the cue arrives);
    ///  - early: press at "3" + inputOffsetNs (server default 1 s when unknown), always classified TOO EARLY
    ///    (if the cue arrives first, the press is placed 1 ns before the presented cue);
    ///  - noshot: stays silent, the authority times it out.
    /// It never knows the cue time in advance. Engine-free, no per-frame allocation.
    /// </summary>
    public sealed class GhostOpponent : IOpponent
    {
        /// <summary>Server harness: an early foul without inputOffsetNs lands 1 s after "3" (recorded_match.ts HARNESS).</summary>
        public const long DefaultEarlyOffsetNs = 1000000000L;

        private readonly DuelClient client;
        private GhostAttempt[] attempts = new GhostAttempt[0];
        private int started;
        private int current = -1;
        private bool shotPlanned;
        private long shotAtNs;
        private bool foulPlanned;
        private long foulAtNs;

        public GhostOpponent(int slot, IDuelTransport transport, DuelConfig cfg)
        {
            client = new DuelClient(slot, transport, cfg ?? new DuelConfig());
        }

        public DuelClient Client { get { return client; } }
        public bool FoulPlanned { get { return foulPlanned; } }
        public bool HostActing { get { return false; } }

        /// <summary>Attempts in the loaded recording.</summary>
        public int AttemptCount { get { return attempts.Length; } }
        /// <summary>Countdowns received this match (= index of the next attempt).</summary>
        public int AttemptsStarted { get { return started; } }
        /// <summary>Index of the attempt being played, -1 before the first countdown.</summary>
        public int CurrentAttempt { get { return current; } }
        public GhostAttempt Attempt(int k) { return attempts[k]; }

        /// <summary>Loads a recording (copied; allocation happens here only). Rewinds.</summary>
        public void Load(IList<GhostAttempt> list)
        {
            int n = list != null ? list.Count : 0;
            attempts = new GhostAttempt[n];
            for (int i = 0; i < n; i++) attempts[i] = list[i];
            Rewind();
        }

        public void Rewind()
        {
            started = 0;
            current = -1;
            shotPlanned = false;
            foulPlanned = false;
        }

        public void StartMatch(int seed) { Rewind(); }

        public void SetPlayerRoundWins(int wins) { }

        public void PreAuthorityTick(long frameStartNs) { FireDue(frameStartNs); }

        public void Tick(long frameStartNs, long presentLatencyNs)
        {
            RoundStartMsg rs;
            if (client.PollRoundStart(frameStartNs, out rs)) Plan(frameStartNs);
            if (client.PollCue(frameStartNs, presentLatencyNs)) OnCue();
            // press before the client's own no-shot close: the planned time is what counts, not the frame
            FireDue(frameStartNs);
            client.Tick(frameStartNs);
        }

        private void Plan(long countdownStartNs)
        {
            current = started++;
            shotPlanned = false;
            foulPlanned = false;
            if (current >= attempts.Length) return; // exhausted: silent (the match driver ends before this)
            var a = attempts[current];
            if (a.kind == AttemptKind.Early)
            {
                long off = a.inputOffsetNs >= 0 ? a.inputOffsetNs : DefaultEarlyOffsetNs;
                foulAtNs = countdownStartNs + off;
                foulPlanned = true;
            }
        }

        private void OnCue()
        {
            if (current < 0 || current >= attempts.Length) return;
            var a = attempts[current];
            if (foulPlanned)
            {
                // they jumped the gun before THEIR cue: replay it as a foul whatever our frame timing did
                foulPlanned = false;
                long at = foulAtNs < client.CuePresentedNs ? foulAtNs : client.CuePresentedNs - 1;
                client.Press(at, InputSource.Remote, false);
                return;
            }
            if (a.kind == AttemptKind.Shot || a.kind == AttemptKind.Anticipation)
            {
                shotAtNs = client.CuePresentedNs + Math.Max(0L, a.reactionNs);
                shotPlanned = true;
            }
        }

        private void FireDue(long nowNs)
        {
            if (foulPlanned && nowNs >= foulAtNs)
            {
                foulPlanned = false;
                client.Press(foulAtNs, InputSource.Remote, false);
            }
            if (shotPlanned && nowNs >= shotAtNs)
            {
                shotPlanned = false;
                client.Press(shotAtNs, InputSource.Remote, false);
            }
        }
    }
}
