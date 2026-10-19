# Pixel world rebuild

The user rejected the first presentation and confirmed direct Unity implementation. The pinned direction is immersive portrait pixel-art Western combat with a gun-tap action and characters grounded in their environment.

The Impeccable direction seed was fc905ab9. Its alternative paper, provenance, printing, film-bench, and zine systems are declined because the user explicitly pins a playable pixel world. Useful disciplines retained: discrete authored pose states (fold sequence), clear causal feedback (ribbon), consistent restricted palette (printing), frame-accurate action (film bench), and economy of display text (zine). No alternative decorative motif is imported.

## References

- Westerado: Double Barreled — official store, https://store.steampowered.com/app/275200/Westerado_Double_Barreled/ . Western pixel-art game reference; use readable silhouettes and focused action, not its assets.
- Eastward — official media page, https://eastwardgame.com/media/ . The developer describes combining retro pixel artwork with modern lighting. Apply coherent shared light and environmental integration, not copied imagery.

## Concrete implementation

- World camera renders at 240 x 480; point-filtered RenderTexture is displayed full screen.
- SpriteRenderer actors, contact shadows, particles, and background share this world camera.
- Four-frame pose sheets; anchored feet; dust crossing the street; short muzzle flash, smoke, recoil and camera impact.
- Bitmap 5x7 text and compact health rails. No permanent footer hiding the scene.
- Large gun icon under the right thumb; pointer-down captures reaction before animation.
- A local bot physically fires at its sampled reaction timestamp.

## Verification boundary

Unity editor captures prove the rendered Unity presentation and scripted pointer path. They are not physical iPhone performance or signing evidence. iPhone deployment still requires device validation.
