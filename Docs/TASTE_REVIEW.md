# Paw Noon: taste review (creative direction pass, 2026-10-19)

Sources: SPEC §1/§5/§10/§11, ARCHITECTURE.md, `work/play_v4.png` + `work/play_swipe.png` (real captures),
`work/v3/ref_final.png`, all presentation code. The game reads well and the duel beat lands. What follows
are the places where the image or the feel loses intensity, ranked by impact. Each one is marked
**DONE** (implemented in this pass), **HOOK** (implemented, needs a one-line call from the lead), or
**SKIP** (judged weak or against the art bible, with the reason).

## Ranked findings

1. **The HUD and cues zoom and shake with the world, and the DRAW flash bleaches the DRAW word. HOOK (1 line in PawGame).**
   Everything, including HUD, badge, timer and stamps, lives in the one world RT, so:
   (a) the hold push-in (`holdZoom` 1.035) scales the HUD up to 3.5 %: in `play_v4` frame 3 the YOU/PAWTRUTH
   labels are cut off at the top, which on a device puts them under the Dynamic Island;
   (b) the DRAW frame's white flash (1.0, then 0.45 fading over about 110 ms) washes over the
   DRAW word and the timer it is meant to announce: `play_v4` frame 4 is a milky frame with a white blob
   where DRAW should be;
   (c) every shot shake and kick blurs the 6-decimal timer, the most important number on screen.
   Fix: `PixelPipeline.SetOverlayRoot(Transform)` moves a hierarchy (the `UI` root) to a second
   pixel-perfect RT, composited **after** grade, vignette, flash, zoom, shake, tear and chroma. The cue now
   pops crisp on top of the white frame, and the HUD stays locked in the safe area during the push-in.
   `ScreenToCanvas` already assumed "UI is shown unzoomed", so the code now matches that comment.
2. **The DRAW strike has no texture. DONE + HOOK.** `PixelPipeline.Strike()`: a 2-frame screen tear
   (alternating 4-px bands offset ±1 virtual px, with the sign flipping per frame) plus a 1-frame chromatic
   split (R and B offset 1 px), on the world only. Durations are counted at 60 Hz ticks, so they run
   just as long on 120 Hz ProMotion. `TearEnabled`/`ChromaEnabled` are exposed, and Reduced Motion skips both.
   The flash fade is now **stepped** (1/8 levels): one full frame, then about 0.35 fading out in
   visible steps, instead of a 110 ms smooth milk.
3. **Hits are not felt on the victim's side of the frame. HOOK.**
   - Hero hit: `HeroHurt()` gives a 1-frame red flash at 25 % plus a 3-tick "wince" vignette in blood-ink at the edges.
   - Pawtruth hit: `PopAt(canvasPoint)` gives a 3-tick radial brightness pop locked to the content pixels
     around him. It is Bayer-dithered and stepped, like the painted sun halo in the reference.
4. **The camera has no point of view. HOOK.** The hold push-in zooms on the screen centre (sky and
   badge), not on the duel. `SetZoomFocus(canvas)` anchors the zoom (on Pawtruth's chest the street
   narrows on him, and the hero slides out of frame toward the camera). `PushTo(victim, 0.02, s)` on the
   killing blow leans 2 % into the victim during the slow-mo, then releases. Both are disabled under Reduced Motion.
5. **No arc across the match. HOOK.** `SetTension(0..1)` gives the "last light" look: about 30 % desaturated,
   warmer, slightly darker, with a heavier and tighter vignette. It eases at about 2 s per full step, and
   `PixelPipeline.TensionFromHp(hp0, hp1)` holds the ladder (0 while both cats are above 70 %, rising as
   the leader drops, and 1 once both are below 40 %). The last round should look different from the first.
6. **The vignette was a smooth screen-resolution gradient** (smoothstep in device px), the one
   non-pixel-honest element on screen. **DONE:** it is now computed per virtual pixel and quantized
   with a 4×4 ordered dither into 1/6 steps. It "tightens" during the hold (the inner radius shrinks as
   `Vignette` rises above its standoff baseline), and the baseline drops 0.28 to 0.24 ("barely there").
