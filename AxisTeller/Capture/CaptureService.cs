using System.Runtime.InteropServices;
using Composition.WindowsRuntimeHelpers;
using SharpDX.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace AxisTeller.Capture;

/// <summary>
/// 用 Windows.Graphics.Capture 抓取一个窗口/显示器，把每帧输出成 BGRA 字节流。
/// </summary>
public sealed class CaptureService : IDisposable
{
    private IDirect3DDevice? _device;
    private Device? _d3d;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Texture2D? _staging;
    private int _width;
    private int _height;

    public GraphicsCaptureItem? Item { get; private set; }
    public bool IsCapturing { get; private set; }
    public string? SourceTitle => Item?.DisplayName;

    /// <summary>每帧回调：宽、高、BGRA 像素（线程为捕获回调线程，注意别做重活）。</summary>
    public event Action<int, int, byte[]>? FrameArrived;

    public event Action<string>? Error;

    public void Start(GraphicsCaptureItem item)
    {
        Stop();

        try
        {
            Item = item;
            _device = Direct3D11Helper.CreateDevice();
            _d3d = Direct3D11Helper.CreateSharpDXDevice(_device);
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);
            _session = _pool.CreateCaptureSession(item);
            _pool.FrameArrived += OnFrameArrived;

            EnsureStaging(item.Size.Width, item.Size.Height);
            _session.StartCapture();
            IsCapturing = true;
        }
        catch (Exception ex)
        {
            Error?.Invoke("无法开始捕获：" + ex.Message);
            Stop();
        }
    }

    public void Stop()
    {
        IsCapturing = false;
        if (_pool is not null) _pool.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _session = null;
        _pool?.Dispose();
        _pool = null;
        _staging?.Dispose();
        _staging = null;
        _d3d?.Dispose();
        _d3d = null;
        _device?.Dispose();
        _device = null;
        Item = null;
    }

    private void EnsureStaging(int width, int height)
    {
        _staging?.Dispose();
        _staging = null;
        if (_d3d is null || width <= 0 || height <= 0) return;

        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
        };
        _staging = new Texture2D(_d3d, desc);
        _width = width;
        _height = height;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!IsCapturing || _d3d is null || _staging is null) return;

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            var size = frame.ContentSize;
            if (size.Width != _width || size.Height != _height)
                EnsureStaging(size.Width, size.Height);

            using var source = Direct3D11Helper.CreateSharpDXTexture2D(frame.Surface);
            _d3d.ImmediateContext.CopyResource(source, _staging);

            var dataBox = _d3d.ImmediateContext.MapSubresource(_staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                var bytes = new byte[size.Width * size.Height * 4];
                var rowBytes = size.Width * 4;
                if (dataBox.RowPitch == rowBytes)
                {
                    Marshal.Copy(dataBox.DataPointer, bytes, 0, bytes.Length);
                }
                else
                {
                    for (var y = 0; y < size.Height; y++)
                        Marshal.Copy(IntPtr.Add(dataBox.DataPointer, y * dataBox.RowPitch), bytes, y * rowBytes, rowBytes);
                }

                FrameArrived?.Invoke(size.Width, size.Height, bytes);
            }
            finally
            {
                _d3d.ImmediateContext.UnmapSubresource(_staging, 0);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
    }

    public void Dispose() => Stop();
}
