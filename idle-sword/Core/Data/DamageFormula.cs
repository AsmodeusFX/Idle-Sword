namespace IdleSword.Core;

/// <summary>
/// 伤害类型。**当前没有任何消费者**——它是为"按类型分池的通用增伤"预留的标签位
/// （`skill_damage` / `basic_attack_damage` 这类属性要不要加，见 `docs/design/fightattr.md` 的投放判断）。
/// 现在先定下取值与边界，等真的要加那类属性时不必再改一遍结构。
/// </summary>
public enum DamageType { Basic, Skill, Dot, Summon, Counter }

/// <summary>
/// DMG3 的三个**条件**乘区。注意普通成长（加算池）不在其中——它由
/// <see cref="DamageFormula.GenericMultiplier(double)"/> 单独表达，因为它的口径是 `1 + Σ` 而不是 `Π`。
/// 边界与判断流程见 `docs/design/fightattr.md`。
/// </summary>
public enum ModifierZone { Generic, Build, Vulnerability }

/// <summary>
/// 一条针对**某一笔 DamageEvent** 生效的动态倍率。它与 FightAttr 的分界是这份配置里最容易搞错的一条：
/// 角色长期拥有、各系统可以直接投放的静态属性进 `fightattr.csv`；
/// "满足某个条件才对这一笔生效"的倍率一律是 DamageModifier
/// （对 BOSS ×1.5、目标残血 ×2、目标带状态 ×1.3、第 5 次攻击 ×2 …）。
///
/// **结构先行**：本轮只由现有三个机制生成（共享伤害倍率窗 / 斩杀 / 利用状态），
/// 注册表与条件求值留到事件系统那一轮——先有唯一的落点，再往里加内容。
/// </summary>
public readonly record struct DamageModifier(string Source, ModifierZone Zone, double Multiplier);

/// <summary>
/// 一笔伤害事件的全部输入。**全工程唯一的最终伤害公式入口**——普攻、法术、多段、额外攻击、
/// 灼烧跳伤、玩家承伤都构造成它，任何地方都不许再写 `damage *= 1.x`。
///
/// 字段与文档里四个乘区的对应（完整口径见 `docs/design/combat.md`）：
/// <list type="bullet">
/// <item><c>Dmg1</c> ← `AttackPower × SkillRate + SkillFlat`（由 <see cref="DamageFormula.Dmg1"/> 算，只在出手处算一次）</item>
/// <item><c>GenericBonus</c> ← 第一乘区，**加算池**的 Σ（乘区值 = `1 + 它`）</item>
/// <item><c>IsCritical</c> / <c>CritDamage</c> ← 第二乘区（**判定**属于 DMG2，**倍率**属于 DMG3）</item>
/// <item><c>BuildMultiplier</c> ← 第三乘区，**乘积**（攻击者侧、带条件）</item>
/// <item><c>VulnerabilityMultiplier</c> ← 第四乘区，**乘积**（目标侧）</item>
/// </list>
///
/// 为什么 Build / Vulnerability 传"乘好的乘积"而不是列表：乘算池对顺序不敏感，
/// 而把集合塞进 readonly struct 会让它在热路径上分配。列表形态的聚合留在
/// <see cref="DamageFormula.BuildMultiplier"/> / <see cref="DamageFormula.VulnerabilityMultiplier"/>，
/// 供将来的注册表按 <see cref="DamageModifier"/> 求值。
/// </summary>
public readonly record struct DamageEvent(
    double Dmg1,
    double GenericBonus,
    bool IsCritical,
    double CritDamage,
    double BuildMultiplier,
    double VulnerabilityMultiplier)
{
    /// <summary>一笔"裸"伤害：四个乘区全中性。调试、敌方效果与自检用。</summary>
    public static DamageEvent Raw(double dmg1) => new(dmg1, 0, false, 1, 1, 1);
}

