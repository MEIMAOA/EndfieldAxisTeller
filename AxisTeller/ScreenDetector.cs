using System.IO;
using AxisTeller.Capture;
using AxisTeller.Vision;
using Windows.Graphics.Capture;

namespace AxisTeller;

/// <summary>画面检测：抓帧 + 用模板库匹配当前动作类型，命中即触发。</summary>
public sealed class ScreenDetector : IDisposable
{
    private readonly CaptureService _capture = new();
    private readonly TemplateLibrary _library = new();
    private readonly TemplateMatcher _matcher = new();
    private ActionKind _targetKind = ActionKind.Unknown;
    private double _threshold = 0.8;
    private DateTime _lastDetected = DateTime.MinValue;

    public event Action<ActionKind>? Detected;
    public event Action<string>? Error;
    public event Action<int, int, byte[]>? FrameArrived; // 供模板管理窗口预览

    public bool IsRunning => _capture.IsCapturing;
    public CaptureService Capture => _capture;
    public TemplateLibrary Library => _library;

    public void Start(GraphicsCaptureItem item, double threshold)
    {
        _threshold = threshold;
        _library.Reload();
        _capture.Error += OnError;
        _capture.FrameArrived += OnFrame;
        _capture.Start(item);
    }

    public void Stop()
    {
        _capture.FrameArrived -= OnFrame;
        _capture.Error -= OnError;
        _capture.Stop();
    }

    public void SetTarget(ActionKind kind) => _targetKind = kind;

    private void OnError(string message) => Error?.Invoke(message);

    private void OnFrame(int width, int height, byte[] bgra)
    {
        FrameArrived?.Invoke(width, height, bgra);

        if (_targetKind == ActionKind.Unknown) return;
        var templates = _library.For(_targetKind);
        if (templates.Count == 0) return;

        var now = DateTime.UtcNow;
        if ((now - _lastDetected).TotalMilliseconds < 800) return;

        foreach (var tpl in templates)
        {
            byte[] png;
            try
            {
                png = File.ReadAllBytes(tpl.FilePath);
            }
            catch
            {
                continue;
            }

            var score = _matcher.Match(bgra, width, height, png);
            if (score >= _threshold)
            {
                _lastDetected = now;
                Detected?.Invoke(_targetKind);
                return;
            }
        }
    }

    public void Dispose() => Stop();
}
