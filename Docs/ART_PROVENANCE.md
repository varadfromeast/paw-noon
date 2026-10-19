# Art provenance — 18 October 2026

The user's attached Western duel image informed atmosphere and composition. It was not sliced into gameplay assets.

Generated using the built-in image_gen tool, then copied into `Assets/Resources/Showdown`:

- Town.png — original portrait Western street with wooden saloon and gunsmith, distant chapel, mesas, blue sky, warm amber afternoon, empty central ground; no characters or HUD. Source: exec-7f822e2b-d01e-44ed-8b4d-3edc86230407.png.
- Rival.png — isolated front-facing orange tabby outlaw, brown hat/duster, red bandana, holstered revolver, green eyes, full-body pixel art with transparent background. Source: exec-2a1ef428-7fbd-4522-bf0e-bfc7870e8c79.png.
- Hero.png — isolated grey striped cat outlaw from behind in three-quarter view, brown hat, rust-red poncho, gun belt, full body pixel art with transparent background. Source: exec-1643e9e2-ae54-4c4a-b273-aa814cd2d493.png.

Original generated files are retained at `/Users/varad/.codex/generated_images/01a14f92-7c1c-7700-bf69-356b937e14ff/`. No image edits were performed after generation. Unity applies point filtering, alpha transparency, no mipmaps, and a 2048 maximum texture dimension.

Rye and Barlow Condensed font files were reused from the older project; their SIL Open Font License texts are included beside the fonts. Audio cues are generated mathematically at runtime.

## Generation prompts

### Town

Create an original production background asset for a portrait iPhone pixel art Western cat quick-draw game. Portrait 2:3 composition. Detailed crisp pixel art, golden amber afternoon, dusty empty main street narrowing toward a distant white chapel, red sandstone mesas and luminous pale blue sky, saloon wooden balconies on left, gunsmith and barrels on right, windmill, sparse cacti. Camera low down the street, rich dark brown foreground side frames, large empty sandy central area for separately rendered characters. No characters, no animals, no text, no lettering, no HUD, no border. Beautiful authored pixel clusters, limited warm palette, atmospheric depth, no blur, no 3D render. The upper 20 percent mostly sky for game HUD, lower 25 percent sandy ground for controls. Save asset for Unity game.

### Rival

Production game sprite, single full body anthropomorphic orange tabby CAT outlaw facing the camera in a tense western quick draw stance, both boots visible, ears visible, tail curling at side, broad brown cowboy hat, dark chocolate duster, red bandana, cream vest, gun belt, one paw hovering at holstered revolver. Cat anatomy face, green eyes, serious charming expression. Detailed crisp SNES-inspired pixel art with strong dark outline and warm golden light from upper right. Transparent background, isolated character only, centered with tight but complete framing, no shadow ground, no text, no props outside costume. 2:3 portrait asset. Consistent readable pixel clusters, not smooth illustration.

### Hero

Single production game sprite, anthropomorphic grey striped CAT outlaw gunslinger seen FROM BEHIND in three-quarter over-the-shoulder view facing toward right, full body boots visible, wide brown cowboy hat with two cat ears, rust red poncho fluttering left, dark trousers, gun belt, furry grey right paw near holstered revolver at right hip, striped tail. Head looking toward an opponent off screen right. Strong dark silhouette. Detailed crisp pixel art with dark brown outlines, golden western afternoon edge light from upper right. Transparent background, no environment, no text, no ground shadow, only one complete isolated character. Portrait 2:3 framing tight around character. This is the foreground hero sprite of a pixel art western duel game, distinct large readable pixel clusters, not smooth painted illustration.

## Rebuild assets

All four were generated with the built-in image_gen tool and copied into Assets/Resources/Showdown. No third-party game art was copied.

- Gun.png: single isolated chunky pixel-art six-shooter revolver icon, steel barrel angled upper-right, walnut grip, brass trigger guard, transparent background. Source exec-faf2653f-d8d5-451b-8db5-e708fe1898f7.png.
- RivalSheet.png: four equal columns of one orange-tabby outlaw in idle, draw, aim, and hit poses, aligned boots, limited palette and chunky low-resolution clusters, transparent background. Source exec-0d7d2ec1-75ab-4b5a-a009-0b0d3c676170.png.
- HeroSheet.png: four equal columns of a grey striped cat seen from behind in idle, draw, aim, and recoil poses, rust poncho, aligned boots, transparent background. Source exec-ac77be76-3fe3-4112-ae7d-a66f5cfd53d6.png.
- TownV2.png: dense portrait frontier street, ochre sunlight, violet-brown shaded timber, empty ground for two duelists, consistent blocky pixel treatment, no characters or HUD. Source exec-fa3208bd-4535-4989-9740-e4234df8a190.png.

The runtime cuts animation cells using Sprite.Create, draws contact-shadow primitives, and renders the entire SpriteRenderer scene into a 240 x 480 point-filtered target. This is runtime game rendering, not a composite screenshot used as the game.
