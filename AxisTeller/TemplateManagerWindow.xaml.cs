using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AxisTeller.Capture;
using AxisTeller.Vision;
using Windows.Graphics.Capture;

namespace AxisTeller;

public partial class TemplateManagerWindow : Window
{
    /// <summary>当前打开的模板管理窗口（供全局「保存模板」热键调用）。</summary>
    public static TemplateManagerWindow? Current { get; private set; }

    private readonly CaptureService _capture = new();
    private readonly TemplateLibrary _library = new();
    private readonly TemplateMatcher _matcher = new();
    private readonly GraphicsCaptureItem _item;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _checkTimer;

    private ActionKind _selectedKind = ActionKind.ComboSkill;
    private readonly double _threshold;
    private int _frameWidth;
    private int _frameHeight;
    private byte[]? _frame;
    private Rectangle? _selection;
    private Point _dragStart;
    private bool _dragging;
    private Rect _roiNative;
    private RoiMapper.Fit _fit;
    private bool _checking;

    private sealed record Row(string Text, TemplateInfo Info);

    public TemplateManagerWindow(GraphicsCaptureItem item, double threshold = 0.8)
    {
        InitializeComponent();
        _item = item;
        _threshold = threshold;
        Current = this;

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _renderTimer.Tick += (_, _) => Render();

        _checkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _checkTimer.Tick += (_, _) => SelfCheck();

        BuildKindButtons();
        ReloadTemplates();
        RefreshAiStatus();

        Closed += (_, _) =>
        {
            if (ReferenceEquals(Current, this)) Current = null;
            _capture.Stop();
            _renderTimer.Stop();
            _checkTimer.Stop();
        };
        Loaded += (_, _) => StartCapture();
    }

    private void StartCapture()
    {
        CaptureStatusText.Text = "捕获：" + _item.DisplayName;
        _capture.Error += e => Dispatcher.BeginInvoke(() => StatusText.Text = "捕获错误：" + e);
        _capture.FrameArrived += OnFrame;
        _capture.Start(_item);
        _renderTimer.Start();
        _checkTimer.Start();
        EmptyHint.Visibility = Visibility.Visible;
        StatusText.Text = $"拖框选区后，按「{App.State.Settings.SaveTemplateKey}」或「Ctrl+S」保存模板（也可点按钮）";
    }

    private void OnFrame(int width, int height, byte[] bgra)
    {
        _frameWidth = width;
        _frameHeight = height;
        _frame = bgra;
    }

    private void Render()
    {
        if (_frame is null || _frameWidth <= 0 || _frameHeight <= 0) return;

        try
        {
            var bitmap = BitmapSource.Create(
                _frameWidth, _frameHeight, 96, 96, PixelFormats.Bgra32, null, _frame, _frameWidth * 4);
            bitmap.Freeze();
            PreviewImage.Source = bitmap;

            // Image 用 Uniform 拉伸，实际显示区域可能上下/左右留黑边：按内容区算缩放与偏移
            var cw = PreviewImage.ActualWidth;
            var ch = PreviewImage.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            _fit = RoiMapper.Compute(_frameWidth, _frameHeight, cw, ch);
            OverlayCanvas.Width = _fit.Width;
            OverlayCanvas.Height = _fit.Height;
            OverlayCanvas.HorizontalAlignment = HorizontalAlignment.Left;
            OverlayCanvas.VerticalAlignment = VerticalAlignment.Top;
            OverlayCanvas.Margin = new Thickness(_fit.OffsetX, _fit.OffsetY, 0, 0);

            EmptyHint.Visibility = Visibility.Collapsed;
            FrameInfoText.Text = $"{_frameWidth}×{_frameHeight}";
        }
        catch
        {
            // 忽略渲染异常
        }
    }

    private void BuildKindButtons()
    {
        KindPanel.Children.Clear();
        foreach (var kind in ActionKinds.Selectable)
        {
            var button = new Button
            {
                Content = ActionKinds.FullLabel(kind),
                Tag = kind,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 5, 10, 5),
                FontSize = 11.5,
            };
            button.Click += (_, _) => SelectKind(kind);
            KindPanel.Children.Add(button);
        }

