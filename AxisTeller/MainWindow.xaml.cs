using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AxisTeller;

public partial class MainWindow : Window
{
    private readonly PlaybackEngine _engine = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly DispatcherTimer _saveTimer;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settings;
    private bool _loading;
    private bool _exiting;

    private static readonly string[] ActionPresets =
    {
        "普攻", "战技", "连携技", "终结技", "闪避", "切换", "支援", "技能", "待机/等CD",
    };

    private AppState State => App.State;
    private OperatorLibrary Library => App.Library;
    private Axis? CurrentAxis => AxisList.SelectedItem as Axis;

    public MainWindow()
    {
        InitializeComponent();

        App.Input.Detected += OnInputDetected;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        RestoreWindowBounds();
        RefreshAxisList();
        UpdateSourceChip();
        UpdateHotkeyHint();
        StatusText.Text = App.LastStatus ?? "就绪";

        Loaded += (_, _) => ApplyHotkeys();
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void RestoreWindowBounds()
    {
        var s = State.Settings;
        if (!double.IsNaN(s.EditorLeft) && !double.IsNaN(s.EditorTop))
        {
            Left = s.EditorLeft;
            Top = s.EditorTop;
        }

        if (s.EditorWidth >= MinWidth) Width = s.EditorWidth;
        if (s.EditorHeight >= MinHeight) Height = s.EditorHeight;

        if (Left + Width < 100 || Top + Height < 100 || Top < -50)
        {
            Left = double.NaN;
            Top = double.NaN;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hotkeys.Attach(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        _hotkeys.Pressed += OnHotkey;
        ApplyHotkeys();
    }

    // ============================== 轴库 ==============================

    private void RefreshAxisList()
    {
        _loading = true;
        var selectedId = CurrentAxis?.Id ?? State.Settings.LastAxisId;
        AxisList.ItemsSource = State.Axes;
        var axis = State.Axes.FirstOrDefault(a => a.Id == selectedId) ?? State.Axes.FirstOrDefault();
        AxisList.SelectedItem = axis;
        _loading = false;
        LoadAxis(axis);
    }

    private void AxisList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CurrentAxis is null) return;
        LoadAxis(CurrentAxis);
        State.Settings.LastAxisId = CurrentAxis.Id;
        MarkDirty();
    }

    private void LoadAxis(Axis? axis)
    {
        if (axis is null) return;
        _loading = true;
        AxisNameBox.Text = axis.Name;
        TeamBox.Text = axis.Team;
        LoopsText.Text = axis.Loops.ToString();
        CycleItems.ItemsSource = axis.Cycles;
        _loading = false;

        State.Settings.LastAxisId = axis.Id;
        _engine.Attach(axis, State.Settings.LoopInfinite);
        _overlay?.Refresh();
        UpdateStatusForStep();
    }

    private void AddAxis_Click(object sender, RoutedEventArgs e)
    {
        var axis = new Axis { Name = $"新轴 {State.Axes.Count + 1}", Loops = 2 };
        axis.Cycles.Add(NewCycle(1));
        State.Axes.Add(axis);
        AxisList.SelectedItem = axis;
        MarkDirty();
        AxisNameBox.Focus();
        AxisNameBox.SelectAll();
    }

    private void DuplicateAxis_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAxis is null) return;
        var copy = JsonSerializer.Deserialize<Axis>(JsonSerializer.Serialize(CurrentAxis, Json.Options), Json.Options);
        if (copy is null) return;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = CurrentAxis.Name + " 副本";
        State.Axes.Add(copy);
        Library.ResolveAll(new[] { copy });
        AxisList.SelectedItem = copy;
        MarkDirty();
    }

    private void DeleteAxis_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAxis is null) return;
        if (State.Axes.Count <= 1)
        {
            MessageBox.Show(this, "至少要保留一条轴。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(this, $"删除轴「{CurrentAxis.Name}」？此操作不可撤销。", "删除轴",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        var index = AxisList.SelectedIndex;
        State.Axes.Remove(CurrentAxis);
        AxisList.SelectedIndex = Math.Clamp(index, 0, State.Axes.Count - 1);
        MarkDirty();
    }

    private void ImportExport_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("导入轴（JSON 文件）", ImportAxes));
        menu.Items.Add(MakeMenuItem("导出当前轴（JSON 文件）", ExportAxis));
        menu.Items.Add(MakeMenuItem("导出全部轴（JSON 文件）", ExportAll));
        menu.Items.Add(MakeMenuItem("复制当前轴到剪贴板", () =>
        {
            if (CurrentAxis is null) return;
            Clipboard.SetText(JsonSerializer.Serialize(CurrentAxis, Json.Options));
            SetStatus("已复制当前轴 JSON 到剪贴板");
        }));
        OpenMenu(menu, (FrameworkElement)sender);
    }

    private static MenuItem MakeMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenMenu(ContextMenu menu, FrameworkElement anchor)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ImportAxes()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            Title = "导入轴",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var text = File.ReadAllText(dialog.FileName);
            var imported = ParseAxes(text);
            if (imported.Count == 0)
            {
                MessageBox.Show(this, "没有从文件里读到轴数据。", "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            foreach (var axis in imported)
            {
                axis.Id = Guid.NewGuid().ToString("N");
                axis.Name = MakeUniqueAxisName(axis.Name);
                State.Axes.Add(axis);
            }

            Library.ResolveAll(imported);
            AxisList.SelectedItem = imported[^1];
            MarkDirty();
            SetStatus($"已导入 {imported.Count} 条轴");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导入失败：" + ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string MakeUniqueAxisName(string name)
    {
        var candidate = string.IsNullOrWhiteSpace(name) ? "导入的轴" : name;
        var suffix = 2;
        while (State.Axes.Any(a => a.Name == candidate)) candidate = $"{name} ({suffix++})";
        return candidate;
    }

    private static List<Axis> ParseAxes(string text)
    {
        var options = new JsonSerializerOptions(Json.Options);
        try
        {
            var state = JsonSerializer.Deserialize<AppState>(text, options);
            if (state is { Axes.Count: > 0 }) return state.Axes.ToList();
        }
        catch
        {
            // 继续尝试其他格式
        }

        try
        {
            var list = JsonSerializer.Deserialize<List<Axis>>(text, options);
            if (list is { Count: > 0 }) return list;
        }
        catch
        {
            // 继续尝试其他格式
        }

        var single = JsonSerializer.Deserialize<Axis>(text, options);
        return single is null ? new List<Axis>() : new List<Axis> { single };
    }

    private void ExportAxis()
    {
        if (CurrentAxis is null) return;
        ExportJson(JsonSerializer.Serialize(CurrentAxis, Json.Options), SafeFileName(CurrentAxis.Name) + ".json");
    }

    private void ExportAll()
    {
        ExportJson(JsonSerializer.Serialize(State.Axes, Json.Options), "终末地-全部轴.json");
    }

    private void ExportJson(string json, string defaultName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = defaultName,
            Title = "导出轴",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, json);
            SetStatus("已导出到 " + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Where(ch => !invalid.Contains(ch)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "axis" : safe;
    }

    // ============================== 循环 / 动作 ==============================

    private static Cycle NewCycle(int index) => new() { Name = $"循环 {index}" };

    private void AddCycle_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAxis is null) return;
        var cycle = NewCycle(CurrentAxis.Cycles.Count + 1);
        cycle.Steps.Add(new Step { Action = "战技" });
        CurrentAxis.Cycles.Add(cycle);
        MarkDirty();
        CycleScroll.ScrollToEnd();
    }

    private void CycleUp_Click(object sender, RoutedEventArgs e) => MoveCycle(sender, -1);

    private void CycleDown_Click(object sender, RoutedEventArgs e) => MoveCycle(sender, +1);

    private void MoveCycle(object sender, int delta)
    {
        if (CurrentAxis is null || (sender as FrameworkElement)?.DataContext is not Cycle cycle) return;
        var index = CurrentAxis.Cycles.IndexOf(cycle);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= CurrentAxis.Cycles.Count) return;
        CurrentAxis.Cycles.Move(index, target);
        MarkDirty();
    }

    private void CycleDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAxis is null || (sender as FrameworkElement)?.DataContext is not Cycle cycle) return;
        var copy = new Cycle { Name = cycle.Name + " 副本", RepeatHint = cycle.RepeatHint };
        foreach (var step in cycle.Steps)
        {
            var newStep = new Step
            {
                OperatorId = step.OperatorId,
                OperatorName = step.OperatorName,
                Action = step.Action,
                Note = step.Note,
            };
            Library.Resolve(newStep);
            copy.Steps.Add(newStep);
        }

        CurrentAxis.Cycles.Insert(CurrentAxis.Cycles.IndexOf(cycle) + 1, copy);
        MarkDirty();
    }

    private void CycleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAxis is null || (sender as FrameworkElement)?.DataContext is not Cycle cycle) return;
        if (CurrentAxis.Cycles.Count <= 1)
        {
            MessageBox.Show(this, "每条轴至少要保留一个循环。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CurrentAxis.Cycles.Remove(cycle);
        MarkDirty();
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Cycle cycle) return;
        cycle.Steps.Add(new Step { Action = "战技" });
        MarkDirty();
    }

    private Cycle? FindCycleOfStep(Step step) =>
        CurrentAxis?.Cycles.FirstOrDefault(c => c.Steps.Contains(step));

    private void StepUp_Click(object sender, RoutedEventArgs e) => MoveStep(sender, -1);

    private void StepDown_Click(object sender, RoutedEventArgs e) => MoveStep(sender, +1);

    private void MoveStep(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.DataContext is not Step step) return;
        var cycle = FindCycleOfStep(step);
        if (cycle is null) return;
        var index = cycle.Steps.IndexOf(step);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= cycle.Steps.Count) return;
        cycle.Steps.Move(index, target);
        MarkDirty();
    }

    private void StepDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Step step) return;
        var cycle = FindCycleOfStep(step);
        if (cycle is null) return;
        cycle.Steps.Remove(step);
        MarkDirty();
    }

    private void PickOperator_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Step step) return;
        var picker = new OperatorPickerWindow(Library, step.OperatorName) { Owner = this };
        if (picker.ShowDialog() != true) return;

        var op = picker.SelectedOperator;
        step.OperatorId = op?.Id ?? "";
        step.OperatorName = op?.Name ?? picker.CustomName;
        step.Avatar = op?.Avatar;
        MarkDirty();
        UpdateOverlayIfOpen();
    }

    private void ActionMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Step step) return;
        var menu = new ContextMenu();
        foreach (var preset in ActionPresets)
            menu.Items.Add(MakeMenuItem(preset, () =>
            {
                step.Action = preset;
                MarkDirty();
            }));
        OpenMenu(menu, (FrameworkElement)sender);
    }

    private void KindMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Step step) return;
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("自动识别（按动作文字）", () =>
        {
            step.Kind = "";
            MarkDirty();
        }));
        menu.Items.Add(new Separator());
        foreach (var kind in ActionKinds.Selectable)
        {
            var target = kind;
            menu.Items.Add(MakeMenuItem(ActionKinds.FullLabel(target), () =>
            {
                step.Kind = target.ToString();
                MarkDirty();
            }));
        }

        OpenMenu(menu, (FrameworkElement)sender);
    }

    /// <summary>全局键鼠监听回调：检测到与当前步骤匹配的操作就自动推进。</summary>
    private void OnInputDetected(string binding)
    {
        var settings = State.Settings;
        if (!settings.AutoDetect) return;
        if (_overlay is not { IsVisible: true }) return;

        var detected = ActionKinds.MatchInput(binding, settings);
        if (detected is null) return;

        var step = _engine.CurrentStep;
        if (step is null) return;
        if (!ActionKinds.IsCurrentStepCompleted(step, detected.Value, settings.AutoDetectMode)) return;

        _engine.Next(settings.LoopInfinite);
        _overlay.Refresh();
        _overlay.ShowDetected(ActionKinds.FullLabel(detected.Value));
        UpdateStatusForStep();
    }

    private void LoopsMinus_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAxis is null) return;
        CurrentAxis.Loops--;
        LoopsText.Text = CurrentAxis.Loops.ToString();
        MarkDirty();
    }

    private void LoopsPlus_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAxis is null) return;
        CurrentAxis.Loops++;
        LoopsText.Text = CurrentAxis.Loops.ToString();
        MarkDirty();
    }

    private void DataTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (ReferenceEquals(sender, AxisNameBox) && CurrentAxis is not null) CurrentAxis.Name = AxisNameBox.Text;
        else if (ReferenceEquals(sender, TeamBox) && CurrentAxis is not null) CurrentAxis.Team = TeamBox.Text;
        MarkDirty();
        UpdateOverlayIfOpen();
    }

    // ============================== 提示窗 / 热键 / 设置 ==============================

    private void StartOverlay_Click(object sender, RoutedEventArgs e) => ShowOverlay();

    private void PreviewCycle_Click(object sender, RoutedEventArgs e) => ShowOverlay();

    private void ShowOverlay()
    {
        if (CurrentAxis is null) return;
        _overlay ??= CreateOverlay();
        _engine.Attach(CurrentAxis, State.Settings.LoopInfinite);
        _overlay.ApplySettings();
        _overlay.Refresh();
        _overlay.Show();
        Hide();
        SetStatus("提示窗已打开：按键推进动作，F6 显示/隐藏");
    }

    private OverlayWindow CreateOverlay()
    {
        var overlay = new OverlayWindow(_engine, State.Settings);
        overlay.EditorRequested += (_, _) =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        };
        overlay.ExitRequested += (_, _) => RequestExit();
        overlay.AxisSwitchRequested += (_, index) =>
        {
            if (index < 0 || index >= State.Axes.Count) return;
            AxisList.SelectedIndex = index;
            _overlay?.Refresh();
        };
        overlay.Closed += (_, _) =>
        {
            _overlay = null;
            if (_exiting) return;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        };
        return overlay;
    }

    private void OnHotkey(HotkeyAction action)
    {
        var infinite = State.Settings.LoopInfinite;
        switch (action)
        {
            case HotkeyAction.Next:
                _engine.Next(infinite);
                break;
            case HotkeyAction.Previous:
                _engine.Previous();
                break;
            case HotkeyAction.NextCycle:
                _engine.NextCycle(infinite);
                break;
            case HotkeyAction.Reset:
                _engine.Reset(infinite);
                break;
            case HotkeyAction.ToggleOverlay:
                ToggleOverlay();
                return;
        }

        _overlay?.Refresh();
        UpdateStatusForStep();
    }

    private void ToggleOverlay()
    {
        if (_overlay is { IsVisible: true })
        {
            _overlay.Hide();
            if (!IsVisible) Show();
            return;
        }

        ShowOverlay();
    }

    private void UpdateStatusForStep()
    {
        if (CurrentAxis is null) return;
        var step = _engine.CurrentStep;
        var cycle = _engine.CurrentCycle;
        var round = _engine.TotalRounds > 1 ? $"第 {_engine.Round}/{_engine.TotalRounds} 轮 · " : "";
        if (step is null)
        {
            SetStatus("这条轴还没有动作，先添加一个吧");
            return;
        }

        SetStatus($"{round}循环 {_engine.CycleIndex + 1}/{CurrentAxis.Cycles.Count}「{cycle?.Name}」· 第 {_engine.StepNumberInRound}/{_engine.TotalSteps} 拍：" +
                  $"{step.DisplayName} · {step.ActionText}");
    }

    private void ApplyHotkeys()
    {
        var s = State.Settings;
        var bindings = new[]
        {
            HotkeyManager.Build(HotkeyAction.Next, s.NextKey),
            HotkeyManager.Build(HotkeyAction.Previous, s.PrevKey),
            HotkeyManager.Build(HotkeyAction.NextCycle, s.NextCycleKey),
            HotkeyManager.Build(HotkeyAction.Reset, s.ResetKey),
            HotkeyManager.Build(HotkeyAction.ToggleOverlay, s.ToggleOverlayKey),
        }.Where(b => b is not null).Select(b => b!).ToList();

        _hotkeys.Apply(bindings);
        UpdateHotkeyHint();
        if (_hotkeys.Failed.Count > 0)
        {
            var names = _hotkeys.Failed.Select(a => a switch
            {
                HotkeyAction.Next => s.NextKey,
                HotkeyAction.Previous => s.PrevKey,
                HotkeyAction.NextCycle => s.NextCycleKey,
                HotkeyAction.Reset => s.ResetKey,
                _ => s.ToggleOverlayKey,
            });
            SetStatus("以下热键被占用，可在设置里换一个：" + string.Join("、", names));
        }
    }

    private void UpdateHotkeyHint()
    {
        var s = State.Settings;
        HotkeyHint.Text = $"热键：{s.NextKey} 下一步 · {s.NextCycleKey} 下一循环 · {s.PrevKey} 上一步 · {s.ResetKey} 重来 · {s.ToggleOverlayKey} 提示窗";
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is { IsVisible: true })
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(State.Settings, Library) { Owner = this };
        _settings.Synced += (_, _) => UpdateSourceChip();
        _settings.Saved += (_, _) =>
        {
            ApplyHotkeys();
            UpdateSourceChip();
            if (CurrentAxis is not null) _engine.Attach(CurrentAxis, State.Settings.LoopInfinite);
            _overlay?.ApplySettings();
            _overlay?.Refresh();
            MarkDirty();
        };
        _settings.ShowDialog();
        _settings = null;
        UpdateHotkeyHint();
        UpdateSourceChip();
    }

    private void UpdateSourceChip()
    {
        var owned = Library.Owned.Count;
        SourceText.Text = owned > 0
            ? $"数据源：森空岛 · 持有 {owned} 名"
            : $"数据源：森空岛 · {Library.Operators.Count} 名干员";
        var detail = string.IsNullOrWhiteSpace(Library.Source) ? "内置干员快照" : Library.Source;
        SourceChip.ToolTip = string.IsNullOrWhiteSpace(Library.UpdatedAt)
            ? detail + "\n可在「设置 → 森空岛数据」里同步账号角色"
            : $"{detail}\n更新时间：{Library.UpdatedAt}\n可在「设置 → 森空岛数据」里同步账号角色";
    }

    private void UpdateOverlayIfOpen()
    {
        if (_overlay is { IsVisible: true })
        {
            _overlay.Refresh();
            UpdateStatusForStep();
        }
    }

    // ============================== 窗口杂项 ==============================

    private void MarkDirty()
    {
        if (_loading) return;
        StatusText.Text = "未保存…";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        App.SaveAll();
        SetStatus($"已保存 {DateTime.Now:HH:mm:ss}");
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        App.LastStatus = text;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // 拖动时按键松开等情况忽略
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => RequestExit();

    private void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        SaveWindowBounds();
        SaveNow();
        _hotkeys.Dispose();
        if (_overlay is not null)
        {
            _overlay.Close();
        }

        Application.Current.Shutdown();
    }

    private void SaveWindowBounds()
    {
        var s = State.Settings;
        if (WindowState == WindowState.Normal)
        {
            s.EditorLeft = Left;
            s.EditorTop = Top;
            s.EditorWidth = Width;
            s.EditorHeight = Height;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        RequestExit();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SaveNow();
            e.Handled = true;
        }
    }
}
