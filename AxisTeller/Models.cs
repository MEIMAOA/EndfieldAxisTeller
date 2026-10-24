using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AxisTeller;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一次按键提示中的一个动作。</summary>
public sealed class Step : ObservableObject
{
    private string _operatorId = "";
    private string _operatorName = "";
    private string _action = "战技";
    private string _note = "";
    private ImageSource? _avatar;

    /// <summary>干员 id（如 chr_0025_ardelia），可为空表示自由文本。</summary>
    public string OperatorId { get => _operatorId; set { if (Set(ref _operatorId, value)) OnChanged(); } }

    /// <summary>干员名（冗余保存，角色库变化时仍能显示）。</summary>
    public string OperatorName { get => _operatorName; set { if (Set(ref _operatorName, value)) OnChanged(); } }

    /// <summary>动作，如 战技 / 连携技 / 终结技 / 切换。</summary>
    public string Action { get => _action; set { if (Set(ref _action, value)) OnChanged(); } }

    /// <summary>备注，例如 等CD、留终结技、面向调整。</summary>
    public string Note { get => _note; set { if (Set(ref _note, value)) OnChanged(); } }

    [JsonIgnore]
    public ImageSource? Avatar { get => _avatar; set => Set(ref _avatar, value); }

    private Brush _accent = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1));

    /// <summary>干员属性色（用于卡片/步骤条上的属性色带）。</summary>
    [JsonIgnore]
    public Brush Accent { get => _accent; set => Set(ref _accent, value); }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(OperatorName) ? "未选择干员" : OperatorName;

    [JsonIgnore]
    public string ActionText => string.IsNullOrWhiteSpace(Action) ? "—" : Action;

    [JsonIgnore]
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    private void OnChanged()
    {
        Raise(nameof(DisplayName));
        Raise(nameof(ActionText));
        Raise(nameof(HasNote));
    }
}

/// <summary>一个循环（一段可重复的动作序列）。</summary>
public sealed class Cycle : ObservableObject
{
    private string _name = "循环";
    private string _repeatHint = "";

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>自由备注：例如 起手 / 常规 / 收尾、需要卡的时间点。</summary>
    public string RepeatHint { get => _repeatHint; set { if (Set(ref _repeatHint, value)) Raise(nameof(HintVisible)); } }

    [JsonIgnore]
    public bool HintVisible => !string.IsNullOrWhiteSpace(RepeatHint);

    public ObservableCollection<Step> Steps { get; set; } = new();
}

/// <summary>一条轴：多个循环组成的完整输出流程。</summary>
public sealed class Axis : ObservableObject
{
    private string _name = "新轴";
    private string _team = "";
    private int _loops = 2;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>阵容备注，例如 陈千语 / 佩丽卡 / 骏卫 / 余烬。</summary>
    public string Team { get => _team; set => Set(ref _team, value); }

    /// <summary>整套循环重复的轮数（>=1）。</summary>
    public int Loops
    {
        get => _loops;
        set
        {
            var clamped = Math.Clamp(value, 1, 99);
            if (Set(ref _loops, clamped)) Raise(nameof(Summary));
        }
    }

    public ObservableCollection<Cycle> Cycles
    {
        get => _cycles;
        set
        {
            if (ReferenceEquals(_cycles, value)) return;
            _cycles.CollectionChanged -= OnCyclesChanged;
            _cycles = value ?? new ObservableCollection<Cycle>();
            _cycles.CollectionChanged += OnCyclesChanged;
            foreach (var cycle in _cycles) HookCycle(cycle);
            Raise(nameof(Summary));
        }
    }

    private ObservableCollection<Cycle> _cycles = new();

    public Axis()
    {
        _cycles.CollectionChanged += OnCyclesChanged;
    }

    private void OnCyclesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (Cycle cycle in e.NewItems) HookCycle(cycle);
        Raise(nameof(Summary));
    }

    private void HookCycle(Cycle cycle)
    {
        cycle.Steps.CollectionChanged -= OnStepsChanged;
        cycle.Steps.CollectionChanged += OnStepsChanged;
        cycle.PropertyChanged -= CycleOnPropertyChanged;
        cycle.PropertyChanged += CycleOnPropertyChanged;
    }

    private void CycleOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Cycle.Name) or nameof(Cycle.RepeatHint)) Raise(nameof(Summary));
    }

    private void OnStepsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Raise(nameof(Summary));


    [JsonIgnore]
    public string Summary
    {
        get
        {
            var steps = Cycles.Sum(c => c.Steps.Count);
            return $"{Cycles.Count} 循环 · {steps} 动作 · {Loops} 轮";
        }
    }

    public double TotalStepCount => Cycles.Sum(c => c.Steps.Count);

    public override string ToString() => Name;
}

public sealed class AppSettings : ObservableObject
{
    private string _nextKey = "F8";
    private string _prevKey = "F7";
    private string _nextCycleKey = "F9";
    private string _resetKey = "F10";
    private string _toggleOverlayKey = "F6";
    private bool _loopInfinite = true;
    private bool _topmost = true;
    private bool _showOverlayOnStart;
    private string _lastAxisId = "";
    private string _sklandServer = "cn";
    private string _sklandToken = "";
    private string _sklandUserName = "";

