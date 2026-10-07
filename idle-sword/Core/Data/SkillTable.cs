namespace IdleSword.Core;

// ══════════════════════════════════════════════════════════════════════════════════════
//  技能三层：Skill（释放控制）/ Effect（瞬时行为）/ Buff（时间化行为）
// ══════════════════════════════════════════════════════════════════════════════════════
// 分层口径（并入自 docs/battle/技能结构方案.md，冲突与取舍见 docs/data/fields.md）：
//   Skill  只回答"能不能放、放谁"——冷却、射程、境界、成长、释放条件、选敌、打击层，
//          外加 effect_ids / buff_ids 两个引用列。它不描述"放出去之后发生什么"。
//   Effect 是一次性行为（effect_type + 参数 + condition）。所有真正发生的战斗行为都落到它，
//          包括由 Buff 的时间轴/触发器派生出来的那些（环绕飞剑、暴击缩冷却、影分身复制）。
//   Buff   是拥有生命周期的容器（kind / target / duration / value），按 kind 派发。
//
// ⚠️ **本文件是技能五表的唯一解析入口**，`GameConfig`（游戏侧）与 `LevelCurve`（期望模型）
//    共用它（`tools/BalanceCurve` 用 `<Compile Include>` 链接同一份源文件）。
//    抄第二份的后果与本工程已经栽过的那次完全一样：两边静默分叉，而 `--check` 照样绿。
//
// `SkillDef` 是**派生出来的只读视图**，不是一份手写数据：它按下面 `Derive` 从五张表推导，
// 字段名与拆分前保持一致（`Secondary`/`Trajectory`/`AoeRadius`…），所以 `SkillText`、期望模型
// 与大量既有自检都不必改写——"这次搬家有没有改变语义"因此可以被直接观测，而不是靠人肉对列。
// **新代码请读 `Effects` / `Buffs`**；视图只许由推导扩展，不许手工赋值。
// ══════════════════════════════════════════════════════════════════════════════════════

/// <summary>一次性行为。参数按 <see cref="Type"/> 解释，无关列必须为 0/空（加载期拦）。</summary>
/// <param name="Type">
/// `attack` 释放路径的一次出手（形态与选敌都按技能走）/
/// `auto_attack` **定时自己出手**的一次攻击（自带 `power` 与 `range`，**不吃技能成长**；
/// 环绕飞剑就是它，由护盾增益的时间轴驱动）/
/// `shorten_cooldown` 缩短一个随机法术的冷却 / `mirror_cast` 影分身复制。
/// 后两个是**由 Buff 触发器派生**的内部行为，本身没有参数——它们是"所有战斗行为都落到 Effect"
/// 这条口径的落点，也是触发器表能引用到东西的原因。每种类型**只有一个调用点**，所以派发是穷举的。
/// </param>
/// <param name="Condition">
/// 命中时的条件型倍率（DMG3 的 Build 乘区）：空 / `target_hp_below`（斩杀，阈值走 ConditionValue）/
/// `target_has_state`（利用状态，比例走 ConditionValue）。这就是 `combat.md` §8 说的
/// "尚未实现的 DamageModifier 注册表"的落点——本轮先用枚举 + 穷举 handler 接上，
/// 将来换成注册表时**取值不变**。
/// </param>
public sealed record EffectDef(
    string Id, string Name, string Type, string Condition, double ConditionValue,
    double Power, double SkillFlat, double Duration, double Delay, double TickInterval, double Range,
    string Trajectory, int ProjectileCount, double HoverTime, double ArcMin, double ArcMax, double Speed,
    double Spread, double VolleyInterval, double VolleyJitter, double SpawnJitter, double PierceChance,
    double AoeRadius, bool AoeAll, double Band, double Knockback, double Gather)
{
    /// <summary>
    /// 命中时要挂的那份状态（`buff_id` 解析后的定义）。**在加载期就解析好**——消费方（运行时与文案层）
    /// 因此不必各自再去查表，也没有"查不到键"这种只有在战斗里才会炸的失败方式。
    /// </summary>
    public BuffDef? Buff { get; init; }
}

