using System.Windows;

namespace AxisTeller.Vision;

/// <summary>把「预览控件里的拖框」换算成「原画面里的像素区域」。
/// 预览图是等比缩放居中显示的，会有上下或左右留边，必须按实际渲染区域换算。</summary>
public static class RoiMapper
{
    public readonly record struct Fit(double Scale, double OffsetX, double OffsetY, double Width, double Height);

    public static Fit Compute(int frameWidth, int frameHeight, double viewWidth, double viewHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || viewWidth <= 0 || viewHeight <= 0)
            return new Fit(1, 0, 0, Math.Max(1, viewWidth), Math.Max(1, viewHeight));

        var scale = Math.Min(viewWidth / frameWidth, viewHeight / frameHeight);
        var width = frameWidth * scale;
        var height = frameHeight * scale;
        return new Fit(scale, (viewWidth - width) / 2, (viewHeight - height) / 2, width, height);
    }

    /// <summary>预览控件坐标（含留边）→ 原画面像素坐标。</summary>
    public static Int32Rect Map(double x, double y, double w, double h, Fit fit, int frameWidth, int frameHeight)
    {
        var scale = fit.Scale <= 0 ? 1 : fit.Scale;
        var left = (int)Math.Round((x - fit.OffsetX) / scale);
        var top = (int)Math.Round((y - fit.OffsetY) / scale);
        var width = (int)Math.Round(w / scale);
        var height = (int)Math.Round(h / scale);

        left = Math.Clamp(left, 0, Math.Max(0, frameWidth - 1));
        top = Math.Clamp(top, 0, Math.Max(0, frameHeight - 1));
        width = Math.Clamp(width, 1, Math.Max(1, frameWidth - left));
        height = Math.Clamp(height, 1, Math.Max(1, frameHeight - top));
        return new Int32Rect(left, top, width, height);
    }
}
