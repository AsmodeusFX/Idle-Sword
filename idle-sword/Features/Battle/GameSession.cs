using System.Globalization;
using IdleSword.Core;

namespace IdleSword.Features;

/// <summary>固定步长的纯 C# 游戏会话；Godot 仅负责输入、表现和持久化调度。</summary>
public sealed partial class GameSession
{
    public GameConfig Config { get; }
    public PlayerState State { get; }
    public BattleState Battle => State.Battle;
    public LevelDef Level => Config.Levels.Single(l => l.Id == Battle.LevelId);
    public List<CombatEffect> Effects { get; } = [];
    public event Action? PersistRequested;
    public string Message { get; private set; } = "踏入青岚，问道长生。";
    public bool Moving { get; private set; }
    public double Elapsed { get; private set; }
    private readonly Random _random;
    /// <summary>
    /// `fightattr` 的取值缓存。**为什么要有它**：`Config.Attr(id)` 是一次 LINQ 扫描
    /// （`Rows("fightattr").Single(...)`），而攻速与 CDR 在冷却衰减那条热路径上**每步、每个冷却键**都要读，
    /// 一秒就是几百次扫描。配置在 `Load` 之后只读、会话内不会变（改配置要重新加载并新建会话），所以缓存安全。
    /// </summary>
    private readonly Dictionary<string, double> _attrs = [];
    /// <summary>属性上下限的缓存，与上面的取值缓存同一理由（暴击率合计每次出手都要夹一次）。</summary>
    private readonly Dictionary<string, (double Min, double Max)> _bounds = [];
    private double Attr(string id) => _attrs.TryGetValue(id, out double value) ? value : _attrs[id] = Config.Attr(id);
    /// <summary>该属性的上下限。`max_value` 留空 = 不设上限。</summary>
    private (double Min, double Max) Bounds(string id)
    {
        if (_bounds.TryGetValue(id, out var bounds)) return bounds;
        var row = Config.Row("fightattr", id);
        string max = row.Text("max_value").Trim();
        return _bounds[id] = (row.Number("min_value"),
            max.Length > 0 ? double.Parse(max, CultureInfo.InvariantCulture) : double.MaxValue);
    }
    /// <summary>
    /// 把属性的**合计值**夹回上下限内。夹的是"基础值 + 各来源加成"之后的总量，这才是上下限该管的东西。
    /// 上限是**防手滑的护栏**（例如暴击率合计不得超过 1），不是平衡旋钮——要调平衡请改属性的持有者，
    /// 不要把上限往下压：那会让超出部分**静默消失**，而玩家看到的面板值还停在夹取之前。
    /// </summary>
    private double ClampAttr(string id, double total)
    {
        var (min, max) = Bounds(id);
        return Math.Clamp(total, min, max);
    }
    private bool _persist;
    // 自身增益（伤害倍率窗 / 护盾 / 回血 / 吸血 / 攻速 / 暴击 / 影分身）统一由 `BuffSystem` 持有，
    // 见 `Features/Battle/Buffs.cs`——它们从前是十来个散字段 + 手写计时器，与敌方状态各写一套。
    // 神通的"概率累加"：技能 id → 已累加的概率。`trigger_chance_step > 0` 的法术每普攻一次就涨一点，
    // 摇中后清零。跨关卡 / 死亡 / 切预览一并清掉（挂在 ClearBuffs 上），与其它短时状态同生命周期。
    // 它**不是**增益：它是"这一式什么时候能放"（Skill 层的释放控制），所以不搬进 BuffSystem。
    private readonly Dictionary<string, double> _triggerRamp = [];
    public bool RiftUnlocked => Battle.BossDefeated && !Battle.Enemies.Any(e => e.Hp > 0 && e.Kind != "rift");
    /// <summary>最终气血。平血（`hp_flat`）与平攻同理**加在乘区之外**，见 <see cref="Attack"/>：
    /// 并进乘区的话，后期会被 `max_hp_percent` 放大成完全不同的量级。</summary>
    public double MaxHp => Attr("max_hp_base") * (1 + Attr("max_hp_percent") + TalentBonus("hp"))
        + TalentBonus("hp_flat");
    /// <summary>
    /// 最终攻击 = **DMG1 里的 `AttackPower`**（见 `docs/design/combat.md`）。
    /// 构成写死在这一行，别的模块不许再拼一遍：
    /// `(atk_base + 武器攻击) × (1 + atk_percent + 天赋百分比) + 天赋平攻`。
    /// **武器在乘区之内**（与 `atk_base` 同类，都是"基础攻击"），**平攻在乘区之外**——
    /// 它是"开局先从 +1 起步"那种固定值，若并进乘区，后期会被武器与百分比放大成完全不同的量级，
    /// 那不是这个节点想表达的东西。
    /// </summary>
    public double Attack => (Attr("atk_base") + WeaponAttack) * (1 + Attr("atk_percent") + TalentBonus("atk"))
        + TalentBonus("atk_flat");
    public double WeaponAttack => State.Weapon == "" ? 0 : Config.Row("Equip", State.Weapon).Number("base_atk") * (1 + .15 * State.WeaponLevel) * State.WeaponRoll;
    /// <summary>角色能打到的最大距离：取已习得法术的最大射程（不含 buff 类，它们不造成伤害）。
    /// **只算法术，不并入普攻射程**：接近裂隙的停步判定靠它，若改由普攻射程决定，
    /// 角色会停在裂隙射程外空转（见 Step 里裂隙解锁那段的说明）。普攻射程单列在 `fightattr.basic_range`，
    /// 它不小于停步距离 640，因此角色站定时普攻必定够得着。
    /// **一个法术都没学时回落到普攻射程**：开局不再白送御剑术（见构造器），若不回落这里会是 0，
    /// 角色在 BOSS 格会一路走到底也不停——普攻虽然仍够得着裂隙，但"停下来打"的读法就没有了。
    /// 回落只在**完全没有**伤害型法术时生效，不是把普攻射程并进最大值。
    /// **回落值必须跟着普攻形态走**（`BasicAttackRange`）：近战只有 150，若这里仍回落到远程的 950，
    /// 角色会停在离裂隙 950 处"以为够得着"，而近战根本打不到——**裂隙打不掉、整关卡死**。
    /// 这条与"不能并入普攻射程"是同一条护栏的两面：射程要取自**真的能打到那么远**的那个值。</summary>
    public double AttackRange
    {
        get
        {
            var ranges = State.Skills.Where(kv => kv.Value > 0 && Config.Skills.TryGetValue(kv.Key, out var s) && s.Kind != "buff")
                .Select(kv => Config.Skills[kv.Key].Range).ToList();
            return ranges.Count > 0 ? ranges.Max() : BasicAttackRange;
        }
    }

    /// <summary>
    /// 普攻当前是不是**近战**（挥剑斩击）。**开局就是近战**——玩家一开始只会挥剑，
    /// 要到修行树上点亮「剑气」（`ranged_basic`）才恢复远程平射。这是教学闭环的起点。
    ///
    /// 与其它解锁节点一样是**开关**不是档位：只看有没有，不看几级。
    /// </summary>
    public bool MeleeBasic => TalentBonus("ranged_basic") <= 0;

    /// <summary>
    /// 自动攻击有没有被「生根」（`auto_basic`）激活。**没激活之前只能手动点**——
    /// 这正是那个节点的价值：省的是手，不是伤害（手动与自动共用同一条判定与同一个冷却键，
    /// 所以点得再快也不会比自动多打一下）。
    /// </summary>
    public bool AutoBasicUnlocked => TalentBonus("auto_basic") > 0;

    /// <summary>
    /// 普攻能打到的距离。**近战不是"换个画法"，射程真的短**——`Target()` 与命中的可达性都用它。
    /// 与 `StopRange` 一起必须满足 `BasicAttackRange ≥ StopRange`：否则角色站定了却够不着怪，
    /// 表现为"停在原地永远不出手"（挂机彻底中断，且不报任何错）。
    /// </summary>
    public double BasicAttackRange => MeleeBasic ? Attr("melee_range") : Attr("basic_range");

    /// <summary>
    /// 走到离怪多近就停下。近战时缩到贴身——这是"真近战"的落脚点：不只是把飞剑换成挥剑，
    /// 而是角色真的要走进去。副作用是玩家会站进远程怪（460~620）的射程里挨打，这正是
    /// "血少、不点修为就容易死"那一段压力的来源，是有意的。
    /// </summary>
    public double StopRange => MeleeBasic ? Attr("melee_stop_range") : Attr("stop_range");

    /// <summary>
    /// 近战挥砍的**动作时长**：这一式在结算完成后还要在场上留这么久，纯粹给表现层画挥剑的余韵。
    /// 定成常量而不是配置项，是因为它只是动画长度，不参与任何判定——
    /// 伤害在出手当拍就已经进了 `HurtEnemy`（见 TickEffects 的 `melee_slash` 分支）。
    /// </summary>
    private const double MeleeSwingLife = .22;

    /// <summary>普攻占用的冷却键。放进 Battle.Cooldowns 是为了复用 Step 里那唯一的冷却衰减点：
    /// 攻速增益因此自动作用于普攻，键本身也随存档往返（SaveStore 不校验冷却键，旧存档缺该键即视为就绪）。
    /// **绝不能放进 State.Skills**：存档校验会用 SwordSkill 表核对技能键，且 CastSkills 对键是无保护索引。</summary>
    public const string BasicAttackKey = "basic_attack";

    /// <summary>**自动**普攻开关。开启时角色每 `fightattr.basic_interval` 秒向最近的合法目标出手，
    /// 并在出手的那一刻摇各神通的触发概率（见 TickBasicAttack）。
    /// 自检里关掉它是为了给受控靶场保留确定的伤害预算——普攻每秒都在加伤害，也会消耗随机数。
    /// **它只管自动那一条路**：手动点击（<see cref="ManualBasicAttack"/>）是玩家自己的动作，
    /// 不受它影响，否则"关掉自动"会连"点也点不动"——那是两件事。</summary>
    public bool BasicAttackEnabled { get; set; } = true;

    /// <summary>
    /// **沙盒模式：世界静止，只有战斗结算在跑。**
    ///
    /// `Step` 里跳过 移动 / 激活格子 / 刷怪 / 死亡 / 通关 / 触发存档；冷却流逝、施法、
    /// 效果推进、命中结算、状态计时**照常**——所以技能照样放得出来、打得到靶子。
    ///
    /// 用途是"固定靶场"（GM 技能预览）：场上只有自己摆的靶子，玩家站在原地，
    /// 15 个技能面对完全相同的对照条件。
    ///
    /// **为什么要有这个开关，而不是让调用方在外面打补丁**：调用方要冻结世界，
    /// 就得自己对抗 `Step` 里的每一个推进源（移动的停步判定、`ActivateCell` 建刷怪点、
    /// `TickSpawns` 补怪……），而这些东西会随正规玩法改动而变——技能预览就这么被击穿过一次
    /// （近战停步 120 而靶子钉在 520 ⇒ 角色一路前进 ⇒ 走进新格就刷怪）。
    /// 显式模式之下，"世界不动"是会话自己的保证，不由调用方维护。
    ///
    /// 会话态、**不落盘**，与 `BasicAttackEnabled` / `PlayerInvincible` / `WaveBonus` / `MonsterHpScale`
    /// 同性质（调试与工具用）。
    /// </summary>
    public bool SandboxMode { get; set; }

    public GameSession(GameConfig config, PlayerState? state = null, int? seed = null)
    {
        Config = config; State = state ?? new(); _random = seed is null ? new Random() : new Random(seed.Value);
        if (state is null)
        {
            State.Wallet["gold"] = config.Setting("starting_gold");
            State.UnlockedLevels.Add(config.Levels[0].Id);
            foreach (var r in config.Rows("SwordLevel").Where(r => r.Flag("default_unlocked"))) State.Realms.Add(r.Text("id"));
            // 开局不附带任何法术：第 1 关先靠普攻（裸开局 88 ÷ 25 ≈ 3.5 下杀一只标准怪），
            // 让玩家自己点「习得」花 30 灵石学御剑术——第一次花灵石换来"一支剑秒一只"的对比，
            // 比开局白送一个技能更能说明这个系统在干什么。数值锚点见 docs/design/balance_ttk.md。
            EnterLevel(config.Levels[0].Id);
        }
        // 教学期的第一句提示。开局**不会自动出手**（要点了「生根」才会），不写清楚的话
        // 玩家会盯着一动不动的角色，不知道这游戏要自己点。读过档的老玩家本来就没这句话。
        if (!AutoBasicUnlocked) Message = "点击画面挥剑。";
    }

