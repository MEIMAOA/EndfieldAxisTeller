using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AxisTeller.Capture;

public sealed record WindowInfo(IntPtr Handle, uint ProcessId, string ProcessName, string Title)
{
    public string Display => string.IsNullOrWhiteSpace(ProcessName) ? Title : $"{Title}　·　{ProcessName}";
}

/// <summary>枚举可见顶层窗口，用来让用户挑游戏窗口 / 自动找游戏窗口。</summary>
public static class WindowEnumerator
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    public static List<WindowInfo> VisibleWindows()
    {
        var list = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var title = new StringBuilder(512);
            if (GetWindowText(hwnd, title, title.Capacity) <= 0) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            var processName = "";
            try
            {
                processName = Process.GetProcessById((int)pid).ProcessName;
            }
            catch
            {
                // 忽略取不到进程名的情况
            }

            list.Add(new WindowInfo(hwnd, pid, processName, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>优先按进程名找《终末地》窗口，其次按标题关键词。</summary>
    public static WindowInfo? FindGameWindow()
    {
        var windows = VisibleWindows();
        var byProcess = windows.FirstOrDefault(w =>
            w.ProcessName.Contains("endfield", StringComparison.OrdinalIgnoreCase) ||
            w.ProcessName.Contains("arknights", StringComparison.OrdinalIgnoreCase));
        if (byProcess is not null) return byProcess;

        return windows.FirstOrDefault(w =>
            w.Title.Contains("Endfield", StringComparison.OrdinalIgnoreCase) ||
            w.Title.Contains("终末地", StringComparison.OrdinalIgnoreCase));
    }

    public static WindowInfo? FindByTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        return VisibleWindows().FirstOrDefault(w =>
            string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase));
    }
}