    public string NextKey { get => _nextKey; set => Set(ref _nextKey, value); }
    public string PrevKey { get => _prevKey; set => Set(ref _prevKey, value); }
    public string NextCycleKey { get => _nextCycleKey; set => Set(ref _nextCycleKey, value); }
    public string ResetKey { get => _resetKey; set => Set(ref _resetKey, value); }
    public string ToggleOverlayKey { get => _toggleOverlayKey; set => Set(ref _toggleOverlayKey, value); }

    /// <summary>走完所有轮数后是否回到第一轮继续。</summary>
    public bool LoopInfinite { get => _loopInfinite; set => Set(ref _loopInfinite, value); }

    public bool Topmost { get => _topmost; set => Set(ref _topmost, value); }
    public bool ShowOverlayOnStart { get => _showOverlayOnStart; set => Set(ref _showOverlayOnStart, value); }
    public string LastAxisId { get => _lastAxisId; set => Set(ref _lastAxisId, value); }

    /// <summary>cn = 国服（森空岛 / zonai.skland.com），global = 国际服（Skport / zonai.skport.com）。</summary>
    public string SklandServer { get => _sklandServer; set => Set(ref _sklandServer, value); }

    /// <summary>森空岛 App 的 token（仅保存在本机）。</summary>
    public string SklandToken { get => _sklandToken; set => Set(ref _sklandToken, value); }

    public string SklandUserName { get => _sklandUserName; set => Set(ref _sklandUserName, value); }

    public double OverlayLeft { get; set; } = double.NaN;
    public double OverlayTop { get; set; } = double.NaN;
    public double OverlayWidth { get; set; } = 520;
    public double OverlayHeight { get; set; } = 0; // 0 = 自适应
    public double EditorLeft { get; set; } = double.NaN;
    public double EditorTop { get; set; } = double.NaN;
    public double EditorWidth { get; set; } = 1180;
    public double EditorHeight { get; set; } = 780;

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json.Options), Json.Options)
        ?? new AppSettings();

    public void CopyFrom(AppSettings other)
    {
        NextKey = other.NextKey;
        PrevKey = other.PrevKey;
        NextCycleKey = other.NextCycleKey;
        ResetKey = other.ResetKey;
        ToggleOverlayKey = other.ToggleOverlayKey;
        LoopInfinite = other.LoopInfinite;
        Topmost = other.Topmost;
        ShowOverlayOnStart = other.ShowOverlayOnStart;
        SklandServer = other.SklandServer;
        SklandToken = other.SklandToken;
        SklandUserName = other.SklandUserName;
        OverlayLeft = other.OverlayLeft;
        OverlayTop = other.OverlayTop;
        OverlayWidth = other.OverlayWidth;
        OverlayHeight = other.OverlayHeight;
        EditorLeft = other.EditorLeft;
        EditorTop = other.EditorTop;
        EditorWidth = other.EditorWidth;
        EditorHeight = other.EditorHeight;
        LastAxisId = other.LastAxisId;
    }
}

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public ObservableCollection<Axis> Axes { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

/// <summary>角色库中的一名干员。</summary>
public sealed class Operator
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Rarity { get; set; }
    public string Profession { get; set; } = "";
    public string Element { get; set; } = "";
    public string ElementColor { get; set; } = "#8b93a1";

    /// <summary>图标文件名（Assets/Icons 或森空岛缓存目录下）。</summary>
    [JsonPropertyName("icon")]
    public string? IconFile { get; set; }

    [JsonIgnore]
    public ImageSource? Avatar { get; set; }

    [JsonIgnore]
    public string RarityText => Rarity > 0 ? new string('★', Rarity) : "";

    [JsonIgnore]
    public string Subtitle => string.Join(" · ", new[] { Profession, Element }.Where(s => !string.IsNullOrWhiteSpace(s)));

    [JsonIgnore]
    public Brush AccentBrush
    {
        get
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(ElementColor);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch
            {
                return Brushes.Gray;
            }
        }
    }
}

public sealed class CatalogFile
{
    public int Version { get; set; }
    public string Source { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public List<Operator> Operators { get; set; } = new();
}

/// <summary>干员库：内置快照 + 森空岛同步来的角色，负责图标加载与查询。</summary>
public sealed class OperatorLibrary
{
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Operator> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Operator> _byName = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<Operator> Operators { get; } = new();
    public ObservableCollection<Operator> Owned { get; } = new();
    public string Source { get; private set; } = "内置数据";
    public string UpdatedAt { get; private set; } = "";
    public string IconDirectory { get; private set; } = "";

    public void LoadBundled(string assetsDirectory)
    {
        var file = Path.Combine(assetsDirectory, "characters.json");
        if (!File.Exists(file))
        {
            Source = "内置数据缺失";
            return;
        }

        var catalog = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(file), Json.Options);
        if (catalog is null) return;
        Source = string.IsNullOrWhiteSpace(catalog.Source) ? "内置数据" : catalog.Source;
        UpdatedAt = catalog.UpdatedAt;
        IconDirectory = Path.Combine(assetsDirectory, "Icons");
        foreach (var op in catalog.Operators)
        {
            Add(op);
            op.Avatar = LoadIcon(IconDirectory, string.IsNullOrWhiteSpace(op.IconFile) ? op.Id + ".png" : op.IconFile);
        }
    }