    public void Step(double dt)
    {
        if (dt <= 0 || !double.IsFinite(dt)) return;
        Elapsed += dt;
        if (Battle.RespawnTimer > 0)
        {
            Battle.RespawnTimer = Math.Max(0, Battle.RespawnTimer - dt);
            // **整关重置放在这一刻，而不是死亡瞬间**：倒地那段路里怪、尸体、伤势都还在，
            // 玩家看到的是"倒在杀死它的那堆怪中间"，而不是一片空场景（见 Die 的说明）。
            // EnterLevel 自己会建新的 BattleState（回起点、满血、清刷怪与冷却）并清效果与增益。
            if (Battle.RespawnTimer == 0) { EnterLevel(Level.Id); _persist = true; }
            FinishStep(); return;
        }
        // 冷却流逝（**唯一的衰减点**）：攻速与 CDR **各管各的**，两者都不进伤害乘区。
        //   · 普攻键（不在 SwordSkill 表里的键）吃 `attack_speed`
        //   · 输出类法术、神通与剑灵的键吃 `skill_cdr`
        //   · **增益类法术两样都不吃**：若连增益一起加速，仙风云体术（15s 冷却 / 6s 持续）会在持续期内就转好，
        //     等于自己给自己减冷却，变成 100% 常驻；两个 15s 增益还会互相锁死。
        // 按 `dt × (1 + x)` 递减，等价于"间隔 ÷ (1 + x)"——所以 x 再大也不会出现零冷却。
        foreach (var key in Battle.Cooldowns.Keys.ToArray())
        {
            double factor = 1 + (Config.Skills.TryGetValue(key, out var cooling) && cooling.Kind == "buff"
                ? 0            // 增益类：不吃加速
                : Config.Skills.ContainsKey(key) ? SkillCdr : AttackSpeed);
            Battle.Cooldowns[key] = Math.Max(0, Battle.Cooldowns[key] - dt * factor);
        }
        // 自身增益（含伤害倍率窗）由 `BuffSystem` 统一推进：扣时间、到期摘掉、跑时间轴。
        // 见 `Features/Battle/Buffs.cs`。
        TickBuffs(dt);
        // ── 沙盒模式：**世界静止，只有战斗结算在跑** ──────────────────────────────
        // 下方整段（移动 / 激活格子 / 刷怪）跳过。它是技能预览那种"固定靶场"的唯一正确做法：
        // 从前靠 UI 侧打补丁（关普攻 + 把当前格标 `Passed` + 每步重钉靶子），
        // 被"近战停步 120 而靶子钉在 520"这种**正规改动的连带影响**击穿过一次——
        // 角色一路前进、走进新格就激活新刷怪点、当拍刷怪。显式模式才对后续改动免疫。
        if (!SandboxMode)
        {
            ActivateCell();
            Moving = !Battle.Enemies.Any(e => e.Hp > 0 && e.Kind != "rift" && Math.Abs(e.X - Battle.PlayerX) <= StopRange);
            // 裂隙解锁后停步，但前提是已经打得到它。裂隙位于格内 1450+360 处，而 BOSS 格
            // 阵亡重生点在格首 80：两者相距 1730，远超任一法术射程（950）。若解锁瞬间直接冻结，
            // 玩家会停在射程外空转，挂机永久中断（取决于最后一个非裂隙敌人倒下时玩家站在哪，
            // 故表现为「有概率」）。因此射程外解锁时必须继续沿格推进。
            if (Battle.Cell == Level.Cells - 1 && RiftUnlocked)
                Moving = !InRange(Battle.Enemies.FirstOrDefault(e => e.Hp > 0 && e.Kind == "rift"), AttackRange);
        }
        else Moving = false;
        if (Moving)
        {
            // 推进上限：**够得着的边界**，不是一堵写死的墙。
            //
            // 原来是"格内 1450 再往回 300"，而 BOSS（格内 1580）与裂隙（1810）都在它之外——**恒差 430 / 660**。
            // 远程（停步 640、射程 950）靠上面的停步判定本来就会先停住，所以一直没露馅；
            // 但**近战停步只有 120、射程 150**：角色一路走到上限、贴着它原地踏步，永远够不着——
            // BOSS 打不死、门也打不碎，整关卡死（两次实测反馈，第一版只特判了裂隙、没看见 BOSS 是同一个病）。
            //
            // 所以放宽到"场上每个该打的目标减去自己的停步距离"：**接近这件事由射程决定，不由边界决定**。
            // 用停步距离而不是射程，是因为那正是玩家**本来就会停下**的位置；配置里有不变量
            // `StopRange ≤ BasicAttackRange`，所以停在停步距离处必定已经在射程内。
            double limit = (Level.Cells - 1) * Config.Setting("cell_width") + SpawnOffset - 300;
            foreach (var e in Battle.Enemies)
                if (e.Hp > 0 && (e.Kind != "rift" || RiftUnlocked))
                    limit = Math.Max(limit, e.X - StopRange);
            Battle.PlayerX = Math.Min(limit, Battle.PlayerX + Attr("move_speed") * dt);
            ActivateCell();
        }
        if (!SandboxMode) TickSpawns(dt);
        TickEnemies(dt);
        TickBasicAttack();
        CastSkills();
        TickEffects(dt);
        Battle.Enemies.RemoveAll(e => e.Hp <= 0);
        // 同帧击杀与死亡：奖励已结算；优先执行复活，传送留到存活后。
        // 沙盒里死亡与通关一律不结算——靶场不该因为一次试验性爆发而"打穿关卡"。
        if (SandboxMode) { _persist = false; return; }
        if (Battle.PlayerHp <= 0) Die();
        else if (Battle.PortalDestroyed && RiftUnlocked) CompleteLevel();
        FinishStep();
    }

    private void FinishStep() { if (_persist) { _persist = false; PersistRequested?.Invoke(); } }
    // 表现层读取的增益剩余时间：只读，不参与任何战斗判定，界面据此绘制各增益光环。
    // 一律**按 kind 查在役的那一份**——`BuffSystem` 把它们统一收编了（从前是十几个散字段 +
    // 手写计时器，两边各 decay 一次）。查询本身就是"这一份在不在役"的判据。
    /// <summary>伤害倍率窗还剩多久（取所有在役窗里最长的那个）。</summary>
    public double BuffRemaining => _buffs.Values.Where(b => b.Def.DamageWindow).Select(b => b.Remaining).DefaultIfEmpty(0).Max();
    public double ShieldRemaining => Buff("shield")?.Remaining ?? 0;
    public double RegenRemaining => Buff("regen")?.Remaining ?? 0;
    public double LifestealRemaining => Buff("lifesteal")?.Remaining ?? 0;
    public double HasteRemaining => Buff("haste")?.Remaining ?? 0;
    public double CritBonusRemaining => Buff("crit_reduce")?.Remaining ?? 0;
    /// <summary>影分身剩余时间（剑二十三）。只读，供表现层决定要不要画那个半透明分身。</summary>
    public double MirrorRemaining => Buff("mirror")?.Remaining ?? 0;
    // 同一个口径的"**值**"（上面那批只有剩余时间）——GM 的「属性面板」要看"这一条现在是多少"。
    // 一律 `生效期外给中性值`（乘区给 1、加法给 0、绝对值给 0），于是面板不必自己判断
    // "这一条到底在不在生效"，直接把值摆出来就行。
    /// <summary>共享伤害倍率窗的**乘积**（DMG3 的 Build 乘区：声明了窗的增益各自写一格，按来源相乘）。
    /// **它乘的是结算伤害、不是攻击属性**——属性面板的动态列据它算"当前真正打出去多少"，
    /// 别误读成"攻击被改成了这个数"。</summary>
    public double BuffPower
    {
        get
        {
            double product = 1;
            foreach (var buff in _buffs.Values.Where(b => b.Def.DamageWindow)) product *= buff.WindowPower;
            return product;
        }
    }
    /// <summary>护盾当前吸收池（还剩多少能吸）。池子在施加那一刻按攻击力算定，此后被逐次扣减。</summary>
    public double ShieldAmount => Buff("shield")?.Value ?? 0;
    /// <summary>每秒回血比例（占气血上限）。</summary>
    public double RegenRate => Buff("regen")?.Value ?? 0;
    /// <summary>吸血比例（按造成的伤害折算）。</summary>
    public double LifestealFactor => Buff("lifesteal")?.Value ?? 0;
    /// <summary>影分身继承比例（剑二十三放出的那一份按它打折）。</summary>
    public double MirrorRatio => Buff("mirror")?.Value ?? 0;
    /// <summary>暴击后缩短的冷却秒数（醉仙望月步那条触发器的参数）。</summary>
    public double CritReduceSeconds => Buff("crit_reduce")?.Extra ?? 0;
    /// <summary>该法术**当前**的触发概率（含每次普攻累加的那部分）。自检用它断言累加与清零，不必靠概率碰运气。</summary>
    internal double TriggerChanceNow(string id) => Config.Skills.TryGetValue(id, out var s)
        ? Math.Min(1, s.TriggerChance + _triggerRamp.GetValueOrDefault(id)) : 0;
    /// <summary>该法术在指定等级下的**实际冷却**（增益类会随等级缩短，见 `BuffCooldown`）。自检用。</summary>
    internal double BuffCooldownNow(string id, int rank) => Config.Skills.TryGetValue(id, out var s) ? BuffCooldown(s, rank) : 0;
    /// <summary>
    /// 增益给的那一份加速（仙风云体术 = `1 + value`，未生效时 1）。面板的「攻速」那一条读它。
    /// 它**不改属性、也不进伤害乘区**——乘的是冷却流逝的速度。
    /// </summary>
    public double HasteFactor => Buff("haste") is { } haste ? 1 + haste.Value : 1;
    private double HasteBonus => HasteFactor - 1;
    /// <summary>
    /// **普攻攻速合计**（`fightattr.attack_speed` + 增益那一份），已按上下限夹取。
    /// 它只决定普攻多久出手一次：`普攻间隔 = basic_interval ÷ (1 + 它)`。
    /// **不进 DMG1、也不进 DMG3**——攻速改的是"每秒几下"，不是"每下多疼"；
    /// 两者混在一起的话，DPS 会随攻速平方增长，而玩家在面板上永远算不清自己为什么变强了。
    /// </summary>
    public double AttackSpeed => ClampAttr("attack_speed", Attr("attack_speed") + HasteBonus);
    /// <summary>
    /// **法术冷却缩减合计**（`fightattr.skill_cdr` + 增益那一份），已按上下限夹取。
    /// 它只决定法术多久能再放：`冷却 = cooldown ÷ (1 + 它)`。
    /// 用**除法**而不是"按秒数缩减"，所以它**不可能**把冷却压成 0 或负数——这也是 CDR 唯一需要的上限护栏
    /// （要更强的上限就改 `fightattr.skill_cdr` 的 `max_value`，不要再在别处写一个数）。
    /// 「暴击后缩短若干秒冷却」（醉仙望月步）是另一条路：它按**秒**扣，不受这里约束，
    /// 所以那个秒数必须小于它能砸到的最短冷却，见 `docs/design/combat.md`。
    /// </summary>
    public double SkillCdr => ClampAttr("skill_cdr", Attr("skill_cdr") + HasteBonus);
    /// <summary>暴击率加成（绝对值）：醉仙望月步生效期间提高，直接叠在配置的基础暴击率上。</summary>
    public double CritBonus => Buff("crit_reduce")?.Value ?? 0;
    /// <summary>
    /// 本次出手的**暴击率合计**（`fightattr.crit_rate` + 增益那一份），已按上下限夹取（上限 1）。
    /// 注意分工：暴击**判定**（"这一发是不是暴击"）属于 DMG2，暴击**倍率**（"暴击后是多少倍"）属于 DMG3。
    /// 两件事混在一起的话，"提高暴击率"和"提高暴击伤害"就没法各自调平衡了。
    /// </summary>
    private double CritRateTotal => ClampAttr("crit_rate", Attr("crit_rate") + CritBonus);
    /// <summary>清除玩家短时增益（伤害倍率窗 / 护盾 / 回血 / 吸血 / 攻速 / 暴击 / 影分身）。
    /// 关卡切换、死亡重生与技能预览切换时调用。清掉实例之后时间轴倒计时也一并归零——这是对的，
    /// 那些派生效果本来就该随增益一起消失。</summary>
    public void ClearBuffs()
    {
        _buffs.Clear();
        _triggerRamp.Clear();
    }
    private double SpawnOffset => Config.Rows("spawn_point")[0].Number("offset");
    private void ActivateCell()
    {
        Battle.Cell = Math.Min(Level.Cells - 1, (int)(Battle.PlayerX / Config.Setting("cell_width")));
        if (Battle.Spawns.ContainsKey(Battle.Cell)) return;
        Battle.Spawns[Battle.Cell] = new();
        if (Battle.Cell == Level.Cells - 1)
        {
            Spawn(Level.Boss, Battle.Cell, 130);
            Spawn(Level.Rift, Battle.Cell, 360);
            Message = "妖王现身。击败群妖，方可破开裂隙。";
        }
    }
    /// <summary>
    /// **调试用**：每波额外多刷几只（GM 面板加减，跨关卡保留但不落盘）。用来测"怪物多的时候技能够不够爽"，
    /// 而不必把 `wave.csv` 的正式数值改掉。额外那几只**从普通怪原型里随机挑**——所以它同时能把地/空、
    /// 快/慢、远/近的比例搅乱，正好用来观察各技能的覆盖情况。
    /// 上限 20 是防手滑：刷怪落点是 `40 + i×100` 连续铺开的，堆太多会溢到下一格（那只是画面难看，不影响判定）。
    /// </summary>
    public int WaveBonus
    {
        get => _waveBonus;
        set => _waveBonus = Math.Clamp(value, 0, 20);
    }
    private int _waveBonus;

