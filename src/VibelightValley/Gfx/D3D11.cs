using System;
using System.Runtime.InteropServices;

namespace VibelightValley.Gfx;

[StructLayout(LayoutKind.Sequential)]
public struct D3D11Texture2DDesc
{
    public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    public override string ToString() =>
        $"{Width}x{Height} mips={MipLevels} array={ArraySize} fmt={Format}({DxgiFormat.Name(Format)}) samples={SampleCount} usage={Usage} bind=0x{BindFlags:X} cpu=0x{CpuAccessFlags:X} misc=0x{MiscFlags:X}";
}

/// <summary>
/// Minimal D3D11/DXGI COM access through raw vtable calls, so no C++ helper DLL is needed.
/// Vtable slot numbers are from d3d11.h / dxgi.h (stable ABI).
/// </summary>
public static unsafe class D3D11
{
    // IUnknown
    private const int SlotQueryInterface = 0, SlotAddRef = 1, SlotRelease = 2;
    // ID3D11DeviceChild
    private const int SlotChildGetDevice = 3;
    // ID3D11Texture2D
    private const int SlotTex2DGetDesc = 10;
    // ID3D11Device
    private const int SlotDevGetFeatureLevel = 37, SlotDevGetCreationFlags = 38, SlotDevGetDeviceRemovedReason = 39, SlotDevGetImmediateContext = 40;
    // ID3D11DeviceContext
    private const int SlotCtxCopyResource = 47, SlotCtxFlush = 111;
    // IDXGIDevice / IDXGIAdapter
    private const int SlotDxgiDevGetAdapter = 7, SlotDxgiAdapterGetDesc = 8;

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private static void** Vtbl(IntPtr obj) => *(void***)obj;

    public static uint AddRef(IntPtr obj) =>
        obj == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(obj)[SlotAddRef])(obj);

    public static uint Release(IntPtr obj) =>
        obj == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(obj)[SlotRelease])(obj);

    /// <summary>ID3D11DeviceChild::GetDevice. Returned device is AddRef'd by D3D; caller must Release.</summary>
    public static IntPtr GetDevice(IntPtr deviceChild)
    {
        IntPtr dev = IntPtr.Zero;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)Vtbl(deviceChild)[SlotChildGetDevice])(deviceChild, &dev);
        return dev;
    }

    public static D3D11Texture2DDesc GetDesc(IntPtr texture2D)
    {
        D3D11Texture2DDesc d = default;
        ((delegate* unmanaged[Stdcall]<IntPtr, D3D11Texture2DDesc*, void>)Vtbl(texture2D)[SlotTex2DGetDesc])(texture2D, &d);
        return d;
    }

    public static uint GetFeatureLevel(IntPtr device) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(device)[SlotDevGetFeatureLevel])(device);

    public static uint GetCreationFlags(IntPtr device) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(device)[SlotDevGetCreationFlags])(device);

    public static int GetDeviceRemovedReason(IntPtr device) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(device)[SlotDevGetDeviceRemovedReason])(device);

    /// <summary>Returned context is AddRef'd; caller must Release.</summary>
    public static IntPtr GetImmediateContext(IntPtr device)
    {
        IntPtr ctx = IntPtr.Zero;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)Vtbl(device)[SlotDevGetImmediateContext])(device, &ctx);
        return ctx;
    }

    public static void CopyResource(IntPtr context, IntPtr dst, IntPtr src) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Vtbl(context)[SlotCtxCopyResource])(context, dst, src);

    public static void Flush(IntPtr context) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)Vtbl(context)[SlotCtxFlush])(context);

    public static string FeatureLevelName(uint fl) => fl switch
    {
        0x9100 => "9_1", 0x9200 => "9_2", 0x9300 => "9_3", 0xa000 => "10_0", 0xa100 => "10_1",
        0xb000 => "11_0", 0xb100 => "11_1", 0xc000 => "12_0", 0xc100 => "12_1", 0xc200 => "12_2",
        _ => $"0x{fl:X}",
    };

    public struct AdapterInfo
    {
        public string Description;
        public uint VendorId, DeviceId;
        public ulong DedicatedVideoMemory;
        public long Luid;
        public override string ToString() =>
            $"'{Description}' vendor=0x{VendorId:X4} device=0x{DeviceId:X4} vram={DedicatedVideoMemory / (1024 * 1024)}MB luid=0x{Luid:X16}";
    }

    /// <summary>Device -> IDXGIDevice -> IDXGIAdapter -> DXGI_ADAPTER_DESC (incl. LUID).</summary>
    public static bool TryGetAdapter(IntPtr device, out AdapterInfo info)
    {
        info = default;
        IntPtr dxgiDevice = IntPtr.Zero, adapter = IntPtr.Zero;
        try
        {
            Guid iid = IID_IDXGIDevice;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(device)[SlotQueryInterface])(device, &iid, &dxgiDevice);
            if (hr < 0 || dxgiDevice == IntPtr.Zero) return false;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(dxgiDevice)[SlotDxgiDevGetAdapter])(dxgiDevice, &adapter);
            if (hr < 0 || adapter == IntPtr.Zero) return false;
            byte* desc = stackalloc byte[304];
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Vtbl(adapter)[SlotDxgiAdapterGetDesc])(adapter, desc);
            if (hr < 0) return false;
            info.Description = new string((char*)desc).TrimEnd('\0');
            info.VendorId = *(uint*)(desc + 256);
            info.DeviceId = *(uint*)(desc + 260);
            info.DedicatedVideoMemory = *(ulong*)(desc + 272);
            info.Luid = *(long*)(desc + 296);
            return true;
        }
        finally
        {
            Release(adapter);
            Release(dxgiDevice);
        }
    }
}