7. **The heat shimmer ignored the hold.** ARCHITECTURE says that during the hold "clouds, props, motes,
   shimmer ease to a stop", but the shader ran on `_Time.y`. **DONE:** it now runs on `PawTime.AmbientT`
   (via a MaterialPropertyBlock), so the street holds its breath and the air starts moving again on DRAW.
8. **Grade. DONE.** The warm grade stays, with an added slight black crush (`Crush` 0.025) for the
   chunky-outline punch of the reference. This is a small contrast lift, not a look change.
9. **Mechanical idle. DONE + HOOK.** `SpriteAnimator.Jitter` (0..1) gives organic pacing on looping
   strips: frame 0 (rest) holds longer and more variably, and the other frames get a little jitter.
   The 2-frame `ready` breath at 2 fps currently loops exactly every 1.0 s and reads robotic. It uses a
   private xorshift, so the game's RNG is untouched.
10. **Tumbleweed overuse. DONE.** It came every 9–18 s, so it appeared in 4 of the 8 swipe captures,
    rolling in front of Pawtruth's feet. It now comes every 24–42 s (first one at 6–10 s), so it stays a beat
    and not wallpaper. For the classic beat, the lead can trigger one on round-1 standoff (hook below).
11. **Slow-mo exit snapped back to 1×. DONE.** In `PawTime`, the last 0.18 s of slow-mo ramps back to 1.
    `PawTime.SlowMoFactor` exposes the current factor for audio pitch (hook below).
12. **Heartbeat glyph alpha was a smooth float. DONE:** quantized to 1/8 steps (pixel-honest).
13. **Upscale verification.** `PixelUpscale` implements sharp-bilinear correctly for non-integer
    scales (270→1179 is 4.367×, and vertical 585→2556 is 4.369×): flat texel interiors, a 1-device-px blended seam,
    and scale includes the zoom. No shimmer at rest. Hardening **DONE**: taps use `tex2Dlod`
    (no implicit derivatives at block boundaries), and dither and vignette sit on the virtual grid.
    The only remaining "crawl" is the deliberate sub-pixel shake and zoom on the world, which is now
    off the HUD (finding 1).

### Skipped
- **2× live timer (font_timer at 2× nearest): SKIP.** §10 says everything must be "pixel art at this
  same pixel size". 2× glyphs are mixels and would clash with every other glyph. Once the overlay
  composite (finding 1) is in, the timer never shakes, zooms or gets flashed out, which was the real
  legibility problem. Live digits stay cream on ink and turn brass when frozen (as now).
- **Sun-flare shaft: SKIP.** The v3 plate already paints a dithered sun halo. A drifting diagonal
  band at 10 % reads as a lens/UI artifact and muddies the sky that frames Pawtruth's hat.
- **Heat-band parallax on push-in / 0.5 Hz band drift: SKIP.** 1 px of parallax at 3.5 % zoom is
  invisible, and a whole-band lateral drift reads as the background sliding.
- **Extra vulture / dust motes:** both already exist. The `sun` pulse in StreetScene is dead with the
  v3 plate (no separate sun layer) and is harmless.

## New / changed API (all zero-alloc per frame, MaterialPropertyBlock / shared material)

`PixelPipeline` (existing signatures unchanged):
| Member | What |
|---|---|
| `const int OverlayLayer = 30` | layer used for the overlay hierarchy |
| `void SetOverlayRoot(Transform root)` | composite this hierarchy unzoomed/unshaken/unflashed over the graded world (up to 4 roots; re-layered every frame, so children created later follow) |
| `bool OverlayShakes` | default false; true = overlay follows whole-pixel shake |
| `void Strike()` | DRAW: 2-tick tear + 1-tick chroma (world only) |
| `bool TearEnabled, ChromaEnabled` | per-effect switches (Reduced Motion also disables) |
| `void HeroHurt()` | 1-frame red 25 % flash + 3-tick wince vignette |
| `void PopAt(Vector2 canvas, float radiusPx = 26, float amount = 0.5f)` | stepped radial brightness pop (3 ticks) |
| `bool HitFxEnabled` | switch for HeroHurt/PopAt |
| `void SetZoomFocus(Vector2 canvas)` / `ClearZoomFocus()` | anchor point of `Zoom` |
| `void PushTo(Vector2 canvas, float amount = 0.02f, float seconds = 0.6f)` | kill lean-in, eases out by itself |
| `void SetTension(float t)` / `static float TensionFromHp(double f0, double f1)` | last-light ladder |
| `float Crush` | black crush (0.025) |