    /// <summary>
    /// **调试用**：怪物血量 / 攻击的额外倍率（GM 面板加减，1 = 不额外缩放）。测 debuff 与范围技能的堆积效率时，
    /// 怪太脆会什么都看不出来——把它们调厚一点，看"打死一波要多久、debuff 有没有机会生效"。
    /// 与 <see cref="WaveBonus"/> 一样是**会话态**：不落盘、不碰 `monster.csv`；乘在关卡倍率与波次系数**之后**，
    /// 精英 / BOSS / 裂隙同样吃（它们也是"怪"）。
    /// 改它**立刻按比例缩放场上已有的怪**（保持血量百分比不变），不必等下一波。
    /// </summary>
    public double MonsterHpScale
    {
        get => _monsterHpScale;
        set
        {
            double next = Math.Clamp(value, .1, 100), ratio = next / _monsterHpScale;
            if (Math.Abs(ratio - 1) > 1e-9)
                foreach (var e in Battle.Enemies) { e.MaxHp *= ratio; e.Hp *= ratio; }
            _monsterHpScale = next;
        }
    }
    /// <summary>怪物攻击的额外倍率，语义与 <see cref="MonsterHpScale"/> 相同。</summary>
    public double MonsterAtkScale
    {
        get => _monsterAtkScale;
        set
        {
            double next = Math.Clamp(value, .1, 100), ratio = next / _monsterAtkScale;
            if (Math.Abs(ratio - 1) > 1e-9)
                foreach (var e in Battle.Enemies) e.Atk *= ratio;
            _monsterAtkScale = next;
        }
    }
    private double _monsterHpScale = 1, _monsterAtkScale = 1;

    /// <summary>
    /// **调试用**：主角无敌——`HurtPlayer` 直接返回，不掉血也不死亡。
    /// 为什么需要它：死亡会 `ClearBuffs` 并重置冷却，于是**伤害统计里各技能的周期会断掉**、每秒数字失真；
    /// 想量准就得先让自己站得住。与其它 GM 开关一样是会话态：不写配置、不落盘。
    /// 注意它会改变随机数的消耗——`HurtPlayer` 里那次闪避判定被跳过了，所以开关前后同 seed 的随机序列不再一致。
    /// </summary>
    public bool PlayerInvincible { get; set; }
    private void TickSpawns(double dt)
    {
        var wave = Config.Waves[Level.Wave];
        foreach (var (cell, spawn) in Battle.Spawns)
        {
            if (Battle.PlayerX > cell * Config.Setting("cell_width") + SpawnOffset) spawn.Passed = true;
            if (spawn.Passed || Battle.BossDefeated) continue;
            spawn.Timer -= dt;
            if (spawn.Timer > 0) continue;
            spawn.Timer += wave.Interval; spawn.Wave++;
            // 混编：一条波次可以配多种怪，序号 i 是跨模板连续的，两种怪不会落在同一个点上。
            var units = Config.WaveUnits[wave.Id];
            var counts = WaveCounts(wave);
            int i = 0;
            for (int u = 0; u < units.Count; u++)
                for (int n = 0; n < counts[u]; n++) Spawn(units[u].Monster, cell, 40 + i++ * 100);
            // 调试加成（GM 面板）：额外那几只从普通怪里随机挑，见 WaveBonus 的说明。
            for (int n = 0; n < _waveBonus; n++) Spawn(RandomNormalMonster(), cell, 40 + i++ * 100);
            if (spawn.Wave % wave.EliteEvery == 0) Spawn(wave.Elite, cell, 40 + i * 100);
        }
    }
    /// <summary>
    /// 波次只数的关卡倍率：随关卡递增，且递增本身在加快（线性一项 + 平方一项，两项都在 `game_settings` 可调）。
    /// </summary>
    public double WaveScale => WaveScaleFor(Config, Level.Order);

    /// <summary>
    /// 上面那条的**纯函数形态**：只吃配置与关卡序号，不需要一场战斗。
    /// 战斗探针（`Features/Battle/CombatProbe.cs`）要在**不建会话**的前提下算出同一批靶子的血量倍率，
    /// 而"再写一遍这个公式"就是第二把尺子——改了一边另一边静默不跟。
    /// </summary>
    internal static double WaveScaleFor(GameConfig config, int order) =>
        1 + (order - 1) * config.Setting("wave_growth") + Math.Pow(order - 1, 2) * config.Setting("wave_accel");
    /// <summary>
    /// 这一波各模板刷几只。**只数先按关卡算，再按比例摊给模板**——所以"两只小怪配一只肉盾"永远是这个配比，
    /// 数量涨起来不会把阵容比例改掉（旧写法是各模板各自乘一个关卡倍率，`count = 1` 的模板会跳变：
    /// 第 2 关 `round(1.33)` 还是 1、第 3 关直接跳到 2）。
    /// 只数 = `round(count × 关卡倍率)`，到 `count_max` 封顶。
    /// </summary>
    /// <summary>随机挑一个普通怪原型（波次调试加成用）。走会话自己的 `_random`，所以同 seed 仍可复现。</summary>
    private string RandomNormalMonster()
    {
        var normal = Config.Monsters.Values.Where(m => m.Kind == "normal").ToArray();
        return normal[_random.Next(normal.Length)].Id;
    }
    private int[] WaveCounts(WaveDef wave) => WaveCountsFor(wave, Config.WaveUnits[wave.Id], WaveScale);
    /// <summary>上面那条的纯函数形态：只吃波次、模板与关卡倍率，自检可以直接断言（不必造一场战斗）。</summary>
    internal static int[] WaveCountsFor(WaveDef wave, List<WaveUnitDef> units, double scale)
    {
        int total = Math.Min(wave.CountMax, (int)Math.Round(wave.Count * scale, MidpointRounding.AwayFromZero));
        int sum = units.Sum(u => u.Weight);
        var counts = units.Select(u => total * u.Weight / sum).ToArray();     // 先按比例向下取整
        // 余数按"被舍掉的小数部分"从大到小补一只，保证合计**恰好**等于 total（并列时按表内顺序，结果确定可复现）。
        foreach (int i in Enumerable.Range(0, units.Count)
            .OrderByDescending(i => total * units[i].Weight % sum).ThenBy(i => i)
            .Take(total - counts.Sum()))
            counts[i]++;
        return counts;
    }

    /// <summary>
    /// 按模板 + 关卡与波次倍率造一只敌人生到场上。**战斗探针复用这一个**（`internal` 而非 `private`）——
    /// 它要摆的靶子必须和真实刷怪**逐条同源**：`Kind` 分流、两重倍率、`X` 的算法。
    /// 探针那边自己写一遍"血量 = monster.hp × level.hp_scale"，改了一边就会静默分叉，
    /// 而对账表看上去只会变成"模型就是比实测低一点"。
    /// </summary>
    internal void Spawn(string monsterId, int cell, double offset)
    {
        var m = Config.Monsters[monsterId];
        // 普通怪吃两重倍率：关卡倍率（level.csv，随关卡递增）× 波次强度系数（wave.csv，同一关内的波次梯度）。
        // 精英 / BOSS / 裂隙只吃关卡倍率——它们是关卡节点而非波次阵容的一部分，不该被"这波更硬"影响。
        var wave = Config.Waves[Level.Wave];
        var (hp, atk) = m.Kind switch { "boss" => (Level.BossHp, Level.BossAtk), "elite" => (Level.EliteHp, Level.EliteAtk), "rift" => (Level.RiftHp, 0d), _ => (Level.HpScale * wave.HpScale, Level.AtkScale * wave.AtkScale) };
        // 最后的 `MonsterHpScale` / `MonsterAtkScale` 是 GM 的调试倍率（默认 1），乘在所有关卡与波次倍率**之后**。
        hp *= _monsterHpScale; atk *= _monsterAtkScale;
        Battle.Enemies.Add(new() { Id = Battle.NextEnemyId++, MonsterId = m.Id, Kind = m.Kind,
            X = cell * Config.Setting("cell_width") + SpawnOffset + offset,
            Hp = m.Hp * hp, MaxHp = m.Hp * hp, Atk = m.Atk * atk, AttackTimer = m.Interval });
    }
    private void TickEnemies(double dt)
    {
        foreach (var e in Battle.Enemies.Where(e => e.Hp > 0 && e.Kind != "rift"))
        {
            var m = Config.Monsters[e.MonsterId];
            // 状态计时（眩晕 / 减速 / 寒冷 / 易伤）：归零即摘掉那一份，"到期"与"从未有过"因此是同一种表示。
            // 灼烧不在这里——它由下面那段单独推进，理由见 `TickDot`。
            e.TickStatuses(dt);
            if (e.StunUntil > 0) continue; // 眩晕：无法移动与攻击
            double distance = Math.Abs(e.X - Battle.PlayerX);
            double speed = m.Speed * (e.SlowUntil > 0 ? e.SlowFactor : 1);
            if (distance > m.Range)
                e.X += Math.Sign(Battle.PlayerX - e.X) * Math.Min(speed * dt, distance - m.Range);
            e.AttackTimer -= dt;
            if (Math.Abs(e.X - Battle.PlayerX) > m.Range + 1 || e.AttackTimer > 0) continue;
            e.AttackTimer = m.Interval;
            if (m.Attack == "melee") HurtPlayer(e.Atk);
            else Effects.Add(new() { Kind = m.Attack == "magic" ? "target" : "projectile", X = e.X, Life = m.Attack == "magic" ? .7 : 5, Damage = e.Atk, Hostile = true });
        }
        // 灼烧持续伤害：对非裂隙存活敌人按秒结算，不受眩晕影响。**位置与顺序是 DMG2 口径的一部分**——
        // 它排在敌人行动之后，且"这一帧开局还着着火就跳一次"（先判定再扣时间），与从前的写法逐字一致。
        // 来源记在敌人身上（`DotSkill`）——跳伤这条路上没有 `CombatEffect` 可查，只能施加时先记下来。
        foreach (var e in Battle.Enemies.Where(e => e.Hp > 0 && e.Kind != "rift" && e.DotUntil > 0).ToArray())
            if (e.TickDot(dt, out double dot, out string from)) HurtEnemy(e, dot, from);
    }
    /// <summary>
    /// 按技能累计的伤害账本（GM 面板的「伤害统计」读它）。**纯运行时**：与 `Effects` / `Enemies` 同性质，不落盘。
    /// 口径与归因规则见 <see cref="DamageTally"/>。
    /// </summary>
    public DamageTally DamageStats { get; } = new();
    /// <summary>统计时长（自上一次重置起的模拟秒数）。会话的 `Elapsed` 从不重置，所以分母由账本自己记。</summary>
    public double DamageStatsSeconds => DamageStats.Seconds(Elapsed);
    /// <summary>清空伤害账本，并把统计起点挪到此刻。</summary>
    public void ResetDamageStats() => DamageStats.Reset(Elapsed);
    private bool Legal(EnemyState e) => e.Hp > 0 && (e.Kind != "rift" || RiftUnlocked);
    /// <summary>怪物所在层。层从模板推导而不是存进 `EnemyState`：存档格式一个字节都不用改，老存档的 MonsterId 照样能定位。</summary>
    private bool Flying(EnemyState e) => Config.Monsters[e.MonsterId].Layer == "air";
    /// <summary>技能定位与敌人所在层的匹配。空 / both 打两者，ground 只打地面，air 只打空中。</summary>
    private bool LayerHit(string hits, EnemyState e) => hits switch
    {
        "ground" => !Flying(e),
        "air" => Flying(e),
        _ => true,
    };
    /// <summary>能成为这一发的合法目标：目标本身合法，且层数对得上。地面招不会去锁飞行单位。</summary>
    private bool Legal(EnemyState e, string hits) => Legal(e) && LayerHit(hits, e);
    private bool InRange(EnemyState? e, double range) => e is not null && Math.Abs(e.X - Battle.PlayerX) <= range;
    /// <summary>最近的合法目标。hits 为空表示不限层（普攻、剑灵弹、召唤弹都走这一档）。</summary>
    private EnemyState? Target(double range, string hits = "") => Battle.Enemies.Where(e => Legal(e, hits) && Math.Abs(e.X - Battle.PlayerX) <= range).OrderBy(e => Math.Abs(e.X - Battle.PlayerX)).FirstOrDefault();

