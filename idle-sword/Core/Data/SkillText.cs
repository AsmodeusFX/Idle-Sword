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

    /// <summary>
    /// 出手形态：几支、怎么飞、范围多大、节奏如何。**间距 / 错时只在真的起作用时才印**——
    /// 单支时它们不参与计算，印出来是给审核的人看假数字。没有形态（增益类、纯派发标记）返回空串。
    /// 读的是**效果行**（`SkillEffect.csv`）：形态本来就是它的属性，视图只是它的派生。
    /// </summary>
    public static string Shape(string kind, EffectDef fx)
    {
        bool many = fx.ProjectileCount > 1;
        string count = many ? $" ×{fx.ProjectileCount}" : "";
        string speed = fx.Speed > 0 ? $" · 速度 {fx.Speed:0}" : "";
        string interval = many && fx.VolleyInterval > 0 ? $" · 错时 {fx.VolleyInterval:0.##}" : "";
        string gap = many && fx.Spread > 0 ? $" · 间距 {fx.Spread:0}" : "";
        return fx.Trajectory switch
        {
            "hover_homing" => $"悬浮追踪{count} · 停 {fx.HoverTime:0.##}s{interval}",
            // 「铺开宽度」（band > 0 的各锁一敌）与「按 spread 在阵心两侧铺开」是**两条互斥的编排**
            // （见 `CastVolley`）：band > 0 时 spread 完全不参与计算，印出来就是给审核的人看假数字
            // ——与 combat.md §12.3 清掉的那些死配置同一类。高差同理，为 0 时不必占字数。
            "sky_drop" => $"空降剑阵{count}{(fx.Band > 0 ? "" : gap)} · 落点 {fx.AoeRadius:0}"
                + (fx.Band > 0 ? $" · 铺开 {fx.Band:0}" : "")
                + $" · 停 {fx.HoverTime:0.##}s"
                + (fx.SpawnJitter > 0 ? $" · 高差 {fx.SpawnJitter:0}" : "") + interval,
            "arc_homing" => $"弧线{count} · 弧高 {fx.ArcMin:0}~{fx.ArcMax:0}{interval}{speed}",
            "line_shot" => $"肩侧平射{count}{gap} · 命中即散{interval}{speed}"
                + (fx.PierceChance > 0 ? $" · 概率穿透 {fx.PierceChance:P0}" : ""),
            "line_pierce" => $"平射贯穿{count}{gap}{interval}{speed}",
            // 没有弹道形态时，**地面持续与定点各有自己的几何要交代**（范围 / 持续 / 延迟）。
            // 从前它们一律返回空串，于是"寒冰龙卷的范围 240""斩鬼神要 0.9 秒才落"这些在界面上都没地方看。
            // 空形态是合法的（bolt / ground / target / buff），但**未知形态要响亮失败**——
            // 否则新增一种形态时这里会静默不印，而摘要看起来一切正常。
            _ when fx.Trajectory.Length > 0 => throw new InvalidDataException(
                $"飞行形态 '{fx.Trajectory}' 没有文案——新增形态时要在这里补一条"),
            _ => kind switch
            {
                "ground" => $"地面持续 · 范围 {fx.AoeRadius:0} · 持续 {fx.Duration:0.##}s",
                "target" => $"定点命中 · 延迟 {fx.Delay:0.##}s",
                _ => kind == "projectile" && many ? $"直线追踪{count}{interval}" : "",
            },
        };
    }

    /// <summary>
    /// 一份增益/状态的文案。**按 `BuffDef.kind` 穷举 + 抛错**——新增一种 kind 却没补文案会当场炸，
    /// 而不是落到兜底分支把内部标识符印给玩家（本工程真栽过：`bonus_vs_state` 就这么漏出去过）。
    /// </summary>
    public static string BuffText(BuffDef def) => def.Kind switch
    {
        "slow" => $"减速 {def.Value:P0} · {def.Duration:0.#}s",
        "chill" => $"寒冷（减速 {def.Value:P0}）· {def.Duration:0.#}s",
        "stun" => $"眩晕 {def.Duration:0.#}s",
        "dot" => $"灼烧 {def.Value:P0}/秒 · {def.Duration:0.#}s",
        "vulnerable" => $"易伤 +{def.Value:P0} · {def.Duration:0.#}s",
        "lifesteal" => $"吸血 {def.Value:P0} · {def.Duration:0.#}s",
        "shield" => $"护盾：吸收 攻击×{def.Value:0.##} · {def.Duration:0.#}s",
        "regen" => $"回血 {def.Value:P0}/秒 · {def.Duration:0.#}s",
        "haste" => $"出手加速 +{def.Value:P0} · {def.Duration:0.#}s（普攻与法术一起）",
        "crit_reduce" => $"暴击率 +{def.Value:P0} · {def.Duration:0.#}s；暴击后缩短一个随机法术的冷却 {def.Extra:0.##}s",
        "mirror" => $"影分身：同步复制法术 · 复制出的伤害 {def.Value:P0} 起 · {def.Duration:0.#}s",
        _ => throw new InvalidDataException($"增益 '{def.Kind}' 没有文案——新增一种时间化效果时要在这里补一条"),
    };

    /// <summary>命中时的条件型倍率（DMG3 的 Build 乘区）。**按 `condition` 穷举 + 抛错**，同上。</summary>
    public static string ConditionText(string condition, double value) => condition switch
    {
        "" => "",
        "target_hp_below" => $"斩杀：目标气血低于 {value:P0} 时伤害翻倍",
        "target_has_state" => $"利用状态：目标带着任意状态时伤害 +{value:P0}",
        _ => throw new InvalidDataException($"条件 '{condition}' 没有文案——新增取值时要在这里补一条"),
    };

    /// <summary>
    /// 这一式会给谁带来什么：**遍历技能实际引用的东西**（自身增益 + 每条效果命中时挂的状态与吃的条件），
    /// 逐条渲染。没有就返回空串（调用方据此决定要不要印这一行）。
    /// 逐条渲染而不是从某个枚举字段猜——技能拆成三层之后，"这一式有什么效果"本来就是一张列表。
    /// </summary>
    public static string Effects(SkillDef s)
    {
        var parts = new List<string>();
        foreach (var buff in s.Buffs) parts.Add(BuffText(buff));
        foreach (var fx in s.Effects)
        {
            if (fx.Buff is { } applied) parts.Add(BuffText(applied));
            if (ConditionText(fx.Condition, fx.ConditionValue) is { Length: > 0 } condition) parts.Add(condition);
        }
        return string.Join(" · ", parts);
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
        // 效果自带的旋钮**逐条效果印**：一个技能可以有多条效果，各自的击退 / 吸附 / 落点都是各自的事。
        foreach (var fx in s.Effects)
        {
            if (fx.Knockback > 0) yield return $"命中击退 {fx.Knockback:0}";
            if (fx.Gather > 0) yield return $"命中吸附 {fx.Gather:0}";
            if (fx.AoeAll) yield return "命中场上全体";
            if (fx.SkillFlat > 0) yield return $"固定伤害 +{fx.SkillFlat:0.##}";
        }
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
        if (Effects(s) is { Length: > 0 } effects) parts.Add(effects);
        // 形态逐条印：一个技能可以有多个效果，各自怎么飞是各自的事（当前 15 式都只有一条）。
        foreach (var fx in s.Effects)
            if (Shape(s.Kind, fx) is { Length: > 0 } shape) parts.Add(shape);
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
    public static IEnumerable<(string Label, string Value)> Params(SkillDef s, double attack, double rate, double nextRate)
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
        foreach (var fx in s.Effects)
            if (Shape(s.Kind, fx) is { Length: > 0 } shape) yield return ("形态", shape);
        if (Layer(s) is { Length: > 0 } layer) yield return ("打击层", layer);
        yield return ("冷却", Timing(s));
        yield return ("射程", $"{s.Range:0}");
        if (Effects(s) is { Length: > 0 } effects) yield return ("效果", effects);
        // 派生效果（护盾期间每隔一段时间还手的那柄飞剑）：它的伤害**不在这一式的 `power` 上**，
        // 而在**它自己那条效果行**上——所以它由 `SkillDef.TickEffect` 带进来，不再由调用方传参。
        if (s.TickEffect is { } tick)
            yield return ("派生效果", $"{tick.Name}　{Short(DamageFormula.Dmg1(attack, tick.Power, 0))} / 每 {s.TickEffectInterval:0.##}s（这一式生效期间自动出手）");
        foreach (string extra in Special(s)) yield return ("机制", extra);
    }

    /// <summary>把关键参数排成提示框的正文（键值两列，用全角空格补宽对齐）。</summary>
    public static string ParamsBlock(SkillDef s, double attack, double rate, double nextRate) =>
        string.Join("\n", Params(s, attack, rate, nextRate).Select(p => Pad(p.Label) + p.Value));

    /// <summary>伤害数字的显示口径：大数值走 K / M / B，避免一长串数字把提示框撑破。</summary>
    private static string Short(double value) => value switch
    {
        >= 1e9 => $"{value / 1e9:0.##}B",
        >= 1e6 => $"{value / 1e6:0.##}M",
        >= 1e4 => $"{value / 1e3:0.##}K",
        _ => $"{value:0.#}",
    };
}