public static class DxgiFormat
{
    public const uint R8G8B8A8_TYPELESS = 27, R8G8B8A8_UNORM = 28, R8G8B8A8_UNORM_SRGB = 29;
    public const uint B8G8R8A8_UNORM = 87, B8G8R8A8_TYPELESS = 90, B8G8R8A8_UNORM_SRGB = 91;
    public const uint R16G16B16A16_FLOAT = 10, R16G16B16A16_TYPELESS = 9;
    public const uint R10G10B10A2_UNORM = 24, R10G10B10A2_TYPELESS = 23, R11G11B10_FLOAT = 26;

    public static string Name(uint f) => f switch
    {
        2 => "R32G32B32A32_FLOAT", 9 => "R16G16B16A16_TYPELESS", 10 => "R16G16B16A16_FLOAT", 11 => "R16G16B16A16_UNORM",
        23 => "R10G10B10A2_TYPELESS", 24 => "R10G10B10A2_UNORM", 26 => "R11G11B10_FLOAT",
        27 => "R8G8B8A8_TYPELESS", 28 => "R8G8B8A8_UNORM", 29 => "R8G8B8A8_UNORM_SRGB",
        87 => "B8G8R8A8_UNORM", 88 => "B8G8R8X8_UNORM", 90 => "B8G8R8A8_TYPELESS", 91 => "B8G8R8A8_UNORM_SRGB",
        40 => "D32_FLOAT", 45 => "D24_UNORM_S8_UINT", 20 => "D32_FLOAT_S8X24_UINT", 55 => "D16_UNORM",
        _ => $"#{f}",
    };

    /// <summary>Typeless family, for CopyResource compatibility checks.</summary>
    public static uint Family(uint f) => f switch
    {
        27 or 28 or 29 or 30 or 31 or 32 => 27,
        87 or 90 or 91 => 90,
        88 or 92 or 93 => 92,
        9 or 10 or 11 or 12 or 13 or 14 => 9,
        23 or 24 or 25 => 23,
        _ => f,
    };

    public static bool CopyCompatible(uint a, uint b) => Family(a) == Family(b);
    public static bool IsSrgb(uint f) => f is 29 or 91 or 93;
}
