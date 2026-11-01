using OpenCvSharp;

namespace AxisTeller.Vision;

public readonly record struct MatchResult(double Score, int X, int Y, double Scale, string Template);

/// <summary>OpenCV 模板匹配（多尺度 + 降采样加速），输入 BGRA 帧字节与模板 PNG 字节。</summary>
public sealed class TemplateMatcher
{
    private const int MaxMatchWidth = 1280;

    /// <summary>返回画面中模板的最佳相似度（0~1，越高越像）。找不到返回 -1。</summary>
    public double Match(byte[] frameBgra, int width, int height, byte[] templatePng)
        => BestMatch(frameBgra, width, height, templatePng, out _);

    public double BestMatch(byte[] frameBgra, int width, int height, byte[] templatePng, out MatchResult best)
    {
        best = default;
        try
        {
            using var frameFull = ToMat(frameBgra, width, height);
            if (frameFull.Empty()) return -1;

            using var frameBgrFull = new Mat();
            Cv2.CvtColor(frameFull, frameBgrFull, ColorConversionCodes.BGRA2BGR);

            // 降采样到 1280 宽以内再匹配，速度提升 4 倍左右，对小图标精度影响很小
            var scale = Math.Min(1.0, MaxMatchWidth / (double)Math.Max(1, width));
            using var frame = new Mat();
            if (scale < 0.999)
                Cv2.Resize(frameBgrFull, frame, new Size((int)(width * scale), (int)(height * scale)), interpolation: InterpolationFlags.Area);
            else
                frameBgrFull.CopyTo(frame);

            using var tplFull = Cv2.ImDecode(templatePng, ImreadModes.Color);
            if (tplFull.Empty()) return -1;

            using var tplBase = new Mat();
            if (scale < 0.999)
                Cv2.Resize(tplFull, tplBase, new Size((int)Math.Round(tplFull.Width * scale), (int)Math.Round(tplFull.Height * scale)), interpolation: InterpolationFlags.Area);
            else
                tplFull.CopyTo(tplBase);

            var bestScore = -1.0;
            MatchResult bestResult = default;
            for (var s = 0.75; s <= 1.25 + 1e-6; s += 0.05)
            {
                var tw = (int)Math.Round(tplBase.Width * s);
                var th = (int)Math.Round(tplBase.Height * s);
                if (tw < 4 || th < 4 || tw >= frame.Width || th >= frame.Height) continue;

                using var resized = new Mat();
                Cv2.Resize(tplBase, resized, new Size(tw, th));
                using var result = new Mat();
                Cv2.MatchTemplate(frame, resized, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out var maxLoc);
                if (maxVal > bestScore)
                {
                    bestScore = maxVal;
                    bestResult = new MatchResult(maxVal, (int)(maxLoc.X / scale), (int)(maxLoc.Y / scale), s * scale, "");
                }
            }

            best = bestResult;
            return bestScore;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>整帧平均亮度（0~255），用于判断是不是抓到黑屏。</summary>
    public static double MeanBrightness(byte[] bgra, int width, int height)
    {
        try
        {
            using var mat = ToMat(bgra, width, height);
            if (mat.Empty()) return 0;
            using var gray = new Mat();
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGRA2GRAY);
            return Cv2.Mean(gray).Val0;
        }
        catch
        {
            return 0;
        }
    }

    private static Mat ToMat(byte[] bgra, int width, int height)
    {
        var mat = new Mat(height, width, MatType.CV_8UC4);
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, mat.Data, bgra.Length);
        return mat;
    }

    /// <summary>把 BGRA 帧裁一块出来（不做编码），给 AI 特征提取用。</summary>
    public static byte[] CropBgra(byte[] frameBgra, int frameWidth, int frameHeight, int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, Math.Max(0, frameWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, frameHeight - 1));
        w = Math.Clamp(w, 1, frameWidth - x);
        h = Math.Clamp(h, 1, frameHeight - y);

        var result = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(frameBgra, ((y + row) * frameWidth + x) * 4, result, row * w * 4, w * 4);
        return result;
    }

    /// <summary>把 BGRA 帧裁一块出来，编码成 PNG 字节（用于保存模板）。</summary>
    public static byte[] CropToPng(byte[] frameBgra, int frameWidth, int frameHeight, int x, int y, int w, int h)
    {
        using var frame = new Mat(frameHeight, frameWidth, MatType.CV_8UC4);
        System.Runtime.InteropServices.Marshal.Copy(frameBgra, 0, frame.Data, frameBgra.Length);

        x = Math.Clamp(x, 0, frameWidth - 1);
        y = Math.Clamp(y, 0, frameHeight - 1);
        w = Math.Clamp(w, 1, frameWidth - x);
        h = Math.Clamp(h, 1, frameHeight - y);

        using var crop = new Mat(frame, new OpenCvSharp.Rect(x, y, w, h));
        using var bgr = new Mat();
        Cv2.CvtColor(crop, bgr, ColorConversionCodes.BGRA2BGR);
        Cv2.ImEncode(".png", bgr, out var bytes);
        return bytes;
    }
}