/// <summary>时间化行为。`Kind` 决定运行时怎么解释 `Value`；`Target` 决定挂在谁身上。</summary>
/// <param name="Value">该 kind 的强度：dot 每秒倍率 / slow·chill 减速比例 / vulnerable 增伤比例 /
/// haste 提高比例 / crit_reduce 暴击率绝对加成 / shield 护盾×攻击 / regen 每秒回血比例 /
/// lifesteal 吸血比例 / mirror 继承比例。它是**基础值**：技能等级与参悟的成长只施加在
/// "由技能直接释放的那一手"上（`SkillRate` 那条轴），由时间轴/触发器派生的效果不带成长。</param>
/// <param name="Extra">附加参数：当前只有 `crit_reduce` 用它（每次暴击缩短的冷却秒数）。</param>
/// <param name="DamageWindow">这一份增益是否**写一格共享伤害倍率窗**（DMG3 的 Build 乘区）。
/// **判据与量必须是两个字段**——这正是「剑罡护体」那次事故（`power` 漏改 ⇒ 静默变成全队乘区）
/// 的防线：拿 `DamageWindowPower` 当判据的话，给它点一级参悟就会把窗重新打开。</param>
/// <param name="DamageWindowPower">窗的倍率**基础值**（同样只吃技能成长）。未声明窗时必须为 0。</param>
public sealed record BuffDef(
    string Id, string Name, string Kind, string Target, double Duration, double Value, double Extra,
    bool DamageWindow, double DamageWindowPower);

/// <summary>
/// **运行态的一份增益**：`BuffDef` 是配置（"护盾 = 攻击×2 / 6 秒"），它才是"这只单位身上这一份
/// 还剩多久、是谁给的、强度定格在多少"。两者必须分开——配置只读，运行态随时在变
/// （这是技能结构方案 §21 强调的那条；本工程从前把它俩糊在一起：玩家侧是十来个散字段，
/// 敌人侧是 `EnemyState` 上的五个状态字段，各写一套计时器）。
///
/// 它放在 `Core` 而不是 `Features`：`EnemyState` 也是纯逻辑类型，`Core` 不许反向依赖 `Features`。
/// </summary>
public sealed class BuffInstance
{
    public required BuffDef Def { get; init; }
    /// <summary>施加它的法术 id。伤害归因、以及"派生效果该归到谁名下"都用它
    /// （灼烧跳伤那条路上没有 `CombatEffect` 可查，只能施加时先记下来）。</summary>
    public required string Source { get; set; }
    public double Remaining { get; set; }
    /// <summary>时间轴（`SkillBuffTimeline.csv`）下一次触发的倒计时。没触发成功时不重置，
    /// 于是"场上没有目标"时会下一帧继续找，而不是白等一整个间隔。</summary>
    public double TickTimer { get; set; }
    /// <summary>
    /// 这一份的强度，**在施加那一刻定格**。多数 kind 就是配置的 `value`；三处例外各有理由：
    /// 护盾在这里存的是**吸收池**（攻击 × value，此后被逐次扣减）；影分身的继承比例含等级与参悟的成长；
    /// 灼烧存的是**施放那一刻的攻击者侧结算值**（出手快照——暴击与倍率窗都冻在这里，之后每跳只再叠目标侧修正）。
    /// 定格是刻意的：正在生效的这一次不该随后续升级/参悟追溯变动。
    /// </summary>
    public double Value { get; set; }
    public double Extra { get; init; }
    /// <summary>
    /// 伤害倍率窗的倍率（未声明窗时为 1）。它必须与 <see cref="Value"/> **分开**：退役的「护心剑罡」
    /// 既是护盾（value 0.5）又声明了窗（倍率 1.3），一个字段装不下两个数——而把它们挤成一个，
    /// 正是当年「剑罡护体漏改 power ⇒ 静默变成全队乘区」那类事故的温床。
    /// </summary>
    public double WindowPower { get; init; } = 1;
}

/// <summary>Buff 的时间轴：到点就执行一次 Effect。目前只有 `on_tick`（周期结算）。</summary>
public sealed record BuffTimelineDef(string Id, string BuffId, string Trigger, string EffectId, double Interval);

/// <summary>Buff 的事件触发：战斗里发生某件事时执行一次 Effect。
/// 白名单**只放已经有消费点的事件**——新增取值时必须同时有 handler，否则就是"配置上写了、战斗里没人读"。</summary>
public sealed record BuffTriggerDef(string Id, string BuffId, string TriggerType, string EffectId);

