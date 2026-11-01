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
    private double _renderScale = 1;
    private bool _checking;

    private sealed record Row(string Text, TemplateInfo Info);

    public TemplateManagerWindow(GraphicsCaptureItem item, double threshold = 0.8)
    {
        InitializeComponent();
        _item = item;
        _threshold = threshold;

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _renderTimer.Tick += (_, _) => Render();

        _checkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _checkTimer.Tick += (_, _) => SelfCheck();

        BuildKindButtons();
        ReloadTemplates();

        Closed += (_, _) =>
        {
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

            _renderScale = Math.Min(cw / _frameWidth, ch / _frameHeight);
            var rw = _frameWidth * _renderScale;
            var rh = _frameHeight * _renderScale;

            OverlayCanvas.Width = rw;
            OverlayCanvas.Height = rh;
            OverlayCanvas.HorizontalAlignment = HorizontalAlignment.Left;
            OverlayCanvas.VerticalAlignment = VerticalAlignment.Top;
            OverlayCanvas.Margin = new Thickness((cw - rw) / 2, (ch - rh) / 2, 0, 0);

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

        var scale = _renderScale <= 0 ? 1 : 1 / _renderScale;
        _roiNative = new Rect(x * scale, y * scale, w * scale, h * scale);
        StatusText.Text = $"已选区域：{_roiNative.Width:0}×{_roiNative.Height:0}（原始像素）";
    }

    // ================= 模板操作 =================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_frame is null || _frameWidth <= 0)
        {
            StatusText.Text = "还没有画面，等预览出现后再保存。";
            return;
        }

        if (_roiNative.Width < 6 || _roiNative.Height < 6)
        {
            StatusText.Text = "请先在左侧画面上拖出要识别的区域。";
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
            StatusText.Text = $"已保存模板：{System.IO.Path.GetFileName(file)}　当前画面自测匹配度 {score * 100:0}%";
        }
        catch
        {
            StatusText.Text = $"已保存模板：{System.IO.Path.GetFileName(file)}";
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
