# Performance notes

Target: a steady 60 / 120 Hz on a mid-range phone with **zero managed allocations per frame** in a duel, so the GC
never runs between the DRAW cue and the swipe. Timing does not depend on the frame rate (input timestamps come from
the OS events, see `TIMING.md`). A hitch still shows up as a late DRAW frame on screen, though, so frame pacing is
part of fairness.

## What was checked (cleanup pass, 21 Oct 2026)

There's no Unity here, so nothing below is a profiler capture. It's a static pass over every `Update` / `LateUpdate`
/ `Tick*` body in `Code/Runtime` (DuelDirector and its partials, DuelHud, DuelCues, EcgMonitor, SwipeCue,
ReplayStrip, PixelPipeline, PixelParticles, StreetScene, BitmapText, SpeechBubble, MatchIntro, the Online /
Account / Shop / Bounty directors, HapticMoments and PawHaptics) plus the helpers they call. Checked for: `new` of
classes or arrays, string concatenation / interpolation / `ToString`, LINQ, closures, boxing, `GetComponent`,
`Find*`, `Resources.Load`, and texture uploads.

`python3 Tools/Lint/check_cs.py` repeats the allocation part automatically, one call level deep (the bodies of
methods named `Update`, `LateUpdate`, `FixedUpdate` or `Tick*`). It reports nothing in `Code/Runtime` today.

Found already clean: every per-frame path is allocation-free. Text goes through `char[]` buffers
(`BitmapText.SetChars`, `NumFmt`). Strings are built only on state changes (round sign, messages, bounty strip, at
most 4 checks/s). `Resources.Load` only runs at boot or on first use, and results are cached (`PawAudio.Clip`,
`SpriteCatalog`). Lambdas are created once at init. No `FindObjectsByType` / `GetComponent` per frame (the one
`FindObjectsByType<AudioListener>` is in `PixelPipeline` setup). The replay strip and the share card upload only on
change.

## Changed

| Where | Before | Now |
|---|---|---|
| `EcgMonitor.Tick` (pass 1 D12) | cleared and re-uploaded the whole texture (`SetPixels32` + `Apply`) every hold frame | renders only when the write head reached a new column or a column's stepped trail intensity is due to drop a step (the next due time is computed during the render); the grid background is a preallocated buffer copied with `Array.Copy`. Pixel output is identical (checked against an every-frame render over randomized frame times). About half the uploads at 120 Hz, fewer at 60 Hz with hitches |
| `PixelPipeline.LateUpdate` | walked the entire UI hierarchy every frame to keep late-created children on the overlay layer (hundreds of `gameObject` / `layer` / `GetChild` native calls) | walks only when the root's `Transform.hierarchyCount` changed (nothing in the game destroys UI objects, so new objects always change it), plus a safety-net walk every 120 frames |
| `DuelHud.Update` | re-assigned both portrait sprites + transforms, the holster frame sprite, and the wood sign's sliced size every frame | written only when the hurt / jolt state, the holster frame or the sign's swing step changes |
| `DuelHud.TickDemo` | `SwipeCue.Show(false)` (loops over 13 renderers) every frame after the draw | once |
| `PixelButton.SetVisible` | `Root.SetActive` on every call (the SHOP button is set every title frame) | no-op when nothing changes |
| `DuelCues.ShowNumeral` | `"cue_" + n` per countdown beat | cached keys |
| Retired paths | the ghost-paw / trail / painted demo / heart glyph objects were built at boot and kept around hidden (one texture each + 12 trail renderers) | deleted |

Not changed on purpose: `SwipeCue` and `StreetScene` set a few `SpriteRenderer.color` values per frame (structs,
no allocation, a handful of renderers), and `PixelPipeline` sets its ~16 material floats every frame (cheaper than
tracking them, and one material).

## How to profile on a device

1. **Build** a Development Build with *Autoconnect Profiler* (Build Profiles: Development Build + Autoconnect
   Profiler; for IL2CPP also *Script Debugging* off, which keeps timings realistic).
2. **Connect**: Window > Analysis > Profiler, then pick the device in the target drop-down (USB is steadier than
   Wi-Fi; on iOS use the device's IP, on Android `adb forward` is set up by Unity).
3. **Play a duel** through at least 3 rounds (or `call PawNoon.Runtime.ReviewDirector.SetAutoTap 240` +
   `StartDuel` from the bridge for a repeatable run).
4. **CPU Usage module**: select a hold frame and the DRAW frame. In Hierarchy view, sort by *GC Alloc*. The target
   is 0 B under `PawGame.Update` and every `Update.ScriptRunBehaviourUpdate` / `PreLateUpdate` entry. Turn on *Deep
   Profile* only to find a culprit; it distorts timings.
5. **Memory module / GC**: the *GC Allocated In Frame* counter should stay flat during a round. A sawtooth means a
   per-frame allocation.
6. **Frame pacing**: with *Rendering* + *CPU* modules, check `Gfx.WaitForPresent` vs script time, and that the
   frame time at the DRAW frame matches its neighbours (a spike there delays the cue's photons).
7. **Textures**: Profiler > *Rendering* shows `Texture.Apply` uploads. During the hold only the ECG should upload,
   and only on frames where it changed.
8. Xcode Instruments (*Time Profiler*, *Game Performance*) or Android GPU Inspector for anything below Unity's
   view (driver, thermals).

Before shipping, run `python3 Tools/Lint/check_cs.py` (no errors). Treat its `alloc` warnings as review items:
mark a line `// lint: alloc-ok (reason)` when it only runs on a rare transition.