    private void CastSkills()
    {
        if (Battle.PlayerHp <= 0) return;
        foreach (var (id, rank) in State.Skills)
        {
            if (rank <= 0 || !Ready(id)) continue;
            var skill = Config.Skills[id];
            // 神通（trigger_chance > 0）不在冷却到点时自动释放，改为普攻出手时按概率触发，见 TickBasicAttack。
            if (skill.TriggerChance > 0) continue;
            if (Release(skill, SkillPower(skill, rank), rank)) Battle.Cooldowns[id] = BuffCooldown(skill, rank);   // 没有合法目标时不空放、也不消耗冷却
        }
        foreach (var pet in State.EquippedPets)
        {
            var row = Config.Row("PetSkill", Config.Row("Pet", pet).Text("skill_id"));
            var target = Target(row.Number("range"));
            if (target is null || !Ready(pet)) continue;
            var bonus = State.PetBuffs.GetValueOrDefault(pet, []).Sum(id => Config.Row("PetEquip", id).Number("power"));
            // 剑灵弹与法术同一条 DMG1：它的"技能倍率"是 `PetSkill.power × (1 + 增强 buff)`。
            Launch(row.Text("kind"), target, DamageFormula.Dmg1(Attack, row.Number("power") * (1 + bonus), 0), .5, skill: row.Text("id"));
            Battle.Cooldowns[pet] = row.Number("cooldown");
        }
    }
    /// <summary>
    /// 法术的 **SkillRate**（文档口径里的那个；代码里叫 Power 是因为它读的是效果行的 `power`）：
    /// `配置 power × (1 + 技能等级加成)`。普攻不走这里（它用 `fightattr.basic_power`）。
    ///
    /// 它是**技能自身的永久成长**：等级是"这一式被培养到什么程度"，属于技能定义，
    /// 所以落在 **DMG1** 的 `SkillRate` 上；而"这一发因为目标残血/带着状态/某个 BD 条件而被放大多少"
    /// 落在 **DMG3** 的 Build 乘区。两者的分界见 `docs/design/combat.md`——混在一起就会出现
    /// "同一个技能在不同 BD 下的定义都不一样"，任何数值对照都失去意义。
    ///
    /// 每级的加成走 `game_settings.skill_level_bonus` 而不是硬编码——技能页的「每级 +X%」提示读的是同一个值，
    /// 两边必须同源，否则提示会撒谎。
    /// </summary>
    private double SkillPower(SkillDef skill, int rank) =>
        skill.Power * (1 + Config.Setting("skill_level_bonus") * (rank - 1));

    /// <summary>
    /// 增益类法术的**实际冷却**：每升一级缩短 `buff_cooldown_per_level`，但**下限是持续时长 × `buff_cooldown_floor_ratio`**。
    /// 增益的峰值强度不随等级变（它的效果走 Buff 的 `value`，那是个定值），所以若升级什么都不给，
    /// 玩家花灵石点「强化」就什么都没发生——仙风云体术与醉仙望月步原本就是这种零收益。
    /// 改成缩冷却之后，成长体现在**覆盖率**上，而峰值不变（「小妖档不该给满」那条口径因此保住）。
    /// 下限把覆盖率封在 80%，避免它变成常驻——与「攻速不加速增益类法术」是同一条护栏。
    /// 非增益类返回配置冷却，行为不变。
    /// </summary>
    private double BuffCooldown(SkillDef skill, int rank)
    {
        if (skill.Kind != "buff") return skill.Cooldown;
        double floor = skill.Duration * Config.Setting("buff_cooldown_floor_ratio");
        return Math.Max(floor, skill.Cooldown * (1 - Config.Setting("buff_cooldown_per_level") * (rank - 1)));
    }

    /// <summary>
    /// **释放这个法术**（Skill 层的动作）。返回 false 表示场上没有合法目标、这一手没有放出去，
    /// 调用方据此决定不写冷却。
    ///
    /// 三条路都收口在这里，一次施法只走一条：
    /// <list type="number">
    /// <item><b>增益类</b>：施加自身增益（它没有攻击效果，见 `ApplySelfBuff`）。</item>
    /// <item><b>其余</b>：把技能的主效果交给 `ExecuteEffect` —— 形态、弹数、范围全部从效果行读。</item>
    /// <item><b>事件触发</b>：本体这一手放出去之后再跑一遍 `on_skill_cast` 触发器（影分身就是这样复制的）。</item>
    /// </list>
    /// 顺序不能动：触发（分身那一份）在**本体之后、定身之前**——镜像不写冷却、也不重复结算定身。
    /// </summary>
    private bool Release(SkillDef skill, double power, int rank)
    {
        if (skill.Kind == "buff")
        {
            // 增益以自身为目标，仅在交战时触发，避免无敌人时浪费冷却。
            if (Target(skill.Range) is null) return false;
            ApplySelfBuff(skill, power, rank);
        }
        else
        {
            if (skill.Effects.Count == 0) return false;
            if (!ExecuteEffect(skill.Effects[0], new EffectContext(skill.Id, power, rank))) return false;
        }
        // 影分身（剑二十三）：本体这一手放出去之后，分身同步再放一份，伤害按继承比例。
        // 复制走的是 `MirrorCast`（"发出这一式"而不是"释放这一式"），所以镜像不写冷却、
        // 不会多发一次释放音（TrackSkillCasts 只认"冷却 0 → 正"的边沿），也不会把定身又结算一遍。
        FireTriggers("on_skill_cast", new EffectContext(skill.Id, power, rank));
        // 施放瞬间的全屏定身。放在这里而不是 Launch 里：一次施法只结算一次；
        // 定身先于剑气出手，配置把 hover_time 也配成同长，读起来就是"定住一秒再放剑气"。
        // 复用 StunUntil，于是"无法移动与攻击"与表现层的眩晕电弧都是现成的，不必另加状态。
        // 排除裂隙：它本来就不移动、不攻击，定身对它没有意义；而 TickEnemies 只在非裂隙敌人上衰减状态，
        // 给它挂上 StunUntil 会永远减不掉，表现层会一直画着一圈电弧。
        if (skill.CastRoot > 0)
            foreach (var e in Battle.Enemies.Where(e => e.Hp > 0 && e.Kind != "rift"))
                e.StunUntil = Math.Max(e.StunUntil, skill.CastRoot);
        return true;
    }

    /// <summary>
    /// 无视冷却与触发概率，立刻释放一次指定法术。技能预览与自检专用，不参与正常战斗流程。
    /// 照常写冷却：释放音是靠"冷却 0 → 正"的边沿触发的（见 UI/Audio.cs 的 TrackSkillCasts），
    /// 这里不写的话神通在预览里出手了却没有声音，而那时唯一出得了手的就只剩它。
    /// </summary>
    internal bool ForceRelease(string id)
    {
        if (!Config.Skills.TryGetValue(id, out var skill)) return false;
        int rank = State.Skills.GetValueOrDefault(id);
        if (!Release(skill, SkillPower(skill, rank), rank)) return false;
        Battle.Cooldowns[id] = skill.Cooldown;
        return true;
    }

    /// <summary>
    /// 自动普攻：每 `basic_interval` 秒向最近的合法目标出手，并在出手的这一刻摇各神通的触发概率。
    /// 没有合法目标时不空放、也不消耗间隔，与法术同口径。
    /// </summary>
    private void TickBasicAttack() { if (BasicAttackEnabled && AutoBasicUnlocked) TryBasicAttack(); }

    /// <summary>
    /// 手动普攻（点击画面触发）。**与自动普攻共用同一条判定、同一个冷却键**——
    /// 点击只是"自己扣扳机"，不会凭空多出输出：点快了就是白点。这个口径是刻意的，
    /// 「激活自动攻击」的价值在"不用再点"，不在"打得更快"。返回是否真的挥出去了，供表现层给反馈。
    /// </summary>
    public bool ManualBasicAttack() => TryBasicAttack();

    /// <summary>
    /// 普攻的**唯一结算点**：冷却就绪 + 射程内有合法目标才出手。手动与自动都走这里，
    /// 于是暴击、增益倍率、吸血与伤害统计**不可能只覆盖其中一条路径**（分两条写一定会漏掉一条）。
    ///
    /// 形态按 <see cref="MeleeBasic"/> 分两种，但**都走 `Launch("projectile", …)`**：
    /// 近战只是射程短、并带一个 `melee_slash` 形态标记（表现层据此画挥砍而不是飞剑），
    /// 不另开"瞬时命中"的捷径——`Launch` 里带着全游戏唯一的暴击判定与增益乘区，绕开它就会静默丢暴击。
    /// Skill 传空串，于是技能名标签、形态参数回退、以及「效果来源必须是已知法术」的自检都不受影响。
    /// </summary>
    private bool TryBasicAttack()
    {
        if (Battle.PlayerHp <= 0 || !Ready(BasicAttackKey)) return false;
        var target = Target(BasicAttackRange);
        if (target is null) return false;
        // 普攻间隔从 `basic_interval` 起步，此后每步按攻速加速（见 Step 的冷却流逝）——
        // 即 `实际间隔 = basic_interval ÷ (1 + attack_speed)`。这里写的是**未加速的原值**，
        // 与法术写 `cooldown` 是同一条口径：冷却值本身不含倍率，倍率只作用在流逝速度上。
        Battle.Cooldowns[BasicAttackKey] = Attr("basic_interval");
        // 普攻的 `SkillRate` 就是 `fightattr.basic_power`，`SkillFlat` 为 0——它与法术走同一份 DMG1。
        Launch("projectile", target, DamageFormula.Dmg1(Attack, Attr("basic_power"), 0),
            MeleeBasic ? MeleeSwingLife : 4, skill: "",
            trajectory: MeleeBasic ? "melee_slash" : "");
        RollTriggerSkills();
        return true;
    }

    /// <summary>
    /// 神通触发：普攻出手时，对每个「已习得、当前冷却就绪、且配了触发概率」的法术各摇一次。
    /// 冷却未就绪的不摇——省下一次随机数，也让「冷却就绪才可能触发」这句话成立。
    /// 冷却对神通而言是「最短触发间隔」：实际间隔由触发概率主导（5% 每次普攻 ≈ 平均 20 秒一次）。
    /// </summary>
    private void RollTriggerSkills()
    {
        foreach (var (id, rank) in State.Skills)
        {
            if (rank <= 0 || !Ready(id) || !Config.Skills.TryGetValue(id, out var skill) || skill.TriggerChance <= 0) continue;
            // 概率可以是"每次普攻累加"的形态（`trigger_chance_step > 0`）：起步低、越打越近，摇中后回到起步值。
            // 它既不会"冷却一好就放"（起步只有 15%），也不会连十几次摇不中——把"看脸"换成"越打越近"。
            // `step = 0` 时这一路退化成原来的固定概率，行为不变。
            double acc = _triggerRamp.GetValueOrDefault(id);
            if (_random.NextDouble() >= Math.Min(1, skill.TriggerChance + acc))
            {
                _triggerRamp[id] = acc + skill.TriggerChanceStep;
                continue;
            }
            _triggerRamp[id] = 0;
            if (Release(skill, SkillPower(skill, rank), rank)) Battle.Cooldowns[id] = skill.Cooldown;
        }
    }

