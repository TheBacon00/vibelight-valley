using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VibelightValley.Xr;

// Blittable OpenXR 1.0 structures (x64 layout, natural alignment). Only what this mod uses.

public enum XrResult : int
{
    Success = 0, TimeoutExpired = 1, SessionLossPending = 3, EventUnavailable = 4, SpaceBoundsUnavailable = 7,
    SessionNotFocused = 8, FrameDiscarded = 9,
    ErrorValidationFailure = -1, ErrorRuntimeFailure = -2, ErrorOutOfMemory = -3, ErrorApiVersionUnsupported = -4,
    ErrorInitializationFailed = -6, ErrorFunctionUnsupported = -7, ErrorFeatureUnsupported = -8,
    ErrorExtensionNotPresent = -9, ErrorLimitReached = -10, ErrorSizeInsufficient = -11, ErrorHandleInvalid = -12,
    ErrorInstanceLost = -13, ErrorSessionRunning = -14, ErrorSessionNotRunning = -16, ErrorSessionLost = -17,
    ErrorSystemInvalid = -18, ErrorPathInvalid = -19, ErrorPathCountExceeded = -20, ErrorPathFormatInvalid = -21,
    ErrorPathUnsupported = -22, ErrorLayerInvalid = -23, ErrorLayerLimitExceeded = -24, ErrorSwapchainRectInvalid = -25,
    ErrorSwapchainFormatUnsupported = -26, ErrorActionTypeMismatch = -27, ErrorSessionNotReady = -28,
    ErrorSessionNotStopping = -29, ErrorTimeInvalid = -30, ErrorReferenceSpaceUnsupported = -31,
    ErrorFileAccessError = -32, ErrorFileContentsInvalid = -33, ErrorFormFactorUnsupported = -34,
    ErrorFormFactorUnavailable = -35, ErrorApiLayerNotPresent = -36, ErrorCallOrderInvalid = -37,
    ErrorGraphicsDeviceInvalid = -38, ErrorPoseInvalid = -39, ErrorIndexOutOfRange = -40,
    ErrorViewConfigurationTypeUnsupported = -41, ErrorEnvironmentBlendModeUnsupported = -42,
    ErrorNameDuplicated = -44, ErrorNameInvalid = -45, ErrorActionsetNotAttached = -46,
    ErrorActionsetsAlreadyAttached = -47, ErrorLocalizedNameDuplicated = -48, ErrorLocalizedNameInvalid = -49,
    ErrorGraphicsRequirementsCallMissing = -50, ErrorRuntimeUnavailable = -51,
}

public enum XrStructureType : int
{
    Unknown = 0,
    ApiLayerProperties = 1,
    ExtensionProperties = 2,
    InstanceCreateInfo = 3,
    SystemGetInfo = 4,
    SystemProperties = 5,
    ViewLocateInfo = 6,
    View = 7,
    SessionCreateInfo = 8,
    SwapchainCreateInfo = 9,
    SessionBeginInfo = 10,
    ViewState = 11,
    FrameEndInfo = 12,
    EventDataBuffer = 16,
    EventDataInstanceLossPending = 17,
    EventDataSessionStateChanged = 18,
    InstanceProperties = 32,
    FrameWaitInfo = 33,
    CompositionLayerProjection = 35,
    CompositionLayerQuad = 36,
    ReferenceSpaceCreateInfo = 37,
    EventDataReferenceSpaceChangePending = 40,
    ViewConfigurationView = 41,
    SpaceLocation = 42,
    FrameState = 44,
    ViewConfigurationProperties = 45,
    FrameBeginInfo = 46,
    CompositionLayerProjectionView = 48,
    EventDataEventsLost = 49,
    EventDataInteractionProfileChanged = 52,
    SwapchainImageAcquireInfo = 55,
    SwapchainImageWaitInfo = 56,
    SwapchainImageReleaseInfo = 57,
    GraphicsBindingD3D11KHR = 1000027000,
    SwapchainImageD3D11KHR = 1000027001,
    GraphicsRequirementsD3D11KHR = 1000027002,
    EventDataUserPresenceChangedEXT = 1000470000,
    EventDataDisplayRefreshRateChangedFB = 1000101000,
}

public enum XrSessionState : int
{
    Unknown = 0, Idle = 1, Ready = 2, Synchronized = 3, Visible = 4, Focused = 5, Stopping = 6, LossPending = 7, Exiting = 8,
}

