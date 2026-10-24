using System.IO;
using System.Text;
using System.Text.Json;

namespace AxisTeller;

/// <summary>命令行自测：dotnet run -- --selftest report.txt</summary>
public static class Selftest
{
    private static readonly StringBuilder Log = new();
    private static int _passed;
    private static int _failed;

    public static bool Run(string outputPath)
    {
        Log.Clear();
        _passed = 0;
        _failed = 0;

        TestEngineForward();
        TestEngineInfiniteLoop();
        TestEnginePreviousAndJump();
        TestEngineNextCycle();
        TestEnginePeek();
        TestEngineEmptyCycles();
        TestStateRoundtrip();
        TestStoreSaveLoad();
        TestCatalog();
        TestHotkeyParse();
        TestTokenNormalize();
        TestSeed();

        var summary = $"\n{_passed} passed, {_failed} failed";
        Log.AppendLine(summary);
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, Log.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 写报告失败不影响退出码
        }

        Console.WriteLine(Log.ToString());
        return _failed == 0;
    }

    private static void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            _passed++;
            Log.AppendLine($"PASS  {name}");
        }
        else
        {
            _failed++;
            Log.AppendLine($"FAIL  {name}  {detail}");
        }
    }

    private static Axis BuildAxis(int loops = 1, params int[] stepCounts)
    {
        var axis = new Axis { Name = "test", Loops = loops };
        for (var i = 0; i < stepCounts.Length; i++)
        {
            var cycle = new Cycle { Name = $"C{i + 1}" };
            for (var s = 0; s < stepCounts[i]; s++)
                cycle.Steps.Add(new Step { OperatorName = $"op{i + 1}_{s + 1}", Action = "战技" });
            axis.Cycles.Add(cycle);
        }

        return axis;
    }

    private static void TestEngineForward()
    {
        var axis = BuildAxis(1, 3, 2);
        var engine = new PlaybackEngine();
        engine.Attach(axis, false);
        Check("engine 起点", engine.CycleIndex == 0 && engine.StepIndex == 0 && engine.Round == 1,
            $"actual ({engine.CycleIndex},{engine.StepIndex},{engine.Round})");

        var visited = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var step = engine.CurrentStep;
            visited.Add(step?.OperatorName ?? "-");
            if (!engine.Finished) engine.Next(false);
        }

        Check("engine 顺序推进",
            string.Join(",", visited) == "op1_1,op1_2,op1_3,op2_1,op2_2,op2_2,op2_2",
            string.Join(",", visited));
        Check("engine 全部走完会结束", engine.Finished);

        engine.Next(false);
        Check("engine 结束后再按键重新开始", !engine.Finished && engine.CycleIndex == 0 && engine.StepIndex == 0);
    }

    private static void TestEngineInfiniteLoop()
    {
        var axis = BuildAxis(1, 2, 2);
        var engine = new PlaybackEngine();
        engine.Attach(axis, true);
        for (var i = 0; i < 4; i++) engine.Next(true);
        Check("无限循环会进入第二轮", engine.Round == 2 && engine.CycleIndex == 0 && engine.StepIndex == 0,
            $"actual round={engine.Round} cycle={engine.CycleIndex} step={engine.StepIndex}");
        Check("无限循环不结束", !engine.Finished);
    }

    private static void TestEnginePreviousAndJump()
    {
        var axis = BuildAxis(1, 2, 3);
        var engine = new PlaybackEngine();
        engine.Attach(axis, false);
        engine.Next(false);
        engine.Next(false);
        engine.Previous();
        Check("回退到上一个动作", engine.CycleIndex == 0 && engine.StepIndex == 1,
            $"actual ({engine.CycleIndex},{engine.StepIndex})");

        engine.JumpTo(1, 2);
        Check("跳转到指定动作", engine.CycleIndex == 1 && engine.StepIndex == 2,
            $"actual ({engine.CycleIndex},{engine.StepIndex})");
        engine.Previous();
        Check("跨循环回退", engine.CycleIndex == 1 && engine.StepIndex == 1,
            $"actual ({engine.CycleIndex},{engine.StepIndex})");
    }

    private static void TestEngineNextCycle()
    {
        var axis = BuildAxis(2, 3, 3);
        var engine = new PlaybackEngine();
        engine.Attach(axis, false);
        engine.Next(false);
        engine.NextCycle(false);
        Check("跳到下一循环", engine.CycleIndex == 1 && engine.StepIndex == 0,
            $"actual ({engine.CycleIndex},{engine.StepIndex})");

        engine.NextCycle(false);
        Check("跳过最后一个循环后进入下一轮", engine.Round == 2 && engine.CycleIndex == 0,
            $"actual round={engine.Round} cycle={engine.CycleIndex}");
    }

    private static void TestEnginePeek()
    {
        var axis = BuildAxis(1, 2, 2);
        var engine = new PlaybackEngine();
        engine.Attach(axis, true);
        var peek = engine.PeekForward(3, true);
        Check("预览后续动作", peek.Count == 3 && peek[0].OperatorName == "op1_2" && peek[1].OperatorName == "op2_1",
            string.Join(",", peek.Select(p => p.OperatorName)));
        var nextPos = engine.PeekNextPosition(true);
        Check("预览下一个动作位置", nextPos is { cycle: 0 } or null);
    }

    private static void TestEngineEmptyCycles()
    {
        var axis = BuildAxis(1, 0, 2, 0);
        var engine = new PlaybackEngine();
        engine.Attach(axis, false);
        Check("自动跳过空循环", engine.CycleIndex == 1 && engine.CurrentStep is not null,
            $"actual cycle={engine.CycleIndex} step={engine.CurrentStep?.OperatorName}");
        engine.Next(false);
        engine.Next(false);
        Check("空循环不会卡住", engine.Finished);

        var empty = new Axis { Name = "empty" };
        empty.Cycles.Add(new Cycle { Name = "nothing" });
        var engine2 = new PlaybackEngine();
        engine2.Attach(empty, false);
        engine2.Next(false);
        Check("完全没有动作时不崩溃", engine2.CurrentStep is null);
    }

    private static void TestStateRoundtrip()
    {
        var state = new AppState();
        var axis = BuildAxis(3, 2, 1);
        axis.Name = "陈千语 · 中文名字";
        axis.Cycles[0].Steps[0].Note = "等 CD，别提前放";
        state.Axes.Add(axis);

        var json = JsonSerializer.Serialize(state, Json.Options);
        var back = JsonSerializer.Deserialize<AppState>(json, Json.Options);

        Check("存档 JSON 往返：轴数量", back?.Axes.Count == 1);
        Check("存档 JSON 往返：中文不转义", json.Contains("陈千语") && json.Contains("等 CD"));
        Check("存档 JSON 往返：循环与动作",
            back!.Axes[0].Cycles.Count == 2 && back.Axes[0].Cycles[0].Steps.Count == 2 &&
            back.Axes[0].Cycles[0].Steps[0].Note == "等 CD，别提前放");
        Check("存档 JSON 往返：轮数", back.Axes[0].Loops == 3);
    }

    private static void TestStoreSaveLoad()
    {
        var temp = Path.Combine(Path.GetTempPath(), "axis-teller-selftest-" + Guid.NewGuid().ToString("N"));
        var oldRoot = Store.Root;
        try
        {
            Store.RedirectTo(temp);
            var state = new AppState();
            state.Axes.Add(BuildAxis(2, 2, 2));
            state.Settings.SklandToken = "abc";
            Store.Save(state);
            Store.SaveRoster(new[]
            {
                new Operator { Id = "chr_x", Name = "测试干员", Rarity = 6, IconFile = "chr_x.png" },
            });
            var loaded = Store.Load();
            Check("存档落盘并读回", loaded.Axes.Count == 1 && loaded.Axes[0].Cycles.Count == 2);
            Check("设置落盘并读回", loaded.Settings.SklandToken == "abc");
            Check("角色缓存文件生成", File.Exists(Store.RosterPath));

            File.WriteAllText(Store.StatePath, "{ 坏掉的 json ");
            var fallback = Store.Load();
            Check("坏存档自动回退到示例轴", fallback.Axes.Count >= 2);
        }
        finally
        {
            Store.RedirectTo(oldRoot);
            try
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
            }
            catch
            {
                // 忽略清理失败
            }
        }
    }

    private static void TestCatalog()
    {
        var lib = new OperatorLibrary();
        lib.LoadBundled(Store.AssetsDir);
        Check("内置角色库可读取", lib.Operators.Count >= 30, $"count={lib.Operators.Count}");
        Check("角色库带中文名", lib.Operators.Any(o => o.Name == "陈千语"));
        var withIcon = lib.Operators.Count(o => o.Avatar is not null);
        Check("角色图标可加载", withIcon >= lib.Operators.Count * 0.9, $"{withIcon}/{lib.Operators.Count}");
        Check("角色库包含职业信息", lib.Operators.Count(o => !string.IsNullOrWhiteSpace(o.Profession)) > 20);
        var op = lib.Find("佩丽卡");
        Check("按名字查找干员", op is not null && op.Rarity == 5, op?.Id ?? "null");
    }

    private static void TestHotkeyParse()
    {
        var parsed = HotkeyManager.Parse("F8");
        Check("解析热键 F8", parsed is not null && parsed.Value.vk == 0x77, parsed?.vk.ToString() ?? "null");
        var combo = HotkeyManager.Parse("Ctrl+Shift+F7");
        Check("解析组合键", combo is not null &&
                          (combo.Value.mods & (HotkeyManager.ModControl | HotkeyManager.ModShift)) ==
                          (HotkeyManager.ModControl | HotkeyManager.ModShift));
        Check("非法按键返回空", HotkeyManager.Parse("这不是键") is null);
    }

    private static void TestTokenNormalize()
    {
        var raw = "{\"code\":0,\"data\":{\"content\":\"  real-token  \"}}";
        Check("解析森空岛 JSON token", SklandClient.NormalizeToken(raw) == "real-token");
        Check("纯 token 原样保留", SklandClient.NormalizeToken(" abc ") == "abc");
    }

    private static void TestSeed()
    {
        var seed = Store.CreateSeed();
        Check("示例轴数量", seed.Axes.Count == 2, $"count={seed.Axes.Count}");
        Check("示例轴多循环", seed.Axes[0].Cycles.Count == 3 && seed.Axes[0].Cycles[0].Steps.Count > 0);
        Check("示例轴绑定到真实干员", seed.Axes[0].Cycles[0].Steps[0].OperatorId.StartsWith("chr_"));
    }
}