    private ImageSource? LoadIcon(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var png = Path.Combine(directory, fileName);
        return LoadBitmap(png) ?? LoadBitmap(Path.ChangeExtension(png, ".webp"));
    }

    public ImageSource? LoadBitmap(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public void Add(Operator op)
    {
        if (string.IsNullOrWhiteSpace(op.Id)) op.Id = "op_" + Guid.NewGuid().ToString("N");
        _byId[op.Id] = op;
        if (!string.IsNullOrWhiteSpace(op.Name)) _byName[op.Name] = op;
        var existing = Operators.FirstOrDefault(o => string.Equals(o.Id, op.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is null) Operators.Add(op);
    }

    public Operator? GetById(string id) =>
        !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id, out var op) ? op : null;

    public Operator? Find(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        return GetById(idOrName) ?? (_byName.TryGetValue(idOrName, out var op) ? op : null);
    }

    /// <summary>给动作补上干员头像（角色库变化后调用）。</summary>
    public void Resolve(Step step)
    {
        var op = Find(step.OperatorId) ?? Find(step.OperatorName);
        if (op is null)
        {
            step.Avatar = null;
            step.Accent = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1));
            return;
        }
        if (string.IsNullOrWhiteSpace(step.OperatorId)) step.OperatorId = op.Id;
        step.Avatar = op.Avatar;
        step.Accent = op.AccentBrush;
    }

    public void ResolveAll(IEnumerable<Axis> axes)
    {
        foreach (var axis in axes)
        foreach (var cycle in axis.Cycles)
        foreach (var step in cycle.Steps)
            Resolve(step);
    }

    public ImageSource? IconFor(Operator op)
    {
        if (op.Avatar is not null) return op.Avatar;
        if (!string.IsNullOrWhiteSpace(op.Id) && _iconCache.TryGetValue(op.Id, out var cached)) return cached;
        var dir = IconDirectory;
        if (!string.IsNullOrWhiteSpace(dir))
            op.Avatar = LoadIcon(dir, string.IsNullOrWhiteSpace(op.IconFile) ? op.Id + ".png" : op.IconFile);
        if (op.Avatar is not null) _iconCache[op.Id] = op.Avatar;
        return op.Avatar;
    }

    /// <summary>加载森空岛同步缓存（含本地图标）。</summary>
    public bool LoadSklandRoster(string rosterPath, string iconDirectory)
    {
        try
        {
            if (!File.Exists(rosterPath)) return false;
            var ops = JsonSerializer.Deserialize<List<Operator>>(File.ReadAllText(rosterPath), Json.Options);
            if (ops is null || ops.Count == 0) return false;
            Owned.Clear();
            foreach (var op in ops)
            {
                op.Avatar = LoadIcon(iconDirectory, string.IsNullOrWhiteSpace(op.IconFile) ? op.Id + ".png" : op.IconFile);
                var known = Find(op.Id) ?? Find(op.Name);
                if (known is not null)
                {
                    if (op.Avatar is not null) known.Avatar = op.Avatar;
                    if (!string.IsNullOrWhiteSpace(op.Name)) known.Name = op.Name;
                    if (op.Rarity > 0) known.Rarity = op.Rarity;
                    if (!string.IsNullOrWhiteSpace(op.Profession)) known.Profession = op.Profession;
                    if (!string.IsNullOrWhiteSpace(op.Element)) known.Element = op.Element;
                    if (!string.IsNullOrWhiteSpace(op.ElementColor)) known.ElementColor = op.ElementColor;
                    Owned.Add(known);
                }
                else
                {
                    Add(op);
                    Owned.Add(op);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>合并森空岛同步结果（拥有角色 + 更新图标）。</summary>
    public void MergeSkland(IReadOnlyList<Operator> owned, string sourceLabel)
    {
        Owned.Clear();
        foreach (var op in owned)
        {
            var known = Find(op.Id) ?? Find(op.Name);
            if (known is not null)
            {
                if (!string.IsNullOrWhiteSpace(op.Name)) known.Name = op.Name;
                if (op.Rarity > 0) known.Rarity = op.Rarity;
                if (!string.IsNullOrWhiteSpace(op.Profession)) known.Profession = op.Profession;
                if (!string.IsNullOrWhiteSpace(op.Element)) known.Element = op.Element;
                if (op.Avatar is not null) known.Avatar = op.Avatar;
                Owned.Add(known);
            }
            else
            {
                Add(op);
                Owned.Add(op);
            }
        }
        Source = sourceLabel;
        UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
    }

    public void MarkOwnedByName(IEnumerable<string> names)
    {
        var set = new HashSet<string>(names.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return;
        Owned.Clear();
        foreach (var op in Operators.Where(o => set.Contains(o.Name))) Owned.Add(op);
    }
}
