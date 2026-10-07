namespace IdleSword.Core;

/// <summary>
/// **把技能配置翻成人话的唯一一份实现**。三处消费者共用：
/// 玩家技能页的悬停提示（`UI/Pages.cs`）、GM 技能预览的摘要行（`UI/SkillPreview.cs`）、以及自检（`tests/`）。
///
/// 为什么必须是同一份、而且必须穷举：本工程栽过一次——`bonus_vs_state` 没进文案表，
/// 落到 `_ =>` 兜底**原样印出内部标识符**，而两边都不报错。收成一个**穷举 + 抛错**的函数之后，
/// 漏一个取值会在"遍历所有技能"的自检里当场炸掉。
///
/// ⚠️ 这里是**纯函数、不碰 Godot**（所以放在 `Core/` 而不是 `UI/`），自检可以直接断言它的输出：
/// 例如"任何一个在役技能的文案里都不许出现配置里的原始枚举值"。
/// </summary>
public static class SkillText
{
    /// <summary>标签补到 4 个全角字宽**再加一个空格**，让键值两列对齐（提示框是纯文本，没有列排版）。
    /// 那个"至少一个空格"不能省：4 字标签（如「单发伤害」）补 0 个的话，值会直接贴上去。</summary>
    private static string Pad(string label) => label + new string('　', Math.Max(1, 5 - label.Length));

    /// <summary>
    /// 类别（`kind`）。**这是一个会印给玩家看的词**，所以也翻成中文——
    /// 自检里有一条"文案不许包含配置的原始枚举值"，它把整条线兜住了：将来不管是新增
    /// `secondary` / `trajectory` / `targeting` / `kind` 的哪个取值，只要没在这里补文案就会当场炸。
    /// </summary>
    public static string Kind(SkillDef s) => s.Kind switch
    {
        "projectile" => "弹道",
        "target" => "定点",
        "ground" => "地面持续",
        "buff" => "增益",
        "summon" => "召唤",
        _ => throw new InvalidDataException($"技能类别 '{s.Kind}' 没有文案——新增类别时要在这里补一条"),
    };

    /// <summary>次级效果 + 数值。**没有次级效果返回空串**（调用方据此决定要不要印这一行）。</summary>
    public static string Secondary(SkillDef s) => s.Secondary switch
    {
        "" => "",
        "pierce" => "穿透",
        "multi" => $"多重 ×{s.SecondaryValue:0}",
        "slow" => $"减速 {s.SecondaryValue:P0} · {s.SecondaryDuration:0.#}s",
        "chill" => $"寒冷（减速 {s.SecondaryValue:P0}）· {s.SecondaryDuration:0.#}s",
        "stun" => $"眩晕 {s.SecondaryDuration:0.#}s",
        "dot" => $"灼烧 {s.SecondaryValue:P0}/秒 · {s.SecondaryDuration:0.#}s",
        "vulnerable" => $"易伤 +{s.SecondaryValue:P0} · {s.SecondaryDuration:0.#}s",
        "lifesteal" => $"吸血 {s.SecondaryValue:P0} · {s.SecondaryDuration:0.#}s",
        "execute" => $"斩杀：目标气血低于 {s.SecondaryValue:P0} 时伤害翻倍",
        "shield" => $"护盾：吸收 攻击×{s.SecondaryValue:0.##} · {s.SecondaryDuration:0.#}s",
        "regen" => $"回血 {s.SecondaryValue:P0}/秒 · {s.SecondaryDuration:0.#}s",
        "haste" => $"出手加速 +{s.SecondaryValue:P0} · {s.SecondaryDuration:0.#}s（普攻与法术一起）",
        "crit_reduce" => $"暴击率 +{s.SecondaryValue:P0} · {s.SecondaryDuration:0.#}s；暴击后缩短一个随机法术的冷却 {s.SecondaryExtra:0.##}s",
        "bonus_vs_state" => $"利用状态：目标带着任意状态时伤害 +{s.SecondaryValue:P0}",
        "mirror" => $"影分身：同步复制法术 · 复制出的伤害 {s.SecondaryValue:P0} 起 · {s.SecondaryDuration:0.#}s",
        // 兜底是**响亮失败**，不是把原始 id 印给玩家看。`GameConfig` 已把 secondary 校验成一份白名单，
        // 所以落到这里只可能是"新增了一种次级效果却没补文案"——那种时候必须当场炸掉。
        _ => throw new InvalidDataException($"次级效果 '{s.Secondary}' 没有文案——新增次级效果时要在这里补一条"),
    };