/// <summary>
/// 释放控制层 + **派生只读视图**。
///
/// 前 17 项直接来自 `SwordSkill.csv`；其余是从 Effect / Buff 表推导出来的（见 `SkillTable.Derive`），
/// 字段名沿用拆分前的口径，好让既有消费方与自检原封不动地继续工作。
/// </summary>
public sealed record SkillDef(
    string Id, string Name, string Realm, string Kind, double Cooldown, double Range,
    double Power, double Duration, int MaxLevel, double Cost, double CostGrowth, string Description,
    string Secondary, double SecondaryValue, double SecondaryDuration,
    double AoeRadius, string Trajectory, int ProjectileCount, double HoverTime,
    double ArcMin, double ArcMax, double Speed, double Spread, double VolleyInterval, double VolleyJitter,
    double SpawnJitter, double PierceChance, double SecondaryExtra,
    double TriggerChance, double TriggerChanceStep, double CastRoot, double Knockback,
    string Targeting, bool AoeAll, string Hits, double Gather, double Band,
    double SkillFlat, bool DamageWindow)
{
    /// <summary>这一式引用的瞬时行为（第一条是**主效果**：释放路径按它走，视图也由它推导）。</summary>
    public IReadOnlyList<EffectDef> Effects { get; init; } = [];
    /// <summary>施放瞬间**无条件施加的自增益**。命中才挂的状态不在这里，它在效果行的 `buff_id` 上。</summary>
    public IReadOnlyList<BuffDef> Buffs { get; init; } = [];
    /// <summary>
    /// 由这一式的自增益**派生**出来的持续效果（当前只有护盾时间轴上的环绕飞剑）。
    /// 挂在视图上是为了让文案层不必"由调用方记得传参数"——从前那两个数由 UI 各自从
    /// `game_settings` 取出来传进 `SkillText`，漏传一次就让提示里少掉这一式一半的内容
    /// （它是护盾之外唯一在打人的部分），而漏传不会报错。
    /// </summary>
    public EffectDef? TickEffect { get; init; }
    /// <summary>派生效果的触发间隔（秒）；没有派生效果时为 0。</summary>
    public double TickEffectInterval { get; init; }
}

/// <summary>技能五表解析出来的全部只读定义。</summary>
public sealed class SkillTable
{
    public required Dictionary<string, SkillDef> Skills { get; init; }
    public required Dictionary<string, EffectDef> Effects { get; init; }
    public required Dictionary<string, BuffDef> Buffs { get; init; }
    public required List<BuffTimelineDef> Timelines { get; init; }
    public required List<BuffTriggerDef> Triggers { get; init; }

    /// <summary>时间轴按 buff 归组，运行时按 BuffInstance 查它。</summary>
    public IEnumerable<BuffTimelineDef> TimelinesOf(string buffId) => Timelines.Where(t => t.BuffId == buffId);
    public IEnumerable<BuffTriggerDef> TriggersOf(string buffId) => Triggers.Where(t => t.BuffId == buffId);

    // ── 词表：**只放已经有消费点的取值** ────────────────────────────────────────────
    // "先放开白名单、实现留到以后"会留下"配置上写了、战斗里没人读"的静默失效，
    // 而拦住静默失效正是这个工程一直在做的事（见 docs/design/fightmodes 与 SwordUpgrade.effect 的先例）。
    public static readonly string[] EffectTypes = ["attack", "auto_attack", "shorten_cooldown", "mirror_cast"];
    public static readonly string[] Conditions = ["", "target_hp_below", "target_has_state"];
    /// <summary>11 种时间化效果。前 5 种作用于目标，后 6 种作用于自身。</summary>
    public static readonly string[] BuffKinds = ["dot", "slow", "chill", "stun", "vulnerable", "haste", "crit_reduce", "shield", "regen", "lifesteal", "mirror"];
    public static readonly string[] SelfKinds = ["haste", "crit_reduce", "shield", "regen", "lifesteal", "mirror"];
    public static readonly string[] TimelineTriggers = ["on_tick"];
    /// <summary>**只有这两个事件有消费点**。on_hit / on_kill / on_damage / on_enemy_death 等
    /// 要等 `combat.md` §8 的事件总线上线——在那之前放进白名单就是一条永不执行的分支。</summary>
    public static readonly string[] TriggerTypes = ["on_crit", "on_skill_cast"];

