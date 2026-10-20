# Current direction

User-confirmed: portrait iOS; reaction-only quick draw; animal outlaws, cats only for now. The supplied image defines atmosphere rather than a timing-meter mechanic.

Implementation choices: Dust & Claws working title, grey tabby player versus orange tabby Blackpaw, offline bot MVP, health-based repeated exchanges, new art, built in the empty PawNoonPixel shell. The original PawNoon project is reference-only.

Later product decisions: final name, online PvP scope, additional outlaw characters and progression, music direction. These do not block this playable slice.

# M1 notes: Difficulty (SPEC_V2 §1)

- **Rules files.** Hard is `Resources/Data/duel_config.json` unchanged. Easy is `Resources/Data/duel_config.easy.json`, which holds only the 15 keys in the §1 table. `DuelConfig.Load(hardJson, overlayJson)` merges the overlay with `PnJson.Merge`: overlay keys win, nested objects are merged one level deep, and arrays are replaced whole. `DuelConfig.FromJson` still works as before. `DuelConfig.Difficulty` is `"hard"` or `"easy"` and is set by `Load`, never read from JSON.
- **Switching rules without rebuilding the core.** The authority, clients, bot and director all hold the same `DuelConfig` reference and read it live. A difficulty change copies the new values into that object (`DuelConfig.CopyFrom`). On the title screen the copy happens at once. Otherwise it happens at the next `StartDuel` / `ShowTitle`, so a match in progress keeps its rules. No change to Core timing, `SwipeDraw` or `LocalAuthority`.
- **Persistence.** PlayerPrefs key `pn_difficulty` (`"easy"`/`"hard"`). On first launch there is no key, so the title shows a one-time **PICK YOUR DUEL** card (panel_wood with btn_large_alt EASY and btn_large HARD). PLAY, the gear and the toggle wait until the player picks. After that, an EASY | HARD segmented toggle sits under PLAY, using the same seg_on/seg_off sprites as the settings music-style row, and BEST moves under the toggle.
- **Tags.** These are text only, with no new art. The HUD puts EASY/HARD just right of the round sign on the same row, because the row below the sign is where the result card sits. The round result card puts it after the note line ("TAP TO CONTINUE  EASY").
- **Bridge.** `ReviewDirector.SetDifficulty easy|hard|reset` and `ReviewDirector.Show difficulty`. `Status` now reports `difficulty=`, shown as `hard->easy` while a change is waiting for the next match.
- **Tests.** `DifficultyTests` checks the following. The overlay file keys equal the whitelist. Merged Easy differs from Hard in exactly those fields. Easy 300 ms deals Hard ~220 ms damage (35 vs 33.6 HP, same PERFECT band). Easy is never harsher than Hard. anticipationMs, tieWindowMs and the countdown are unchanged. The merge semantics and `CopyFrom` are covered too.