`SpriteAnimator.Jitter` (0..1). `PawTime.SlowMoFactor` (1 outside slow-mo).

## Hooks for the lead (exact lines)

PawGame.Init, after `matchScreen.Setup(...)`:
```csharp
Pipeline.SetOverlayRoot(ui);                       // HUD/cues/panels: crisp, unzoomed, unshaken, unflashed
hero.Anim.Jitter = 0.35f; bandit.Anim.Jitter = 0.3f; // organic idle/ready pacing
```
DuelDirector.BeginStandoff (end):
```csharp
pipe.SetTension(PixelPipeline.TensionFromHp(authority.Match.HpFraction(0), authority.Match.HpFraction(1)));
pipe.SetZoomFocus(new Vector2(layout.BanditFeet.x, layout.BanditFeet.y - 40)); // push-in on Pawtruth
```
DuelDirector.PresentDraw (after `pipe.FlashScreen(...)`):
```csharp
pipe.Strike();
```
DuelDirector.ApplyImpact (after `PawHaptics.Notify(...)`):
```csharp
if (victim == 0) pipe.HeroHurt(); else pipe.PopAt(hit);
```
DuelDirector.ApplyImpact, in `if (e.killed)` after `PawTime.SlowMo(...)`:
```csharp
pipe.PushTo(hit, 0.02f, (float)cfg.killSlowMoSeconds + 0.25f);
```
DuelDirector.StartMatch / Stop / EnterReview (reset):
```csharp
pipe.SetTension(0f);
```
Optional, round 1 standoff only (classic beat): `street.LaunchTumbleweed();`. The director does not
hold `StreetScene` today; PawGame can call `Street.LaunchTumbleweed()` from `StartDuel()`.

Audio (PawAudio has no API yet). Add `public void SetPitch(float p)` that sets `pitch` on the SFX pool +
ambience sources, then in PawGame.Update after the mode switch:
```csharp
Audio.SetPitch(Mathf.Lerp(0.72f, 1f, Mathf.InverseLerp(0.35f, 1f, PawTime.SlowMoFactor)));
```
(At 0.35× slow-mo this gives about 0.72 pitch, and it follows the new slow-mo exit ramp back to 1. Keep the DRAW cue and
music sources out of it.)

## Notes for integration
- **Overlay and SpeechBubble:** the bubble is created under the UI root, so with the overlay it stays unzoomed.
  With `SetZoomFocus` on Pawtruth, he is the fixed point of the push-in, so a bubble pointing at him stays
  on target. Only the hero side drifts, by at most about 3 % while zoomed.
- Anything under the overlay root always draws above the whole world (as before: all UI orders are ≥ 150).
  Keep world-anchored FX (particles, sweat, shadows) under `World`.
- `SpriteFx` now writes alpha as `One OneMinusSrcAlpha`, so the overlay RT is premultiplied. Opaque world
  output is unchanged.
- The pipeline's flash peak still lasts exactly one frame (the same contract as before), then fades in 1/8 steps.
- `PixelPipeline.cs` was edited concurrently by someone else (depth-less RT, AudioListener guard). My
  changes were merged on top of those and keep them.

## Files
Changed: `Code/Runtime/PixelPipeline.cs`, `Resources/Shaders/PixelUpscale.shader`, `Resources/Shaders/SpriteFx.shader`,
`Code/Runtime/StreetScene.cs`, `Code/Runtime/SpriteAnimator.cs`, `Code/Runtime/PawTime.cs`, `Code/Runtime/DuelCues.cs`.
Added: `Docs/TASTE_REVIEW.md`.

## Reduced Motion
Tear, chroma, flash, wince, pop, zoom focus and push are all off when `PixelPipeline.ReducedMotion` is set.
Tension (a colour grade, not motion), the dithered vignette, the grade and Jitter stay on. The cue still
conveys everything through the word, burst, sound and haptic.