    private bool Ready(string key) => Battle.Cooldowns.GetValueOrDefault(key) <= 0;
    /// <summary>
    /// 弹群编排：数量、落点/排列间距与错时都在这里决定，返回是否真的出手了（false 表示没有合法目标）。
    /// </summary>
    private bool CastVolley(SkillDef skill, EffectDef fx, double dmg1, bool mirrored = false)
    {
        // 形态与弹数读**效果行**（`SkillDef` 上的那几个字段本来就是它的派生视图）：
        // 这样"一次出手怎么飞、放几支"的真相源只有一个。
        int count = skill.Kind == "projectile" ? fx.ProjectileCount : 1;
        if (fx.Trajectory == "sky_drop")
        {
            // 固定剑阵：阵心取选中的合法敌人，各支按 spread 在阵心两侧铺开，**与敌人数无关**——
            // 只有一个敌人时不会缩成一束。落点由 Core 算进 X，因为它直接参与落地判定。
            // 各锁一敌（`band > 0`，18 苍穹剑陨）：每支**各锁一个（尽量不同的）目标、落在它当时的位置爆炸**，
            // 目标中途死了也照炸那个位置（"击中谁是谁"，与火海同一条契约）。天上的黑洞则以**目标群的中轴**为心
            // 铺开 `band` 宽、再夹进画面——**锚在目标上而不是角色上**：角色每秒走 340，一发 1.3 秒的轰炸若从
            // 角色量起，落点会甩到身后，一整排剑全落在空地上。敌人不足时 `Picks` 会循环重复，所以打 BOSS
            // 就是 count 支全砸在它身上；聚怪把怪捏成一簇时，count 支落在同一片、爆炸叠起来。
            if (fx.Band > 0)
            {
                var volley = Picks(skill, count);
                if (volley.Count == 0) return false;
                double mid = volley.Average(e => e.X);
                // 夹取：角色锚点在屏幕 330、逻辑画布 1920，所以黑洞整条带子要落在 [玩家X + 60, 玩家X + 1520] 内。
                double near = Math.Clamp(mid - fx.Band / 2, Battle.PlayerX + 60, Battle.PlayerX + 1520 - fx.Band);
                for (int i = 0; i < count; i++)
                {
                    double slot = count == 1 ? .5 : i / (double)(count - 1);
                    LaunchSkill(skill, fx, volley[i % volley.Count], dmg1, i, 0, mirrored, near + slot * fx.Band);
                }
                return true;
            }
            var center = Pick(skill);
            if (center is null) return false;
            for (int i = 0; i < count; i++) LaunchSkill(skill, fx, center, dmg1, i, (i - (count - 1) / 2.0) * fx.Spread, mirrored);
            return true;
        }
        // 其余形态各自选敌、尽量不重复（敌人不足才循环重复）。
        var picks = Picks(skill, count);
        if (picks.Count == 0) return false;
        for (int i = 0; i < picks.Count; i++) LaunchSkill(skill, fx, picks[i], dmg1, i, 0, mirrored);
        return true;
    }

    /// <summary>
    /// 选敌。默认最近的合法目标（受射程限制），即法术一直以来的行为；
    /// `highest_hp` = 全场血量最高者、**无视射程**（斩鬼神）；
    /// `lowest_hp` = **射程内**血量最低者（青元剑芒的收割，与前者不同：它仍受射程约束）。
    /// 同血量 / 同距离按 Id 升序，自检才有确定结果。
    /// </summary>
    private EnemyState? Pick(SkillDef skill) => skill.Targeting switch
    {
        "highest_hp" => Battle.Enemies.Where(e => Legal(e, skill.Hits)).OrderByDescending(e => e.Hp).ThenBy(e => e.Id).FirstOrDefault(),
        "lowest_hp" => Targets(skill.Range, 1, skill.Hits, "lowest_hp").FirstOrDefault(),
        // 取射程内**最靠前**的那只（X 最大）。给"在地上留一片持久灼烧"的技能用：怪从右边来，
        // 那条路是必经之路——落在最靠前的位置等于铺在"迎宾位"，后面进来的都要穿过去；
        // 落在最近敌人身上等于落在脚边，怪几乎立刻就走过它了，收益最低（焚天剑诀的火海尤其吃亏）。
        "farthest" => Battle.Enemies.Where(e => Legal(e, skill.Hits) && Math.Abs(e.X - Battle.PlayerX) <= skill.Range)
            .OrderByDescending(e => e.X).ThenBy(e => e.Id).FirstOrDefault(),
        _ => Target(skill.Range, skill.Hits),
    };

    /// <summary>
    /// 多发取目标。`highest_hp` 取全场血量最高的前 n 个（不重复，斩鬼神用）；
    /// `lowest_hp` 取**射程内**血量最低的前 n 个，**敌人不足时循环重复打同一个**——
    /// 青元剑芒因此"场上只有一只时三段全打在它身上"，打 BOSS 不吃亏。
    /// 将来要"依次出现多个剑光"时这里天然支持，只需放宽"非 projectile 只能单发"那条校验。
    /// </summary>
    private List<EnemyState> Picks(SkillDef skill, int n) => skill.Targeting switch
    {
        "highest_hp" => Battle.Enemies.Where(e => Legal(e, skill.Hits)).OrderByDescending(e => e.Hp).ThenBy(e => e.Id).Take(n).ToList(),
        "lowest_hp" => TargetsWithRepeats(skill.Range, n, skill.Hits, "lowest_hp"),
        "farthest" => TargetsWithRepeats(skill.Range, n, skill.Hits, "farthest"),
        _ => TargetsWithRepeats(skill.Range, n, skill.Hits),
    };
    // ThenBy(Id) 只为同 X 多敌时排序确定：自检常把多个敌人重叠放到同一坐标。
    // order 由 `SwordSkill.targeting` 传进来：空 = **离玩家最近**（默认口径）、`lowest_hp` = 血量升序（收割）、
    // `farthest` = 最靠前（给"在地上留一片持久灼烧"的技能用，见 `Pick`）。
    private List<EnemyState> Targets(double range, int n, string hits = "", string order = "")
    {
        var inRange = Battle.Enemies.Where(e => Legal(e, hits) && Math.Abs(e.X - Battle.PlayerX) <= range);
        var ordered = order switch
        {
            "lowest_hp" => inRange.OrderBy(e => e.Hp).ThenBy(e => e.Id),
            "farthest" => inRange.OrderByDescending(e => e.X).ThenBy(e => e.Id),
            _ => inRange.OrderBy(e => Math.Abs(e.X - Battle.PlayerX)).ThenBy(e => e.Id),
        };
        return ordered.Take(n).ToList();
    }
    /// <summary>多发弹群选敌：先取 n 个不同敌人（各自选敌、尽量不重复）；敌人不足时才按序循环重复打同一个。</summary>
    private List<EnemyState> TargetsWithRepeats(double range, int n, string hits = "", string order = "")
    {
        var distinct = Targets(range, n, hits, order);
        if (distinct.Count == 0) return [];
        var picks = new List<EnemyState>(n);
        for (int i = 0; i < n; i++) picks.Add(distinct[i % distinct.Count]);
        return picks;
    }
    // 影分身在本体**身后**的站位偏移（逻辑单位）。玩家朝右推进，所以身后是更小的 X。
    private const double MirrorOffset = -110;
    // 分身那一式比本体晚多久出现。要短到仍读作"同一式跟着放"，又长到不会被看成同一发——
    // 少了它就永远是同帧落地，画面上糊成一团（尤其是 target / ground / sky_drop 那三类没有发射点的形态）。
    public const double MirrorDelay = .18;
    // 目标中途死亡时，"落点重判定"的搜索半径（逻辑单位，约一个身位）。
    // 追踪弹与定点弹在**发射时**锁定目标，而目标很可能在弹丸飞到之前就被别的技能打死——
    // 这时若什么都不做，这一发就白飞了（青元剑芒专挑残血，于是它系统性白飞）。
    // 补救只在**落点附近**挑一个合法敌人结算，**不改追远处**：改追会让多发齐射收敛到同一只
    // （Picks 刻意"尽量不重复"就是为了铺开），剑气也会明显拐弯打远处的怪。
    private const double FallbackRadius = 80;
    // "各锁一敌"的剑陨落地后留下的**爆炸余韵**时长（秒）。它只有寿命、不造成伤害（`Damage = 0`），
    // 纯粹是给表现层一个 0→1 的进度去画扩散的剑气爆炸——否则效果在落地当帧就被回收，画不出"炸开"。
    // 定得比 ground 的 0.6 秒结算间隔短，于是它连一次结算都不会走到。
    public const double BurstLife = .3;
    // 追踪弹最多改换几次目标。到顶之后即使落点附近还有人也不再接力，弹丸自然消散——
    // 否则一只都追不到时它会一段接一段地追下去，永远不消失。**索不到敌就是本次伤害丢失**，这是有意的。
    private const int MaxReacquire = 2;

