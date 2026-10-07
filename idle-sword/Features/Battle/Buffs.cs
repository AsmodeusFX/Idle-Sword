using IdleSword.Core;

namespace IdleSword.Features;

/// <summary>增益与效果执行的运行时（`Features/Battle` 的一部分，与 `GameSession` 同生命期）。
/// `BuffInstance`（运行态那一份）与 `BuffDef`（配置）同在 `Core/Data/SkillTable.cs`。</summary>
public sealed partial class GameSession
{
    /// <summary>在役的自身增益，按 `BuffDef.Id` 索引。
    /// **一个 kind 同时只能有一份**：本工程没有叠层（DoT 是刷新覆盖，见 `docs/data/fields.md`），
    /// 所以"两份护盾"在 V1 不成立，查询也按 kind 走。</summary>
    private readonly Dictionary<string, BuffInstance> _buffs = [];

    /// <summary>
    /// 执行一次效果的上下文。**只带"这一发是谁放的、什么力道"**——具体的形态、范围、倍率一律
    /// 从效果定义里读；把它们抄进上下文就又多了一份真相源。
    /// </summary>
    private readonly record struct EffectContext(
        string Skill = "", double Power = 0, int Rank = 0, bool Mirrored = false, BuffInstance? Buff = null);

    /// <summary>在役的某一类增益（按 kind）。没有就是 null——调用方据此回落到中性值。</summary>
    private BuffInstance? Buff(string kind) => _buffs.Values.FirstOrDefault(b => b.Def.Kind == kind);

    // ── 施加 ──────────────────────────────────────────────────────────────────────
    /// <summary>
    /// 施加一个增益类法术的**自身增益**（`kind = buff` 那一档）。同一个增益再次施放是**刷新**
    /// （剩余时间与定格值都按这一次重算），不是叠加。
    /// </summary>
    private void ApplySelfBuff(SkillDef skill, double power, int rank)
    {
        // 施放时回一次 5% 气血：这是**所有增益类法术共用**的一条老规则（`phase_01.md` 记着），
        // 与具体是哪个增益无关，所以留在这里、不拆进某一行的配置。**一次施法只回一次**（不在下面的
        // 循环里）——否则一个技能挂两份增益就会回两次血。文案里没写它，那是待办，不是这一轮的事。
        Battle.PlayerHp = Math.Min(MaxHp, Battle.PlayerHp + MaxHp * .05);
        foreach (var def in skill.Buffs)
        {
            _buffs[def.Id] = new BuffInstance
            {
                Def = def,
                Source = skill.Id,
                Remaining = def.Duration,
                Value = def.Kind switch
                {
                    // 护盾的量按**施放这一刻**的攻击力算定，此后武器升级不改它（与"冷却写施放时的值"同一口径）。
                    "shield" => Attack * def.Value,
                    // 影分身继承比例 = 基础值 + 每级 +1% + 参悟那一份，**不封顶**（用户定）。同样此刻算定。
                    "mirror" => def.Value + .01 * (rank - 1) + SkillBonus(skill.Id, "inherit_percent"),
                    // 其余 kind 的强度是定值，不随等级变（增益的成长体现在**覆盖率**上，见 `BuffCooldown`）。
                    _ => def.Value,
                },
                Extra = def.Extra,
                // 声明了窗的增益把**算上等级与参悟的威力**（`SkillPower`）写进窗——不是配置里的基础幂。
                WindowPower = def.DamageWindow ? power : 1,
            };
        }
    }

    /// <summary>
    /// 把一个状态挂到**敌人**身上（命中时由 `ApplySecondary` 调用，火海每跳也会走这里刷灼烧）。
    /// **按 kind 派发**（穷举 + 抛错），强度与寿命一律取自 `BuffDef`——不再从效果实例上抄一份副本。
    /// 同一个 kind 再挂一次是**刷新**，不是叠加。
    /// </summary>
    private void ApplyBuff(EnemyState e, BuffDef def, CombatEffect fx)
    {
        switch (def.Kind)
        {
            case "slow": e.SlowUntil = def.Duration; e.SlowFactor = 1 - def.Value; break;
            // 寒冷：移速仍走减速那一份（战斗判定只有一条路径），它额外记一份状态，供表现层把受击角色染成冰蓝。
            // 与减速互相覆盖时谁都可能后写，所以两者都按"最后一次命中"为准。
            case "chill": e.SlowUntil = def.Duration; e.SlowFactor = 1 - def.Value; e.ChillUntil = def.Duration; break;
            case "stun": e.StunUntil = def.Duration; break;
            // 灼烧：每秒伤害在**施放那一刻**按攻击者侧结算定格（出手快照——暴击与倍率窗都冻在这里），
            // 此后每跳只再叠目标侧修正（易伤），所以跳伤走 `HurtEnemy` 而不是 `Hit`：那条路上再算一次
            // 攻击者侧就是双重结算。一并记下**来源法术**，供伤害统计在跳伤时归因（那时没有 fx 可查）。
            // ⚠️ 用的是攻击者侧结算值（`AttackerSettled`）而**不是** `Hit` 里那个已经乘过斩杀/利用状态的局部量——
            // 斩杀与利用状态是**逐次命中**的条件，不该被折算进"接下来几秒的每秒伤害"。
            case "dot":
                e.DotUntil = def.Duration;
                e.DotDps = AttackerSettled(fx) * def.Value;
                e.DotSkill = fx.DamageSource;
                break;
            case "vulnerable": e.VulnerableUntil = def.Duration; e.VulnerableFactor = 1 + def.Value; break;
            // 自身向的增益（护盾 / 回血 / 加速 / 暴击 / 影分身）挂到敌人身上没有意义：
            // 那说明 `SkillEffect.buff_id` 配错了，而配错**不会报任何错**，只会在战斗里悄悄什么都不发生。
            default: throw new InvalidDataException($"'{def.Kind}' 是自身向的增益，不该挂到敌人身上（检查 SkillEffect.buff_id）");
        }
    }

