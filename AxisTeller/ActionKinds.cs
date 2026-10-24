namespace AxisTeller;

/// <summary>动作类型：用于「自动检测当前步骤是否完成」时匹配键鼠操作。</summary>
public enum ActionKind
{
    Unknown = 0,
    NormalAttack,
    HeavyAttack,
    Skill,
    ComboSkill,
    Ultimate,
    Dodge,
    Switch,
}

public static class ActionKinds
{
    public static readonly ActionKind[] Selectable =
    {
        ActionKind.NormalAttack,
        ActionKind.HeavyAttack,
        ActionKind.Skill,
        ActionKind.ComboSkill,
        ActionKind.Ultimate,
        ActionKind.Dodge,
        ActionKind.Switch,
    };

    public static string Label(ActionKind kind) => kind switch
    {
        ActionKind.NormalAttack => "普攻",
        ActionKind.HeavyAttack => "重攻",
        ActionKind.Skill => "战技",
        ActionKind.ComboSkill => "连携",
        ActionKind.Ultimate => "终结",
        ActionKind.Dodge => "闪避",
        ActionKind.Switch => "切换",
        _ => "自动",
    };

    public static string FullLabel(ActionKind kind) => kind switch
    {
        ActionKind.NormalAttack => "普攻 / A1",
        ActionKind.HeavyAttack => "重攻 / 重击",
        ActionKind.Skill => "战技",
        ActionKind.ComboSkill => "连携技",
        ActionKind.Ultimate => "终结技",
        ActionKind.Dodge => "闪避",
        ActionKind.Switch => "切换干员",
        _ => "自动识别",
    };

    public static ActionKind Parse(string? name) =>
        Enum.TryParse<ActionKind>(name, ignoreCase: true, out var kind) ? kind : ActionKind.Unknown;

    /// <summary>优先使用显式类型，否则从动作文字里猜。</summary>
    public static ActionKind Resolve(string? actionText, string? explicitKind)
    {
        var explicitValue = Parse(explicitKind);
        if (explicitValue != ActionKind.Unknown) return explicitValue;
        return Infer(actionText);
    }

    public static ActionKind Infer(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return ActionKind.Unknown;
        var text = action.Trim().ToLowerInvariant();

        // 顺序很重要：先匹配更具体的词
        if (text.Contains("连携") || text.Contains("combo")) return ActionKind.ComboSkill;
        if (text.Contains("终结") || text.Contains("大招") || text.Contains("ultimate") || text.Contains("ult"))
            return ActionKind.Ultimate;
        if (text.Contains("重攻") || text.Contains("重击") || text.Contains("重砍") || text.Contains("heavy") ||
            text.Contains("charged"))
            return ActionKind.HeavyAttack;
        if (text.Contains("战技") || text.Contains("技能") || text.Contains("skill")) return ActionKind.Skill;
        if (text.Contains("闪避") || text.Contains("闪身") || text.Contains("dodge") || text.Contains("闪现"))
            return ActionKind.Dodge;
        if (text.Contains("切换") || text.Contains("换人") || text.Contains("切人") || text.Contains("switch"))
            return ActionKind.Switch;
        if (text.Contains("普攻") || text.Contains("平a") || text.Contains("普通攻击") || text.Contains("normal") ||
            text.Contains("a1") || text.Contains("a2") || text.Contains("a3") || text == "a")
            return ActionKind.NormalAttack;
        return ActionKind.Unknown;
    }

    /// <summary>把绑定文字规范成统一格式：MouseLeft / MouseLeftHold / 单个按键名（如 E、Space、1）。</summary>
    public static string NormalizeBinding(string? binding)
    {
        var text = (binding ?? "").Trim();
        if (text.Length == 0) return "";
        if (text.Equals("MouseLeft", StringComparison.OrdinalIgnoreCase)) return "MouseLeft";
        if (text.Equals("MouseLeftHold", StringComparison.OrdinalIgnoreCase)) return "MouseLeftHold";
        if (text.StartsWith("Key:", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        return text;
    }

    public static string BindingLabel(string? binding)
    {
        var text = NormalizeBinding(binding);
        return text switch
        {
            "" => "未绑定",
            "MouseLeft" => "鼠标左键",
            "MouseLeftHold" => "长按左键",
            "Space" => "空格",
            _ => text.StartsWith('D') && text.Length == 2 && char.IsDigit(text[1]) ? text[1..] : text,
        };
    }

    /// <summary>把设置里的绑定展开成「实际输入 → 动作类型」的查找表。</summary>
    public static Dictionary<string, ActionKind> BuildBindingMap(AppSettings settings)
    {
        var map = new Dictionary<string, ActionKind>(StringComparer.OrdinalIgnoreCase);
        void Add(string? binding, ActionKind kind)
        {
            var text = NormalizeBinding(binding);
            if (text.Length == 0 || map.ContainsKey(text)) return;
            map[text] = kind;
        }

        Add(settings.AutoBindNormal, ActionKind.NormalAttack);
        Add(settings.AutoBindHeavy, ActionKind.HeavyAttack);
        Add(settings.AutoBindSkill, ActionKind.Skill);
        Add(settings.AutoBindCombo, ActionKind.ComboSkill);
        Add(settings.AutoBindUltimate, ActionKind.Ultimate);
        Add(settings.AutoBindDodge, ActionKind.Dodge);
        Add(settings.AutoBindSwitch, ActionKind.Switch);
        return map;
    }

    /// <summary>实际检测到的输入是否匹配某个动作类型（切换支持 1-4）。</summary>
    public static ActionKind? MatchInput(string actualBinding, AppSettings settings)
    {
        var map = BuildBindingMap(settings);
        var actual = NormalizeBinding(actualBinding);
        if (map.TryGetValue(actual, out var kind)) return kind;

        // 切换干员：绑定了 1 时，按 2/3/4 也算切换
        if (actual.Length == 1 && char.IsDigit(actual[0]))
        {
            var switchBinding = NormalizeBinding(settings.AutoBindSwitch);
            if (switchBinding.Length == 1 && char.IsDigit(switchBinding[0]) && switchBinding != "0")
                return ActionKind.Switch;
        }

        return null;
    }

    /// <summary>检测到的动作是否算完成当前步骤。</summary>
    public static bool IsCurrentStepCompleted(Step step, ActionKind detected, string? mode)
    {
        if (string.Equals(mode, "any", StringComparison.OrdinalIgnoreCase)) return true;
        var kind = step.ResolvedKind;
        if (kind == ActionKind.Unknown) return true; // 自定义文字：匹配任意动作，避免卡住
        return kind == detected;
    }
}
