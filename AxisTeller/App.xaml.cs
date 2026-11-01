using System.IO;
using System.Windows;
using System.Windows.Threading;
using Windows.Graphics.Capture;

namespace AxisTeller;

public partial class App : Application
{
    public static OperatorLibrary Library { get; private set; } = new();
    public static AppState State { get; private set; } = new();
    public static MainWindow? MainWin { get; private set; }
    public static string? LastStatus { get; set; }

    /// <summary>全局键鼠监听（自动检测动作 + 设置里捕获按键）。</summary>
    public static InputMonitor Input { get; } = new();

    /// <summary>画面检测（Windows Graphics Capture + 模板匹配）。</summary>
    public static ScreenDetector Screen { get; } = new();

    /// <summary>用户用系统捕获选择器选中的游戏窗口（会话内保持有效）。</summary>
    public static GraphicsCaptureItem? CaptureItem { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        var selfTestIndex = Array.FindIndex(args, a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
        if (selfTestIndex >= 0)
        {
            var output = selfTestIndex + 1 < args.Length ? args[selfTestIndex + 1] : Path.Combine(Path.GetTempPath(), "axis-teller-selftest.txt");
            var ok = Selftest.Run(output);
            Shutdown(ok ? 0 : 1);
            return;
        }

        var captureIndex = Array.FindIndex(args, a => string.Equals(a, "--capture-smoke", StringComparison.OrdinalIgnoreCase));
        if (captureIndex >= 0)
        {
            var hwnd = captureIndex + 1 < args.Length && long.TryParse(args[captureIndex + 1], out var h)
                ? new IntPtr(h)
                : IntPtr.Zero;
            var output = captureIndex + 2 < args.Length ? args[captureIndex + 2] : Path.Combine(Path.GetTempPath(), "capture-smoke.png");
            var ok = CaptureSmoke.Run(hwnd, output);
            Shutdown(ok ? 0 : 1);
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        LoadEverything();
        Input.HoldThresholdMs = State.Settings.AutoHoldMs;
        Input.Start();

        MainWin = new MainWindow();
        MainWin.Show();
    }

    public static void LoadEverything()
    {
        Library = new OperatorLibrary();
        Library.LoadBundled(Store.AssetsDir);
        Library.LoadSklandRoster(Store.RosterPath, Store.SklandIconDir);

        State = Store.Load();
        State.Settings ??= new AppSettings();
        Library.ResolveAll(State.Axes);
        LastStatus = $"已载入 {State.Axes.Count} 条轴 · 角色库 {Library.Operators.Count} 名干员";
    }

    public static void SaveAll()
    {
        try
        {
            Store.Save(State);
        }
        catch (Exception ex)
        {
            LastStatus = "保存失败：" + ex.Message;
        }
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Store.Root);
            File.AppendAllText(Path.Combine(Store.Root, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{e.Exception}\n\n");
        }
        catch
        {
            // 忽略日志失败
        }

        MessageBox.Show(e.Exception.Message, "终末地轴提示器 · 出错了", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Input.Stop();
            Screen.Stop();
            if (MainWin is not null) SaveAll();
        }
        catch
        {
            // 退出时不再弹出错误
        }

        base.OnExit(e);
    }
}
