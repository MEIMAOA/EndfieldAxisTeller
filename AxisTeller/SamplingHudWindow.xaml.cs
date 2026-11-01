using System.Windows;
using System.Windows.Interop;

namespace AxisTeller;

/// <summary>采样模式的置顶小面板：不抢焦点，一边玩游戏一边按热键采样。</summary>
public partial class SamplingHudWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly TemplateManagerWindow _manager;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public SamplingHudWindow(TemplateManagerWindow manager)
    {
        InitializeComponent();
        _manager = manager;

        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left + 20, area.Right - Width - 24);
        Top = area.Top + 120;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    public void UpdateState(string kindText, string roiText, int positives, int negatives, string feedback, string? warning)
    {
        KindText.Text = kindText;
        RoiText.Text = roiText;
        CountText.Text = $"正 {positives} 张 · 负 {negatives} 张";
        FeedbackText.Text = string.IsNullOrEmpty(warning) ? feedback : "⚠ " + warning;
        HoverPlaceholder();
    }

    private void HoverPlaceholder()
    {
        HintText.Text = "F1 正样本 · F2 负样本 · F3 训练 · F4 退出\nF5 保存模板（旧方式）";
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _manager.StopSampling();
}