public static class XrConst
{
    public const int FormFactorHeadMountedDisplay = 1;
    public const int ViewConfigurationPrimaryStereo = 2;
    public const int ReferenceSpaceView = 1, ReferenceSpaceLocal = 2, ReferenceSpaceStage = 3;
    public const int EnvironmentBlendOpaque = 1;
    public const ulong SwapchainUsageColorAttachment = 0x1, SwapchainUsageTransferSrc = 0x8,
        SwapchainUsageTransferDst = 0x10, SwapchainUsageSampled = 0x20, SwapchainUsageMutableFormat = 0x40;
    public const ulong ViewStateOrientationValid = 1, ViewStatePositionValid = 2, ViewStateOrientationTracked = 4, ViewStatePositionTracked = 8;
    public const ulong SpaceLocationOrientationValid = 1, SpaceLocationPositionValid = 2;
    public const ulong LayerCorrectChromaticAberration = 1, LayerBlendTextureSourceAlpha = 2, LayerUnpremultipliedAlpha = 4;
    public const long InfiniteDuration = 0x7fffffffffffffffL;
    public const int EyeVisibilityBoth = 0;

    public static ulong MakeVersion(ulong major, ulong minor, ulong patch) => (major << 48) | (minor << 32) | patch;
    public static string VersionString(ulong v) => $"{v >> 48}.{(v >> 32) & 0xFFFF}.{v & 0xFFFFFFFF}";
}

