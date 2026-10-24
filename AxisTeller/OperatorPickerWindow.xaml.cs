using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AxisTeller;

public partial class OperatorPickerWindow : Window
{
    private readonly OperatorLibrary _library;

    public Operator? SelectedOperator { get; private set; }
    public string CustomName { get; private set; } = "";

    public OperatorPickerWindow(OperatorLibrary library, string? currentName = null)
    {
        InitializeComponent();
        _library = library;
        CustomNameBox.Text = currentName ?? "";
        Loaded += (_, _) =>
        {
            Rebuild("");
            SearchBox.Focus();
        };
    }

    private void Rebuild(string filter)
    {
        OperatorPanel.Children.Clear();
        var ops = _library.Operators.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter.Trim();
            ops = ops.Where(o =>
                o.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                o.Profession.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                o.Element.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                o.Id.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        var list = ops.OrderByDescending(o => o.Rarity).ThenBy(o => o.Profession).ThenBy(o => o.Name).ToList();
        CountText.Text = $"{list.Count:00} / {_library.Operators.Count:00}";
        for (var i = 0; i < list.Count; i++) OperatorPanel.Children.Add(BuildCard(list[i], i));
    }

    private FrameworkElement BuildCard(Operator op, int index)
    {
        var white = (Brush)FindResource("BgPanel");
        var ink = (Brush)FindResource("Ink");
        var inkMuted = (Brush)FindResource("InkMuted");
        var hoverBg = new SolidColorBrush(Color.FromRgb(0xFF, 0xFD, 0xE0));

        var card = new Border
        {
            Width = 132,
            Margin = new Thickness(7),
            Background = white,
            BorderBrush = ink,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = string.IsNullOrWhiteSpace(op.Subtitle) ? op.Name : $"{op.Name} · {op.Subtitle}",
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(130) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3) });

        // 头像区
        var imageArea = new Grid { ClipToBounds = true };
        imageArea.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xE6)) });
        if (op.Avatar is not null)
            imageArea.Children.Add(new Image { Source = op.Avatar, Stretch = Stretch.UniformToFill });
        else
            imageArea.Children.Add(new TextBlock
            {
                Text = FirstChar(op.Name),
                FontSize = 34,
                FontWeight = FontWeights.SemiBold,
                Foreground = inkMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

        var indexChip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0A, 0x0A, 0x0A)),
            Padding = new Thickness(5, 2, 5, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = $"{index + 1:00}",
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            },
        };
        imageArea.Children.Add(indexChip);
        Grid.SetRow(imageArea, 0);
        grid.Children.Add(imageArea);

        // 信息条：名字 + 职业方块
        var infoBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xEF)),
            BorderBrush = ink,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(7, 5, 0, 5),
        };
        var infoGrid = new Grid();
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = op.Name,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        textStack.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", new[] { op.Profession, op.Element, op.RarityText }.Where(s => !string.IsNullOrWhiteSpace(s))),
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 8.5,
            Foreground = inkMuted,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        infoGrid.Children.Add(textStack);

        var classBlock = new Border
        {
            Width = 26,
            Height = 26,
            Background = ink,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = FirstChar(op.Profession),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(classBlock, 1);
        infoGrid.Children.Add(classBlock);
        infoBar.Child = infoGrid;
        Grid.SetRow(infoBar, 1);
        grid.Children.Add(infoBar);

        // 属性色带
        var accent = new Border { Background = op.AccentBrush };
        Grid.SetRow(accent, 2);
        grid.Children.Add(accent);

        card.Child = grid;
        card.MouseLeftButtonUp += (_, _) =>
        {
            SelectedOperator = op;
            DialogResult = true;
        };
        card.MouseEnter += (_, _) =>
        {
            card.BorderBrush = (Brush)FindResource("AccentDeep");
            card.Background = hoverBg;
        };
        card.MouseLeave += (_, _) =>
        {
            card.BorderBrush = ink;
            card.Background = white;
        };
        return card;
    }

    private static string FirstChar(string? text) => string.IsNullOrWhiteSpace(text) ? "?" : text.Trim()[0].ToString();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Rebuild(SearchBox.Text);

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

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void UseCustom_Click(object sender, RoutedEventArgs e)
    {
        var name = CustomNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "先输入一个名字，或者直接点选上面的干员。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CustomName = name;
        SelectedOperator = null;
        DialogResult = true;
    }
}
