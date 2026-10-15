> Original design notes and plan from the first development session, written when the project was still called DDLV-VR (now Vibelight Valley). Kept for background; the code and README are authoritative.

# DDLV-VR — OpenXR VR mod for Disney Dreamlight Valley

## Status (2026-10-14)
- **M0 ✅** BepInEx be.788 boots and the interop assemblies are generated (about 60 s on first launch).
- **M1 ✅**
  - Every needed Unity API works, nothing is stripped.
  - D3D11 device found (FL 11_1, RTX 5080).
  - Render-thread plugin events and the fence work.
  - The OpenXR loader finds SteamVR and `XR_KHR_D3D11_enable`.
  - The gameplay camera is `SystemRoot/PlayerCamera` (FOV 40, URP post-processing, FXAA); it's the only camera rendering in the valley.
  - The title screen renders no camera (UI only).
- **M2/M3 written, not yet tested with a headset.**
  - Code done: session, swapchains, render-thread submit, eye rig, virtual screen, test pattern.
  - Without a headset it retries the connection cleanly every 10 s.
  - See [TESTING.md](TESTING.md).

Seated/standing 6DOF head tracking, stereoscopic rendering to a SteamVR headset via OpenXR,
gamepad control unchanged, viewpoint "hovering" where the normal game camera is.

---

## 1. What we know about the game (read from disk, 2026-10-14)

| Fact | Value | Source | Consequence |
|---|---|---|---|
| Engine | Unity **6000.0.62f1** (Unity 6) | `UnityPlayer.dll` version, `Player.log` | Legacy built-in VR (OpenVR "VR SDKs") is gone in Unity 6 — must bring our own VR path |
| Scripting backend | **IL2CPP** (`GameAssembly.dll`, 166 MB) | game folder | Needs BepInEx 6 (IL2CPP) + Il2CppInterop |
| Metadata | `global-metadata.dat` magic `FAB11BAF`, **v31**, not encrypted | header bytes | Cpp2IL/BepInEx can generate interop assemblies; no deobfuscation needed |
| GameAssembly | normal PE sections (`.text`, `il2cpp`, `.rdata`…), `il2cpp_*` exports present | PE header | No packer seen (ConSimVR's "wrapped exports" problem probably doesn't apply) |
| Render pipeline | **URP** (`Unity.RenderPipelines.Universal.Runtime`, plus custom `Unity.Pipeline.dll`) | `ScriptingAssemblies.json` | Per-eye rendering must go through URP; HDRP-specific tricks don't apply |
| Graphics API | **Direct3D 11.0, FL 11.1** (D3D12 Agility SDK is shipped but unused by default) | `Player.log` | Target D3D11 + `XR_KHR_D3D11_enable`; mod refuses cleanly on D3D12 |
| Render threading | `ClientWorkerJobs` (separate render thread) | `Player.log` | All D3D11 / `xrEndFrame` work must run on Unity's render thread via plugin events |
| XR modules | `UnityEngine.XRModule`, `VRModule`, `SubsystemsModule` present | `ScriptingAssemblies.json` | Not used by the chosen design (see §2), but available |
| Input | Rewired | Plugins | Untouched — the gamepad keeps working |
| Anti-cheat | none found (EOS SDK is present, but no EAC/BattlEye folder) | Plugins | Code injection is possible; still, keep VR off in multiplayer valley visits at first |
| GPU / display | RTX 5080, 3440×1440 | logs | Plenty of headroom for 3 renders/frame |
| OpenXR runtime | **SteamVR** (`steamxr_win64.json` is the active runtime) | registry | `xrCreateInstance` will start SteamVR if it isn't running |

## 2. Research: ways to put a Unity IL2CPP game into VR

1. **Re-enable Unity's own XR stack** (inject `UnityOpenXR.dll`, `UnitySubsystemsManifest.json`, XR Management).
   Needs managed XR-Management + OpenXR-package C# code that was never compiled into this IL2CPP build,
   and URP's XR code paths (`ENABLE_VR && ENABLE_XR_MODULE`) are likely compiled out or stripped. Fragile → **rejected**.
