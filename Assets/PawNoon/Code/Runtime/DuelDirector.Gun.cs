using PawNoon.Core;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Loadouts (SPEC_V2 §6, M7), kept apart from the main director file. Rules: the authority gets each slot's
    /// gun in effect (Guns.Effective: Standard in ranked) and applies its curve per shot. Presentation: the hero's
    /// holster, draw sound, gunshot and muzzle flash follow the equipped gun in every mode (in ranked only the look
    /// and sound differ). Nothing here touches timing, input or ShotReports.
    /// </summary>
    public sealed partial class DuelDirector
    {
        private const string StdDrawSfx = "sfx_holster_draw";
        private const string StdShotSfx = "sfx_gun_hero";
        private const string StdMuzzle = "muzzle_side";

        private string heroGun = Guns.Standard;
        private string heroDrawSfx = StdDrawSfx;
        private string heroShotSfx = StdShotSfx;
        private string heroMuzzleName = StdMuzzle;
        private SheetDef heroMuzzle;
        private float heroFlashBoost = 1f;

        /// <summary>The hero's equipped gun (look and sound).</summary>
        public string HeroGun { get { return heroGun; } }

        /// <summary>
        /// Sets both loadouts for the next countdown on. Call between matches (PawGame.BeginDuel does, before
        /// StartMatch); the authority latches guns at StartCountdown, so a round in flight never changes.
        /// </summary>
        public void SetGuns(string playerGun, string opponentGun, bool ranked)
        {
            heroGun = Guns.Normalize(playerGun);
            authority.SetGun(0, Guns.Effective(heroGun, ranked));
            authority.SetGun(1, Guns.Effective(opponentGun, ranked));
            bool pm = heroGun == Guns.Peacemaker;
            heroDrawSfx = pm ? "sfx_peacemaker_draw" : StdDrawSfx;
            heroShotSfx = pm ? "sfx_gun_peacemaker" : StdShotSfx;
            heroMuzzleName = pm ? "muzzle_side_peacemaker" : StdMuzzle;
            heroMuzzle = fx.Fx(heroMuzzleName) ?? fx.Fx(StdMuzzle);
            heroFlashBoost = pm ? 1.25f : 1f;
            hud.SetHolster(pm ? "holster_peacemaker" : "holster_zone");
        }

        /// <summary>The hero's muzzle flash sprite (cached; standard until SetGuns).</summary>
        private SheetDef HeroMuzzle
        {
            get
            {
                if (heroMuzzle == null) heroMuzzle = fx.Fx(heroMuzzleName);
                return heroMuzzle;
            }
        }
    }
}