    private void LaunchSkill(SkillDef skill, EffectDef fx, EnemyState target, double dmg1, int index, double lane = 0, bool mirrored = false, double spawnX = 0)
    {
        // 随机量在这里一次摇定，表现层只读结果：逐帧重摇会让弧线/高度抖动，也让自检无法复现。
        // 弧度只有弧线形态抽；Jitter 是 0..1 的通用抖动，由表现层按形态解释（天降形态拿它做出生高度）。
        double arc = fx.Trajectory == "arc_homing"
            ? fx.ArcMin + _random.NextDouble() * (fx.ArcMax - fx.ArcMin) : 0;
        double jitter = _random.NextDouble();
        // 弹群错时：第 i 支额外延迟 i × interval，再叠加 ±jitter 的随机；与 hover_time 一起折算成"起飞前停留"。
        double delay = Math.Max(0, index * fx.VolleyInterval + (_random.NextDouble() * 2 - 1) * fx.VolleyJitter);
        // 命中时要挂的状态（灼烧 / 寒冷 / 麻痹）取**效果行引用的那份 Buff**——它才是强度与寿命的真相源，
        // 不再从技能行上抄三个数。斩杀 / 利用状态不是状态，是"这一笔遇到的条件"（DMG3 的 Build 乘区），
        // 所以它们从效果行的 `condition` 走，两者都映射回旧口径的 `secondary` 名。
        // ⚠️ 寿命传的是 `delay + duration`——**旧表的 `duration` 一列正是这两个之和**（定点类的"延迟"
        // 与天降类的"下坠时长"曾经共用同一列）。拆成两列之后若只传 `duration`，定点类（斩鬼神 / 御雷真诀）
        // 的延迟会变成 0：出手当帧就落地，大招变成瞬发。视图的 `SkillDef.Duration` 同样是这两者之和。
        Launch(skill.Kind, target, dmg1, fx.Delay + fx.Duration, true,
            fx.Buff, fx.Condition, fx.ConditionValue, fx.AoeRadius, skill.Id,
            fx.Trajectory, index, fx.HoverTime + delay, arc, fx.Speed > 0 ? fx.Speed : 1500, lane, jitter,
            fx.PierceChance, fx.Knockback, fx.AoeAll, skill.Hits, mirrored, fx.Gather, spawnX, fx.Band,
            // 周期结算的间隔跟着这一式的效果行走（地面力场 / 天降火海），不再读全局设置。
            fx.TickInterval);
    }
    /// <param name="dmg1">这一发的 **DMG1**（原始伤害），不许在这里再乘任何条件倍率。</param>
    /// <param name="attackerSnapshot">
    /// 为 true 表示**这一发是一次新的 Attack Event**：现在摇暴击、并把共享伤害倍率窗定格成快照。
    /// 为 false 表示它是某个已有 Attack Event **派生的**结算（目前只有召唤物射击），
    /// 攻击者侧已经由 <paramref name="inherits"/> 给定，这里不再重摇——重摇会让一次召唤的十几发
    /// 各自碰运气，而"召唤那一手"才是真正的出手。
    /// </param>
    /// <param name="inherits">派生结算要继承的那一发（出手快照的来源）。</param>
    /// <param name="buff">命中时要挂的状态（`SkillEffect.buff_id` 解析后的定义）；null 表示不挂。</param>
    /// <param name="condition">命中时的条件型倍率。斩杀与利用状态**不在这里结算**，见 `Hit`。</param>
    private void Launch(string kind, EnemyState target, double dmg1, double duration, bool attackerSnapshot = true,
        BuffDef? buff = null, string condition = "", double conditionValue = 0, double aoeRadius = 0, string skill = "",
        string trajectory = "", int index = 0, double hold = 0, double arc = 0, double speed = 1500, double lane = 0, double jitter = 0,
        double pierceChance = 0, double knockback = 0, bool aoeAll = false, string layer = "", bool mirrored = false, double gather = 0, double spawnX = 0, double band = 0,
        double tickInterval = 0, CombatEffect? inherits = null)
    {
        // ── 出手快照 ──────────────────────────────────────────────────────────────
        // 暴击判定**全游戏只有这一处**，逐弹丸各摇一次；摇中的结果与"这一刻的共享伤害倍率窗"
        // 一起写进效果，此后由它派生的一切结算（同一弹丸的其余命中、地面场每跳、天降落点、灼烧每跳）
        // **共享这份快照，不重新摇**。为什么这么定：期望值不变（线性期望），但一次出手的账目与观感对得上——
        // 否则同一发火海会一跳暴击一跳不暴击，玩家看到的是同一个技能在随机翻倍。
        // 口径见 docs/design/combat.md 的「出手快照」。
        //
        // 抽签消耗 _random，会让后续随机序列偏移（同 seed 仍可复现）——**顺序不能动**：
        // 这一行以前也是"先摇暴击、再按需缩冷却"，调换之后所有依赖 seed 的自检都会换一套数字。
        bool critical = inherits?.Critical ?? (attackerSnapshot && _random.NextDouble() < CritRateTotal);
        // "暴击缩短一个随机技能的冷却"（醉仙望月步）按**每次施法**最多触发一次：多发齐射是 3～5 支各摇一次暴击的，
        // 若每支都触发，实际触发密度会放大数倍（实测能把增益冷却吃到覆盖率自涨）。由第一支负责（index == 0）。
        // 另外三类不参与：普攻（skill 为空，它每秒一次，若也算则增益覆盖率自涨）、影分身那一份
        // （不是玩家亲手放的那一手，否则分身窗口内缩冷却凭空翻倍）、派生结算（`inherits` 非空，它没有自己的出手）。
        // 与"增益类法术不吃攻速/CDR"是同一条口径：缩冷却只挂在**玩家亲手放的法术出手**上。
        // 触发之后**具体做什么**由 `SkillBuffTrigger.csv` 说了算（`buff_crit` 的 `on_crit` 指向缩冷却那条效果），
        // 这四条护栏留在调用侧——它们是"什么时候允许触发"，不是"触发做什么"。
        if (critical && attackerSnapshot && inherits is null && index == 0 && skill != "" && !mirrored)
            FireTriggers("on_crit", new EffectContext(skill));
        // 暴击倍率与共享倍率窗跟着快照走：派生结算直接继承，不重算、也不与当下生效的增益串味。
        double critDamage = inherits?.CritDamage ?? Attr("crit_damage");
        // 出手快照取的是**这一刻的窗乘积**；派生结算（继承）直接沿用那一发的快照，不重读当下的窗。
        double buffWindow = inherits?.BuffPower ?? (attackerSnapshot ? BuffPower : 1);
        // 生命期分两种：普通弹道沿用硬编码 4 秒——召唤弹传 2、剑灵弹只传 0.5，
        // 若把 duration 当通用寿命会缩短剑灵弹丸、使其飞不到目标；自定义飞行形态才用 duration（本列对该形态即飞行/下坠时长）。
        // 自定义形态还要加上起飞前停留（hold）：停留与飞行各自计时，落地/命中的时刻才会随错时而变化。
        // `melee_slash` 是**普攻专用**的内部形态（近战挥砍）：不走配置表，所以不在 SwordSkill 的 trajectory 枚举里。
        // 它借这套自定义形态的账，只为拿到"寿命 = duration"这一条——挥砍要的是"结算完还留一小段余韵可以画"。
        bool customFlight = trajectory is "hover_homing" or "sky_drop" or "arc_homing" or "line_pierce" or "line_shot" or "melee_slash";
        double life = kind != "projectile" ? duration
            : customFlight ? duration + hold
            : 4;
        // 起始 X：天降形态生成在目标上空（lane 是剑阵里的落点偏移，落点由 Core 定、不交给表现层），此后冻结；
        // 其余弹道从玩家处出发。
        // 影分身那一份从**分身**身上出发（本体身后 MirrorOffset）。target / ground / sky_drop 三类的起点本就是目标的 X，
        // 分身一式因此与本体落点完全重合——这正是"分身同放一式"该有的样子，不需要偏移。
        double startX = kind is "ground" or "target" || trajectory == "sky_drop"
            ? target.X + lane
            : Battle.PlayerX + (mirrored ? MirrorOffset : 0);
        Effects.Add(new() { Kind = kind, X = startX, LegX = startX,
            // 出生点只给表现层用（生成带模式下的黑洞与斜落轨迹）；为 0 表示与 X 相同。
            SpawnX = spawnX,
            Target = target.Id, TargetX = target.X, Damage = dmg1, Life = life, MaxLife = life,
            Critical = critical, CritDamage = critDamage, BuffPower = buffWindow,
            // 自定义形态复用 Timer 作"起飞前停留"倒计时：TickEffects 每步已经 Timer -= dt，停留期只读 Timer > 0。
            Timer = customFlight ? hold : 0,
            Skill = skill, Buff = buff, Condition = condition, ConditionValue = conditionValue,
            AoeRadius = aoeRadius, TickInterval = tickInterval, Trajectory = trajectory, Index = index, Arc = arc,
            Speed = speed, Jitter = jitter, PierceChance = pierceChance,
            Knockback = knockback, Gather = gather, AoeAll = aoeAll, Layer = layer, Mirrored = mirrored, Band = band,
            // 分身那一份归到**复制它的那个法术**（剑二十三）名下：分身的价值要能单独看见。
            MirrorSkill = mirrored ? Buff("mirror")?.Source ?? "" : "",
            // 分身那一式晚一拍出现：同帧落地两边会完全重叠，尤其 target / ground / sky_drop 三类根本没有发射点可看。
            Delay = mirrored ? MirrorDelay : 0 });
    }
    private void TickEffects(double dt)
    {
        foreach (var effect in Effects.ToArray())
        {
            // 影分身那一式晚一拍出现。拦在 Life / Timer 递减**之前**，于是延迟期间它的寿命与起飞倒计时都不流逝，
            // 到点后按原有逻辑照常走——不必给每个 kind 各打一个补丁（bolt 没有 Timer 通路、ground 的 Timer 又是 tick 间隔）。
            if (effect.Delay > 0) { effect.Delay -= dt; continue; }
            effect.Life -= dt; effect.Timer -= dt;
            if (effect.Hostile)
            {
                if (effect.Kind == "projectile") effect.X += Math.Sign(Battle.PlayerX - effect.X) * Math.Min(900 * dt, Math.Abs(Battle.PlayerX - effect.X));
                if ((effect.Kind == "projectile" && Math.Abs(effect.X - Battle.PlayerX) < 20) || (effect.Kind == "target" && effect.Life <= 0))
                { HurtPlayer(effect.Damage); effect.Life = 0; }
                continue;
            }
            // 锁定型（target / 追踪弹）在这里取目标：层数对不上就当它不存在，后面各分支自然不结算。
            var target = Battle.Enemies.FirstOrDefault(e => e.Id == effect.Target && Legal(e, effect.Layer));
            switch (effect.Kind)
            {
                case "projectile":
                    // 起飞前停留（悬浮蓄势 / 浮现 / 剑阵各支的错时）：所有自定义形态共用同一个倒计时，
                    // 所以 bolt 以外的形态不必各自实现时序。bolt 与退役的 secondary == "pierce" 传 0，行为不变。
                    if (effect.Timer > 0)
                    {
                        // 肩侧/身前发射的形态（平射与平推）在停留期要**跟着施法者走**：这一秒里角色可能还在前进，
                        // 发射点若冻结在施放那一刻，等它起飞时已经被甩到角色身后——剑气流云壁的前摇有整整 1 秒，
                        // 表现为"冲击波从画面左边冒出来"。落点类形态（sky_drop）不在此列：它的 X 是阵心，不能跟人走。
                        // 影分身那一式跟着**分身**走，不是跟着玩家：少了这个偏移，镜像会被这一行拽回本体身上、
                        // 与本体那一支完全重叠（御剑术前摇 0.12 秒、剑气流云壁 0.5 秒，都够把偏移抹掉）。
                        if (effect.Trajectory is "line_shot" or "line_pierce")
                            effect.X = Battle.PlayerX + (effect.Mirrored ? MirrorOffset : 0);
                        break;
                    }
                    // 近战挥砍（普攻专用形态）：**不飞，出手当拍就结算**——点击的反馈必须立刻到位，
                    // 若等挥砍演完再落伤害，读起来就是"点了没反应"。
                    // 用 `Hit` 标记"这一下已经打过了"，于是整段余韵里只结算一次；
                    // 目标中途死了就是落空——与远程弹道同口径（不做落点补救，普攻不该有那份待遇）。
                    if (effect.Trajectory == "melee_slash")
                    {
                        if (target is not null && !effect.Hit.Contains(target.Id))
                        {
                            effect.Hit.Add(target.Id);
                            Hit(target, effect.Damage, effect);
                        }
                        break;
                    }
                    // 自定义飞行形态。bolt（Trajectory 为空）不走这里，下面两条旧路径原样保留。
                    if (effect.Trajectory == "sky_drop")
                    {
                        // 垂直落下：X 在发射时已冻结（不追踪，"击中谁是谁"），落地时对落点半径内所有敌人结算，可命中重叠单位。
                        if (effect.Life > 0) break;
                        double radius = effect.AoeRadius; // 校验保证 > 0，绝不回退 ground 的 220
                        // 天降火海：天降的那条效果若挂着**灼烧**，落地就不做一次性结算，改为在落点留下一片火海。
                        // 火海本身就是一个 ground 效果（寿命 = 那份 Buff 的时长 = 灼烧的持续时间），于是
                        // "每 0.6 秒对半径内全体结算并刷新灼烧"这套现成逻辑原样复用；伤害走 Damage，
                        // 灼烧是它附在敌人身上的持续伤害（`BuffId` 一路带下去，由 `ApplyBuff` 按 kind 派发）。
                        // 校验保证这里的 tick_interval 与 Buff 时长都为正。
                        if (effect.Buff is { Kind: "dot" } burn)
                        {
                            double life = burn.Duration;
                            Effects.Add(new()
                            {
                                Kind = "ground", X = effect.X, Damage = effect.Damage, Life = life, MaxLife = life,
                                // 火海是那把剑**派生**出来的效果，所以它继承那一发的出手快照（暴击与倍率窗），
                                // 不自己重摇——少了这三个字段，火海会在同一发里凭空变成"永不暴击且不吃倍率窗"。
                                Critical = effect.Critical, CritDamage = effect.CritDamage, BuffPower = effect.BuffPower,
                                // Index 非 0：技能名标签只由本次施法的第一支负责，火海是那把剑派生出来的，不该再挂一次名字。
                                Index = 1, Skill = effect.Skill, Buff = effect.Buff,
                                AoeRadius = radius, Layer = effect.Layer,
                                // 火海照抄父效果的结算间隔——它是那一式派生出来的场，节奏必须与它一致。
                                TickInterval = effect.TickInterval,
                            });
                            break;
                        }
                        // 各锁一敌的剑陨（`band > 0`）：落地时**原地留一小段"剑气爆炸"的余韵**。
                        // 它是个 `Damage = 0` 的 ground 效果——所以只走寿命与绘制，一次结算也伤不到人
                        // （`HurtEnemy(e, 0)` 什么都不会改），却让爆炸有了 0→1 的进度可以画真正的扩散动画。
                        // 不留这一下的话，效果在落地当帧就被回收，表现层只剩"坠落最后一帧闪一下"。
                        if (effect.Band > 0)
                        {
                            Effects.Add(new()
                            {
                                Kind = "ground", X = effect.X, Damage = 0, Life = BurstLife, MaxLife = BurstLife,
                                Index = 1,                       // 技能名标签只由本次施法的第一支负责
                                Skill = effect.Skill, AoeRadius = radius, Layer = effect.Layer,
                            });
                        }
                        // 全体命中（aoe_all）：无视位置与半径，对这一把剑落地时的所有合法敌人各结算一次。
                        // radius 在配置里仍要求为正，但此时只作表现层的落点预警圈用。
                        if (effect.AoeAll)
                        {
                            foreach (var e in Battle.Enemies.Where(e => Legal(e, effect.Layer)).ToArray()) Hit(e, effect.Damage, effect);
                            break;
                        }
                        foreach (var e in Battle.Enemies.Where(e => Legal(e, effect.Layer) && Math.Abs(e.X - effect.X) < radius).ToArray())
                            Hit(e, effect.Damage, effect);
                        break;
                    }
                    if (effect.Trajectory == "line_shot")
                    {
                        // 平射、不追踪：命中路径上最先遇到的那个敌人即结算并销毁（与 line_pierce 的恒穿透区分开）。
                        // 用扫过区间拾取，避免一帧跨过多个身位时漏判；只取最靠前的一个，不是全部。
                        double from = effect.X;
                        effect.X += effect.Speed * dt;
                        if (effect.X > Level.Cells * Config.Setting("cell_width")) { effect.Life = 0; break; }
                        var victim = Battle.Enemies.Where(e => Legal(e, effect.Layer) && !effect.Hit.Contains(e.Id) && e.X >= from - 30 && e.X <= effect.X + 30)
                            .OrderBy(e => e.X).ThenBy(e => e.Id).FirstOrDefault();
                        if (victim is null) break;
                        effect.Hit.Add(victim.Id);
                        Hit(victim, effect.Damage, effect);
                        // 概率穿透只有第一次击中时判定一次：没穿就销毁，穿了也标记下来，之后命中一律销毁。
                        if (!effect.Pierced && effect.PierceChance > 0 && _random.NextDouble() < effect.PierceChance) effect.Pierced = true;
                        else effect.Life = 0;
                        break;
                    }
                    if (effect.Trajectory is "hover_homing" or "arc_homing")
                    {
                        if (target is null)
                        {
                            // 目标已死：**先把这一程飞完**（飞到它最后所在的位置），不半路消失。
                            // 旧写法是沿发射方向一直飘到出界，画面上是"剑芒贴着地面平行右移才没"；
                            // 但真正难看的不是这个——是表现层拿不到目标时把进度钉在弧顶，剑芒一直飘在半空。
                            effect.X += Math.Sign(effect.TargetX - effect.X) * Math.Min(effect.Speed * dt, Math.Abs(effect.TargetX - effect.X));
                            if (Math.Abs(effect.X - effect.TargetX) >= 24) break;
                            // 到落点了：**就地索敌**。换一个附近的目标接着追，弧度取反——画面上是一道反向的弧，
                            // 两段连起来像个波。找不到人就到此为止。
                            var next = ReacquireTarget(effect);
                            if (next is null) { effect.Life = 0; break; }
                            effect.Target = next.Id;
                            effect.LegX = effect.X;          // 新的一段从落点起算，第二段弧才不会跳
                            effect.Arc = -effect.Arc;        // 反向弧度：波浪的由来
                            effect.Reacquired++;
                            // 新的一段要有自己的寿命，否则总航程超过原寿命、剑芒在第二段半路被回收。
                            double leg = Math.Abs(next.X - effect.X) / Math.Max(1, effect.Speed) + .2;
                            effect.Life = effect.MaxLife = leg;
                            break;
                        }
                        effect.TargetX = target.X;   // 记住目标最后的位置，供上面那条分支用
                        effect.X += Math.Sign(target.X - effect.X) * Math.Min(effect.Speed * dt, Math.Abs(target.X - effect.X));
                        if (Math.Abs(effect.X - target.X) < 24) { Hit(target, effect.Damage, effect); effect.Life = 0; }
                        break;
                    }
                    if (effect.Pierce)
                    {
                        // 穿透：沿前进方向飞行，命中沿途每个敌人一次（用扫过区间避免单帧跳过）。
                        // line_pierce（御剑术平射）与退役配置的 secondary == "pierce" 共用这一段。
                        double from = effect.X;
                        effect.X += effect.Speed * dt;
                        if (effect.X > Level.Cells * Config.Setting("cell_width")) { effect.Life = 0; break; }
                        foreach (var e in Battle.Enemies.Where(e => Legal(e, effect.Layer) && !effect.Hit.Contains(e.Id) && e.X >= from - 30 && e.X <= effect.X + 30).ToArray())
                        { effect.Hit.Add(e.Id); Hit(e, effect.Damage, effect); }
                        break;
                    }
                    if (target is null) { effect.Life = 0; break; }
                    effect.X += Math.Sign(target.X - effect.X) * Math.Min(effect.Speed * dt, Math.Abs(target.X - effect.X));
                    if (Math.Abs(effect.X - target.X) < 24) { Hit(target, effect.Damage, effect); effect.Life = 0; }
                    break;
                case "target":
                    if (effect.Life > 0) break;
                    // 目标已死就落点重判定——不然 15~30 秒冷却的大招（御雷真诀 / 斩鬼神）会白白打空。
                    if (target is not null) Hit(target, effect.Damage, effect);
                    else FallbackHit(effect);
                    break;
                case "ground":
                    if (effect.Timer <= 0)
                    {
                        // 结算间隔取**效果实例自己带的** `tick_interval`（来自 `SkillEffect.csv`）。
                        // `LevelCurve` 的期望命中数读的是同一列——两处必须是同一个数：写死在两边过一次
                        // 就会静默分叉（模型按 0.6 秒算命中数，实际每 0.5 秒结算一次，而 `--check` 照样绿）。
                        effect.Timer = effect.TickInterval;
                        double radius = effect.AoeRadius > 0 ? effect.AoeRadius : 220;
                        foreach (var e in Battle.Enemies.Where(e => Legal(e, effect.Layer) && Math.Abs(e.X - effect.X) < radius).ToArray()) Hit(e, effect.Damage, effect);
                    }
                    break;
                case "summon":
                    effect.X = Battle.PlayerX + 110;
                    // 召唤物射击是「召唤那一手」**派生**的结算：它不再摇暴击、也不再重读当下的增益，
                    // 而是原样继承召唤时定下的那份快照（`inherits: effect`）。
                    // 这才是"召唤物不吃新的攻击者快照"的准确含义——不是"不算暴击"，而是"不**重新**摇"。
                    if (effect.Timer <= 0) { effect.Timer = 1; var next = Target(1100); if (next is not null) Launch("projectile", next, effect.Damage, 2, false, condition: effect.Condition, conditionValue: effect.ConditionValue, skill: effect.Skill, inherits: effect); }
                    break;
            }
        }
        Effects.RemoveAll(e => e.Life <= 0);
    }
    /// <summary>
    /// **攻击者侧结算**：`DMG1 × 通用增伤 × 暴击 × 共享倍率窗`，**不含目标侧修正**
    /// （易伤按目标当下的状态在落地那一刻算，不吃出手快照）。
    /// 灼烧每跳用的就是它：施放那一刻算一次、冻进 `DotDps`，此后不再重算——这正是「出手快照」。
    /// </summary>
    private double AttackerSettled(CombatEffect fx) => DamageFormula.Final(
        new DamageEvent(fx.Damage, Attr("generic_damage"), fx.Critical, fx.CritDamage, fx.BuffPower, 1));
    /// <summary>
    /// **DMG3 的结算点**：把出手快照（DMG1 / 暴击 / 倍率窗）与**目标当下的状态**凑成一笔
    /// <see cref="DamageEvent"/>，交给 <see cref="DamageFormula.Final"/> 算最终伤害，再落给 <see cref="ApplyDamage"/>。
    /// 全工程只有这里与 <see cref="HurtEnemy"/>（灼烧跳伤那条派生路）给敌人算伤害。
    /// </summary>
    private void Hit(EnemyState e, double dmg1, CombatEffect fx)
    {
        // 层数不匹配就直接不结算：这是"飞行单位免疫地面技能"的最后一道防线，
        // 任何绕过选敌的调用路径（比如 aoe_all 的全场扫描）都在这里被拦住。
        // **它是目标合法性，不是命中率**：没有摇骰子，只是"这一发本来就够不着这一层"。
        if (!Legal(e) || !LayerHit(fx.Layer, e)) return;
        // ── Build 乘区（攻击者侧、带条件）：三个来源全是"条件成立才生效"的倍率，所以进乘算池 ──
        // ① 出手那一刻的共享伤害倍率窗（power != 1 的增益类法术写入，已快照进 fx）。
        double build = fx.BuffPower;
        // ② 斩杀：目标残血时翻倍（天剑）。阈值与倍率都在配置里，这里只是求值。
        if (fx.Condition == "target_hp_below" && fx.ConditionValue > 0 && e.Hp / e.MaxHp < fx.ConditionValue) build *= 2;
        // ③ 利用状态（bonus_vs_state，斩鬼神）：目标身上带着**任意状态**就增伤。
        // 必须在 ApplySecondary 之前读——那一句在函数末尾，会把本次施加的状态覆盖上去，
        // 晚读就会把"刚挂上的"也算成"已有的"。
        if (fx.Condition == "target_has_state" && fx.ConditionValue > 0 && CarriesAnyState(e)) build *= 1 + fx.ConditionValue;
        // 目标侧修正（易伤）**在这里读、按目标当下状态算**，不吃出手快照——飞一半目标才中的易伤也该吃到。
        // 来源在这里是现成的：宠物弹 / 召唤弹 / 剑罡飞剑都带着自己的 id；影分身那一份走 `DamageSource`，
        // 归到**复制它的法术**（剑二十三）名下，而不是被复制的这一式。
        double dealt = ApplyDamage(e, new DamageEvent(dmg1, Attr("generic_damage"), fx.Critical, fx.CritDamage, build,
            e.VulnerableUntil > 0 ? e.VulnerableFactor : 1), fx.DamageSource);
        // 吸血：按**这一笔实际结算出的伤害**折算（含溢出部分，与从前一致）。
        if (dealt > 0 && LifestealFactor > 0) Battle.PlayerHp = Math.Min(MaxHp, Battle.PlayerHp + dealt * LifestealFactor);
        // 击退：沿背离玩家的方向推开。死在本次伤害上的敌人不再后退，免得"尸体会滑动"。
        // 推离战力范围会让角色随即继续前进，正是"剑气推着敌人走"该有的结果，不需要额外处理。
        // 裂隙不可移动，绝不推它——它的位置是关卡与停步判定的锚点。
        if (fx.Knockback > 0 && e.Hp > 0 && e.Kind != "rift")
            e.X += (e.X >= Battle.PlayerX ? 1 : -1) * fx.Knockback;
        // 吸附（聚怪）：与击退互为反向的一对——它把目标朝**本效果的中心**（`fx.X`，ground 的阵心就是施放时选中的那只）
        // 拉近，而不是背离玩家推开。取 `Min` 是刻意的：不会把敌人一口气拽过中心再来回弹，**收敛到中心就停**。
        // 同样的两条护栏：死在这次伤害上的不再动（免得尸体滑动），裂隙永不移动。
        if (fx.Gather > 0 && e.Hp > 0 && e.Kind != "rift")
            e.X += Math.Sign(fx.X - e.X) * Math.Min(fx.Gather, Math.Abs(fx.X - e.X));
        ApplySecondary(e, fx);
    }
    /// <summary>
    /// 追踪弹飞完一程、原目标已死时的**落点重索敌**：在落点附近挑一个合法敌人接着追。
    /// 挑**血量最低**的而不是最近的，是为了契合"收割"——落地那一刻谁最脆就追谁。
    /// 只在 `FallbackRadius` 内找，**不改追远处**（多发齐射若都能改追远处会收敛到同一只，
    /// `Picks` 刻意"尽量不重复"就是为了铺开）；也限制改换次数，免得一只都追不到时无限接力。
    /// </summary>
    private EnemyState? ReacquireTarget(CombatEffect effect) => effect.Reacquired >= MaxReacquire ? null
        : Battle.Enemies
            .Where(e => Legal(e, effect.Layer) && Math.Abs(e.X - effect.X) < FallbackRadius)
            .OrderBy(e => e.Hp).ThenBy(e => e.Id).FirstOrDefault();

