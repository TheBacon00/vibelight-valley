# Vibelight Valley: how to test

Already installed: BepInEx be.788 + `BepInEx\plugins\VibelightValley\`. Config: `BepInEx\config\vibelight.valley.cfg` (Mode = Stereo).

## Steps
1. Connect the headset to SteamVR first (Steam Link / VRLink) until SteamVR shows it as ready.
   You can also connect it while the game is running: the mod retries every 10 s.
2. Start DDLV from Steam. Leave the display mode at borderless/windowed.
3. In the headset:
   - **Title screen / loading:** you should see a floating screen showing the game window.
   - **In the valley:** you should see the 3D world from where the normal camera hovers, with head tracking.
4. If the world looks wrong, press **F12** to show the test pattern. The correct image has:
   - the **top half GREEN** and the bottom half RED
   - a **white square top-left in the left eye and top-right in the right eye**
   If it's upside down, press **F6**. Press F12 again to go back to the world.
5. Close the game and tell me what you saw. I read the logs directly from this PC.

## Hotkeys (keyboard, game window focused)
| Key | Action |
|---|---|
| F4 / click both sticks | HUD layer on/off (game UI as a see-through layer) |
| F5 / Num5 | Recenter (look straight ahead first) |
| F6 / F7 | Flip eye images / flip virtual screen vertically |
| F8 | Toggle the virtual screen (menus, inventory) |
| F9 | Dump cameras/canvases/volumes to the log |
| F10 | Save the eye/screen images as PNGs (BepInEx\VibelightValley\dumps) |
| F11 | VR rendering on/off |
| F12 | Test pattern on/off |
| Num8 / Num2 | Viewpoint forward / back (0.25 m) |
| Num9 / Num3 | Viewpoint up / down (0.25 m) |
| Num+ / Num- | World scale (head-movement multiplier) |

Changes from hotkeys are saved to the config.

## Useful things to report
- Black, grey or SteamVR-grid view? Upside down? Colours too dark or too washed out?
- Does it judder? Is the view at a sensible height and scale?
- Do menus/inventory appear on the virtual screen after pressing F8?

## Logs
- `…\Disney Dreamlight Valley\BepInEx\VibelightValley\vibelight-valley.log` (main log; the previous run is in `vibelight-valley.prev.log`)
- `…\Disney Dreamlight Valley\BepInEx\LogOutput.log`
- `%USERPROFILE%\AppData\LocalLow\Gameloft\Disney Dreamlight Valley\Player.log`

## On/off, update, uninstall
Use `VibelightValley-Installer.exe` (from the release, or `dist\` after `tools\package.ps1`):
- **In the window:** the *Load VR when the game starts* switch. "Off" flips `enabled` in `doorstop_config.ini`, so BepInEx and the mod are not loaded at all and the game starts completely unmodified. It applies at the next start, and it switches off every BepInEx plugin, not just Vibelight Valley.
- **From a script:** `VibelightValley-Installer.exe on|off|toggle|status|install|uninstall [--game-dir "..."]`.
  - Exit codes: 0 = on/ok, 1 = off, 2 = error.
- **Turn VR off but keep the mod loaded:** set `Mode = Diagnostics` in the config.