using PawNoon.Core;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Town hooks (SPEC_V2 §8.3 townsfolk, §8.8 weather), kept apart from the main director file: each hook in
    /// DuelDirector.cs is one line calling into here. Presentation only; nothing here reads input or changes timing.
    /// </summary>
    public sealed partial class DuelDirector
    {
        private TownLife town;

        /// <summary>Optional: the townsfolk + weather (PawGame wires it after construction).</summary>
        public void SetTown(TownLife t)
        {
            town = t;
            if (town != null) town.SetIntro(intro);
        }

        public TownLife Town { get { return town; } }

        /// <summary>StartMatch: weather from the match seed (never in the tutorial, never on the first matches), first peek + creak.</summary>
        private void TownMatchStart(int matchSeed)
        {
            if (town != null) town.BeginMatch(matchSeed, State != Pres.Tutorial);
        }

        private void TownStandoff() { if (town != null) town.Standoff(); }

        private void TownDuck() { if (town != null) town.Duck(); }

        private void TownNoShot() { if (town != null) town.NoShot(); }

        private void TownExchangeOver(Grade playerGrade) { if (town != null) town.ExchangeOver(playerGrade == Grade.Lightning); }

        private void TownMatchEnd() { if (town != null) town.EndMatch(); }

        /// <summary>Review: townsfolk still ("peek", "duck", "cheer", "cheerhold", "hide") on a quiet standoff stage.</summary>
        public string ReviewTownsfolk(string mode)
        {
            if (town == null) return "error: no town wired";
            if (State == Pres.Off)
            {
                EnterReview();
                hud.SetVisible(true);
                hud.SetRound(1);
                hud.SetHpImmediate(0, 1f);
                hud.SetHpImmediate(1, 1f);
                hero.Play("ready");
                bandit.Play("ready");
                hud.ShowHolster(true);
            }
            return town.ReviewSet(mode);
        }
    }
}
