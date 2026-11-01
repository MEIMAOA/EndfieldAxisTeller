using System.IO;
using System.Threading;
using AxisTeller.Capture;
using AxisTeller.Vision;
using Composition.WindowsRuntimeHelpers;

namespace AxisTeller;

/// <summary>命令行诊断：抓取指定窗口的若干帧，用给定模板算匹配分，输出报告。</summary>
public static class ScreenDiag
{
    public static bool Run(IntPtr hwnd, string templatePath, string outFile, double threshold)
    {
        try
        {
            if (!File.Exists(templatePath))
            {
                File.WriteAllText(outFile, "模板文件不存在：" + templatePath);
                return false;
            }

            var templatePng = File.ReadAllBytes(templatePath);
            var item = CaptureHelper.CreateItemForWindow(hwnd);
            using var capture = new CaptureService();
            var matcher = new TemplateMatcher();
            var gate = new CountdownEvent(1);
            var scores = new List<double>();
            var brightness = new List<double>();
            var frames = 0;
            var report = new List<string>();

            capture.Error += message => report.Add("capture error: " + message);
            capture.FrameArrived += (w, h, bgra) =>
            {
                frames++;
                brightness.Add(TemplateMatcher.MeanBrightness(bgra, w, h));
                var score = matcher.Match(bgra, w, h, templatePng);
                scores.Add(score);
                report.Add($"frame {frames}: {w}x{h} brightness={brightness[^1]:F1} score={score:F3}");
                if (frames >= 12 || gate.IsSet) gate.Signal();
            };

            capture.Start(item);
            gate.Wait(TimeSpan.FromSeconds(15));
            capture.Stop();

            var usable = scores.Where(s => s >= 0).ToList();
            var max = usable.Count > 0 ? usable.Max() : -1;
            var avg = usable.Count > 0 ? usable.Average() : -1;
            report.Add("");
            report.Add($"frames={frames} maxScore={max:F3} avgScore={avg:F3} threshold={threshold:F2} " +
                       $"pass={(max >= threshold ? "YES" : "NO")}");
            report.Add($"template={Path.GetFileName(templatePath)} bytes={templatePng.Length}");
            File.WriteAllLines(outFile, report);
            return max >= threshold;
        }
        catch (Exception ex)
        {
            File.WriteAllText(outFile, ex.ToString());
            return false;
        }
    }
}
