namespace PawNoon.Core
{
    /// <summary>
    /// Guns and loadouts (SPEC_V2 §6). A gun id travels in recordings (<c>loadout.gun</c>), the profile and
    /// PlayerPrefs. RULES: the gun only changes the damage curve, never timing; in ranked PvP every gun is
    /// scored with the standard curve (the server does the same, loadouts.ts). Mirrored in TypeScript
    /// (supabase/functions/_shared/duel/loadouts.ts) and Python (Tools/Parity/reference_vectors.py).
    /// </summary>
    public static class Guns
    {
        public const string Standard = "standard";
        public const string Peacemaker = "peacemaker";

        /// <summary>Store product (non-consumable) that unlocks the Peacemaker on both stores.</summary>
        public const string PeacemakerProductId = "com.pawnoon.pixel.peacemaker";

        /// <summary>Known gun id (trimmed, lower case) or Standard for anything else (null, typos, future guns).</summary>
        public static string Normalize(string gun)
        {
            if (string.IsNullOrEmpty(gun)) return Standard;
            if (gun == Peacemaker || gun == Standard) return gun;   // the common case: no allocation
            string g = gun.Trim().ToLowerInvariant();
            return g == Peacemaker ? Peacemaker : Standard;
        }

        public static bool IsPaid(string gun) { return Normalize(gun) == Peacemaker; }

        /// <summary>The gun whose curve applies: Standard in ranked whatever is equipped.</summary>
        public static string Effective(string gun, bool ranked)
        {
            return ranked ? Standard : Normalize(gun);
        }

        /// <summary>Damage values (at cfg.damageMs) for <paramref name="gun"/>. Returns the config's own array (no copy).</summary>
        public static double[] DamageValues(DuelConfig cfg, string gun)
        {
            if (cfg == null) return null;
            if (Normalize(gun) == Peacemaker && cfg.damageValuePeacemaker != null && cfg.damageValuePeacemaker.Length >= 2)
                return cfg.damageValuePeacemaker;
            return cfg.damageValue;
        }

        /// <summary>Store product id for a paid gun, null for the standard one.</summary>
        public static string ProductFor(string gun)
        {
            return Normalize(gun) == Peacemaker ? PeacemakerProductId : null;
        }

        /// <summary>Gun unlocked by a store product, null if the product is not a gun.</summary>
        public static string GunForProduct(string productId)
        {
            return productId == PeacemakerProductId ? Peacemaker : null;
        }
    }
}
