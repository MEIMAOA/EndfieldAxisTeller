// 基于微软官方 Windows.UI.Composition-Win32-Samples 的 ScreenCapture 样例（MIT）。
// https://github.com/microsoft/Windows.UI.Composition-Win32-Samples
// 仅做命名空间与项目适配，用于把 SharpDX 的 D3D 设备/纹理和 WinRT 的 GraphicsCapture 互转。
using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Composition.WindowsRuntimeHelpers;

public static class Direct3D11Helper
{
    static readonly Guid IInspectable = new("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90");
    static readonly Guid ID3D11Resource = new("dc8e63f3-d12b-4952-b47b-5e45026a862d");
    static readonly Guid IDXGIAdapter3 = new("645967A4-1392-4310-A798-8053CE3E93FD");
    static readonly Guid ID3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    static readonly Guid ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11SurfaceFromDXGISurface", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    static extern uint CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

    public static IDirect3DDevice CreateDevice() => CreateDevice(false);

    public static IDirect3DDevice CreateDevice(bool useWarp)
    {
        var d3dDevice = new SharpDX.Direct3D11.Device(
            useWarp ? SharpDX.Direct3D.DriverType.Software : SharpDX.Direct3D.DriverType.Hardware,
            SharpDX.Direct3D11.DeviceCreationFlags.BgraSupport);
        return CreateDirect3DDeviceFromSharpDXDevice(d3dDevice);
    }

    public static IDirect3DDevice CreateDirect3DDeviceFromSharpDXDevice(SharpDX.Direct3D11.Device d3dDevice)
    {
        IDirect3DDevice? device = null;
        using (var dxgiDevice = d3dDevice.QueryInterface<SharpDX.DXGI.Device3>())
        {
            var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pUnknown);
            if (hr == 0 && pUnknown != IntPtr.Zero)
                device = MarshalInterface<IDirect3DDevice>.FromAbi(pUnknown);
        }

        return device!;
    }

    public static SharpDX.Direct3D11.Device CreateSharpDXDevice(IDirect3DDevice device)
    {
        var access = device.As<IDirect3DDxgiInterfaceAccess>();
        var d3dPointer = access.GetInterface(ID3D11Device);
        return new SharpDX.Direct3D11.Device(d3dPointer);
    }

    public static SharpDX.Direct3D11.Texture2D CreateSharpDXTexture2D(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var d3dPointer = access.GetInterface(ID3D11Texture2D);
        return new SharpDX.Direct3D11.Texture2D(d3dPointer);
    }
}

public static class CaptureHelper
{
    static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    interface IInitializeWithWindow
    {
        void Initialize(IntPtr hwnd);
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int WindowsDeleteString(IntPtr hstring);

    public static void SetWindow(this GraphicsCapturePicker picker, IntPtr hwnd)
    {
        // CsWinRT 投影对象不能直接 (T)(object) 强转，需要走 As<T>() 做 QI。
        var interop = picker.As<IInitializeWithWindow>();
        interop.Initialize(hwnd);
    }

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var itemPtr = CreateItem(hwnd, IntPtr.Zero, forMonitor: false);
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
        return item;
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        var itemPtr = CreateItem(IntPtr.Zero, hmon, forMonitor: true);
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
        return item;
    }

    private static IntPtr CreateItem(IntPtr hwnd, IntPtr hmon, bool forMonitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, className.Length, out var hstr);
        var iid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"); // IGraphicsCaptureItemInterop
        RoGetActivationFactory(hstr, ref iid, out var factoryPtr);
        WindowsDeleteString(hstr);

        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
        var itemPtr = forMonitor
            ? interop.CreateForMonitor(hmon, GraphicsCaptureItemGuid)
            : interop.CreateForWindow(hwnd, GraphicsCaptureItemGuid);
        Marshal.Release(factoryPtr);
        return itemPtr;
    }
}
