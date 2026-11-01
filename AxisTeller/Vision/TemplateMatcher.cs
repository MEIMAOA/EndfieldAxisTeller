using OpenCvSharp;

namespace AxisTeller.Vision;

/// <summary>OpenCV 模板匹配（多尺度），输入 BGRA 帧字节与模板 PNG 字节，返回最佳相似度。</summary>
public sealed class TemplateMatcher
{
    public double Match(byte[] frameBgra, int width, int height, byte[] templatePng)
    {
        try
        {
            using var frame = ToMat(frameBgra, width, height);
            if (frame.Empty()) return -1;
            using var frameBgr = new Mat();
            Cv2.CvtColor(frame, frameBgr, ColorConversionCodes.BGRA2BGR);

            using var tpl = Cv2.ImDecode(templatePng, ImreadModes.Color);
            if (tpl.Empty()) return -1;

            var best = -1.0;
            for (var scale = 0.7; scale <= 1.3 + 1e-6; scale += 0.05)
            {
                var tw = Math.Max(8, (int)Math.Round(tpl.Width * scale));
                var th = Math.Max(8, (int)Math.Round(tpl.Height * scale));
                if (tw >= frameBgr.Width || th >= frameBgr.Height) continue;

                using var resized = new Mat();
                Cv2.Resize(tpl, resized, new Size(tw, th));

                using var result = new Mat();
                Cv2.MatchTemplate(frameBgr, resized, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out _);
                if (maxVal > best) best = maxVal;
            }

            return best;
        }
        catch
        {
            return -1;
        }
    }

    private static Mat ToMat(byte[] bgra, int width, int height)
    {
        var mat = new Mat(height, width, MatType.CV_8UC4);
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, mat.Data, bgra.Length);
        return mat;
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