    /// <summary>
    /// 解析并校验五张表，建索引、推导视图。**配置有问题一律抛**（工具与游戏启动都要响亮失败）。
    /// 参数与 `GameConfig.Load` 同一形状：传 CsvRow 列表，不碰文件系统。
    /// </summary>
    public static SkillTable Parse(
        IReadOnlyList<CsvRow> skillRows, IReadOnlyList<CsvRow> effectRows, IReadOnlyList<CsvRow> buffRows,
        IReadOnlyList<CsvRow> timelineRows, IReadOnlyList<CsvRow> triggerRows)
    {
        var effects = new Dictionary<string, EffectDef>();
        var buffs = new Dictionary<string, BuffDef>();

        // ── Buff：生命周期 ──────────────────────────────────────────────────────────
        foreach (var r in buffRows)
        {
            var kind = r.Text("kind");
            if (!BuffKinds.Contains(kind)) throw r.Error("kind", $"未知的时间化效果类型（可用的有 {string.Join(" / ", BuffKinds)}）");
            var target = r.Text("target");
            if (target is not ("self" or "enemy")) throw r.Error("target", "只能是 self 或 enemy");
            // 作用对象由 kind 决定，配反了是"看起来能配、实际挂错人"——那类错不会报错，只会在战斗里表现出来。
            bool mustSelf = SelfKinds.Contains(kind);
            if (mustSelf != (target == "self")) throw r.Error("target", $"{kind} 是{(mustSelf ? "自身" : "目标")}向的，target 应为 {(mustSelf ? "self" : "enemy")}");
            Positive(r, "duration");
            Nonnegative(r, "value", "extra");
            bool window = r.Flag("damage_window");
            double windowPower = r.Number("damage_window_power");
            if (window && windowPower <= 0) throw r.Error("damage_window_power", "声明了伤害倍率窗就必须给正的倍率");
            // 声明了窗却倍率为 1 ⇒ 那格窗乘的是 1，等于没声明。这种"配了但不生效"的写法必须拦。
            if (window && windowPower == 1) throw r.Error("damage_window_power", "倍率为 1 的窗什么都不做，等于没声明");
            if (!window && windowPower != 0) throw r.Error("damage_window_power", "没声明伤害倍率窗（damage_window = 0）时倍率必须留 0");
            // 影分身的继承比例是 0～1 的小数（0.7 = 七成）。误填成 70 不会报任何错、只会静默变成 7000%。
            if (kind == "mirror" && r.Number("value") > 1) throw r.Error("value", "影分身的继承比例是 0～1 的小数（0.7 = 七成）");
            buffs.Add(r.Text("id"), new BuffDef(r.Text("id"), r.Text("name"), kind, target, r.Number("duration"),
                r.Number("value"), r.Number("extra"), window, windowPower));
        }

        // ── Effect：一次性行为 ──────────────────────────────────────────────────────
        foreach (var r in effectRows)
        {
            var type = r.Text("effect_type");
            Choice(r, "effect_type", EffectTypes);
            var condition = r.Text("condition");
            Choice(r, "condition", Conditions);
            double conditionValue = r.Number("condition_value");
            if (condition == "") { if (conditionValue != 0) throw r.Error("condition_value", "没有 condition 时它必须为 0"); }
            else if (conditionValue <= 0) throw r.Error("condition_value", "声明了 condition 就必须给正的值（0 就是一行死配置）");
            foreach (string f in new[] { "power", "skill_flat", "duration", "delay", "tick_interval", "range", "hover_time", "arc_max", "spread", "volley_interval", "volley_jitter", "spawn_jitter", "pierce_chance", "aoe_radius", "band", "knockback", "gather" })
                Nonnegative(r, f);
            int count = r.Int("projectile_count");
            if (count < 1) throw r.Error("projectile_count", "至少 1");
            double speed = r.Number("speed");
            if (speed != 0 && speed < 100) throw r.Error("speed", "0 表示默认 1500；显式配置时必须 ≥ 100");
            if (r.Number("pierce_chance") > 1) throw r.Error("pierce_chance", "是概率，取值 0～1");
            // 弧度允许为负：负值表示从下方掠过（上方空间多、下方少）。下限 -60，再大就会插进地面与下方 UI。
            if (r.Number("arc_min") < -60) throw r.Error("arc_min", "下弧不得超过 -60（会越出地面与下方界面）");
            if (r.Number("arc_min") > r.Number("arc_max")) throw r.Error("arc_min", "不能大于 arc_max");
            var trajectory = r.Text("trajectory");
            if (trajectory != "") Choice(r, "trajectory", "bolt", "hover_homing", "sky_drop", "arc_homing", "line_pierce", "line_shot");
            if (trajectory == "arc_homing")
            {
                // 弧顶 = 发射高度约 300 − 弧高；弧高超过 300 会顶出战斗区上沿。
                if (r.Number("arc_max") <= 0) throw r.Error("arc_max", "弧线形态需要正的弧度上界");
                if (r.Number("arc_max") > 300) throw r.Error("arc_max", "弧度超过 300 会越出战斗画面");
            }
            // 落点判定半径复用 aoe_radius：ground 在 0 时回退 220，天降形态绝不允许回退（否则会变成来路不明的大范围）。
            if (trajectory == "sky_drop" && r.Number("aoe_radius") <= 0) throw r.Error("aoe_radius", "天降形态需要正的落点判定半径");
            // 黑洞铺开宽度只有 sky_drop 会读它——别的形态配了就是一行什么都不做的死配置。
            if (r.Number("band") > 0 && trajectory != "sky_drop") throw r.Error("band", "只有 sky_drop 形态支持黑洞铺开宽度（band）");
            // 铺得太宽就夹不住画面了（表现层要把这条带子夹在 1920 逻辑画布里，可用横向余量只有 1460）。
            if (r.Number("band") > 1400) throw r.Error("band", "黑洞铺开宽度超过 1400 就夹不进画面（角色锚点在 330、逻辑宽 1920）");
            var buffId = r.Text("buff_id");
            if (buffId != "" && !buffs.ContainsKey(buffId)) throw r.Error("buff_id", $"引用不存在: {buffId}");
            var appliedBuff = buffId == "" ? null : buffs[buffId];
            // 一次出手却威力为 0 就是"放了等于没放"，而它不报任何错——与旧表 `power` 必须为正同一条护栏。
            if (type is "attack" or "auto_attack" && r.Number("power") <= 0)
                throw r.Error("power", $"{type} 的威力必须大于 0（0 就是放了等于没放）");
            // 定时出手的攻击**自带出手范围**（它不由技能的释放路径选敌）：没有范围就永远不会出手。
            if (type == "auto_attack" && r.Number("range") <= 0)
                throw r.Error("range", "auto_attack 必须自带正的出手范围（否则它永远够不着任何目标）");
            // 天降 + 灼烧 = 落地后残留一片火海（见 `GameSession.TickEffects` 的 sky_drop 分支）。
            // 火海的寿命取那份 Buff 的 `duration`，所以不必单独校验正负（Buff 一律要求 duration > 0）；
            // 但"火海优先于全体结算"这条互斥仍然要拦。
            if (trajectory == "sky_drop" && appliedBuff is { Kind: "dot" } && r.Flag("aoe_all"))
                throw r.Error("aoe_all", "天降火海与全体命中互斥（火海优先，同配会让 aoe_all 静默失效）");
            // 两个纯派发标记（缩冷却 / 影分身复制）本身没有参数——**每一列都必须是默认值**。
            // 逐列点名而不是只查一两列：漏一列就等于留一个"配了但不生效"的口子。
            if (type is "shorten_cooldown" or "mirror_cast") Marker(r);
            // 定时出手的攻击打的是**一个**目标（它没有阵心、也没有落点半径可铺）：配了全体命中就是死配置。
            if (type == "auto_attack" && r.Flag("aoe_all")) throw r.Error("aoe_all", "auto_attack 只打一个目标");
            effects.Add(r.Text("id"), new EffectDef(r.Text("id"), r.Text("name"), type, condition, conditionValue,
                r.Number("power"), r.Number("skill_flat"), r.Number("duration"), r.Number("delay"), r.Number("tick_interval"),
                r.Number("range"), trajectory, count, r.Number("hover_time"), r.Number("arc_min"), r.Number("arc_max"),
                speed, r.Number("spread"), r.Number("volley_interval"), r.Number("volley_jitter"), r.Number("spawn_jitter"),
                r.Number("pierce_chance"), r.Number("aoe_radius"), r.Flag("aoe_all"), r.Number("band"),
                r.Number("knockback"), r.Number("gather")) { Buff = appliedBuff });
        }

        // ── Skill：释放控制 ─────────────────────────────────────────────────────────
        var skills = new Dictionary<string, SkillDef>();
        foreach (var r in skillRows)
        {
            var kind = r.Text("kind");
            Choice(r, "kind", "projectile", "target", "ground", "buff", "summon");
            Positive(r, "cooldown", "range", "max_level", "cost", "cost_growth");
            var targeting = r.Text("targeting");
            if (targeting != "") Choice(r, "targeting", "nearest", "highest_hp", "lowest_hp", "farthest");
            var hits = r.Text("hits");
            if (hits != "") Choice(r, "hits", "ground", "air", "both");
            Nonnegative(r, "trigger_chance", "trigger_chance_step", "cast_root");
            if (r.Number("trigger_chance") > 1) throw r.Error("trigger_chance", "是概率，取值 0～1");
            var effectIds = r.TextList("effect_ids");
            foreach (string id in effectIds)
                if (!effects.ContainsKey(id)) throw r.Error("effect_ids", $"引用不存在: {id}");
            var buffIds = r.TextList("buff_ids");
            foreach (string id in buffIds)
                if (!buffs.ContainsKey(id)) throw r.Error("buff_ids", $"引用不存在: {id}");
            // 自增益只属于增益类法术；其余类别要挂状态就写在效果行的 `buff_id` 上（命中才挂）。
            // 允许两边都写会让"这个状态是谁挂的、什么时候挂的"变成两个答案。
            if (kind == "buff")
            {
                if (buffIds.Count != 1) throw r.Error("buff_ids", "增益类法术必须恰好引用 1 个自身增益");
                if (effectIds.Count != 0) throw r.Error("effect_ids", "增益类法术不带瞬时效果（环绕飞剑那类由时间轴派生，见 SkillBuffTimeline.csv）");
            }
            else if (buffIds.Count != 0) throw r.Error("buff_ids", "只有 kind = buff 的法术能配自身增益；命中才挂的状态写在效果行的 buff_id 上");
            var primary = effectIds.Count > 0 ? effects[effectIds[0]] : null;
            var ownBuff = kind == "buff" ? buffs[buffIds[0]] : null;
            // 形态与弹数只对弹道类有意义（本来就只有 projectile 会配），其余类别配了就是死配置。
            if (primary is not null)
            {
                if (kind != "projectile" && primary.Trajectory != "")
                    throw r.Error("effect_ids", "只有 projectile 类可使用自定义飞行形态（改效果行的 trajectory）");
                if (kind != "projectile" && primary.ProjectileCount != 1)
                    throw r.Error("effect_ids", "非 projectile 类只能单发（改效果行的 projectile_count）");
                if (kind != "ground" && kind != "projectile" && primary.TickInterval != 0)
                    throw r.Error("effect_ids", "只有 ground 力场与天降火海会周期结算（tick_interval）");
                // 地面力场没有"结算间隔"就永远不会结算——那是"放了没反应"且不报错的一类。
                if (kind == "ground" && primary.TickInterval <= 0)
                    throw r.Error("effect_ids", "ground 力场需要正的 tick_interval（否则一次都不会结算）");
                // 天降火海同样是周期结算：落地点火之后每一跳都在刷新状态。
                if (primary.Trajectory == "sky_drop" && primary.Buff is { Kind: "dot" } && primary.TickInterval <= 0)
                    throw r.Error("effect_ids", "天降火海需要正的 tick_interval（否则火海一次都不会结算）");
                // 主效果必须有自己的时间：延迟（定点类）或时长（下坠 / 力场 / 飞行）。两者都是 0 的话，
                // 自定形态的寿命就是 0 —— 效果在出生当帧被回收，而配置看起来完全正常。
                if (kind != "buff" && primary.Delay + primary.Duration <= 0)
                    throw r.Error("effect_ids", "这一式的延迟与时长不能都为 0（效果会在出生当帧就被回收）");
            }
            if (ownBuff is { DamageWindow: true } && ownBuff.DamageWindowPower == 1)
                throw r.Error("buff_ids", "声明了伤害倍率窗时倍率不能是 1，否则这一格窗什么都不做");
            skills.Add(r.Text("id"), Derive(r, kind, targeting, hits, primary, ownBuff, effects, buffs, effectIds, buffIds));
        }

        // ── 时间轴 / 触发器 ────────────────────────────────────────────────────────
        var timelines = new List<BuffTimelineDef>();
        foreach (var r in timelineRows)
        {
            Ref(r, "buff_id", buffs); Ref(r, "effect_id", effects);
            Choice(r, "trigger", TimelineTriggers);
            Positive(r, "interval");
            timelines.Add(new(r.Text("id"), r.Text("buff_id"), r.Text("trigger"), r.Text("effect_id"), r.Number("interval")));
        }
        var triggers = new List<BuffTriggerDef>();
        foreach (var r in triggerRows)
        {
            Ref(r, "buff_id", buffs); Ref(r, "effect_id", effects);
            Choice(r, "trigger_type", TriggerTypes);
            triggers.Add(new(r.Text("id"), r.Text("buff_id"), r.Text("trigger_type"), r.Text("effect_id")));
        }

        // ── 回填：把"由自增益派生出来的那条效果"挂到视图上 ──────────────────────────
        // 时间轴解析完之后才做得了（它要读 Timeline）。这么挂一次，文案层就不必由调用方
        // 各自去查表、也不会因为漏传一个参数而静默少说一半内容。
        foreach (var (id, skill) in skills.ToArray())
        {
            if (skill.Buffs.Count == 0) continue;
            var tick = timelines.FirstOrDefault(t => t.BuffId == skill.Buffs[0].Id);
            if (tick is null) continue;
            skills[id] = skill with { TickEffect = effects[tick.EffectId], TickEffectInterval = tick.Interval };
        }

        return new SkillTable { Skills = skills, Effects = effects, Buffs = buffs, Timelines = timelines, Triggers = triggers };
    }

