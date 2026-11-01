using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AxisTeller.Capture;

namespace AxisTeller;

public partial class WindowPickerWindow : Window
{
    private List<WindowInfo> _all = new();

    public WindowInfo? SelectedWindow { get; private set; }

    public WindowPickerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LoadWindows();
            // 默认选中游戏窗口
            var game = WindowEnumerator.FindGameWindow();
            if (game is not null)
            {
                var match = _all.FirstOrDefault(w => w.Handle == game.Handle);
                if (match is not null)
                {
                    WindowList.SelectedItem = match;
                    WindowList.ScrollIntoView(match);
                }
            }

            SearchBox.Focus();
        };
    }

    private void LoadWindows()
    {
        var ownPid = (uint)Environment.ProcessId;
        _all = WindowEnumerator.VisibleWindows()
            .Where(w => w.ProcessId != ownPid)
            .OrderBy(w => w.ProcessName)
            .ThenBy(w => w.Title)
            .ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = SearchBox.Text.Trim();
        var list = _all
            .Where(w => filter.Length == 0 ||
                        w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        w.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        WindowList.ItemsSource = list;
        HintText.Text = $"共 {list.Count} 个可见窗口。选中《终末地》窗口后点「选择」。";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadWindows();

    private void WindowList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Confirm();

    private void Confirm()
    {
        if (WindowList.SelectedItem is not WindowInfo info)
        {
            HintText.Text = "请先在列表里选中一个窗口。";
            return;
        }

        SelectedWindow = info;
        DialogResult = true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

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
