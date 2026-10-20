namespace PawNoon.Core
{
    /// <summary>
    /// The slot-1 duelist as the host sees it (SPEC_V2 §4.1, §4.6). <see cref="BotClient"/> (solo) and
    /// <see cref="GhostOpponent"/> (recorded PvP) implement it, and DuelDirector does not know which one it
    /// has. Both press through their own <see cref="DuelClient"/> on the same <see cref="IDuelTransport"/> as
    /// the player, so the authority validates and orders their ShotReports exactly like the player's.
    /// </summary>
    public interface IOpponent
    {
        /// <summary>The opponent's measurement endpoint (slot 1).</summary>
        DuelClient Client { get; }

        /// <summary>A new match starts. The bot reseeds its RNG from <paramref name="seed"/>; a recording rewinds.</summary>
        void StartMatch(int seed);

        /// <summary>Host context before each tick (the bot's adaptive skill reads it; a recording ignores it).</summary>
        void SetPlayerRoundWins(int wins);

        /// <summary>
        /// Called at frame start BEFORE the authority ticks. A recording delivers any press that is already
        /// due, so a long frame (app backgrounded mid-draw) can never let the authority time it out as a
        /// no-shot first. The bot does nothing here (its behaviour is unchanged).
        /// </summary>
        void PreAuthorityTick(long frameStartNs);

        /// <summary>Call every frame at frame start (after the authority ticked).</summary>
        void Tick(long frameStartNs, long presentLatencyNs);

        /// <summary>True from round start until a planned foul is pressed.</summary>
        bool FoulPlanned { get; }

        /// <summary>
        /// True when the local host may stage presentation-only body language for this opponent (feints, the
        /// late tell, banter): the bot. False for a recorded or remote player.
        /// </summary>
        bool HostActing { get; }
    }
}
