using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace QuailImageSearch.Services.Models;

/// <summary>A graphics adapter the models could run on.</summary>
/// <param name="Index">DXGI adapter index, which is DirectML's device id.</param>
public sealed record GpuAdapter(int Index, string Name, long DedicatedMemory)
{
    public string MemoryText => DedicatedMemory > 0 ? $"{DedicatedMemory / 1_073_741_824.0:0.#} GB" : "shared memory";
    public override string ToString() => $"{Name} ({MemoryText})";
}

/// <summary>
/// What GPU acceleration this machine offers: on Windows the DirectX 12 adapters (via DXGI, which
/// reports the real amount of video memory, unlike WMI), on macOS Core ML, elsewhere nothing.
/// </summary>
public static class GpuInfo
{
    public enum Backend { None, DirectML, CoreML }

    public static Backend Available { get; } =
        OperatingSystem.IsWindows() ? Backend.DirectML
        : OperatingSystem.IsMacOS() ? Backend.CoreML
        : Backend.None;

    private static readonly Lazy<IReadOnlyList<GpuAdapter>> LazyAdapters = new(() =>
    {
        try { return OperatingSystem.IsWindows() ? EnumerateDxgi() : []; }
        catch (Exception) { return []; }
    });

    /// <summary>Hardware adapters (software renderers excluded), in DXGI order.</summary>
    public static IReadOnlyList<GpuAdapter> Adapters => LazyAdapters.Value;

    /// <summary>The adapter with the most dedicated memory, if any.</summary>
    public static GpuAdapter? Best => Adapters.OrderByDescending(a => a.DedicatedMemory).FirstOrDefault();

    // ---- DXGI (minimal COM interop: CreateDXGIFactory1 -> EnumAdapters1 -> GetDesc1) ----

    private static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh;
        public uint Flags;
    }

    private static unsafe List<GpuAdapter> EnumerateDxgi()
    {
        var result = new List<GpuAdapter>();
        if (CreateDXGIFactory1(IidDxgiFactory1, out var factory) < 0) return result;
        try
        {
            var factoryVtbl = *(IntPtr**)factory;
            // IDXGIFactory1::EnumAdapters1 is vtable slot 12 (IUnknown 3 + IDXGIObject 4 + IDXGIFactory 5).
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)factoryVtbl[12];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                var hr = enumAdapters1(factory, i, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0) break;
                try
                {
                    var adapterVtbl = *(IntPtr**)adapter;
                    // IDXGIAdapter1::GetDesc1 is slot 10 (IUnknown 3 + IDXGIObject 4 + IDXGIAdapter 3).
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)adapterVtbl[10];
                    var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<DxgiAdapterDesc1>());
                    try
                    {
                        if (getDesc1(adapter, buffer) >= 0)
                        {
                            var desc = Marshal.PtrToStructure<DxgiAdapterDesc1>(buffer);
                            if ((desc.Flags & DxgiAdapterFlagSoftware) == 0)
                                result.Add(new GpuAdapter((int)i, desc.Description.Trim(), (long)desc.DedicatedVideoMemory));
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }
        return result;
    }

    private static unsafe void Release(IntPtr comObject)
    {
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)comObject)[2];
        release(comObject);
    }
}
