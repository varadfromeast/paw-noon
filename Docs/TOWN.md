# The town comes alive (SPEC_V2 §8.3) and weather (§8.8)

Presentation only. `TownLife` (Runtime/TownLife.cs) is driven by one-line hooks in DuelDirector.cs that call
`DuelDirector.Town.cs`; it never reads input, the clock or the rules.

## Townsfolk
- Art: `Tools/ArtGen/town/gen_townsfolk.py` → `Art/FX/town_bonnet.png`, `town_bowler.png` (3 frames 16x18: peek, ducked,
  blink), `town_bowler_hat.png` (12x6), `town_cover_left/right.png` (plate pixels in front of each onlooker: frame 0
  noon plate, frame 1 dawn plate). Entries in `Art/FX/fx.json` carry `pivot`, the canvas anchor `at`, `duckDrop`,
  `hat` / `hatOffset` and `cover`. Re-run the script after the plate changes.
- Left: ginger tabby in a sunbonnet behind the planter barrel on the saloon porch (x 141..163). Right: grey tom in a
  bowler on the right-hand balcony. The right-hand street barrels sit behind Pawtruth (x 239..325) or outside
  the 270-px phone core, so he watches from upstairs instead.
- Order: plate 0, dawn plate 1, heat band 2, onlooker 3, hat 4, cover 5 / dawn cover 6 (stepped alpha = the
  street's dawn alpha), dust 7. Never over a duelist or the holster.
- Beats: first peek ~0.9 s after match start (after the intro) with `sfx_door_creak`; idle blink + 1 px sway on
  ambient time; countdown "1" = duck + 3 px of dust; NO SHOT = the left one peeks early + a tumbleweed; round card =
  peek back (staggered); LIGHTNING = both pop up 3 px, the bowler flies 4 px and bounces. Title / Stop and the tutorial stage hide them (and clear the weather).
- The saloon has no door sprite (one painted plate), so the door creak is sound only.

## Weather
- Picked per match from the match seed (`TownTuning.Pick`, ghost duels replay the same sky): 1 in 6 dusk, 1 in 10
  dust storm. Never in the tutorial, never while `PawSettings.MatchesPlayed` < 3 (new counter, `pn_matchesPlayed`,
  counted by the director at each player match start; review / auto-tap runs and the tutorial never count).
- Dusk: `StreetScene.SetWeatherDawn` keeps the dawn plate at 0.75 (stepped) under LAST LIGHT / title, plus
  `PixelPipeline.Warm` +0.3. The partial blend shows a faint noon-sun ghost; a real dusk plate would fix it.
- Storm: plate, heat band, covers and onlookers at 90 % brightness, wind motes right to left
  (`PixelParticles.SpawnWind`, none with Reduced Motion), tumbleweeds hop and sway more.

## Tunables / review
`feel_config.json` keys `town*` and `weather*`. `ReviewDirector.Weather dusk|storm|clear|status`,
`ReviewDirector.Townsfolk peek|duck|cheer|cheerhold|hide`.
