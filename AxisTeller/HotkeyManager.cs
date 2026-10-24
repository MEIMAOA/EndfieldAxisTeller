using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace AxisTeller;

public enum HotkeyAction
{
    Next,
    Previous,
    NextCycle,
    Reset,
    ToggleOverlay,
}

public sealed record HotkeyBinding(HotkeyAction Action, uint Modifiers, uint VirtualKey, string Text);

/// <summary>全局热键：即使游戏窗口在前台也能按键推进提示。</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    public static readonly string[] KeyChoices =
    {
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "Insert", "Delete", "Home", "End", "PageUp", "PageDown",
        "Ctrl+F6", "Ctrl+F7", "Ctrl+F8", "Ctrl+F9", "Ctrl+F10", "Ctrl+F11", "Ctrl+F12",
        "Alt+F6", "Alt+F7", "Alt+F8", "Alt+F9", "Ctrl+Shift+F6", "Ctrl+Shift+F7", "Ctrl+Shift+F8", "Ctrl+Shift+F9",
    };

    private readonly Dictionary<int, HotkeyAction> _ids = new();
    private HwndSource? _source;
    private IntPtr _hwnd;
    private int _nextId = 0x1A00;

    public IReadOnlyList<HotkeyAction> Failed { get; private set; } = Array.Empty<HotkeyAction>();

    public event Action<HotkeyAction>? Pressed;

    public void Attach(IntPtr hwnd)
    {
        if (_hwnd == hwnd) return;
        Detach();
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(Hook);
    }

    public void Apply(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterAll();
        _nextId = 0x1A00;
        var failed = new List<HotkeyAction>();
        foreach (var binding in bindings)
        {
            if (binding.VirtualKey == 0) continue;
            var id = _nextId++;
            if (RegisterHotKey(_hwnd, id, binding.Modifiers | ModNoRepeat, binding.VirtualKey))
            {
                _ids[id] = binding.Action;
            }
            else
            {
                failed.Add(binding.Action);
            }
        }

        Failed = failed;
    }

    public static HotkeyBinding? Build(HotkeyAction action, string text)
    {
        var parsed = Parse(text);
        return parsed is null ? null : new HotkeyBinding(action, parsed.Value.mods, parsed.Value.vk, text);
    }

    public static (uint mods, uint vk)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i < parts.Length - 1)
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl" or "control": mods |= ModControl; break;
                    case "alt": mods |= ModAlt; break;
                    case "shift": mods |= ModShift; break;
                    case "win": mods |= ModWin; break;
                    default: return null;
                }
            }
            else
            {
                if (!Enum.TryParse<Key>(part, ignoreCase: true, out var key)) return null;
                var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                return vk == 0 ? null : (mods, vk);
            }
        }

        return null;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _ids.TryGetValue(wParam.ToInt32(), out var action))
        {
            Pressed?.Invoke(action);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        foreach (var id in _ids.Keys) UnregisterHotKey(_hwnd, id);
        _ids.Clear();
    }

    public void Detach()
    {
        UnregisterAll();
        _source?.RemoveHook(Hook);
        _source = null;
        _hwnd = IntPtr.Zero;
    }

    public void Dispose() => Detach();
}