[StructLayout(LayoutKind.Sequential)] public struct XrVector3f { public float X, Y, Z; public override string ToString() => $"({X:F3},{Y:F3},{Z:F3})"; }
[StructLayout(LayoutKind.Sequential)] public struct XrQuaternionf { public float X, Y, Z, W; public override string ToString() => $"({X:F3},{Y:F3},{Z:F3},{W:F3})"; }
[StructLayout(LayoutKind.Sequential)]
public struct XrPosef
{
    public XrQuaternionf Orientation; public XrVector3f Position;
    public static XrPosef Identity => new() { Orientation = new XrQuaternionf { W = 1 } };
    public override string ToString() => $"pos{Position} rot{Orientation}";
}
[StructLayout(LayoutKind.Sequential)]
public struct XrFovf
{
    public float AngleLeft, AngleRight, AngleUp, AngleDown;
    public override string ToString() =>
        $"L{AngleLeft * 57.2958f:F1} R{AngleRight * 57.2958f:F1} U{AngleUp * 57.2958f:F1} D{AngleDown * 57.2958f:F1}deg";
}
[StructLayout(LayoutKind.Sequential)] public struct XrOffset2Di { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] public struct XrExtent2Di { public int Width, Height; }
[StructLayout(LayoutKind.Sequential)] public struct XrExtent2Df { public float Width, Height; }
[StructLayout(LayoutKind.Sequential)] public struct XrRect2Di { public XrOffset2Di Offset; public XrExtent2Di Extent; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrApplicationInfo
{
    public fixed byte ApplicationName[128];
    public uint ApplicationVersion;
    public fixed byte EngineName[128];
    public uint EngineVersion;
    public ulong ApiVersion;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrInstanceCreateInfo
{
    public XrStructureType Type; public void* Next;
    public ulong CreateFlags;
    public XrApplicationInfo ApplicationInfo;
    public uint EnabledApiLayerCount; public byte** EnabledApiLayerNames;
    public uint EnabledExtensionCount; public byte** EnabledExtensionNames;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrExtensionProperties
{
    public XrStructureType Type; public void* Next;
    public fixed byte ExtensionName[128];
    public uint ExtensionVersion;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrApiLayerProperties
{
    public XrStructureType Type; public void* Next;
    public fixed byte LayerName[256];
    public ulong SpecVersion;
    public uint LayerVersion;
    public fixed byte Description[256];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrInstanceProperties
{
    public XrStructureType Type; public void* Next;
    public ulong RuntimeVersion;
    public fixed byte RuntimeName[128];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrSystemGetInfo { public XrStructureType Type; public void* Next; public int FormFactor; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrSystemProperties
{
    public XrStructureType Type; public void* Next;
    public ulong SystemId;
    public uint VendorId;
    public fixed byte SystemName[256];
    public uint MaxSwapchainImageHeight, MaxSwapchainImageWidth, MaxLayerCount;
    public uint OrientationTracking, PositionTracking;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrGraphicsRequirementsD3D11KHR
{
    public XrStructureType Type; public void* Next;
    public long AdapterLuid;
    public uint MinFeatureLevel;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrGraphicsBindingD3D11KHR { public XrStructureType Type; public void* Next; public IntPtr Device; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrSessionCreateInfo { public XrStructureType Type; public void* Next; public ulong CreateFlags; public ulong SystemId; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrSessionBeginInfo { public XrStructureType Type; public void* Next; public int PrimaryViewConfigurationType; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrReferenceSpaceCreateInfo
{
    public XrStructureType Type; public void* Next;
    public int ReferenceSpaceType;
    public XrPosef PoseInReferenceSpace;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrViewConfigurationView
{
    public XrStructureType Type; public void* Next;
    public uint RecommendedImageRectWidth, MaxImageRectWidth, RecommendedImageRectHeight, MaxImageRectHeight;
    public uint RecommendedSwapchainSampleCount, MaxSwapchainSampleCount;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrSwapchainCreateInfo
{
    public XrStructureType Type; public void* Next;
    public ulong CreateFlags;
    public ulong UsageFlags;
    public long Format;
    public uint SampleCount, Width, Height, FaceCount, ArraySize, MipCount;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrSwapchainImageD3D11KHR { public XrStructureType Type; public void* Next; public IntPtr Texture; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrSwapchainImageAcquireInfo { public XrStructureType Type; public void* Next; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrSwapchainImageWaitInfo { public XrStructureType Type; public void* Next; public long Timeout; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrSwapchainImageReleaseInfo { public XrStructureType Type; public void* Next; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrFrameWaitInfo { public XrStructureType Type; public void* Next; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrFrameState
{
    public XrStructureType Type; public void* Next;
    public long PredictedDisplayTime;
    public long PredictedDisplayPeriod;
    public uint ShouldRender;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrFrameBeginInfo { public XrStructureType Type; public void* Next; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrFrameEndInfo
{
    public XrStructureType Type; public void* Next;
    public long DisplayTime;
    public int EnvironmentBlendMode;
    public uint LayerCount;
    public void** Layers;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrViewLocateInfo
{
    public XrStructureType Type; public void* Next;
    public int ViewConfigurationType;
    public long DisplayTime;
    public ulong Space;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrViewState { public XrStructureType Type; public void* Next; public ulong ViewStateFlags; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrView { public XrStructureType Type; public void* Next; public XrPosef Pose; public XrFovf Fov; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrSpaceLocation { public XrStructureType Type; public void* Next; public ulong LocationFlags; public XrPosef Pose; }

[StructLayout(LayoutKind.Sequential)]
public struct XrSwapchainSubImage { public ulong Swapchain; public XrRect2Di ImageRect; public uint ImageArrayIndex; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrCompositionLayerProjectionView
{
    public XrStructureType Type; public void* Next;
    public XrPosef Pose;
    public XrFovf Fov;
    public XrSwapchainSubImage SubImage;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrCompositionLayerProjection
{
    public XrStructureType Type; public void* Next;
    public ulong LayerFlags;
    public ulong Space;
    public uint ViewCount;
    public XrCompositionLayerProjectionView* Views;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrCompositionLayerQuad
{
    public XrStructureType Type; public void* Next;
    public ulong LayerFlags;
    public ulong Space;
    public int EyeVisibility;
    public XrSwapchainSubImage SubImage;
    public XrPosef Pose;
    public XrExtent2Df Size;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrEventDataBuffer
{
    public XrStructureType Type; public void* Next;
    public fixed byte Varying[4000];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrEventDataSessionStateChanged
{
    public XrStructureType Type; public void* Next;
    public ulong Session;
    public XrSessionState State;
    public long Time;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrEventDataUserPresenceChangedEXT
{
    public XrStructureType Type; public void* Next;
    public ulong Session;
    public uint IsUserPresent;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct XrEventDataDisplayRefreshRateChangedFB
{
    public XrStructureType Type; public void* Next;
    public float FromDisplayRefreshRate, ToDisplayRefreshRate;
}

public static unsafe class XrStr
{
    public static string Read(byte* p, int max)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return Encoding.UTF8.GetString(p, n);
    }

    public static void Write(byte* dst, int max, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        int n = Math.Min(b.Length, max - 1);
        for (int i = 0; i < n; i++) dst[i] = b[i];
        dst[n] = 0;
    }
}

public static class XrLayoutCheck
{
    /// <summary>Sizes from openxr.h on x64; logged at startup so a layout mistake shows up immediately.</summary>
    public static string Report()
    {
        var sb = new StringBuilder();
        void C<T>(int expected) where T : unmanaged
        {
            int actual;
            unsafe { actual = sizeof(T); }
            sb.Append($"{typeof(T).Name}={actual}{(actual == expected ? "" : $" (EXPECTED {expected}!)")} ");
        }
        C<XrApplicationInfo>(272);
        C<XrInstanceCreateInfo>(328);
        C<XrExtensionProperties>(152);
        C<XrSystemProperties>(304);
        C<XrGraphicsRequirementsD3D11KHR>(32);
        C<XrReferenceSpaceCreateInfo>(48);
        C<XrViewConfigurationView>(40);
        C<XrSwapchainCreateInfo>(64);
        C<XrFrameState>(40);
        C<XrFrameEndInfo>(40);
        C<XrViewLocateInfo>(40);
        C<XrView>(64);
        C<XrCompositionLayerProjectionView>(96);
        C<XrCompositionLayerProjection>(48);
        C<XrCompositionLayerQuad>(112);
        C<XrEventDataBuffer>(4016);
        C<XrEventDataSessionStateChanged>(40);
        return sb.ToString();
    }

    public static bool AllMatch() => !Report().Contains("EXPECTED");
}