    /// <summary>出手形态：几支、怎么飞、范围多大、节奏如何。**间距 / 错时只在真的起作用时才印**——
    /// 单支时它们不参与计算，印出来是给审核的人看假数字。没有形态（如增益类）返回空串。</summary>
    public static string Shape(SkillDef s)
    {
        bool many = s.ProjectileCount > 1;
        string count = many ? $" ×{s.ProjectileCount}" : "";
        string speed = s.Speed > 0 ? $" · 速度 {s.Speed:0}" : "";
        string interval = many && s.VolleyInterval > 0 ? $" · 错时 {s.VolleyInterval:0.##}" : "";
        string gap = many && s.Spread > 0 ? $" · 间距 {s.Spread:0}" : "";
        return s.Trajectory switch
        {
            "hover_homing" => $"悬浮追踪{count} · 停 {s.HoverTime:0.##}s{interval}",
            "sky_drop" => $"空降剑阵{count}{gap} · 落点 {s.AoeRadius:0}"
                + (s.Band > 0 ? $" · 铺开 {s.Band:0}" : "")
                + $" · 停 {s.HoverTime:0.##}s · 高差 {s.SpawnJitter:0}{interval}",
            "arc_homing" => $"弧线{count} · 弧高 {s.ArcMin:0}~{s.ArcMax:0}{interval}{speed}",
            "line_shot" => $"肩侧平射{count}{gap} · 命中即散{interval}{speed}"
                + (s.PierceChance > 0 ? $" · 概率穿透 {s.PierceChance:P0}" : ""),
            "line_pierce" => $"平射贯穿{count}{gap}{interval}{speed}",
            // 没有弹道形态时，**地面持续与定点各有自己的几何要交代**（范围 / 持续 / 延迟）。
            // 从前它们一律返回空串，于是"寒冰龙卷的范围 240""斩鬼神要 0.9 秒才落"这些在界面上都没地方看。
            // 空形态是合法的（bolt / ground / target / buff），但**未知形态要响亮失败**——
            // 否则新增一种形态时这里会静默不印，而摘要看起来一切正常。
            _ when s.Trajectory.Length > 0 => throw new InvalidDataException(
                $"飞行形态 '{s.Trajectory}' 没有文案——新增形态时要在这里补一条"),
            _ => s.Kind switch
            {
                "ground" => $"地面持续 · 范围 {s.AoeRadius:0} · 持续 {s.Duration:0.##}s",
                "target" => $"定点命中 · 延迟 {s.Duration:0.##}s",
                _ => s.Kind == "projectile" && many ? $"直线追踪{count}{interval}" : "",
            },
        };
    }

    /// <summary>
    /// 打击层。**空串 = 地面与空中都能打**（那是不受限的默认情形，不必占一行）；
    /// 受限时返回一句警告式的短句——"飞行单位免疫只打地面的技能"是玩家会反复踩到的硬规则，
    /// 从前它在界面上**任何地方都查不到**。
    /// </summary>
    public static string Layer(SkillDef s) => s.Hits switch
    {
        "ground" => "只打地面（打不到空中）",
        "air" => "只打空中（打不到地面）",
        _ => "",
    };

    /// <summary>出手时机：普通法术是冷却；神通写清"由普攻引动"——配置里的 cooldown 只是最短间隔，
    /// 不点明的话「冷却 5s」会被读成"每 5 秒放一次"。</summary>
    public static string Timing(SkillDef s) => s.TriggerChance > 0
        ? $"神通：由普攻引动，每次普攻 {s.TriggerChance:P0}"
            + (s.TriggerChanceStep > 0 ? $"（每次未触发再 +{s.TriggerChanceStep:P0}）" : "")
            + $" · 最短间隔 {s.Cooldown:0.#}s"
        : $"{s.Cooldown:0.#}s";

    /// <summary>除了伤害与冷却之外，还会改变玩法的旋钮。逐个露面，缺一个就会出现"界面上查不到"的盲区。</summary>
    public static IEnumerable<string> Special(SkillDef s)
    {
        if (s.Kind == "buff" && s.DamageWindow) yield return $"全队增伤 ×{s.Power:0.##}";
        if (s.CastRoot > 0) yield return $"施放时定身全场 {s.CastRoot:0.##}s";
        if (s.Knockback > 0) yield return $"命中击退 {s.Knockback:0}";
        if (s.Gather > 0) yield return $"命中吸附 {s.Gather:0}";
        if (s.AoeAll) yield return "命中场上全体";
        if (s.SkillFlat > 0) yield return $"固定伤害 +{s.SkillFlat:0.##}";
        if (s.Targeting.Length > 0) yield return "选敌·" + s.Targeting switch
        {
            "nearest" => "最近的一只",
            "highest_hp" => "气血最高（无视射程）",
            "lowest_hp" => "气血最低（射程内）",
            "farthest" => "最靠前的一只",
            _ => throw new InvalidDataException($"选敌方式 '{s.Targeting}' 没有文案——新增方式时要在这里补一条"),
        };
    }