    /// <summary>
    /// 把"释放控制 + 效果 + 增益"推导成旧的扁平视图。**每一项的推导规则都写在旁边**，
    /// 因为它是"搬家没改语义"这条保证的实现，自检会逐字段与拆分前的值对账。
    /// </summary>
    private static SkillDef Derive(CsvRow r, string kind, string targeting, string hits,
        EffectDef? primary, BuffDef? ownBuff, Dictionary<string, EffectDef> effects, Dictionary<string, BuffDef> buffs,
        List<string> effectIds, List<string> buffIds)
    {
        // 命中才挂的状态（火海 / 龙卷 / 雷击）在效果行的 buff_id 上；自增益在自己的 buff_ids 上。
        // 两者都有时以自增益为准——旧表一个技能只有一个 secondary，增益类不会同时挂敌方状态。
        var applied = ownBuff ?? primary?.Buff;
        // `Duration` 是旧表的"主时长"：非增益类取"延迟 + 自身时长"（定点类的 duration 就是延迟，
        // 天降类是下坠时长，地面类是力场寿命，弹道类是飞行寿命），增益类取增益自己的时长。
        double duration = primary is not null ? primary.Delay + primary.Duration : ownBuff?.Duration ?? 0;
        // `Power` 是这一式的威力：有攻击效果就取它；否则只有"写了伤害倍率窗"的增益才有威力（其余增益的
        // power 不产生任何伤害，旧表里它们一律是 1）。
        double power = primary is not null ? primary.Power
            : ownBuff is { DamageWindow: true } ? ownBuff.DamageWindowPower : 1;
        return new SkillDef(r.Text("id"), r.Text("name"), r.Text("realm_id"), kind,
            r.Number("cooldown"), r.Number("range"), power, duration, r.Int("max_level"), r.Number("cost"),
            r.Number("cost_growth"), r.Text("description"),
            SecondaryOf(applied, primary), applied?.Value ?? primary?.ConditionValue ?? 0, applied?.Duration ?? 0,
            primary?.AoeRadius ?? 0, primary?.Trajectory ?? "", primary?.ProjectileCount ?? 1, primary?.HoverTime ?? 0,
            primary?.ArcMin ?? 0, primary?.ArcMax ?? 0, primary?.Speed ?? 0, primary?.Spread ?? 0,
            primary?.VolleyInterval ?? 0, primary?.VolleyJitter ?? 0, primary?.SpawnJitter ?? 0, primary?.PierceChance ?? 0,
            applied?.Extra ?? 0, r.Number("trigger_chance"), r.Number("trigger_chance_step"), r.Number("cast_root"),
            primary?.Knockback ?? 0, targeting, primary?.AoeAll ?? false, hits, primary?.Gather ?? 0, primary?.Band ?? 0,
            primary?.SkillFlat ?? 0, ownBuff?.DamageWindow ?? false)
        {
            Effects = [.. effectIds.Select(id => effects[id])],
            Buffs = [.. buffIds.Select(id => buffs[id])],
        };
    }