        SelectKind(_selectedKind);
    }

    private void SelectKind(ActionKind kind)
    {
        _selectedKind = kind;
        var ink = (Brush)FindResource("Ink");
        var white = (Brush)FindResource("BgPanel");
        foreach (var child in KindPanel.Children.OfType<Button>())
        {
            var active = (ActionKind)child.Tag == kind;
            child.Background = active ? ink : white;
            child.Foreground = active ? (Brush)FindResource("OnInk") : ink;
            child.BorderBrush = ink;
        }

        RoiHint.Text = $"拖出【{ActionKinds.FullLabel(kind)}】的识别区域，再点「保存为模板」。";
        if (IsLoaded) RefreshAiStatus();
    }

    // ================= ROI 拖拽 =================

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(OverlayCanvas);
        _dragging = true;

        if (_selection is null)
        {
            _selection = new Rectangle
            {
                Stroke = (Brush)FindResource("Accent"),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xF2, 0xEA, 0x00)),
                IsHitTestVisible = false,
            };
            OverlayCanvas.Children.Add(_selection);
        }

        Canvas.SetLeft(_selection, _dragStart.X);
        Canvas.SetTop(_selection, _dragStart.Y);
        _selection.Width = 0;
        _selection.Height = 0;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _selection is null) return;
        var p = e.GetPosition(OverlayCanvas);
        var x = Math.Min(_dragStart.X, p.X);
        var y = Math.Min(_dragStart.Y, p.Y);
        var w = Math.Abs(p.X - _dragStart.X);
        var h = Math.Abs(p.Y - _dragStart.Y);
        Canvas.SetLeft(_selection, x);
        Canvas.SetTop(_selection, y);
        _selection.Width = w;
        _selection.Height = h;
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;

        var p = e.GetPosition(OverlayCanvas);
        var x = Math.Min(_dragStart.X, p.X);
        var y = Math.Min(_dragStart.Y, p.Y);
        var w = Math.Abs(p.X - _dragStart.X);
        var h = Math.Abs(p.Y - _dragStart.Y);

        if (w < 6 || h < 6 || _frame is null)
        {
            StatusText.Text = "选择区域太小，请重新拖。";
            return;
        }

        var mapped = RoiMapper.Map(x + _fit.OffsetX, y + _fit.OffsetY, w, h, _fit, _frameWidth, _frameHeight);
        _roiNative = new Rect(mapped.X, mapped.Y, mapped.Width, mapped.Height);
        StatusText.Text = $"已选区域：{mapped.X},{mapped.Y} {mapped.Width}×{mapped.Height}（原始像素）";
    }

    // ================= 模板操作 =================

    private void Save_Click(object sender, RoutedEventArgs e) => SaveTemplate("点击按钮");

    /// <summary>全局「保存模板」热键：把当前画面里已选好的区域存成模板。</summary>
    public void SaveFromHotkey() => SaveTemplate("快捷键 " + App.State.Settings.SaveTemplateKey);

    private void SaveTemplate(string via)
    {
        if (_frame is null || _frameWidth <= 0)
        {
            StatusText.Text = $"{via}保存：还没拿到画面，等预览出现后再试。";
            return;
        }

        if (_roiNative.Width < 6 || _roiNative.Height < 6)
        {
            StatusText.Text = $"{via}保存：请先在左侧预览上拖出要识别的区域。";
            return;
        }

        var file = _library.Save(
            _selectedKind, _frame, _frameWidth, _frameHeight,
            (int)_roiNative.X, (int)_roiNative.Y, (int)_roiNative.Width, (int)_roiNative.Height);
        ReloadTemplates();

        // 保存后立刻用当前画面自测这张模板，给出真实分数
        try
        {
            var png = System.IO.File.ReadAllBytes(file);
            var score = _matcher.Match(_frame, _frameWidth, _frameHeight, png);
            StatusText.Text = $"已保存模板（{via}）：{System.IO.Path.GetFileName(file)}　当前画面自测 {score * 100:0}%";
            MatchScoreText.Text = $"刚保存的模板：{score * 100:0}%（{ActionKinds.FullLabel(_selectedKind)}）";
        }
        catch
        {
            StatusText.Text = $"已保存模板（{via}）：{System.IO.Path.GetFileName(file)}";
        }
    }

    private void ReloadTemplates()
    {
        _library.Reload();
        TemplatesList.ItemsSource = _library.Templates
            .Select(t => new Row($"{ActionKinds.FullLabel(t.Kind)} · {t.Name}", t))
            .ToList();
        TemplateCountText.Text = $"共 {_library.Templates.Count} 张模板";
    }

    private void TemplatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteButton.IsEnabled = TemplatesList.SelectedItem is Row;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (TemplatesList.SelectedItem is not Row row) return;
        _library.Delete(row.Info.FilePath);
        ReloadTemplates();
        StatusText.Text = "已删除模板。";
    }

    /// <summary>定时用最新画面跑一遍当前类型的模板，显示真实匹配度。</summary>
    private void SelfCheck()
    {
        if (_checking || _frame is null || _frameWidth <= 0) return;
        var templates = _library.For(_selectedKind);
        if (templates.Count == 0)
        {
            MatchScoreText.Text = $"【{ActionKinds.FullLabel(_selectedKind)}】还没有模板，先拖框保存一张。";
            return;
        }

        _checking = true;
        try
        {
            var best = -1.0;
            var bestName = "";
            foreach (var tpl in templates)
            {
                byte[] png;
                try
                {
                    png = System.IO.File.ReadAllBytes(tpl.FilePath);
                }
                catch
                {
                    continue;
                }

                var score = _matcher.Match(_frame, _frameWidth, _frameHeight, png);
                if (score > best)
                {
                    best = score;
                    bestName = tpl.Name;
                }
            }

            MatchScoreText.Text = best < 0
                ? "自测失败（模板文件读取不到）"
                : $"当前画面 × 【{ActionKinds.FullLabel(_selectedKind)}】最佳匹配 {best * 100:0}%（{bestName}）\n" +
                  $"阈值 {ThresholdHint()}，低于阈值时不会判定完成。";
        }
        finally
        {
            _checking = false;
        }
    }

    private string ThresholdHint() => $"{_threshold * 100:0}%";

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SaveFromHotkey();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            Delete_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    // ================= AI 训练 =================

    private void RefreshAiStatus()
    {
        var kindName = _selectedKind.ToString();
        var pos = AiSampleStore.Count(kindName, true);
        var neg = AiSampleStore.Count(kindName, false);
        var model = AiModel.Load(kindName);
        var extractor = App.Screen.Extractor;

        var lines = new List<string>
        {
            $"【{ActionKinds.FullLabel(_selectedKind)}】样本：正 {pos} 张 / 负 {neg} 张",
        };

        if (!extractor.Available)
            lines.Add("⚠ ONNX 模型不可用：" + (extractor.Error ?? "未知原因"));
        else if (model is null)
            lines.Add("模型：未训练（当前会走模板匹配）");
        else
            lines.Add($"模型：已训练 {model.TrainedAt} · 验证集 {model.ValidationAccuracy:P0}");

        AiStatusText.Text = string.Join("\n", lines);
    }

    private void AiCapturePos_Click(object sender, RoutedEventArgs e) => CaptureAiSample(true);

    private void AiCaptureNeg_Click(object sender, RoutedEventArgs e) => CaptureAiSample(false);

    private void CaptureAiSample(bool positive)
    {
        if (_frame is null || _frameWidth <= 0)
        {
            StatusText.Text = "还没有画面，等预览出现后再采样。";
            return;
        }

        if (_roiNative.Width < 6 || _roiNative.Height < 6)
        {
            StatusText.Text = "请先在左侧预览上拖出识别区域。";
            return;
        }

        var roi = new Int32Rect((int)_roiNative.X, (int)_roiNative.Y, (int)_roiNative.Width, (int)_roiNative.Height);
        var file = AiSampleStore.SavePatch(_selectedKind.ToString(), positive, _frame, _frameWidth, _frameHeight, roi);
        RefreshAiStatus();
        StatusText.Text = file is null
            ? "采样失败。"
            : $"已采集{(positive ? "正" : "负")}样本：{System.IO.Path.GetFileName(file)}";
    }

    private async void AiTrain_Click(object sender, RoutedEventArgs e)
    {
        if (_roiNative.Width < 6 || _roiNative.Height < 6)
        {
            StatusText.Text = "请先拖出识别区域，训练会用这个区域提取特征。";
            return;
        }

        var kindName = _selectedKind.ToString();
        var roi = new Int32Rect((int)_roiNative.X, (int)_roiNative.Y, (int)_roiNative.Width, (int)_roiNative.Height);
        AiTrainButton.IsEnabled = false;
        StatusText.Text = "正在提取特征并训练，请稍候…";

        try
        {
            var (model, log) = await System.Threading.Tasks.Task.Run(() =>
            {
                var m = AiTrainer.Train(kindName, roi, App.Screen.Extractor, out var lg);
                return (Model: m, Log: lg);
            });

            StatusText.Text = log;
            if (model is not null)
            {
                App.Screen.InvalidateAiModels();
                RefreshAiStatus();
            }
        }
        finally
        {
            AiTrainButton.IsEnabled = true;
        }
    }

    private void AiClear_Click(object sender, RoutedEventArgs e)
    {
        var kindName = _selectedKind.ToString();
        AiSampleStore.Clear(kindName);
        AiModel.Delete(kindName);
        App.Screen.InvalidateAiModels();
        RefreshAiStatus();
        StatusText.Text = "已清除该动作的 AI 样本与模型。";
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch
        {
            // 忽略
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