    // ── 每步推进 ──────────────────────────────────────────────────────────────────
    /// <summary>
    /// 推进所有自身增益：先扣时间、到期的摘掉，再跑时间轴（`SkillBuffTimeline.csv`）。
    /// 到期的这一帧**不再触发任何效果**——与"计时器归零就不再生效"同一条口径。
    /// </summary>
    private void TickBuffs(double dt)
    {
        foreach (var buff in _buffs.Values.ToArray())
        {
            buff.Remaining -= dt;
            if (buff.Remaining <= 0) { _buffs.Remove(buff.Def.Id); continue; }
            // 回血是这一份增益**自己的**语义（按秒连续恢复），不是时间轴上的一行——
            // 给它配一行"每 0.05 秒回一点"是把实现细节写进配置。
            if (buff.Def.Kind == "regen" && Battle.PlayerHp > 0)
                Battle.PlayerHp = Math.Min(MaxHp, Battle.PlayerHp + MaxHp * buff.Value * dt);
            foreach (var tick in Config.SkillTables.TimelinesOf(buff.Def.Id))
            {
                buff.TickTimer -= dt;
                if (buff.TickTimer > 0) continue;
                if (ExecuteEffect(Config.SkillTables.Effects[tick.EffectId], new EffectContext(buff.Source, Buff: buff)))
                    buff.TickTimer = tick.Interval;
            }
        }
    }

    // ── 事件触发 ──────────────────────────────────────────────────────────────────
    /// <summary>
    /// 把在役增益上登记的**事件触发**跑一遍（`SkillBuffTrigger.csv`）。
    /// 白名单里只有 `on_crit` 与 `on_skill_cast`——其余事件（OnHit / OnKill / OnDamage…）要等
    /// `docs/design/combat.md` §8 的事件总线上线之后再进白名单，在那之前放进来就是一条永不执行的分支。
    /// </summary>
    private void FireTriggers(string triggerType, in EffectContext ctx)
    {
        foreach (var buff in _buffs.Values.ToArray())
            foreach (var trigger in Config.SkillTables.TriggersOf(buff.Def.Id))
                if (trigger.TriggerType == triggerType)
                    ExecuteEffect(Config.SkillTables.Effects[trigger.EffectId], ctx with { Buff = buff });
    }

    // ── 效果执行：**唯一的分派点** ────────────────────────────────────────────────
    /// <summary>
    /// 按 `effect_type` 分派。每种类型只有一个 handler，兜底是**响亮失败**而不是静默什么都不做——
    /// 新增一种效果类型却忘了接上运行时，必须在这里当场炸掉（与 `SkillText` 的穷举同一条纪律）。
    /// 返回是否真的"发生了"（放了、还是没目标可放），调用方据此决定要不要重置时间轴倒计时。
    /// </summary>
    private bool ExecuteEffect(EffectDef fx, in EffectContext ctx) => fx.Type switch
    {
        "attack" => EmitAttack(fx, ctx),
        "auto_attack" => EmitStandaloneAttack(fx, ctx),
        "shorten_cooldown" => ShortenCooldown(),
        "mirror_cast" => MirrorCast(ctx),
        _ => throw new InvalidDataException($"效果类型 '{fx.Type}' 没有 handler——新增类型时要在这里补一条"),
    };

    /// <summary>
    /// `attack` 的 handler：**释放路径的那一次出手**。DMG1 在这里算一次，往下只传结果
    /// （某个形态忘了乘攻击力这类错误因此在结构上不可能发生）。
    /// </summary>
    private bool EmitAttack(EffectDef fx, in EffectContext ctx)
    {
        double dmg1 = DamageFormula.Dmg1(Attack, ctx.Power, fx.SkillFlat);
        return CastVolley(Config.Skills[ctx.Skill], fx, dmg1, ctx.Mirrored);
    }

