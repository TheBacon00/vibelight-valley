using BepInEx.Configuration;

namespace VibelightValley.Core;

public enum VrMode
{
    /// <summary>Plugin loads and writes diagnostics; no OpenXR session.</summary>
    Diagnostics,
    /// <summary>OpenXR session showing a test image per eye (checks orientation and colours).</summary>
    TestPattern,
    /// <summary>Play the game in VR.</summary>
    Stereo,
}

public enum ScreenMode { Off, Auto, Always }

public enum DesktopView
{
    /// <summary>The desktop window shows the left eye (cropped) with the UI on top; the game camera renders nothing.</summary>
    Mirror,
    /// <summary>The desktop window shows only the UI on black; the game camera renders nothing.</summary>
    Minimal,
    /// <summary>The game camera renders the world for the desktop as usual (costs a third world render).</summary>
    Full,
}

public enum HudPlacement
{
    /// <summary>In front of your VR viewpoint, tilted like the game camera; stays put when the camera orbits.</summary>
    View,
    /// <summary>Exactly in the game camera's view: icons line up with objects, but the panel swings with the camera.</summary>
    GameCamera,
}

public enum FollowMode
{
    /// <summary>Viewpoint at a fixed height and distance behind the character; only the camera's turning is used.</summary>
    Character,
    /// <summary>Viewpoint rides the game camera (including its up/down tilt arc).</summary>
    GameCamera,
}

/// <summary>All user settings (BepInEx/config/vibelight.valley.cfg). Hotkey changes are written back automatically.</summary>
public static class Settings
{
    // General
    public static ConfigEntry<VrMode> Mode;
    public static ConfigEntry<float> ConnectRetrySeconds;

    // Rendering
    public static ConfigEntry<float> RenderScale;
    public static ConfigEntry<float> RefreshRate;
    public static ConfigEntry<bool> PreferSrgbSwapchain;
    public static ConfigEntry<bool> FlipEyesY;
    public static ConfigEntry<bool> FlipScreenY;
    public static ConfigEntry<float> NearClip;
    public static ConfigEntry<bool> DisableVSync;
    public static ConfigEntry<string> EyeDisabledFeatures;
    public static ConfigEntry<DesktopView> Desktop;
    public static ConfigEntry<bool> SymmetricEyes;
    public static ConfigEntry<bool> EyeShadows;
    public static ConfigEntry<float> EyeDrawDistance;

    // Comfort
    public static ConfigEntry<bool> FollowPitch;
    public static ConfigEntry<float> WorldScale;
    public static ConfigEntry<float> AnchorForward;
    public static ConfigEntry<float> AnchorUp;
    public static ConfigEntry<float> HeightSmoothing;
    public static ConfigEntry<FollowMode> Follow;
    public static ConfigEntry<float> FollowDistance;
    public static ConfigEntry<float> FollowHeight;
    public static ConfigEntry<float> TurnSmoothing;
    public static ConfigEntry<float> OrbitDistance;
    public static ConfigEntry<float> Vignette;

    // HUD / virtual screen
    public static ConfigEntry<bool> Hud;
    public static ConfigEntry<float> HudDistance;
    public static ConfigEntry<HudPlacement> HudPlacementMode;
    public static ConfigEntry<float> HudPitchOffset;
    public static ConfigEntry<bool> DesktopMenus;
    public static ConfigEntry<float> EyeShadowDistance;
    public static ConfigEntry<float> EyeLodBias;
    public static ConfigEntry<ScreenMode> Screen;
    public static ConfigEntry<float> ScreenDistance;
    public static ConfigEntry<float> ScreenWidth;
    public static ConfigEntry<string> MenuCameraMarkers;

    // Input
    public static ConfigEntry<string> HudToggleButtons;

    // Diagnostics
    public static ConfigEntry<bool> ProbeOpenXR;
    public static ConfigEntry<bool> VerboseLogging;
    public static ConfigEntry<int> DetailedFrameTraceCount;
    public static ConfigEntry<bool> EnableValidationLayer;