    /// <summary>GM 技能预览的一行摘要：一眼扫完这个技能实际会做什么。**不许出现配置里的原始枚举值。**</summary>
    public static string Summary(SkillDef s)
    {
        var parts = new List<string> { Kind(s), Timing(s), $"射程 {s.Range:0}" };
        // `power` 对增益类**只有声明了伤害倍率窗才产生作用**，所以不能一律印「威力 ×N」——
        // 那会让人以为它有一发普攻级的伤害（`power = 1` 的增益其实一点伤害都不打）。
        if (s.Kind != "buff") parts.Add($"威力 ×{s.Power:0.##}");
        if (Secondary(s) is { Length: > 0 } effect) parts.Add(effect);
        if (Shape(s) is { Length: > 0 } shape) parts.Add(shape);
        if (Layer(s) is { Length: > 0 } layer) parts.Add($"【{layer}】");
        parts.AddRange(Special(s));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 玩家技能 tips 的**关键参数**（标签 → 值）。口径是"玩家做选择时真正要看的东西"，
    /// 不是把配置表整张摆出来：
    /// <list type="bullet">
    /// <item><b>单发威力</b>：当前 → 下一级。用**实际伤害数**（DMG1 = 攻击 × 倍率 + 平值）而不是抽象倍率——
    /// 玩家关心的是"能打多少"，而倍率要靠他自己乘。</item>
    /// <item><b>投射物 / 形态</b>：几支、怎么飞、范围多大。</item>
    /// <item><b>打击层</b>：只在受限时才列（那是"这个技能能不能用来打这一波"的硬条件）。</item>
    /// <item><b>冷却 / 射程 / 效果 / 机制</b>。</item>
    /// </list>
    /// 增益类的 `power` 不产生伤害（除非声明了伤害倍率窗），所以**对它们不印威力行**。
    /// </summary>
    public static IEnumerable<(string Label, string Value)> Params(SkillDef s, double attack, double rate, double nextRate,
        double guardBladePower = 0, double guardInterval = 0)
    {
        bool damages = s.Kind != "buff" || s.DamageWindow;
        if (damages)
        {
            // 单支/单发的原始伤害；多支时这是"每支"的数，总量由玩家按投射物数自己乘（下面单独列）。
            double now = DamageFormula.Dmg1(attack, rate, s.SkillFlat);
            double next = DamageFormula.Dmg1(attack, nextRate, s.SkillFlat);
            string per = s.ProjectileCount > 1 ? "（每支）" : "";
            yield return ("单发伤害", $"{Short(now)} → {Short(next)}{per}");
        }
        if (s.ProjectileCount > 1) yield return ("投射物", $"{s.ProjectileCount} 支");
        if (Shape(s) is { Length: > 0 } shape) yield return ("形态", shape);
        if (Layer(s) is { Length: > 0 } layer) yield return ("打击层", layer);
        yield return ("冷却", Timing(s));
        yield return ("射程", $"{s.Range:0}");
        if (Secondary(s) is { Length: > 0 } effect) yield return ("效果", effect);
        // 环绕飞剑（`secondary = shield`）：它的伤害**不在 `power` 上**，而在 `game_settings` 的两个参数里
        // （`guard_blade_power` / `guard_interval`）——所以这两个数必须由调用方显式传进来，
        // 否则提示里会漏掉这个技能一半的内容（它是护盾之外唯一在打人的部分）。
        if (s.Secondary == "shield" && guardBladePower > 0 && guardInterval > 0)
            yield return ("环绕飞剑", $"{Short(DamageFormula.Dmg1(attack, guardBladePower, 0))} / 每 {guardInterval:0.##}s（护盾期间自动还手）");
        foreach (string extra in Special(s)) yield return ("机制", extra);
    }

    /// <summary>把关键参数排成提示框的正文（键值两列，用全角空格补宽对齐）。</summary>
    public static string ParamsBlock(SkillDef s, double attack, double rate, double nextRate,
        double guardBladePower = 0, double guardInterval = 0) =>
        string.Join("\n", Params(s, attack, rate, nextRate, guardBladePower, guardInterval)
            .Select(p => Pad(p.Label) + p.Value));

    /// <summary>伤害数字的显示口径：大数值走 K / M / B，避免一长串数字把提示框撑破。</summary>
    private static string Short(double value) => value switch
    {
        >= 1e9 => $"{value / 1e9:0.##}B",
        >= 1e6 => $"{value / 1e6:0.##}M",
        >= 1e4 => $"{value / 1e3:0.##}K",
        _ => $"{value:0.#}",
    };
}