    /// <summary>
    /// `auto_attack` 的 handler：**定时自己出手的一次攻击**（护盾生效期间的环绕飞剑）。
    /// 与释放路径的出手有三点刻意的不同：① 威力取效果行、**不吃技能成长**（它是护盾的附属还手，
    /// 不是被培养的那一式）；② 出手范围取效果行自己的 `range`——与技能的 `range` 无关，
    /// 后者管的是"这个增益本身能不能放出来"（两者压成一个，远程怪在场时增益就放不出去了，踩过）；
    /// ③ 不写任何冷却。`index` 传 1：技能名标签与"暴击缩冷却"都只认第 0 支，持续输出不该抢那个名额。
    /// </summary>
    private bool EmitStandaloneAttack(EffectDef fx, in EffectContext ctx)
    {
        var target = Target(fx.Range);
        if (target is null) return false;
        Launch("projectile", target, DamageFormula.Dmg1(Attack, fx.Power, fx.SkillFlat), .5,
            skill: ctx.Buff?.Source ?? fx.Id, index: 1);
        return true;
    }

    /// <summary>
    /// `mirror_cast` 的 handler：影分身复制本体刚放出的那一式。
    ///
    /// ⚠️ 它走的是**"发出这一式"而不是"释放这一式"**：不写冷却（否则会多发一次释放音——释放音认
    /// 「冷却 0 → 正」的边沿，也不该把 `cast_root` 的全屏定身重复结算一次），并跳过增益类与召唤类
    /// （对分身没有意义 / 召唤物会活过分身寿命），于是递归不可能发生。
    /// </summary>
    private bool MirrorCast(in EffectContext ctx)
    {
        var skill = Config.Skills[ctx.Skill];
        if (skill.Kind is "buff" or "summon" || skill.Effects.Count == 0) return false;
        // 继承比例取自**在役的那一份影分身**（施放那一刻定格）。取不到就是接线错了——
        // 用一个 `?? 0` 兜底会变成"分身打出一发 0 伤害"，那比什么都不做更难查。
        if (ctx.Buff is not { Value: > 0 } mirror) return false;
        return ExecuteEffect(skill.Effects[0], ctx with { Power = ctx.Power * mirror.Value, Mirrored = true });
    }

    /// <summary>
    /// 缩短一个随机法术的冷却（`buff_crit` 的 `on_crit` 触发器）。
    ///
    /// ⚠️ 它按**秒**扣，与 `skill_cdr` 的 `÷(1 + cdr)` 是两条实现，所以**必须服从同一条上限**：
    /// 扣减后的冷却不得低于 `该法术冷却 ÷ (1 + skill_cdr 上限)`。少了这一步，
    /// "暴击 → 缩冷却 → 更多次出手 → 更多暴击"会把冷却一路吃到接近 0，而面板上看不出来。
    /// </summary>
    private bool ShortenCooldown()
    {
        var crit = Buff("crit_reduce");
        if (crit is null || crit.Extra <= 0) return false;
        // 候选里排除增益类法术：否则暴击会不断给增益减冷却，它的覆盖率从标称的 40% 实测涨到 50%+，
        // 形成"暴击 → 增益来得更勤 → 出手更快 → 更多暴击"的正反馈。与"攻速不加速增益类法术的冷却"
        // 是同一条口径：增益之间的冷却不该互相喂。
        // 神通（`trigger_chance > 0`）同样排除：它们的冷却只是"最短触发间隔"，实际由概率主导，
        // 缩几秒几乎等于白给，还会稀释这次暴击本该给输出法术的收益。
        var cooling = State.Skills.Where(kv => kv.Value > 0 && Battle.Cooldowns.GetValueOrDefault(kv.Key) > 0
                && Config.Skills.TryGetValue(kv.Key, out var s) && s.Kind != "buff" && s.TriggerChance <= 0)
            .Select(kv => kv.Key).ToArray();
        if (cooling.Length == 0) return false;
        string pick = cooling[_random.Next(cooling.Length)];
        // **按秒扣也要服从同一条上限**：冷却缩减的全部路径都必须落在 `cooldown ÷ (1 + skill_cdr)` 这个
        // 频率模型里，否则"把冷却扣到接近 0"就是一条绕过频率轴的暗路（而玩家在面板上完全看不到它）。
        // 下限取**该法术自身冷却 ÷ (1 + skill_cdr 的上限)**——也就是"CDR 拉满时能压到多低"。
        // 注意它**只会让冷却变短、绝不会让它变长**：已经在落地过程中低于下限的冷却原样不动。
        double live = Battle.Cooldowns[pick];
        double floor = Config.Skills[pick].Cooldown / (1 + Bounds("skill_cdr").Max);
        Battle.Cooldowns[pick] = live <= floor ? live : Math.Max(floor, live - crit.Extra);
        return true;
    }
}
