using System.IO;
using System.Text.Json;

namespace AxisTeller;

/// <summary>本地存档：轴数据、设置、森空岛角色缓存。</summary>
public static class Store
{
    public static string Root { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EndfieldAxisTeller");

    /// <summary>自测时把存档目录重定向到临时目录。</summary>
    public static void RedirectTo(string directory) => Root = directory;

    public static string StatePath => Path.Combine(Root, "state.json");
    public static string RosterPath => Path.Combine(Root, "skland-roster.json");
    public static string SklandIconDir => Path.Combine(Root, "icons");
    public static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "Assets");

    public static AppState Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), Json.Options);
                if (state is not null) MigrateHotkeys(state.Settings);
                if (state is { Axes.Count: > 0 }) return state;
                if (state is not null)
                {
                    state.Settings ??= new AppSettings();
                    if (state.Axes.Count == 0) return state;
                }
            }
        }
        catch (Exception ex)
        {
            BackupBroken(ex);
        }

        return CreateSeed();
    }

    /// <summary>热键已迁移标记（为 true 时启动后提示一次）。</summary>
    public static bool HotkeysMigrated { get; private set; }

    /// <summary>旧版默认热键是 F6~F10，会在终末地里顶掉游戏自己的 F 键功能，迁移到 F11/F12 组合。</summary>
    private static void MigrateHotkeys(AppSettings? settings)
    {
        if (settings is null) return;
        var changed = false;
        void Fix(Action<string> set, string current, string oldValue, string newValue)
        {
            if (!string.Equals(current, oldValue, StringComparison.OrdinalIgnoreCase)) return;
            set(newValue);
            changed = true;
        }

        Fix(v => settings.NextKey = v, settings.NextKey, "F8", "F11");
        Fix(v => settings.PrevKey = v, settings.PrevKey, "F7", "Shift+F11");
        Fix(v => settings.NextCycleKey = v, settings.NextCycleKey, "F9", "Ctrl+F11");
        Fix(v => settings.ResetKey = v, settings.ResetKey, "F10", "Ctrl+Shift+F11");
        Fix(v => settings.ToggleOverlayKey = v, settings.ToggleOverlayKey, "F6", "Alt+F11");
        Fix(v => settings.SaveTemplateKey = v, settings.SaveTemplateKey, "F5", "Ctrl+F12");
        // v1.3.2 之前的采样默认是 F1~F4（会和终末地按键打架）
        Fix(v => settings.SamplePosKey = v, settings.SamplePosKey, "F1", "F11");
        Fix(v => settings.SampleNegKey = v, settings.SampleNegKey, "F2", "Alt+F11");
        Fix(v => settings.TrainAiKey = v, settings.TrainAiKey, "F3", "Ctrl+F11");
        Fix(v => settings.StopSampleKey = v, settings.StopSampleKey, "F4", "Alt+F12");
        if (changed) HotkeysMigrated = true;
    }

    private static void BackupBroken(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Root);
            if (File.Exists(StatePath))
            {
                var target = Path.Combine(Root, $"state.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(StatePath, target, overwrite: true);
            }

            File.WriteAllText(Path.Combine(Root, "last-error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 读取存档失败：{ex}\n");
        }
        catch
        {
            // 忽略备份失败
        }
    }

    public static void Save(AppState state)
    {
        Directory.CreateDirectory(Root);
        var json = JsonSerializer.Serialize(state, Json.Options);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, StatePath, overwrite: true);
    }

    public static void SaveRoster(IEnumerable<Operator> operators)
    {
        Directory.CreateDirectory(Root);
        var list = operators.ToList();
        File.WriteAllText(RosterPath, JsonSerializer.Serialize(list, Json.Options));
    }

    /// <summary>第一次启动时的示例轴（可以用编辑界面随意改）。</summary>
    public static AppState CreateSeed()
    {
        var state = new AppState();
        var lib = new OperatorLibrary();
        try
        {
            lib.LoadBundled(AssetsDir);
        }
        catch
        {
            // 角色库缺失也不影响示例轴的文字内容
        }

        Step S(string name, string action, string note = "")
        {
            var op = lib.Find(name);
            return new Step
            {
                OperatorId = op?.Id ?? "",
                OperatorName = name,
                Action = action,
                Note = note,
                Avatar = op?.Avatar,
            };
        }

        var a1 = new Axis
        {
            Name = "陈千语 · 标准爆发",
            Team = "陈千语 / 佩丽卡 / 骏卫 / 余烬",
            Loops = 3,
        };
        a1.Cycles.Add(new Cycle
        {
            Name = "起手",
            RepeatHint = "开场直接起，注意留一个战技给第二轮",
            Steps =
            {
                S("骏卫", "战技", "先手破韧"),
                S("余烬", "战技", "铺火"),
                S("佩丽卡", "连携技"),
                S("陈千语", "终结技", "打满爆发"),
            },
        });
        a1.Cycles.Add(new Cycle
        {
            Name = "常规循环",
            RepeatHint = "每轮重复即可",
            Steps =
            {
                S("陈千语", "战技"),
                S("佩丽卡", "战技"),
                S("骏卫", "连携技"),
                S("陈千语", "战技", "卡 CD 收尾"),
            },
        });
        a1.Cycles.Add(new Cycle
        {
            Name = "收尾",
            Steps =
            {
                S("余烬", "终结技"),
                S("陈千语", "连携技"),
            },
        });

        var a2 = new Axis
        {
            Name = "佩丽卡 · 法术循环",
            Team = "佩丽卡 / 祀 / 秋栗 / 陈千语",
            Loops = 2,
        };
        a2.Cycles.Add(new Cycle
        {
            Name = "循环 A",
            Steps =
            {
                S("祀", "战技", "先挂减抗"),
                S("佩丽卡", "战技"),
                S("秋栗", "连携技"),
                S("佩丽卡", "终结技"),
            },
        });
        a2.Cycles.Add(new Cycle
        {
            Name = "循环 B",
            Steps =
            {
                S("佩丽卡", "战技"),
                S("陈千语", "连携技"),
                S("祀", "终结技"),
            },
        });

        state.Axes.Add(a1);
        state.Axes.Add(a2);
        state.Settings.LastAxisId = a1.Id;
        return state;
    }
}