    /// <summary>
    /// 定点弹（`target` 类）目标中途死亡时的补救：在落点附近挑一个合法敌人**当场结算一次**。
    /// 它没有飞行过程、没有表现可保，所以直接打，不像追踪弹那样"飞过去再追"。
    /// 不这么做的话，御雷真诀 / 斩鬼神 这类 15~30 秒冷却的大招会白白打空。
    /// </summary>
    private void FallbackHit(CombatEffect effect)
    {
        var victim = Battle.Enemies
            .Where(e => Legal(e, effect.Layer) && Math.Abs(e.X - effect.TargetX) < FallbackRadius)
            .OrderBy(e => e.Hp).ThenBy(e => e.Id).FirstOrDefault();
        if (victim is not null) Hit(victim, effect.Damage, effect);
    }

    /// <summary>
    /// 目标身上是否带着任一状态。判定集合与 `ApplyBuff` 的 switch 对齐（slow / chill / stun / dot / vulnerable）。
    /// 注意"定身"也被 `cast_root`（剑气流云壁的全屏定身）写入，所以被定身的目标也算"有状态"——这是有意的。
    /// </summary>
    private static bool CarriesAnyState(EnemyState e) =>
        e.SlowUntil > 0 || e.ChillUntil > 0 || e.StunUntil > 0 || e.DotUntil > 0 || e.VulnerableUntil > 0;