/// <summary>
/// **伤害公式的唯一实现**。纯函数、不读任何状态，所以三处消费者共用同一份：
/// 逐拍模拟（`Features/Battle/GameSession`）、期望模型（`Core/Data/LevelCurve`）、自检（`tests/`）。
///
/// 为什么"必须是同一份"不是一句口号：`LevelCurve` 的 `Dps()` 与逐拍模拟是同一条公式的两种算路
/// （一个解期望、一个逐次结算），抄第二份的后果是两边**静默分叉**——工具给的建议值和实际
/// 打出来的不一样，而两边都不报错。`LevelCurve` 里那条"monster.csv 的 elite/boss hp 必须等于
/// 模型要求的 SU 份额"的守卫、以及本文件的共用，都是同一条纪律。
///
/// 本项目没有防御体系（无 DEF / 护甲 / 魔抗 / 减伤 / 元素抗性 / 格挡 / 招架 / 穿甲），
/// 所以公式里也没有"守方减伤"这一项：唯一的目标侧缩放是易伤（VulnerabilityMultiplier）。
/// </summary>
public static class DamageFormula
{
    /// <summary>
    /// DMG1：这一发本该能打多少（还没被任何条件放大）。
    /// <paramref name="attackPower"/> 是**最终面板攻击力**——已含武器、`atk_percent` 与天赋平攻。
    /// </summary>
    public static double Dmg1(double attackPower, double skillRate, double skillFlat) =>
        attackPower * skillRate + skillFlat;

    /// <summary>DMG3 第一乘区：通用增伤是**加算池**（`1 + Σ`），不是逐条相乘——基础成长区不制造组合爆炸。</summary>
    public static double GenericMultiplier(double bonus) => 1 + bonus;

    /// <summary>DMG3 第二乘区：非暴击恒为 1。暴击与否由 DMG2 的判定给出。</summary>
    public static double CriticalMultiplier(bool isCritical, double critDamage) => isCritical ? critDamage : 1;

    /// <summary>
    /// 暴击的**期望**倍率 `(1 − C) + C × M`，写成 `1 + C × (M − 1)`。
    /// 期望模型（`LevelCurve`）与玩家可见的"平均一下能打多少"用它；逐次结算用不到。
    /// </summary>
    public static double ExpectedCritMultiplier(double critRate, double critDamage) =>
        1 + critRate * (critDamage - 1);

    /// <summary>加算池求值（Generic 用）。</summary>
    public static double Sum(IEnumerable<DamageModifier> modifiers, ModifierZone zone) =>
        modifiers.Where(m => m.Zone == zone).Sum(m => m.Multiplier);

    /// <summary>乘算池求值（Build / Vulnerability 用）。</summary>
    public static double Product(IEnumerable<DamageModifier> modifiers, ModifierZone zone) =>
        modifiers.Where(m => m.Zone == zone).Aggregate(1d, (acc, m) => acc * m.Multiplier);

    /// <summary>DMG3 第一乘区（列表形态；注册表接上之后由调用方传有效 Modifier 集合）。</summary>
    public static double GenericMultiplier(IEnumerable<DamageModifier> modifiers) =>
        GenericMultiplier(Sum(modifiers, ModifierZone.Generic));

    /// <summary>DMG3 第三乘区（列表形态）。多个 BD 组件因此产生**组合放大**，这正是"成型感"的来源。</summary>
    public static double BuildMultiplier(IEnumerable<DamageModifier> modifiers) =>
        Product(modifiers, ModifierZone.Build);

    /// <summary>DMG3 第四乘区（列表形态）。它是全局放大器，所以要控制投放节奏。</summary>
    public static double VulnerabilityMultiplier(IEnumerable<DamageModifier> modifiers) =>
        Product(modifiers, ModifierZone.Vulnerability);

    /// <summary>DMG3：四个乘区**相乘**。加算只发生在第一乘区**内部**。</summary>
    public static double Dmg3(double genericMultiplier, double criticalMultiplier, double buildMultiplier,
        double vulnerabilityMultiplier) =>
        genericMultiplier * criticalMultiplier * buildMultiplier * vulnerabilityMultiplier;

    /// <summary>最终伤害 = `DMG1 × DMG3`。**这是全工程唯一的最终伤害公式。**</summary>
    public static double Final(in DamageEvent e) => e.Dmg1 * Dmg3(
        GenericMultiplier(e.GenericBonus),
        CriticalMultiplier(e.IsCritical, e.CritDamage),
        e.BuildMultiplier,
        e.VulnerabilityMultiplier);
}