    /// <summary>
    /// 旧表的 `secondary` 视图：挂在身上的增益直接取它的 kind；斩杀 / 利用状态这两条
    /// 是**命中时的条件倍率**（DMG3 的 Build 乘区），旧口径把它们也叫 secondary，这里映射回去
    /// ——`SkillText` 与既有多条自检都按旧口径断言。
    /// </summary>
    private static string SecondaryOf(BuffDef? applied, EffectDef? primary) =>
        applied?.Kind is { Length: > 0 } kind ? kind : SecondaryOfCondition(primary?.Condition ?? "");

    /// <summary>
    /// 条件 → 旧口径的次级效果名（斩杀 / 利用状态）。**穷举 + 抛错**：新增一个 condition 却没补映射，
    /// 会在这里当场炸，而不是在战斗里静默地什么都不做。
    /// 运行时（`GameSession.Hit` 与 `LaunchSkill`）也读它，所以是 public——口径只许有一份。
    /// </summary>
    public static string SecondaryOfCondition(string condition) => condition switch
    {
        "" => "",
        "target_hp_below" => "execute",
        "target_has_state" => "bonus_vs_state",
        _ => throw new InvalidDataException($"条件 '{condition}' 没有映射到次级效果名——新增取值时要在这里补一条"),
    };

