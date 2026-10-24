using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AxisTeller;

public partial class SettingsWindow : Window
{
    private readonly OperatorLibrary _library;
    private AppSettings _draft;
    private ActionKind? _captureKind;

    public event EventHandler? Saved;
    public event EventHandler? Synced;

    public SettingsWindow(AppSettings settings, OperatorLibrary library)
    {
        InitializeComponent();
        _library = library;
        _draft = settings.Clone();

        App.Input.Captured += OnInputCaptured;
        Closed += (_, _) => CancelCapture();

        Loaded += (_, _) => RefreshUi();
    }

    private void RefreshUi()
    {
        NextKeyButton.Content = _draft.NextKey;
        NextCycleKeyButton.Content = _draft.NextCycleKey;
        PrevKeyButton.Content = _draft.PrevKey;
        ResetKeyButton.Content = _draft.ResetKey;
        ToggleKeyButton.Content = _draft.ToggleOverlayKey;

        LoopInfiniteCheck.IsChecked = _draft.LoopInfinite;
        TopmostCheck.IsChecked = _draft.Topmost;

        AutoDetectCheck.IsChecked = _draft.AutoDetect;
        ApplyModeStyle(AutoModeMatchButton, !string.Equals(_draft.AutoDetectMode, "any", StringComparison.OrdinalIgnoreCase));
        ApplyModeStyle(AutoModeAnyButton, string.Equals(_draft.AutoDetectMode, "any", StringComparison.OrdinalIgnoreCase));

        BindNormalButton.Content = ActionKinds.BindingLabel(_draft.AutoBindNormal);
        BindHeavyButton.Content = ActionKinds.BindingLabel(_draft.AutoBindHeavy);
        BindSkillButton.Content = ActionKinds.BindingLabel(_draft.AutoBindSkill);
        BindComboButton.Content = ActionKinds.BindingLabel(_draft.AutoBindCombo);
        BindUltimateButton.Content = ActionKinds.BindingLabel(_draft.AutoBindUltimate);
        BindDodgeButton.Content = ActionKinds.BindingLabel(_draft.AutoBindDodge);
        BindSwitchButton.Content = ActionKinds.BindingLabel(_draft.AutoBindSwitch);
        HoldText.Text = $"{_draft.AutoHoldMs}ms";
        if (_captureKind is null)
            AutoStatus.Text = _draft.AutoDetect
                ? "已开启：提示窗运行时，检测到匹配操作会自动推进。"
                : "未开启：仍可用热键手动推进。";

        TokenBox.Text = _draft.SklandToken;
        UserText.Text = string.IsNullOrWhiteSpace(_draft.SklandUserName)
            ? "尚未同步账号角色"
            : $"已同步：{_draft.SklandUserName}";

        UpdateServerButtons();
        AboutVersionText.Text = $"终末地轴提示器 v1.1.0 · 存档目录：{Store.Root}";
        DataPathText.Text = "存档文件：state.json（轴数据 + 设置）";
        SyncStatus.Text = _library.Owned.Count > 0
            ? $"当前已加载 {_library.Owned.Count} 名持有角色"
            : "还未同步过账号角色，内置角色库可以直接用。";
    }

    private void ApplyModeStyle(Button button, bool active)
    {
        button.Background = active ? (Brush)FindResource("Ink") : (Brush)FindResource("BgPanel");
        button.Foreground = active ? (Brush)FindResource("OnInk") : (Brush)FindResource("Ink");
        button.BorderBrush = (Brush)FindResource("Ink");
    }