    public static void Bind(ConfigFile cfg)
    {
        Mode = cfg.Bind("General", "Mode", VrMode.Stereo,
            "Stereo = play in VR. TestPattern = test image per eye (orientation/colour check). Diagnostics = log only, no headset output.");
        ConnectRetrySeconds = cfg.Bind("General", "ConnectRetrySeconds", 10f,
            "If the headset/SteamVR is not available, retry connecting every N seconds.");

        RenderScale = cfg.Bind("Rendering", "RenderScale", 1.0f,
            new ConfigDescription("Multiplier on SteamVR's recommended per-eye resolution. PageUp / PageDown change it live in steps of 0.1.",
                new AcceptableValueRange<float>(0.4f, 2.0f)));
        RefreshRate = cfg.Bind("Rendering", "RefreshRate", 90f,
            "Headset refresh rate (Hz) to request from SteamVR when VR starts; the game then runs at this frame rate. " +
            "The nearest rate the headset supports is used (see the log). 0 = keep whatever SteamVR is set to.");
        PreferSrgbSwapchain = cfg.Bind("Rendering", "PreferSrgbSwapchain", true,
            "Use an sRGB swapchain (correct for Unity's linear colour space). Set false if the headset image looks washed out or too dark.");
        FlipEyesY = cfg.Bind("Rendering", "FlipEyesY", true,
            "Flip the eye images (and HUD) vertically before submitting. Hotkey F6. Change if the world is upside down.");
        FlipScreenY = cfg.Bind("Rendering", "FlipScreenY", false,
            "Flip the virtual screen vertically. Hotkey F7. Change if menus are upside down.");
        NearClip = cfg.Bind("Rendering", "NearClip", 0.05f, "Eye camera near clip plane in metres (multiplied by WorldScale).");
        Desktop = cfg.Bind("Rendering", "DesktopView", DesktopView.Mirror,
            "What the desktop window shows while VR renders the world. Mirror = left eye + UI, Minimal = UI only (both skip the game's own " +
            "world render, a big performance win). Full = the normal game view (renders the world a third time).");
        SymmetricEyes = cfg.Bind("Rendering", "SymmetricEyeFrustum", true,
            "Render each eye with a symmetric frustum that contains the headset's view, with matching field of view/aspect. " +
            "Effects that rebuild view directions from the camera's FOV (the game's Weather sky/clouds) then match between the eyes.");
        EyeShadows = cfg.Bind("Rendering", "EyeShadows", true,
            "Render shadows in the headset. Each eye renders its own shadow maps; false saves a lot of CPU/GPU in busy scenes.");
        EyeShadowDistance = cfg.Bind("Rendering", "EyeShadowDistance", 40f,
            "Shadow distance for the eyes in game metres (the game uses 80; 0 = same as the game). Each eye renders its own " +
            "shadow maps, so a shorter distance saves a lot of CPU and GPU time in busy scenes.");
        EyeLodBias = cfg.Bind("Rendering", "EyeLodBias", 0f,
            "Level-of-detail multiplier for the eyes. Objects switch detail / pop in based on their size on screen, and the eyes' " +
            "wide field of view makes everything look smaller than in the 40 degree game camera. 0 = automatic (matches the game's " +
            "pop-in distances), otherwise e.g. 2 = twice the game's LOD bias. Higher = farther pop-in, more GPU/CPU.");
        EyeDrawDistance = cfg.Bind("Rendering", "EyeDrawDistance", 0f,
            "Far clip distance for the eyes in game metres (0 = same as the game camera). Lower values draw fewer distant objects (faster).");
        EyeDisabledFeatures = cfg.Bind("Rendering", "EyeDisabledFeatures", "",
            "Game render effects to switch off in the headset only (';'-separated): Weather, Highlight, Grass Render Feature, " +
            "FilmEffectRenderFeature, PainterlyEffectRenderFeature. Use F3 in game to try them one at a time (the log says which is off).");
        DisableVSync = cfg.Bind("Rendering", "DisableVSync", true,
            "Turn off desktop VSync while VR runs (the headset paces the game). Strongly recommended.");

        FollowPitch = cfg.Bind("Comfort", "FollowPitch", false,
            "Also tilt the view up/down with the game camera. False (recommended) keeps the horizon level; look down at your character instead.");
        WorldScale = cfg.Bind("Comfort", "WorldScale", 1.0f,
            new ConfigDescription("Head-movement multiplier. >1 makes you feel bigger (world looks smaller). Hotkeys: Num+ / Num-.", new AcceptableValueRange<float>(0.1f, 20f)));
        AnchorForward = cfg.Bind("Comfort", "AnchorForward", 0f,
            "Move the viewpoint forward (+) / back (-) relative to the game camera, metres. Hotkeys: Num8 / Num2.");
        AnchorUp = cfg.Bind("Comfort", "AnchorUp", 0f,
            "Move the viewpoint up (+) / down (-) relative to the game camera, metres. Hotkeys: Num9 / Num3.");
        Follow = cfg.Bind("Comfort", "FollowMode", FollowMode.Character,
            "Character = hover at a fixed height/distance behind your character; the game camera's up/down tilt is ignored completely, only its turning is used. " +
            "GameCamera = ride the game camera itself.");
        FollowDistance = cfg.Bind("Comfort", "FollowDistance", 6f, "Character mode: distance behind the character (game metres). Num8/Num2 also adjust it.");
        FollowHeight = cfg.Bind("Comfort", "FollowHeight", 3.0f, "Character mode: viewpoint height above the character's feet (game metres). Num9/Num3 also adjust it.");
        HeightSmoothing = cfg.Bind("Comfort", "HeightSmoothing", 1.5f,
            new ConfigDescription("Seconds over which the viewpoint follows the game camera's height (it swings a lot when the right stick tilts the camera). " +
                                  "Larger = gentler. 0 = follow instantly.", new AcceptableValueRange<float>(0f, 10f)));
        TurnSmoothing = cfg.Bind("Comfort", "TurnSmoothing", 0.5f,
            new ConfigDescription("Seconds over which the view follows the game camera turning left/right (right stick). Larger = slower, gentler turns. 0 = instant.",
                new AcceptableValueRange<float>(0f, 5f)));
        OrbitDistance = cfg.Bind("Comfort", "OrbitDistance", 6f,
            "Approximate distance (game metres) from the game camera to your character. Smoothed turns orbit around that point so the character stays in view.");
        Vignette = cfg.Bind("Comfort", "Vignette", 0.7f,
            new ConfigDescription("Darken the edges of your view while it turns (0 = off, 1 = strongest).", new AcceptableValueRange<float>(0f, 1f)));

        Hud = cfg.Bind("HUD", "Enabled", true,
            "Show the game's screen UI (interaction icons, HUD, overlay menus) as a see-through layer covering the game camera's view. Toggle: F4 or both sticks.");
        HudPlacementMode = cfg.Bind("HUD", "Placement", HudPlacement.View,
            "View = the HUD stays in front of you at a fixed tilt whatever the camera does. GameCamera = exactly in the game camera's view " +
            "(icons line up with objects, but the panel swings around the character when the camera turns).");
        HudPitchOffset = cfg.Bind("HUD", "PitchOffset", -15f,
            "View placement: the HUD's fixed angle below (negative) or above (positive) the horizon, in degrees. It does not follow " +
            "the game camera's up/down tilt. Num1 / Num7 change it in 2.5 degree steps.");
        DesktopMenus = cfg.Bind("HUD", "ShowMenusOnDesktop", true,
            "Draw the game's menus on the desktop window too (for people watching the screen). In the headset the menu UI is then " +
            "drawn twice at the same place, which can make semi-transparent panels slightly more opaque. False = desktop shows only the menu backdrop.");
        HudDistance = cfg.Bind("HUD", "Distance", 6f,
            "Distance (game metres) of the HUD layer in the game camera's view. About the distance to your character feels best.");

        Screen = cfg.Bind("VirtualScreen", "Mode", ScreenMode.Auto,
            "Floating copy of the game window. Auto = shown on the title screen, while loading, in full-screen menus, or when toggled with F8.");
        ScreenDistance = cfg.Bind("VirtualScreen", "Distance", 1.6f, "Distance of the virtual screen in metres.");
        ScreenWidth = cfg.Bind("VirtualScreen", "Width", 1.8f, "Width of the virtual screen in metres.");
        MenuCameraMarkers = cfg.Bind("VirtualScreen", "MenuCameraMarkers", "UiRoot/",
            "If the game's main camera path contains any of these (';'-separated), it is a menu: world rendering pauses and the virtual screen is shown.");

        HudToggleButtons = cfg.Bind("Input", "HudToggleButtons", "Left Stick Button;Right Stick Button",
            "Two gamepad buttons (Rewired names, ';'-separated) that toggle the HUD when pressed together. The log lists your controller's button names. Empty = off.");

        ProbeOpenXR = cfg.Bind("Diagnostics", "ProbeOpenXR", true,
            "In Diagnostics mode, briefly create an OpenXR instance to log runtime/system info (may start SteamVR).");
        VerboseLogging = cfg.Bind("Diagnostics", "VerboseLogging", true,
            "Write verbose lines (frame traces, per-object details) to BepInEx/VibelightValley/vibelight-valley.log.");
        DetailedFrameTraceCount = cfg.Bind("Diagnostics", "DetailedFrameTraceCount", 300,
            "How many frames after VR starts get a per-frame trace in the log.");
        EnableValidationLayer = cfg.Bind("Diagnostics", "EnableOpenXRValidationLayer", false,
            "Enable the Khronos core_validation API layer (slow; for debugging OpenXR errors).");
    }
}
