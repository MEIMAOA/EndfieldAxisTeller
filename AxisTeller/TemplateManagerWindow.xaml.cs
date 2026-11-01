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
    private readonly GraphicsCaptureItem _item;
    private readonly DispatcherTimer _renderTimer;

    private ActionKind _selectedKind = ActionKind.ComboSkill;
    private int _frameWidth;
    private int _frameHeight;
    private byte[]? _frame;
    private Rectangle? _selection;
    private Point _dragStart;
    private bool _dragging;
    private Rect _roiNative;

    private sealed record Row(string Text, TemplateInfo Info);

    public TemplateManagerWindow(GraphicsCaptureItem item)
    {
        InitializeComponent();
        _item = item;

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _renderTimer.Tick += (_, _) => Render();

        BuildKindButtons();
        ReloadTemplates();

        Closed += (_, _) => _capture.Stop();
        Loaded += (_, _) => StartCapture();
    }

    private void StartCapture()
    {
        CaptureStatusText.Text = "捕获：" + _item.DisplayName;
        _capture.Error += e => Dispatcher.BeginInvoke(() => StatusText.Text = "捕获错误：" + e);
        _capture.FrameArrived += OnFrame;
        _capture.Start(_item);
        _renderTimer.Start();
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
            OverlayCanvas.Width = PreviewImage.ActualWidth;
            OverlayCanvas.Height = PreviewImage.ActualHeight;
            EmptyHint.Visibility = Visibility.Collapsed;
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

        var scaleX = OverlayCanvas.Width <= 0 ? 1 : _frameWidth / OverlayCanvas.Width;
        var scaleY = OverlayCanvas.Height <= 0 ? 1 : _frameHeight / OverlayCanvas.Height;
        _roiNative = new Rect(
            (int)(x * scaleX), (int)(y * scaleY), (int)(w * scaleX), (int)(h * scaleY));
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
        StatusText.Text = $"已保存模板：{System.IO.Path.GetFileName(file)}";
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