2. **Generic injectors** (UEVR-style for Unity, "UUVR", UnityVRMod). UnityVRMod is the closest
   (BepInEx 6, IL2CPP, C# OpenXR, D3D11 only). It's a beta: it does GPU copies on the main thread,
   which races Unity's render thread; it has no URP-specific handling, no UI solution, and it's GPL-3.0.
   Good reference, but not a base to build on.
3. **Custom mod: our own OpenXR session + extra Unity cameras per eye + copy to the OpenXR swapchain on the render thread.**
   This is what ConSimVR (a working IL2CPP/D3D11/BepInEx 6 VR mod) does, and UnityVRMod is a simpler
   version of the same thing. Every piece uses public Unity API, so it's the most debuggable route. → **chosen**.

References:
- ConSimVR (OpenXR on IL2CPP + D3D11 + BepInEx 6, render-thread design): https://github.com/nick10180/ConSimVR
- UnityVRMod (C# OpenXR, eye cameras, FOV→projection math): https://github.com/NewUnityModder/UnityVRMod
- BepInEx bleeding edge builds (IL2CPP needs these; newest is **be.788**, 2026-09-01, which can survive stripped Unity members): https://builds.bepinex.dev/projects/bepinex_be
- OpenXR loader (Khronos, newest 1.1.63): https://github.com/KhronosGroup/OpenXR-SDK-Source/releases
- A 2024 DDLV Nexus report says MelonLoader stopped working and a *specific* BepInEx build plus a config change was needed → test the loader **first**, before any VR code.

## 3. Architecture

```
ddv.exe
 └─ BepInEx 6 (IL2CPP, be.788) ── loads ── DDLV-VR.dll (C#, .NET 6, Il2CppInterop)
       ├─ XrRuntime      : openxr_loader.dll (shipped next to the plugin), C# function pointers
       ├─ D3D11Interop   : raw vtable calls (GetDevice, CopyResource, LUID check). No C++ needed
       ├─ RenderThread   : [UnmanagedCallersOnly] callback fired by GL.IssuePluginEvent
       ├─ VrRig          : 2 hidden URP eye cameras that follow the game's main camera
       ├─ UiPanel        : optional virtual screen (OpenXR quad layer) for menus
       └─ Diagnostics    : log file, periodic stats, dump hotkeys, never-crash wrappers
```

### 3.1 Frame flow (one Unity frame = one OpenXR frame)

1. **Update (main thread)**: `xrPollEvent` → session state machine (READY → `xrBeginSession`, STOPPING → `xrEndSession`, EXITING/LOSS_PENDING → shut down VR, the game keeps running flat).
2. **`RenderPipelineManager.beginContextRendering`** (main thread, after all `LateUpdate`s, so the game camera has already moved):
   - `xrWaitFrame` (this blocks, which paces the game to the headset refresh) → `xrLocateViews(predictedDisplayTime)`.
   - Anchor = game main camera position + **yaw only** (pitch/roll from the game camera optional, off by default to avoid nausea).
   - Eye camera pose = anchor · recenter⁻¹ · headPose(eye) · worldScale. Projection matrix built from `XrFovf` (asymmetric frustum).
   - Queue the `BeginFrame` render-thread event.
3. URP renders the **two eye cameras** (depth = main−1, `targetTexture` = per-eye RT), then the **normal main camera** (desktop view, UI, untouched game logic).
4. **`endContextRendering`** (main thread) → queue the `EndFrame` render-thread event:
   - Render thread: acquire/wait each eye's swapchain image → `CopyResource(swapchainImage, submitRT)` → release → `xrEndFrame` with a projection layer (+ the optional UI quad layer).
5. Safety nets taken from ConSimVR:
   - Every successful `xrWaitFrame` is **always** paired with begin+end (zero layers if nothing rendered), otherwise the next wait deadlocks.
   - A frame that doesn't finish is flushed at end of frame.
   - A render-thread fence runs before any teardown.

### 3.2 Eye rendering details (URP)

- Eye cameras: `Camera.CopyFrom(main)` + copy `UniversalAdditionalCameraData` (renderer index, post-processing, AA mode, volume layer mask/trigger, shadows, depth/opaque textures). Settings are re-synced if the game changes them (scene loads).
- Eye RT = render-target format the camera wants (HDR handled inside URP) → **submit RT** (8-bit sRGB, exactly the swapchain format) via a blit that can **flip Y**. The flip is runtime-toggleable because D3D RT orientation under URP can't be known ahead of time.
- Swapchain: prefer `R8G8B8A8_UNORM_SRGB` / `B8G8R8A8_UNORM_SRGB` matched to an sRGB `RenderTexture`. Usage = color | transfer_dst | sampled. One swapchain per eye. Size = recommended × `RenderScale` (config).
- Native textures are AddRef'd, and RTs are re-validated every frame (`IsCreated`) because scene loads can release them.
- Post effects that are bad in VR (depth of field, motion blur, lens distortion, chromatic aberration, panini, vignette) get turned off for eye cameras only, via a high-priority runtime `Volume` on a private layer (**phase 2**).
- **Desktop window keeps showing the normal game** (phase 1). Option for later: replace the desktop render with a left-eye mirror to save GPU time.

### 3.3 "Hover behind the character"

- The game's own third-person camera drives the rig, so the viewpoint sits exactly where the flat game puts it and follows the character and right-stick orbit.
- Your head movement (6DOF) is added on top: lean in to look closer, look around freely.
- Hotkeys (keyboard, so the gamepad isn't affected): recenter, move anchor forward/back/up/down, world scale ±, toggle pitch-follow, toggle UI panel, flip Y.
  Values are saved to the BepInEx config.
- Near clip is scaled so leaning close to the character doesn't clip.

### 3.4 UI (menus, inventory, dialogue)

DDLV draws its UI as screen-space overlay canvases, which **won't appear in the eye cameras**. Plan:

- **Phase 1:** "virtual screen" — `ScreenCapture.CaptureScreenshotIntoRenderTexture` of the desktop (game + UI) shown as an OpenXR **quad layer** ~2 m in front of you.
  Toggled with a hotkey, and optionally automatic when the game shows the cursor or pauses world input. Always works, so menus are never unusable.
- **Phase 2:** "UI-only overlay" — main camera clears to transparent and draws only UI, that capture becomes an alpha-blended quad; the desktop shows the left eye. Small HUD (quest tracker, hotbar) floats in view.

## 4. Logging & diagnostics (built in from the first build)

- `BepInEx/LogOutput.log` **plus** `BepInEx/DDLV-VR/ddlv-vr.log`.
  - Millisecond timestamps, thread id, Unity frame number on every line.
  - The file is flushed every line, so the tail survives a crash.
  - The previous log is rotated to `.prev.log`.
- **Startup report**:
  - Unity version, graphics device and type, color space, render threading, URP asset and renderer list, screen mode.
  - OpenXR runtime name/version, system name, view config, recommended sizes, offered swapchain formats, chosen format.
  - D3D device feature level, adapter LUID compared with the runtime's LUID.
- **Every** OpenXR call goes through `Check(result, "xrX")`, which logs the result enum name. Nothing fails silently.
- **Lifecycle traces**: session state changes, scene loads, camera (re)discovery, RT (re)creation.
  - The first 300 frames of the frame loop are traced in detail; after that, a 5-second stats line with fps, xrWaitFrame time, shouldRender ratio, dropped/flushed frames and copy failures.
- **Render-thread errors** go to a lock-free queue, are logged by the main thread, and never cross into IL2CPP objects.
- **Never crash the game**:
  - Every hook is wrapped in try/catch.
  - Repeated errors (more than N in a row) switch VR off and leave the flat game running, with a clear "VR DISABLED because …" line.
- **Hotkeys**:
  - `F9`: dump all cameras, URP data and active volumes, plus the scene hierarchy around the main camera.
  - `F10`: save both eye RTs and the swapchain copy as PNGs, which answers "black/upside-down/wrong colours".
- **API probe at startup**:
  - Checks that `GL.IssuePluginEvent`, `RenderPipelineManager` events, `Camera.CopyFrom`, `ScreenCapture` and the URP types exist in the interop assemblies.
  - If a wrapper was stripped, it falls back to resolving the engine icall directly (`il2cpp_resolve_icall`). Each fallback is logged.

## 5. Risk register (what could stop it working and the answer)

| # | Risk | Likelihood | Mitigation / fallback |
|---|---|---|---|
| R1 | BepInEx won't boot on this Unity 6 build | Medium | Use be.788 (has fixes for stripped `Application`/log members). Set `UnityLogListening=false`. **Milestone 0 tests only the loader.** Fallback: MelonLoader 0.7.x; the core code stays loader-agnostic |
| R2 | Needed Unity method stripped from the build | Medium | Interop probe plus `il2cpp_resolve_icall` fallback; the engine registers icalls even when the C# wrapper is stripped |
| R3 | Eye cameras aren't rendered or look wrong because of custom URP features in `Unity.Pipeline.dll` | Medium | F9 dump. Alternate mode: manual `RenderPipeline.SubmitRenderRequest` per eye. Option to drop specific renderer features on eye cams |
| R4 | Image upside down / colours too dark or bright | High on first try | Runtime flip toggle; sRGB/UNORM swapchain choice in config; F10 PNG dump to check |
| R5 | `xrWaitFrame` deadlock / frozen headset | Low (pairing rules) | Strict wait/begin/end pairing, end-of-frame flush, watchdog log if a frame is still open after 1 s |
| R6 | Exclusive fullscreen → white headset (seen in ConSimVR) | Medium | Force borderless/windowed while VR runs; log the screen mode |
| R7 | Framerate below the headset rate | Low (RTX 5080) | `RenderScale` config; SteamVR motion smoothing; optional desktop mirror instead of a 3rd render |
| R8 | Menus unusable | Certain without UI work | Phase-1 virtual screen ensures menus are always usable |
| R9 | Game camera cuts / cutscenes cause jarring jumps | Medium | Smooth or teleport-fade on large anchor jumps (phase 2); recenter hotkey |
| R10 | Game update breaks the mod | Over time | Uses only public Unity API plus the camera; no game-class hooks in phase 1 |

## 6. Milestones (each one is a test you run and report back on)

- **M0: loader smoke test.** Install BepInEx be.788 and launch once (it generates interop assemblies; the first launch takes minutes). Expected: `LogOutput.log` shows "Chainloader startup complete". *I need this before I can compile anything*, because the mod builds against the generated interop DLLs.
- **M1: diagnostics-only plugin.** Loads, writes the startup report, probes the APIs, and F9 dumps cameras. No VR yet. This shows us DDLV's camera setup and whether anything is stripped.
- **M2: OpenXR session + head-locked test.** Instance, session and swapchains on the game's D3D11 device. Both eyes get a solid colour/gradient via the render thread. Proves the OpenXR/D3D11/render-thread path with SteamVR.
- **M3: stereo world.** Eye cameras follow the game camera with 6DOF. Check orientation/colour with F10; tune scale and anchor.
- **M4: UI virtual screen + comfort hotkeys + settings persistence.**
- **M5: polish.** VR-hostile post effects off, UI-only overlay, cutscene handling, performance options.

## 7. Toolchain I'll set up (nothing is installed on this PC yet: no .NET SDK, git, VS or Python)

| Item | Why | How |
|---|---|---|
| .NET 8 SDK | compile the C# plugin (target `net6.0`, which BepInEx 6 IL2CPP runs on) | `dotnet-install.ps1` into `D:\VR\DDLV-VR\.tools\dotnet` (no admin, nothing system-wide) |
| BepInEx 6 be.788 IL2CPP win-x64 | mod loader | extract into the game folder (adds `winhttp.dll`, `doorstop_config.ini`, `BepInEx\`, `dotnet\`) — removable by deleting those |
| `openxr_loader.dll` (Khronos 1.1.x, x64) | talks to SteamVR's OpenXR runtime | from the Khronos release or the `OpenXR.Loader` NuGet package; shipped in `BepInEx\plugins\DDLV-VR\` |
| No C++ compiler needed | D3D11 calls are done in C# via vtables, as ConSimVR does | — |

Project layout (in `D:\VR\DDLV-VR`):
```
src/DDLV-VR/               C# plugin (Plugin.cs, Xr/, D3D/, Rig/, Ui/, Diagnostics/)
lib/                       references copied from the game's BepInEx/interop + BepInEx/core
tools/                     build.ps1 (build + copy into the game), uninstall.ps1
PLAN.md                    this file
```

## 8. Testing instructions (for M0+)

1. Turn off the game's exclusive fullscreen and use borderless/windowed. Keep other D3D hook add-ons (ReShade etc.) disabled while testing.
2. Start SteamVR (optional; OpenXR will start it).
3. Launch DDLV from Steam. On the first BepInEx launch, wait at the black/console screen while interop is generated.
4. After each test, send me (or I read directly):
   - `…\Disney Dreamlight Valley\BepInEx\LogOutput.log`
   - `…\Disney Dreamlight Valley\BepInEx\DDLV-VR\ddlv-vr.log`
   - `%USERPROFILE%\AppData\LocalLow\Gameloft\Disney Dreamlight Valley\Player.log`
   - SteamVR: `C:\Program Files (x86)\Steam\logs\vrserver.txt` / `vrcompositor.txt` if the headset misbehaves
   I can read all of these myself from this machine, so "it crashed" is enough for a report.
5. Uninstall: delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `BepInEx\`, `dotnet\` from the game folder (or Steam → Verify integrity).

## 9. Confidence

- **High** for the OpenXR session, render-thread submission and stereo eye cameras. Two working IL2CPP/D3D11 mods use the same design, and DDLV has no packer or encryption, runs D3D11 and has no anti-cheat.
- **Medium** for "BepInEx boots on day one". DDLV has a history of picky loader versions, so M0 checks this before any VR code is written.
- **Medium** for "the image looks right on the first try" (flip/colour/post effects), so the runtime toggles and PNG dumps exist to fix it in one iteration.
