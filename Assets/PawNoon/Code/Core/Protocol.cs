using System.Collections.Generic;

namespace PawNoon.Core
{
    public enum InputSource
    {
        Unknown = 0,
        Touch = 1,
        Mouse = 2,
        Keyboard = 3,
        Bot = 4,
        Injected = 5,
        Remote = 6
    }

    public enum ReportKind
    {
        /// <summary>Pressed after the client presented the cue.</summary>
        Shot = 0,
        /// <summary>Pressed after the countdown started but before the cue was presented (TOO EARLY).</summary>
        Early = 1
    }

    /// <summary>Authority -> clients: a round's countdown starts now. Carries NO hold/cue information.</summary>
    public struct RoundStartMsg
    {
        public int roundId;
        public int roundNumber;
        public int countdownBeats;
        public long beatNs;
    }

    /// <summary>Authority -> clients, sent at the moment of the cue. Clients present it on arrival.</summary>
    public struct DrawCue
    {
        public int roundId;
        public int cueSeq;
    }

    /// <summary>
    /// Client -> authority. All times on the CLIENT's own clock; network latency is excluded because
    /// reaction = inputNs - cuePresentedNs is measured locally. The authority validates consistency.
    /// </summary>
    public struct ShotReport
    {
        public int roundId;
        public int slot;
        public ReportKind kind;
        public long reactionNs;
        public long cuePresentedNs;
        public long inputNs;
        public InputSource inputSource;
        public bool frameQuantized;
    }

    /// <summary>
    /// Message seam between clients and the authority. LoopbackTransport now; a socket transport later.
    /// Slot 0 = local player, slot 1 = opponent (bot today, remote player later).
    /// </summary>
    public interface IDuelTransport
    {
        // client -> authority
        void SendReport(ShotReport report);
        bool TryReceiveReport(out ShotReport report);

        // authority -> clients
        void BroadcastRoundStart(RoundStartMsg msg);
        void BroadcastCue(DrawCue cue);
        bool TryReceiveRoundStart(int slot, out RoundStartMsg msg);
        bool TryReceiveCue(int slot, out DrawCue cue);

        void Clear();
    }

    public sealed class LoopbackTransport : IDuelTransport
    {
        public const int Slots = 2;
        private readonly Queue<ShotReport> reports = new Queue<ShotReport>(8);
        private readonly Queue<RoundStartMsg>[] starts = { new Queue<RoundStartMsg>(2), new Queue<RoundStartMsg>(2) };
        private readonly Queue<DrawCue>[] cues = { new Queue<DrawCue>(2), new Queue<DrawCue>(2) };

        public void SendReport(ShotReport report) { reports.Enqueue(report); }

        public bool TryReceiveReport(out ShotReport report)
        {
            if (reports.Count > 0) { report = reports.Dequeue(); return true; }
            report = default(ShotReport);
            return false;
        }

        public void BroadcastRoundStart(RoundStartMsg msg)
        {
            for (int i = 0; i < Slots; i++) starts[i].Enqueue(msg);
        }

        public void BroadcastCue(DrawCue cue)
        {
            for (int i = 0; i < Slots; i++) cues[i].Enqueue(cue);
        }

        public bool TryReceiveRoundStart(int slot, out RoundStartMsg msg)
        {
            if (slot >= 0 && slot < Slots && starts[slot].Count > 0) { msg = starts[slot].Dequeue(); return true; }
            msg = default(RoundStartMsg);
            return false;
        }

        public bool TryReceiveCue(int slot, out DrawCue cue)
        {
            if (slot >= 0 && slot < Slots && cues[slot].Count > 0) { cue = cues[slot].Dequeue(); return true; }
            cue = default(DrawCue);
            return false;
        }

        public void Clear()
        {
            reports.Clear();
            for (int i = 0; i < Slots; i++) { starts[i].Clear(); cues[i].Clear(); }
        }
    }
}
