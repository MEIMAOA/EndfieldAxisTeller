using System.IO;
using System.Threading;
using AxisTeller.Capture;
using AxisTeller.Vision;
using Composition.WindowsRuntimeHelpers;

namespace AxisTeller;

/// <summary>命令行自测：抓取指定窗口的第一帧并保存为 PNG。</summary>
public static class CaptureSmoke
{
    public static bool Run(IntPtr hwnd, string outFile)
    {
        try
        {
            var item = CaptureHelper.CreateItemForWindow(hwnd);
            using var capture = new CaptureService();
            byte[]? first = null;
            var width = 0;
            var height = 0;
            using var gate = new ManualResetEventSlim(false);

            capture.Error += message => File.AppendAllText(outFile + ".log", message + "\n");
            capture.FrameArrived += (w, h, bgra) =>
            {
                if (first is not null) return;
                first = bgra;
                width = w;
                height = h;
                gate.Set();
            };

            capture.Start(item);
            gate.Wait(TimeSpan.FromSeconds(12));
            capture.Stop();

            if (first is null || width <= 0)
            {
                File.WriteAllText(outFile + ".err", "未收到任何帧。");
                return false;
            }

            var png = TemplateMatcher.CropToPng(first, width, height, 0, 0, width, height);
            File.WriteAllBytes(outFile, png);
            File.WriteAllText(outFile + ".txt", $"{width}x{height}  {png.Length} bytes  title={item.DisplayName}");
            return true;
        }
        catch (Exception ex)
        {
            File.WriteAllText(outFile + ".err", ex.ToString());
            return false;
        }
    }
}