    /// <summary>命中时挂上这一发携带的状态（`SkillEffect.buff_id`）。没有就什么都不做。</summary>
    private void ApplySecondary(EnemyState e, CombatEffect fx)
    {
        if (fx.Buff is null) return;
        ApplyBuff(e, fx.Buff, fx);
    }
    /// <summary>
    /// 玩家承伤。**与打怪是同一条管线、同一份 `DamageFormula`，只是目标换成了玩家**：
    /// 怪物对玩家也是一次 Attack Event（`怪攻击 × 倍率 1.0 + 0`），只是它的攻击力在 `Spawn` 时
    /// 已经乘过关卡倍率与波次系数，所以这里拿到的 `amount` 就是它的 DMG1。
    ///
    /// 玩家侧的三个环节，顺序**不能改**：
    /// <list type="number">
    /// <item>无敌（GM 开关）/ 复活读条短路；</item>
    /// <item>**命中判定 = `dodge`**——它在承伤方这一侧，见 `docs/design/combat.md`：这不是
    /// "攻方命中 vs 守方闪避"的对抗体系（怪物没有命中属性，玩家也没有可破的敌方闪避），
    /// 而是纯粹的承伤方减伤。闪避排在护盾**之前**，所以躲开的那一下不消耗护盾。</item>
    /// <item>承伤应用：先扣护盾、再扣气血。</item>
    /// </list>
    /// 玩家侧的 DMG3 乘区表**当前为空**（怪物不会暴击，玩家也不吃易伤），所以结算结果就等于传进来的量；
    /// 走一遍 `DamageFormula` 是为了让"以后给玩家加一个受创乘区"不必再开第二条路。
    /// </summary>
    private void HurtPlayer(double amount)
    {
        if (PlayerInvincible) return;   // GM 调试开关，见 PlayerInvincible 的说明
        if (Battle.RespawnTimer > 0 || _random.NextDouble() < ClampAttr("dodge", Attr("dodge"))) return;
        double damage = DamageFormula.Final(DamageEvent.Raw(amount));
        // 护盾的**吸收池存在实例上**（施加时按攻击力算定），扣减就是直接减小它。
        // 池子见底就把这一份增益摘掉，于是"盾破了"与"盾到期了"走同一条路，表现层只看剩余时间。
        if (Buff("shield") is { Value: > 0 } shield)
        {
            double absorbed = Math.Min(shield.Value, damage);
            shield.Value -= absorbed;
            damage -= absorbed;
            if (shield.Value <= 0) _buffs.Remove(shield.Def.Id);
        }
        Battle.PlayerHp = Math.Max(0, Battle.PlayerHp - damage);
    }
    /// <summary>
    /// **承伤的应用步骤**（Apply Damage）——扣血与死亡结算，**全游戏唯一给敌人造成伤害的地方**，
    /// 伤害统计（<see cref="DamageStats"/>）就挂在这里。第 1 步「目标合法性」由调用方保证（见 `Hit`）。
    /// `source` 是来源技能 id（空串 = 普通攻击）。
    ///
    /// 记账必须在这里、**在易伤乘区之后**：改在调用方读 DMG1 会漏掉目标侧那一档，数字会偏小。
    /// `Math.Max(0, …)` 已经把过量击杀吃掉了，所以有效 = before − after、溢出 = 伤害 − 有效。
    /// </summary>
    /// <returns>这一笔的**最终伤害**（含溢出部分），供吸血折算。</returns>
    private double ApplyDamage(EnemyState e, in DamageEvent ev, string source)
    {
        if (!Legal(e)) return 0;
        double damage = DamageFormula.Final(ev);
        double before = e.Hp;
        e.Hp = Math.Max(0, before - damage);
        DamageStats.Add(source, before - e.Hp, damage - (before - e.Hp));
        if (e.Hp > 0) return damage;
        // 掉落 +1（`drop_flat`）：只加在**怪物自身**那一笔上，普通怪 / 精英 / BOSS 都走这一行、自动覆盖。
        // **裂隙除外**：它 `gold = 0`，平白 +1 读成"开一道门送一块灵石"；而且下面几行已经明确
        // 把裂隙排除在"怪"之外（不触发修行解锁），这里跟着同一个口径。
        // 也**不碰** `drop.csv` 的奖励组：那是手工配的关卡节点奖励，BOSS 已经在上面这行白拿过一次，
        // 两处都加等于对 BOSS 重复计一遍天赋。
        AddCurrency("gold", Config.Monsters[e.MonsterId].Gold + (e.Kind == "rift" ? 0 : TalentBonus("drop_flat")));
        // **首次击杀解锁「修行」**——整条教学闭环的起点：先自己点着砍死一只，修行才出现。
        // `HashSet.Add` 的返回值天然"只写一次"，不必另立账本。
        // **不能蹭 `FirstKills`**：那是 BOSS 首杀账本，存档里还绑着"灵核余额 ≤ 首杀数 + 调试发放量"
        // 的一致性校验，把普通击杀混进去会直接把校验算错、拒档。
        // 裂隙不算"怪"（它是一道门，不移动不攻击），不该触发。
        if (e.Kind is not "rift" && State.UnlockedSystems.Add(Systems.Cultivation))
        {
            Message = "初战告捷 · 修行已开启";
            _persist = true;
        }
        if (e.Kind == "boss")
        {
            Battle.BossDefeated = true;
            bool first = State.FirstKills.Add(Level.Id);
            foreach (var reward in Config.Rows("drop").Where(r => r.Text("group_id") == (first ? Level.FirstReward : Level.RepeatReward)))
            {
                if (reward.Text("item_id") != "core") AddCurrency(reward.Text("item_id"), reward.Number("amount"));
            }
            // 灵核只在这一处入账，账本与余额随整个快照原子保存。
            if (first) State.Wallet["core"] = State.Amount("core") + 1;
            Message = first ? "妖王伏诛 · 首杀灵核 +1！" : "妖王伏诛 · 已领取过本关灵核。";
            _persist = true;
        }
        if (e.Kind == "rift") { Battle.PortalDestroyed = true; _persist = true; }
        return damage;
    }
    /// <summary>
    /// 直接扣血：传进来的数**已经是"攻击者侧结算完"的量**，本函数只再叠**目标侧修正**（易伤）。
    ///
    /// 两条调用方：灼烧跳伤（`TickEnemies`）——它的攻击者侧（暴击与倍率窗）在**施放那一刻**就快照进了
    /// `DotDps`，走 `Hit` 会把它再算一遍，等于双重结算；以及自检 / GM 的直接调用（传一个裸伤害，
    /// 预期就是"目标侧修正照常生效"，见 `tests/` 里那条易伤用例）。
    /// **正常命中走 <see cref="Hit"/>**——那条路上才有出手快照与 Build 乘区。
    /// </summary>
    internal void HurtEnemy(EnemyState e, double damage, string source = "") =>
        ApplyDamage(e, new DamageEvent(damage, 0, false, 1, 1, e.VulnerableUntil > 0 ? e.VulnerableFactor : 1), source);
    /// <summary>
    /// 阵亡。**不管死在哪一格都回关卡起点**——Boss 格原先的"本格重生、敌人伤势保留"已取消
    /// （用户要求：死亡行为各处一致）。
    ///
    /// **这里刻意不重置关卡**：重置挪到复活读条归零那一刻（见 <see cref="Step"/> 的倒计时块）。
    /// 这样倒地演出是"倒在杀死它的那堆怪中间"，而不是一张空场景；`PlayerX` 在倒地期间保持为
    /// 死亡点，表现层正好拿它当倒地用的镜头位置。
    /// </summary>
    private void Die()
    {
        Effects.Clear(); ClearBuffs();
        Battle.PlayerHp = 0;
        Battle.RespawnTimer = Config.Setting("respawn_seconds");
        Moving = false; _persist = true;
        Message = "气血耗尽 · 返回本关起点，资源全部保留。";
    }
    private void CompleteLevel()
    {
        int index = Config.Levels.FindIndex(l => l.Id == Level.Id);
        if (index + 1 < Config.Levels.Count) State.UnlockedLevels.Add(Config.Levels[index + 1].Id);
        var next = !State.LoopLevel && index + 1 < Config.Levels.Count ? Config.Levels[index + 1] : Level;
        EnterLevel(next.Id); Message = "裂隙已破 · 抵达 " + next.Name; _persist = true;
    }
    /// <summary>关卡起点（每关都从这儿开始；也是阵亡复活后的落点）。</summary>
    public const double LevelStartX = 80;
    private void EnterLevel(string id)
    {
        State.Battle = new() { LevelId = id, PlayerHp = MaxHp, PlayerX = LevelStartX };
        Effects.Clear(); ClearBuffs();
    }
    public bool SelectLevel(string id)
    {
        if (!State.UnlockedLevels.Contains(id)) return Say("尚未解锁此关。");
        State.LoopLevel = true; EnterLevel(id); return Changed("已切换为整关循环。");
    }
    public void ToggleLoop() { State.LoopLevel = !State.LoopLevel; Changed(State.LoopLevel ? "正在循环本关。" : "已开启自动推进。"); }
    private bool Say(string message) { Message = message; return false; }
    private bool Changed(string message) { Message = message; PersistRequested?.Invoke(); return true; }
    private void AddCurrency(string id, double amount)
    {
        if (id == "core") throw new InvalidOperationException("灵核只能由关卡首杀发放");
        State.Wallet[id] = State.Amount(id) + amount;
    }

    /// <summary>
    /// GM 调试入口：为 item.csv 中每一种货币（kind = currency）各增加固定数量。
    /// 遍历配置表而非硬编码名单，因此新增货币后无需改动此处即可自动纳入发放范围。
    /// 这是开发调试通道，不属于五大玩法系统，也不是任何奖励组的产出路径；
    /// 灵核的玩法产出口仍然只有「关卡首杀」一处，此方法不写入首杀账本（FirstKills），
    /// 而是把发放量记入 DebugGranted，使存档校验能区分调试产出与篡改。
    /// </summary>
    public bool GrantAllCurrencies(double amount = 10000)
    {
        var currencies = Config.Rows("item").Where(r => r.Text("kind") == "currency").ToList();
        if (currencies.Count == 0) return Say("配置中没有可发放的货币。");
        foreach (var r in currencies)
        {
            string id = r.Text("id");
            State.Wallet[id] = State.Amount(id) + amount;
            State.DebugGranted[id] = State.DebugGranted.GetValueOrDefault(id) + amount;
        }
        return Changed($"GM 调试 · {currencies.Count} 种货币各 +{amount:0}");
    }

    /// <summary>
    /// 某个系统解锁了没。UI 拿它决定页签可用不可用。
    ///
    /// 两个来源，取并：**里程碑**（首杀小怪 → 修行，记在 `State.UnlockedSystems` 里）与
    /// **修行节点**（`realm_system` / `forge_system`，见 `Systems.ByEffect`）。
    /// 后者是**推导**出来的、不落盘——买节点时另外写一份状态的话，读档与改配置都可能让两边对不上。
    /// </summary>
    public bool Unlocked(string system) =>
        State.UnlockedSystems.Contains(system)
        || Systems.ByEffect.Any(pair => pair.Value == system && TalentBonus(pair.Key) > 0);

    /// <summary>
    /// GM 调试入口：把**全部系统**一次解锁。开发时不必为了看一页而先打一遍教学。
    /// 与 <see cref="GrantAllCurrencies"/> 同一条护栏——**只动解锁标记**，
    /// 不碰 `Wallet` / `FirstKills` / `DebugGranted`（那三者之间有存档校验绑着）。
    /// </summary>
    public bool UnlockAllSystems()
    {
        State.UnlockedSystems.UnionWith(Systems.All);
        return Changed("GM 调试 · 全部系统已解锁");
    }
}
