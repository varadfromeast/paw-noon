# Paw Noon: Teammate / Agent Setup (Unity → Android)

This guide gets you from a fresh clone to the game running in Unity and on an Android phone. It's written so an AI coding agent can follow it step by step. Every step has a **check** that says how to confirm it worked.

---

## 0. Requirements

| Thing | Version / note |
|---|---|
| Unity Hub | latest |
| Unity Editor | **6000.6.4f1** exactly (see `ProjectSettings/ProjectVersion.txt`) |
| Unity modules | **Android Build Support** + **OpenJDK** + **Android SDK & NDK Tools** (and iOS Build Support only for iPhone builds, Mac only) |
| Android phone | Android 8.0+ (API 26), 64-bit (ARM64) |
| Git | any recent version |

You don't need Android Studio: Unity's bundled SDK/NDK/JDK is enough.

**Install the editor + modules (Unity Hub):** Installs → Install Editor → find 6000.6.4f1 (Archive tab if it isn't listed). Then go to Add modules and tick Android Build Support, OpenJDK, and Android SDK & NDK Tools.

**Command-line alternative (agents):**
```bash
# macOS paths shown; on Windows the Hub is "C:\Program Files\Unity Hub\Unity Hub.exe"
"/Applications/Unity Hub.app/Contents/MacOS/Unity Hub" -- --headless install --version 6000.6.4f1 --changeset 12bfff696524
"/Applications/Unity Hub.app/Contents/MacOS/Unity Hub" -- --headless install-modules --version 6000.6.4f1 -m android android-sdk-ndk-tools android-open-jdk
```
**Check:** `<Unity Editor folder>/6000.6.4f1/PlaybackEngines/AndroidPlayer` exists.

---

## 1. Clone and open

```bash
git clone https://github.com/varadfromeast/paw-noon.git
```
In Unity Hub: **Add → Add project from disk →** select the `paw-noon` folder, then open it with 6000.6.4f1.

The first open takes a few minutes, because Unity rebuilds `Library/` (it isn't in git). On the first import, an editor script (`PawProjectSetup`) configures the project automatically: player settings, portrait orientation, icon, and the scene.

**Check:** the Console has **no red errors** and the menu bar shows a **Paw Noon** menu.

> If a package fails to resolve, open Window → Package Manager and let it finish. `Packages/manifest.json` also references `com.ivanmurzak.unity.mcp` from OpenUPM. That's an optional AI-agent bridge to the Editor, and you can delete that line if the registry is blocked on your network.

---

## 2. Run it in the Editor

1. **Paw Noon → Open Main Scene** (or open `Assets/PawNoon/Scenes/Main.unity`).
2. In the **Game** view, set a portrait resolution, e.g. **1179×2556** (iPhone) or **1080×2400**.
3. Press **Play** (or **Paw Noon → Play**).
4. Tap **DUEL**. After 3-2-1 and the hold, when **DRAW!** appears, **drag the mouse upward from the holster at the bottom of the screen**. That swipe is the draw. A plain click doesn't fire.

**Check:** the round resolves, the reaction timer shows something like `0.231874 s`, and the HP bars drop.

---

## 3. Build for Android

### Option A: APK file (simplest, no cable)
1. **File → Build Profiles** (or Build Settings) → select **Android** → **Switch Platform**. This only happens once and takes a few minutes.
2. **Paw Noon → Build Android APK**.
3. The output is `Builds/Android/PawNoon.apk`. **Paw Noon → Open Builds Folder** opens it.
4. Copy the APK to the phone (Drive, Telegram, USB, anything), tap it, and allow "Install unknown apps" for that app when asked.

### Option B: Build And Run on a USB phone (fastest to iterate)
1. On the phone, go to Settings → About phone and tap **Build number** 7 times. Then go to Developer options and turn on **USB debugging**.
2. Plug the phone into the computer and accept the "Allow USB debugging" prompt on the phone.
3. **Paw Noon → Build And Run Android (USB phone)**. Unity installs and launches the game.

### Option C: command line / CI (agents)
```bash
# macOS
/Applications/Unity/Hub/Editor/6000.6.4f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -quit -projectPath "$(pwd)" -buildTarget Android \
  -executeMethod PawNoon.EditorTools.PawMenu.BuildAndroidBatch -logFile build_android.log
# Windows: "C:\Program Files\Unity\Hub\Editor\6000.6.4f1\Editor\Unity.exe" with the same arguments
```
The exit code is 0 on success. **Check:** `Builds/Android/PawNoon.apk` exists, and `build_android.log` contains `[PawNoon] Build succeeded`.

Install over USB with Unity's bundled adb:
```bash
<Unity Editor folder>/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb install -r Builds/Android/PawNoon.apk
```

Settings applied by `PawProjectSetup.cs`:
- Package `com.pawnoon.pixel`
- IL2CPP, ARM64, min API 26
- Portrait only, Gamma colour space, splash off

The APK is signed with Unity's **debug key**. That's fine for testing. For Google Play you need a release keystore (Player Settings → Publishing Settings) and an AAB (`EditorUserBuildSettings.buildAppBundle = true`).

---

## 4. Where things live (for modifying)

```
Assets/PawNoon/
  Code/Core/        engine-free duel logic: clock, match rules, damage, bot, PvP-ready protocol (unit-tested)
  Code/Runtime/     Unity presentation: DuelDirector (choreography), DuelCues (3-2-1 / DRAW / ECG),
                    EcgMonitor (procedural heart monitor), SwipeDraw (holster swipe input),
                    DuelHud, PixelPipeline (pixel-perfect upscale, vignette, zoom), PawAudio, Screens
  Code/Editor/      PawMenu (Paw Noon menu + builds), PawProjectSetup (auto-config), import rules
  Code/Tests/       EditMode tests (Window → General → Test Runner)
  Resources/Art/    all sprites (pixel art, 270-px-wide grid, point filtered)
  Resources/Audio/  Music/, Loops/, SFX/  (OGG / WAV, 48 kHz)
  Resources/Data/duel_config.json   ← tuning: hold times, damage, bot, vignette, match intro on/off
  Resources/Data/characters.json    ← each cat's name, meow signature and banter lines (Whiskers, Pawtruth)
  Resources/Shaders/PixelUpscale.shader
Tools/ArtGen, Tools/AudioGen        Python scripts that produced the art and the music/SFX
Docs/                               architecture, timing model, decisions
```

**The fastest way to tweak gameplay** is to edit `Assets/PawNoon/Resources/Data/duel_config.json` (hold min/max, damage curve, bot reaction, tie window, vignette strength). The cats' taunts live in `Resources/Data/characters.json`. You don't need any code changes for that.

### House rules (keep the game feeling right)
- **Pixel art only.** Everything sits on the virtual pixel grid (360×640 canvas, 270-px-wide core art). Use point filtering and no smooth alpha glows. Sprites are positioned on whole pixels.
- **Timing is sacred.** Reaction time comes from input-event timestamps against the estimated photon time of the DRAW cue (`Docs/TIMING.md`). Presentation code must never change match timing.
- **PvP-ready.** The authority owns the schedule (`DrawCue`), clients send `ShotReport`, and the bot uses the same path. Keep new logic in `Code/Core` when it affects outcomes.
- UI is drawn on a separate overlay layer (layer 30) so it doesn't zoom or shake with the world.

### Files you can ignore
- `Assets/_Lead/`: an editor-side file bridge the original author used to drive Unity remotely. It's harmless, and you can delete it.
- `Assets/Plugins/NuGet/`: the DLLs of the optional MCP editor bridge (`com.ivanmurzak.unity.mcp` in `Packages/manifest.json`: McpPlugin, ReflectorNet, SignalR, System.Text.Json ...). Nothing in `Assets/PawNoon` references them. Keep them while you use the MCP bridge; to drop it, remove the manifest line and this folder together. Check their import settings before a release build: a DLL whose `.meta` enables **Any** platform ships in the player (REVIEW pass 2, D1).

---

## 5. Troubleshooting

| Symptom | Fix |
|---|---|
| "Android module not installed" / Build Android greyed out | Install Android Build Support + SDK/NDK + OpenJDK for **6000.6.4f1** in Unity Hub |
| Pink/magenta sprites or no sound | Reimport: right-click `Assets/PawNoon/Resources` → Reimport |
| Swipe doesn't fire in the Editor | Drag **upward from the bottom quarter** of the Game view, don't just click |
| Phone not detected | Re-plug, accept the USB debugging prompt, and check that `adb devices` lists it |
| Gradle build failed | Edit → Preferences → External Tools: make sure "installed with Unity" is ticked for JDK/SDK/NDK/Gradle |
| Wrong orientation or settings | **Paw Noon → Reconfigure Project** |
