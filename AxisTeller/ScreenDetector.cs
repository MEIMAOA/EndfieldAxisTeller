using AxisTeller.Capture;
using AxisTeller.Vision;
using Windows.Graphics.Capture;

namespace AxisTeller;

/// <summary>画面检测：抓帧 + 模板匹配 / ONNX 视觉 AI，命中即触发。</summary>
public sealed class ScreenDetector : IDisposable
{
    private static readonly Lazy<OnnxFeatureExtractor> SharedExtractor = new(() => new OnnxFeatureExtractor());

    private readonly CaptureService _capture = new();
    private readonly TemplateLibrary _library = new();
    private readonly TemplateMatcher _matcher = new();
    private readonly Dictionary<string, AiModel?> _aiModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _matchGate = new();

    private ActionKind _targetKind = ActionKind.Unknown;
    private string _detectMode = "template";
    private double _threshold = 0.8;
    private DateTime _lastDetected = DateTime.MinValue;
    private DateTime _lastMatchAttempt = DateTime.MinValue;
    private int _matching;
    private bool _blackWarned;
    private bool _latched;
    private int _lastWidth;
    private int _lastHeight;
    private int _frameCount;
    private DateTime _fpsStart = DateTime.UtcNow;
    private readonly Queue<FrameScore> _recent = new();

    private readonly record struct FrameScore(DateTime At, double Score, ActionKind Kind, string Name);

    public event Action<ActionKind>? Detected;
    public event Action<string>? Error;
    public event Action<string>? Warning;
    public event Action<int, int, byte[]>? FrameArrived; // 供模板管理窗口预览

    public bool IsRunning => _capture.IsCapturing;
    public CaptureService Capture => _capture;
    public TemplateLibrary Library => _library;
    public OnnxFeatureExtractor Extractor => SharedExtractor.Value;
    public string DetectMode => _detectMode;

    /// <summary>最近一次匹配的最佳分数（用于界面显示 / 排查）。</summary>
    public double LastScore { get; private set; } = -1;
    public double LastAiScore { get; private set; } = -1;
    public ActionKind LastMatchedKind { get; private set; } = ActionKind.Unknown;
    public string LastMatchedTemplate { get; private set; } = "";
    public int DisplayFps { get; private set; }
    public int FrameWidth => _lastWidth;
    public int FrameHeight => _lastHeight;

    public void Start(GraphicsCaptureItem item, double threshold, string? detectMode = null)
    {
        _threshold = threshold;
        if (!string.IsNullOrWhiteSpace(detectMode)) _detectMode = detectMode;
        _library.Reload();
        _aiModels.Clear();
        _recent.Clear();
        _latched = false;
        _capture.Error += OnError;
        _capture.FrameArrived += OnFrame;
        _capture.Start(item);
        _fpsStart = DateTime.UtcNow;
        _frameCount = 0;
        _blackWarned = false;
    }

    public void Stop()
    {
        _capture.FrameArrived -= OnFrame;
        _capture.Error -= OnError;
        _capture.Stop();
    }

    public void SetTarget(ActionKind kind) => _targetKind = kind;

    public void SetThreshold(double threshold) => _threshold = threshold;

    public void SetDetectMode(string mode) => _detectMode = mode;

    public void InvalidateAiModels() => _aiModels.Clear();

    /// <summary>某个动作类型是否已经训练过 AI 模型。</summary>
    public AiModel? GetAiModel(ActionKind kind) => GetAiModel(kind.ToString());

    public AiModel? GetAiModel(string kind)
    {
        if (_aiModels.TryGetValue(kind, out var cached)) return cached;
        var model = AiModel.Load(kind);
        _aiModels[kind] = model;
        return model;
    }

    public int TemplateCount(ActionKind kind) => _library.For(kind).Count;

    public int TotalTemplateCount => _library.Templates.Count;

    private void OnError(string message) => Error?.Invoke(message);

    private void OnFrame(int width, int height, byte[] bgra)
    {
        _lastWidth = width;
        _lastHeight = height;
        _frameCount++;
        if ((DateTime.UtcNow - _fpsStart).TotalMilliseconds >= 1000)
        {
            DisplayFps = _frameCount;
            _frameCount = 0;
            _fpsStart = DateTime.UtcNow;
        }

        FrameArrived?.Invoke(width, height, bgra);

        // 黑屏检测（游戏独占全屏 / 受保护窗口时常见）
        if (!_blackWarned)
        {
            var brightness = TemplateMatcher.MeanBrightness(bgra, width, height);
            if (brightness < 4)
            {
                _blackWarned = true;
                Warning?.Invoke("抓到的画面几乎是全黑的：游戏可能在全屏独占模式，请改用「无边框窗口」；或换一个窗口。");
            }
        }

        // 匹配节流：每 150ms 最多一次，且上一次没算完就跳过
        var now = DateTime.UtcNow;
        if ((now - _lastMatchAttempt).TotalMilliseconds < 150) return;
        if (Interlocked.Exchange(ref _matching, 1) == 1) return;
        _lastMatchAttempt = now;

        try
        {
            MatchFrame(width, height, bgra);
        }
        finally
        {
            Interlocked.Exchange(ref _matching, 0);
        }
    }

