using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AxisTeller;

public partial class App : Application
{
    public static OperatorLibrary Library { get; private set; } = new();
    public static AppState State { get; private set; } = new();
    public static MainWindow? MainWin { get; private set; }
    public static string? LastStatus { get; set; }

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

        DispatcherUnhandledException += OnUnhandled;
        LoadEverything();

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
            if (MainWin is not null) SaveAll();
        }
        catch
        {
            // 退出时不再弹出错误
        }

        base.OnExit(e);
    }
}