    // ============================== 自动检测 ==============================

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        _draft.AutoDetect = AutoDetectCheck.IsChecked == true;
        AutoStatus.Text = _draft.AutoDetect
            ? "已开启：提示窗运行时，检测到匹配操作会自动推进。"
            : "未开启：仍可用热键手动推进。";
    }

    private void AutoMode_Click(object sender, RoutedEventArgs e)
    {
        _draft.AutoDetectMode = (sender as FrameworkElement)?.Tag?.ToString() == "any" ? "any" : "match";
        RefreshUi();
    }

    private void BindCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_captureKind is not null)
        {
            CancelCapture();
            RefreshUi();
            AutoStatus.Text = "已取消绑定。";
            return;
        }

        var button = (Button)sender;
        var kind = ActionKinds.Parse(button.Tag?.ToString());
        if (kind == ActionKind.Unknown) return;

        _captureKind = kind;
        App.Input.HoldThresholdMs = _draft.AutoHoldMs;
        App.Input.Capturing = true;
        button.Content = "请按键…";
        AutoStatus.Text = $"正在监听【{ActionKinds.FullLabel(kind)}】：请按键盘，或按一下鼠标左键；再点一次按钮可取消。";
    }

    private void OnInputCaptured(string binding)
    {
        if (_captureKind is not { } kind) return;
        SetBinding(kind, binding);
        AutoStatus.Text = $"已绑定【{ActionKinds.FullLabel(kind)}】→ {ActionKinds.BindingLabel(binding)}";
        CancelCapture();
        RefreshUi();
    }

    private void CancelCapture()
    {
        if (_captureKind is null) return;
        _captureKind = null;
        App.Input.Capturing = false;
    }

    private void SetBinding(ActionKind kind, string binding)
    {
        switch (kind)
        {
            case ActionKind.NormalAttack: _draft.AutoBindNormal = binding; break;
            case ActionKind.HeavyAttack: _draft.AutoBindHeavy = binding; break;
            case ActionKind.Skill: _draft.AutoBindSkill = binding; break;
            case ActionKind.ComboSkill: _draft.AutoBindCombo = binding; break;
            case ActionKind.Ultimate: _draft.AutoBindUltimate = binding; break;
            case ActionKind.Dodge: _draft.AutoBindDodge = binding; break;
            case ActionKind.Switch: _draft.AutoBindSwitch = binding; break;
        }
    }

    private void HoldMinus_Click(object sender, RoutedEventArgs e)
    {
        _draft.AutoHoldMs -= 50;
        HoldText.Text = $"{_draft.AutoHoldMs}ms";
        App.Input.HoldThresholdMs = _draft.AutoHoldMs;
    }

    private void HoldPlus_Click(object sender, RoutedEventArgs e)
    {
        _draft.AutoHoldMs += 50;
        HoldText.Text = $"{_draft.AutoHoldMs}ms";
        App.Input.HoldThresholdMs = _draft.AutoHoldMs;
    }

    private void UpdateServerButtons()
    {
        var cn = string.Equals(_draft.SklandServer, "cn", StringComparison.OrdinalIgnoreCase);
        StyleButton(ServerCnButton, cn);
        StyleButton(ServerGlobalButton, !cn);
    }

    private void StyleButton(Button button, bool active)
    {
        button.Background = active ? (Brush)FindResource("Ink") : (Brush)FindResource("BgPanel");
        button.Foreground = active ? (Brush)FindResource("OnInk") : (Brush)FindResource("Ink");
        button.BorderBrush = (Brush)FindResource("Ink");
    }

    private void ChooseKey_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var property = button.Tag?.ToString() ?? "";
        var menu = new ContextMenu();
        foreach (var key in HotkeyManager.KeyChoices)
        {
            var item = new MenuItem { Header = key };
            item.Click += (_, _) =>
            {
                SetKey(property, key);
                RefreshUi();
            };
            menu.Items.Add(item);
        }

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SetKey(string property, string key)
    {
        switch (property)
        {
            case "NextKey": _draft.NextKey = key; break;
            case "PrevKey": _draft.PrevKey = key; break;
            case "NextCycleKey": _draft.NextCycleKey = key; break;
            case "ResetKey": _draft.ResetKey = key; break;
            case "ToggleOverlayKey": _draft.ToggleOverlayKey = key; break;
        }
    }

    private void LoopInfinite_Click(object sender, RoutedEventArgs e)
    {
        _draft.LoopInfinite = LoopInfiniteCheck.IsChecked == true;
        if (_draft.LoopInfinite) LoopInfiniteCheck.IsChecked = true;
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        _draft.Topmost = TopmostCheck.IsChecked == true;
    }
    private void Server_Click(object sender, RoutedEventArgs e)
    {
        _draft.SklandServer = (sender as FrameworkElement)?.Tag?.ToString() == "global" ? "global" : "cn";
        UpdateServerButtons();
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        var token = SklandClient.NormalizeToken(TokenBox.Text);
        if (string.IsNullOrWhiteSpace(token))
        {
            SyncStatus.Text = "先填入森空岛 token。";
            return;
        }

        _draft.SklandToken = TokenBox.Text.Trim();
        SyncButton.IsEnabled = false;
        SyncStatus.Text = "正在连接森空岛…";
        var progress = new Progress<string>(text => SyncStatus.Text = text);
        try
        {
            var client = new SklandClient();
            var result = await client.SyncAsync(_draft.SklandServer, token, progress);
            if (!result.Ok)
            {
                SyncStatus.Text = result.Message;
                return;
            }

            _library.MergeSkland(result.Operators, "森空岛");
            Store.SaveRoster(result.Operators);
            _draft.SklandUserName = string.IsNullOrWhiteSpace(result.RoleName) ? result.UserName : result.RoleName;
            UserText.Text = "已同步：" + _draft.SklandUserName;
            SyncStatus.Text = $"{result.Message}（已缓存头像；点保存把 token 也存下来）";
            Synced?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SyncStatus.Text = "同步出错：" + ex.Message;
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var fresh = new AppSettings
        {
            SklandToken = _draft.SklandToken,
            SklandServer = _draft.SklandServer,
            SklandUserName = _draft.SklandUserName,
        };
        _draft = fresh;
        RefreshUi();
        SyncStatus.Text = "已恢复默认按键与播放设置（token 保留）。";
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Store.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", Store.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SyncStatus.Text = "打开目录失败：" + ex.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        _draft.SklandToken = TokenBox.Text.Trim();
        App.State.Settings.CopyFrom(_draft);
        App.Input.HoldThresholdMs = App.State.Settings.AutoHoldMs;
        App.SaveAll();
        Saved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

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
}