    private void MatchFrame(int width, int height, byte[] bgra)
    {
        var kinds = _targetKind == ActionKind.Unknown ? ActionKinds.Selectable : new[] { _targetKind };
        var bestScore = -1.0;
        var bestKind = ActionKind.Unknown;
        var bestName = "";

        foreach (var kind in kinds)
        {
            var templateScore = MatchTemplates(kind, bgra, width, height, out var name);

            var aiScore = kind == _targetKind ? MatchAi(kind, bgra, width, height) : -1;
            var score = _detectMode switch
            {
                "ai" => aiScore >= 0 ? aiScore : templateScore,
                "hybrid" => Math.Max(templateScore, aiScore),
                _ => templateScore,
            };

            if (score > bestScore)
            {
                bestScore = score;
                bestKind = kind;
                bestName = _detectMode != "ai" && templateScore >= aiScore ? name : "AI 模型";
            }
        }

        if (bestScore < 0) return;

        // 用最近 ~1.2 秒的峰值判定：UI 动画 / 短暂遮挡也能命中
        var now = DateTime.UtcNow;
        _recent.Enqueue(new FrameScore(now, bestScore, bestKind, bestName));
        while (_recent.Count > 0 && (now - _recent.Peek().At).TotalMilliseconds > 1200)
            _recent.Dequeue();

        var peak = new FrameScore(DateTime.MinValue, -1, ActionKind.Unknown, "");
        foreach (var item in _recent)
            if (item.Score > peak.Score)
                peak = item;

        LastScore = peak.Score;

        // 边沿触发：模板「出现」的那一下才算完成；一直挂在画面上不会连续触发
        if (peak.Score >= _threshold)
        {
            if (!_latched && peak.Kind != ActionKind.Unknown)
            {
                _latched = true;
                Fire(peak.Kind, peak.Name, peak.Score);
            }
        }
        else if (peak.Score >= 0 && peak.Score < _threshold - 0.1)
        {
            _latched = false;
        }
    }

    private double MatchTemplates(ActionKind kind, byte[] bgra, int width, int height, out string name)
    {
        var bestScore = -1.0;
        var bestName = "";
        foreach (var tpl in _library.For(kind))
        {
            var png = _library.Bytes(tpl);
            if (png is null) continue;

            var score = _matcher.Match(bgra, width, height, png);
            if (score > bestScore)
            {
                bestScore = score;
                bestName = tpl.Name;
            }
        }

        name = bestName;
        return bestScore;
    }

    /// <summary>ONNX 特征 + 本地训练的分类头；没有模型时返回 -1。</summary>
    private double MatchAi(ActionKind kind, byte[] bgra, int width, int height)
    {
        if (string.Equals(_detectMode, "template", StringComparison.OrdinalIgnoreCase)) return -1;
        var model = GetAiModel(kind);
        if (model is null) return -1;
        var extractor = SharedExtractor.Value;
        if (!extractor.Available) return -1;

        try
        {
            var rx = Math.Clamp(model.RoiX, 0, Math.Max(0, width - 1));
            var ry = Math.Clamp(model.RoiY, 0, Math.Max(0, height - 1));
            var rw = Math.Clamp(model.RoiWidth, 1, width - rx);
            var rh = Math.Clamp(model.RoiHeight, 1, height - ry);
            var patch = TemplateMatcher.CropBgra(bgra, width, height, rx, ry, rw, rh);
            var features = extractor.Extract(patch, rw, rh);
            if (features is null) return -1;
            var score = model.Score(features);
            LastAiScore = score;
            return score;
        }
        catch
        {
            return -1;
        }
    }

    private void Fire(ActionKind kind, string templateName, double score)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastDetected).TotalMilliseconds < 800) return;

        _lastDetected = now;
        LastScore = score;
        LastMatchedKind = kind;
        LastMatchedTemplate = templateName;
        Detected?.Invoke(kind);
    }

    /// <summary>手动推进/切换步骤后重新武装，让下一次「出现」还能触发。</summary>
    public void ReArm()
    {
        _latched = false;
        _recent.Clear();
    }

    public void Dispose() => Stop();
}
