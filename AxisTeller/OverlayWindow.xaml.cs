using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace AxisTeller;

/// <summary>
/// 悬浮提示窗：参照游戏内「任务目标」追踪面板重做。
/// 半透明黑底横条 + 细线分隔 + 荧光黄强调，显示当前动作、本轮进度和接下来几拍。
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xEA, 0x00));
    private static readonly Brush DimAccentBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x99, 0x99, 0x99));

    private readonly PlaybackEngine _engine;
    private readonly AppSettings _settings;

    public event EventHandler? EditorRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<int>? AxisSwitchRequested;

    public OverlayWindow(PlaybackEngine engine, AppSettings settings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        _engine.Changed += Refresh;
        Loaded += (_, _) =>
        {
            ApplySettings();
            RestorePosition();
            Refresh();
        };
        LocationChanged += (_, _) => SavePosition();
    }

    public void ApplySettings()
    {
        Topmost = _settings.Topmost;
        AutoBadge.Visibility = _settings.AutoDetect ? Visibility.Visible : Visibility.Collapsed;
        HotkeyHintText.Text = _settings.AutoDetect
            ? $"自动检测中 · {_settings.NextKey} 手动下一步 · {_settings.ToggleOverlayKey} 隐藏"
            : $"{_settings.NextKey} 下一步 · {_settings.NextCycleKey} 下一循环 · {_settings.ResetKey} 重来 · {_settings.ToggleOverlayKey} 隐藏";
    }

    /// <summary>自动检测到动作后，在「接下来」行给出一次即时反馈。</summary>
    public void ShowDetected(string label)
    {
        NextCycleHint.Text = "✔ 识别：" + label;
    }

    private void RestorePosition()
    {
        var area = SystemParameters.WorkArea;
        if (!double.IsNaN(_settings.OverlayLeft) && !double.IsNaN(_settings.OverlayTop) &&
            _settings.OverlayLeft > area.Left - Width && _settings.OverlayLeft < area.Right &&
            _settings.OverlayTop > area.Top - 100 && _settings.OverlayTop < area.Bottom)
        {
            Left = _settings.OverlayLeft;
            Top = _settings.OverlayTop;
        }
        else
        {
            // 默认贴在左上角，和游戏里的任务目标面板位置一致
            Left = area.Left + 24;
            Top = area.Top + 96;
        }
    }

    private void SavePosition()
    {
        if (!IsVisible) return;
        _settings.OverlayLeft = Left;
        _settings.OverlayTop = Top;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    public void Refresh()
    {
        var axis = _engine.Axis;
        if (axis is null) return;

        var cycleCount = Math.Max(1, axis.Cycles.Count);
        var rounds = _engine.TotalRounds;
        AxisNameText.Text = axis.Name;
        MetaText.Text = $"CYCLE {_engine.CycleIndex + 1:00}/{cycleCount:00}";
        RoundText.Text = _settings.LoopInfinite
            ? $"ROUND {_engine.Round:00} · ∞"
            : rounds > 1 ? $"ROUND {_engine.Round:00}/{rounds:00}" : "ROUND 01";
        HotkeyHintText.Text = _settings.AutoDetect
            ? $"自动检测中 · {_settings.NextKey} 手动下一步 · {_settings.ToggleOverlayKey} 隐藏"
            : $"{_settings.NextKey} 下一步 · {_settings.NextCycleKey} 下一循环 · {_settings.ResetKey} 重来 · {_settings.ToggleOverlayKey} 隐藏";

        var total = Math.Max(0, _engine.TotalSteps);
        var currentIndex = _engine.StepNumberInRound;
        var ratio = total == 0 ? 0 : Math.Clamp((double)(currentIndex - 1) / total, 0, 1);
        ProgressCompletedCol.Width = new GridLength(Math.Max(0.001, ratio), GridUnitType.Star);
        ProgressRemainingCol.Width = new GridLength(Math.Max(0.001, 1 - ratio), GridUnitType.Star);
        ProgressText.Text = $"{currentIndex:00} / {total:00}";

        var step = _engine.CurrentStep;
        if (_engine.Finished)
        {
            AvatarImage.Source = null;
            AvatarLetter.Text = "✓";
            StepNameText.Text = "整条轴完成";
            StepActionText.Text = "COMPLETE";
            StepActionText.Foreground = AccentBrush;
            StepNoteText.Text = string.Empty;
            StepIndexText.Text = "#--";
            CurrentAccentBar.Background = AccentBrush;
            ProgressCompletedCol.Width = new GridLength(1, GridUnitType.Star);
            ProgressRemainingCol.Width = new GridLength(0.001, GridUnitType.Star);
            ProgressText.Text = $"{total:00} / {total:00}";
            NextCycleHint.Text = "已结束";
            RoundText.Text = "COMPLETE";
            FillUpcoming(new List<Step>());
            return;
        }

        if (step is null)
        {
            AvatarImage.Source = null;
            AvatarLetter.Text = "?";
            StepNameText.Text = "这条轴还没有动作";
            StepActionText.Text = "去编辑";
            StepNoteText.Text = string.Empty;
            StepIndexText.Text = "#--";
            NextCycleHint.Text = string.Empty;
            FillUpcoming(new List<Step>());
            return;
        }

        AvatarImage.Source = step.Avatar;
        AvatarLetter.Text = FirstChar(step.DisplayName);
        StepNameText.Text = step.DisplayName;
        StepActionText.Text = step.ActionText;
        StepActionText.Foreground = AccentBrush;
        StepNoteText.Text = step.Note;
        StepIndexText.Text = $"#{currentIndex:00}";
        CurrentAccentBar.Background = AccentBrush;

        var nextPos = _engine.PeekNextPosition(_settings.LoopInfinite);
        if (nextPos is { } np)
        {
            if (np.round != _engine.Round) NextCycleHint.Text = "→ NEXT ROUND";
            else if (np.cycle != _engine.CycleIndex) NextCycleHint.Text = "→ NEXT CYCLE";
            else NextCycleHint.Text = string.Empty;
        }
        else
        {
            NextCycleHint.Text = string.Empty;
        }

        FillUpcoming(_engine.PeekForward(3, _settings.LoopInfinite));
    }

    private static string FirstChar(string text) => string.IsNullOrWhiteSpace(text) ? "?" : text.Trim()[0].ToString();

    private void FillUpcoming(IReadOnlyList<Step> upcoming)
    {
        var rows = new[]
        {
            (No: UpNo1, Name: UpName1, Action: UpAction1, Accent: UpAccent1),
            (No: UpNo2, Name: UpName2, Action: UpAction2, Accent: UpAccent2),
            (No: UpNo3, Name: UpName3, Action: UpAction3, Accent: UpAccent3),
        };

        var baseIndex = _engine.StepNumberInRound;
        var total = Math.Max(1, _engine.TotalSteps);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (i < upcoming.Count)
            {
                var step = upcoming[i];
                var number = ((baseIndex + i) % total) + 1;
                row.No.Text = $"{number:00}";
                row.No.Foreground = Brushes.White;
                row.Name.Text = step.DisplayName;
                row.Name.Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
                row.Action.Text = step.ActionText;
                row.Action.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
                row.Accent.Background = step.Accent;
            }
            else
            {
                row.No.Text = "--";
                row.No.Foreground = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
                row.Name.Text = "—";
                row.Name.Foreground = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
                row.Action.Text = string.Empty;
                row.Accent.Background = DimAccentBrush;
            }
        }
    }

    // ============================== 交互 ==============================

    private void Tracker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            EditorRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // 忽略
        }
    }

    private void Tracker_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("打开编辑窗口", () => EditorRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(MakeItem("重置到第一个动作", () =>
        {
            _engine.Reset(_settings.LoopInfinite);
            Refresh();
        }));
        menu.Items.Add(new Separator());
        for (var i = 0; i < App.State.Axes.Count; i++)
        {
            var index = i;
            menu.Items.Add(MakeItem($"切换轴：{App.State.Axes[i].Name}", () => AxisSwitchRequested?.Invoke(this, index)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("隐藏提示窗（" + _settings.ToggleOverlayKey + "）", () =>
        {
            Hide();
            EditorRequested?.Invoke(this, EventArgs.Empty);
        }));
        menu.Items.Add(MakeItem("退出程序", () => ExitRequested?.Invoke(this, EventArgs.Empty)));
        menu.PlacementTarget = Tracker;
        menu.IsOpen = true;
    }

    private static MenuItem MakeItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenEditor_Click(object sender, RoutedEventArgs e) =>
        EditorRequested?.Invoke(this, EventArgs.Empty);

    private void CloseOverlay_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SavePosition();
        base.OnClosing(e);
    }
}
