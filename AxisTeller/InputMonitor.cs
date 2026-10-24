using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace AxisTeller;

/// <summary>
/// 全局键鼠监听：只观察、不拦截。用于「自动检测当前步骤是否已完成」，
/// 以及在设置里捕获要绑定的按键。
/// </summary>
public sealed class InputMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private readonly HashSet<int> _pressedKeys = new();
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private long _leftDownAt;
    private bool _leftIsDown;
    private readonly uint _ownPid = (uint)Process.GetCurrentProcess().Id;

    /// <summary>捕获模式下，第一次键鼠输入会以绑定字符串形式抛出（如 E / Space / MouseLeft / MouseLeftHold）。</summary>
    public event Action<string>? Captured;

    /// <summary>非捕获模式下检测到的输入（绑定字符串）。</summary>
    public event Action<string>? Detected;

    public bool Capturing { get; set; }

    /// <summary>按住鼠标左键超过这个时长算重攻。</summary>
    public int HoldThresholdMs { get; set; } = 350;

    public InputMonitor()
    {
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
    }

    public void Start()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
    }

    public void Stop()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _pressedKeys.Clear();
        _leftIsDown = false;
    }

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var vk = (int)info.VkCode;

            if (message is WmKeyDown or WmSysKeyDown)
            {
                if (_pressedKeys.Add(vk))
                {
                    var binding = VirtualKeyToBinding(vk);
                    if (!string.IsNullOrEmpty(binding))
                    {
                        if (Capturing)
                        {
                            Captured?.Invoke(binding);
                        }
                        else if (ShouldReact() && !IsOwnProcessForeground())
                        {
                            Detected?.Invoke(binding);
                        }
                    }
                }
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                _pressedKeys.Remove(vk);
            }
        }
        }
        catch
        {
            // 钩子回调里绝不能抛出异常，否则会直接结束进程
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message == WmLButtonDown)
            {
                _leftIsDown = true;
                _leftDownAt = Environment.TickCount64;
            }
            else if (message == WmLButtonUp && _leftIsDown)
            {
                _leftIsDown = false;
                var duration = Environment.TickCount64 - _leftDownAt;
                var binding = duration >= Math.Clamp(HoldThresholdMs, 120, 1200) ? "MouseLeftHold" : "MouseLeft";
                if (Capturing)
                {
                    Captured?.Invoke(binding);
                }
                else if (ShouldReact() && !IsOwnProcessForeground() && !IsCursorOverOwnWindow())
                {
                    Detected?.Invoke(binding);
                }
            }
        }
        }
        catch
        {
            // 同上
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool ShouldReact() => true;

    /// <summary>前台是本程序的「编辑器 / 设置 / 选人」窗口时忽略输入（提示窗本身不算）。</summary>
    private bool IsOwnProcessForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var pid);
        if (pid != _ownPid) return false;

        var sb = new System.Text.StringBuilder(128);
        GetWindowText(foreground, sb, sb.Capacity);
        var title = sb.ToString();
        // 悬浮提示窗是 WS_EX_NOACTIVATE 的 HUD，正常情况下不会成为前台；
        // 万一成了前台也不应屏蔽检测，只屏蔽真正会打字的窗口。
        return title is "终末地轴提示器" or "设置" or "选择干员" or "终末地轴提示器 · 出错了";
    }

    private bool IsCursorOverOwnWindow()
    {
        if (!GetCursorPos(out var point)) return false;
        var window = WindowFromPoint(point);
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var pid);
        return pid == _ownPid;
    }

    /// <summary>虚拟键码 → 绑定文字（与设置里存储的格式一致）。</summary>
    public static string VirtualKeyToBinding(int vk)
    {
        // 数字键 1-9 直接存 1-9
        if (vk is >= 0x31 and <= 0x39) return ((char)vk).ToString();
        if (vk == 0x20) return "Space";
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key == Key.None ? "" : key.ToString();
    }

    public void Dispose() => Stop();
}