    /// <summary>
    /// 非 attack 的效果是**纯派发标记**（触发器表靠它引用到一个真实的行为单元，见 `EffectTypes` 的注释）：
    /// 它不该带任何参数。逐列点名检查，默认值就是"这一列对这个类型没有意义"。
    /// </summary>
    private static void Marker(CsvRow r)
    {
        foreach (string f in new[] { "condition_value", "power", "skill_flat", "duration", "delay", "tick_interval", "range",
            "hover_time", "arc_min", "arc_max", "speed", "spread", "volley_interval", "volley_jitter", "spawn_jitter",
            "pierce_chance", "aoe_radius", "band", "knockback", "gather" })
            if (r.Number(f) != 0) throw r.Error(f, $"{r.Text("effect_type")} 是纯派发标记、没有参数，这一列应为 0");
        if (r.Int("projectile_count") != 1) throw r.Error("projectile_count", "纯派发标记的弹数应为 1");
        if (r.Flag("aoe_all")) throw r.Error("aoe_all", "纯派发标记不命中任何目标");
        foreach (string f in new[] { "condition", "trajectory", "buff_id" })
            if (r.Text(f) != "") throw r.Error(f, $"{r.Text("effect_type")} 是纯派发标记，这一列应为空");
    }

    private static void Ref(CsvRow r, string field, Dictionary<string, EffectDef> table)
    {
        if (!table.ContainsKey(r.Text(field))) throw r.Error(field, $"引用不存在: {r.Text(field)}");
    }
    private static void Ref(CsvRow r, string field, Dictionary<string, BuffDef> table)
    {
        if (!table.ContainsKey(r.Text(field))) throw r.Error(field, $"引用不存在: {r.Text(field)}");
    }
    private static void Positive(CsvRow r, params string[] fields) { foreach (string f in fields) if (r.Number(f) <= 0) throw r.Error(f, "必须大于 0"); }
    private static void Nonnegative(CsvRow r, params string[] fields) { foreach (string f in fields) if (r.Number(f) < 0) throw r.Error(f, "不能小于 0"); }
    private static void Choice(CsvRow r, string f, params string[] choices) { if (!choices.Contains(r.Text(f))) throw r.Error(f, "未知枚举值"); }
}
