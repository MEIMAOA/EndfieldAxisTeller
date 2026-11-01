using System.IO;
using System.Threading;
using System.Windows;
using AxisTeller.Capture;
using AxisTeller.Vision;
using Composition.WindowsRuntimeHelpers;

namespace AxisTeller;

/// <summary>命令行诊断：抓取指定窗口的若干帧，用给定模板算匹配分，输出报告。</summary>
public static class ScreenDiag
{
    /// <summary>AI 管线冒烟测试：真实游戏帧采样 → 训练 → 报告准确率与推理耗时。</summary>
    public static bool RunAiSmoke(IntPtr hwnd, string outFile)
    {
        try
        {
            var item = CaptureHelper.CreateItemForWindow(hwnd);
            using var capture = new CaptureService();
            var frames = new List<(int W, int H, byte[] Bgra)>();
            using var gate = new CountdownEvent(1);

            capture.Error += message => File.AppendAllText(outFile + ".log", message + "\n");
            capture.FrameArrived += (w, h, bgra) =>
            {
                lock (frames) { frames.Add((w, h, bgra)); }
                if (frames.Count >= 8) gate.Signal();
            };

            capture.Start(item);
            gate.Wait(TimeSpan.FromSeconds(15));
            capture.Stop();

            if (frames.Count == 0)
            {
                File.WriteAllText(outFile, "未收到任何帧。");
                return false;
            }

            using var extractor = new OnnxFeatureExtractor();
            var report = new List<string>();
            report.Add($"extractor available={extractor.Available} error={extractor.Error}");

            var f0 = frames[0];
            var roi = new Int32Rect(f0.W / 4, f0.H / 4, 320, 240);          // 正样本区域
            var negRoi = new Int32Rect(f0.W - 400, 40, 320, 240);            // 负样本区域（另一块地方）

            var temp = Path.Combine(Path.GetTempPath(), "axis-teller-ai-smoke-" + Guid.NewGuid().ToString("N"));
            var oldRoot = Store.Root;
            try
            {
                Store.RedirectTo(temp);
                foreach (var f in frames)
                {
                    AiSampleStore.SavePatch("Skill", true, f.Bgra, f.W, f.H, roi);
                    AiSampleStore.SavePatch("Skill", false, f.Bgra, f.W, f.H, negRoi);
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var model = AiTrainer.Train("Skill", roi, extractor, out var log);
                sw.Stop();
                report.Add($"train {sw.ElapsedMilliseconds}ms: {log}");

                // 推理耗时
                var patch = TemplateMatcher.CropBgra(f0.Bgra, f0.W, f0.H, roi.X, roi.Y, roi.Width, roi.Height);
                var t = System.Diagnostics.Stopwatch.StartNew();
                float[]? feats = null;
                for (var i = 0; i < 3; i++) feats = extractor.Extract(patch, roi.Width, roi.Height);
                t.Stop();
                report.Add($"infer avg {t.ElapsedMilliseconds / 3.0:F1}ms dim={(feats?.Length ?? 0)}");

                if (model is not null && feats is not null)
                    report.Add($"model score(positive ROI)={model.Score(feats):F3} valAcc={model.ValidationAccuracy:P0}");

                var ok = model is not null && feats is not null;
                report.Add("pass=" + (ok ? "YES" : "NO"));
                File.WriteAllLines(outFile, report);
                return ok;
            }
            finally
            {
                Store.RedirectTo(oldRoot);
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(outFile, ex.ToString());
            return false;
        }
    }

    /// <summary>真实采样并训练，模型保存到正式存档目录（给「AI 识别」用）。</summary>
    public static bool RunAiTrain(IntPtr hwnd, string kindText, string outFile)
    {
        try
        {
            var kind = ActionKinds.Parse(kindText);
            if (kind == ActionKind.Unknown) kind = ActionKind.Skill;

            var item = CaptureHelper.CreateItemForWindow(hwnd);
            using var capture = new CaptureService();
            var frames = new List<(int W, int H, byte[] Bgra)>();
            using var gate = new CountdownEvent(1);
            capture.FrameArrived += (w, h, bgra) =>
            {
                lock (frames) { frames.Add((w, h, bgra)); }
                if (frames.Count >= 10) gate.Signal();
            };
            capture.Start(item);
            gate.Wait(TimeSpan.FromSeconds(15));
            capture.Stop();
            if (frames.Count == 0) { File.WriteAllText(outFile, "未收到帧。"); return false; }

            using var extractor = new OnnxFeatureExtractor();
            var f0 = frames[0];
            var roi = new Int32Rect(f0.W / 4, f0.H / 4, 320, 240);
            var negRoi = new Int32Rect(f0.W - 400, 40, 320, 240);

            foreach (var f in frames)
            {
                AiSampleStore.SavePatch(kind.ToString(), true, f.Bgra, f.W, f.H, roi);
                AiSampleStore.SavePatch(kind.ToString(), false, f.Bgra, f.W, f.H, negRoi);
            }

            var model = AiTrainer.Train(kind.ToString(), roi, extractor, out var log);
            File.WriteAllText(outFile, log + $"\npos={AiSampleStore.Count(kind.ToString(), true)} neg={AiSampleStore.Count(kind.ToString(), false)} " +
                                            $"modelAcc={(model?.ValidationAccuracy ?? 0):P0}");
            return model is not null;
        }
        catch (Exception ex)
        {
            File.WriteAllText(outFile, ex.ToString());
            return false;
        }
    }

    public static bool RunTemplateSmoke(IntPtr hwnd, string kindText, string outFile)
    {
        try
        {
            var kind = ActionKinds.Parse(kindText);
            if (kind == ActionKind.Unknown) kind = ActionKind.ComboSkill;

            var item = CaptureHelper.CreateItemForWindow(hwnd);
            using var capture = new CaptureService();
            byte[]? frame = null;
            var width = 0;
            var height = 0;
            using var gate = new ManualResetEventSlim(false);

            capture.Error += message => File.AppendAllText(outFile + ".log", message + "\n");
            capture.FrameArrived += (w, h, bgra) =>
            {
                if (frame is not null) return;
                frame = bgra;
                width = w;
                height = h;
                gate.Set();
            };

            capture.Start(item);
            gate.Wait(TimeSpan.FromSeconds(12));
            capture.Stop();

            if (frame is null || width <= 0)
            {
                File.WriteAllText(outFile, "未收到任何帧。");
                return false;
            }

            // 取画面正中间 200x200 作为模板（真实缩放下应能 100% 匹配自己）
            var w2 = Math.Min(200, width / 2);
            var h2 = Math.Min(200, height / 2);
            var x = (width - w2) / 2;
            var y = (height - h2) / 2;

            var lib = new TemplateLibrary();
            var saved = lib.Save(kind, frame, width, height, x, y, w2, h2);
            var bytes = File.ReadAllBytes(saved);
            var matcher = new TemplateMatcher();
            var score = matcher.Match(frame, width, height, bytes);

            var report = new[]
            {
                $"frame={width}x{height}",
                $"saved={saved}",
                $"kind={kind}",
                $"bytes={bytes.Length}",
                $"selfMatch={score:F3}",
                $"pass={(score >= 0.9 ? "YES" : "NO")}",
            };
            File.WriteAllLines(outFile, report);
            return score >= 0.9;
        }
        catch (Exception ex)
        {
            File.WriteAllText(outFile, ex.ToString());
            return false;
        }
    }

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
