using System.Text.Json.Serialization;

namespace IdleSword.Core;

/// <summary>版本化存档 DTO。永久进度与本轮战斗分离，保存未收取参悟产物，无离线时间戳。</summary>
public sealed class PlayerState
{
    public int Version { get; set; } = 1;
    public Dictionary<string, double> Wallet { get; set; } = new() { ["gold"] = 0, ["core"] = 0 };
    // GM 调试发放记录：按货币 id 累计由调试入口发出的数量。存档校验据此区分
    // 「玩法产出」与「调试产出」，否则调试发出的灵核会被当成篡改而拒绝整份存档。
    public Dictionary<string, double> DebugGranted { get; set; } = [];
    public HashSet<string> FirstKills { get; set; } = [];
    public HashSet<string> UnlockedLevels { get; set; } = [];
    public HashSet<string> Realms { get; set; } = [];
    public Dictionary<string, int> Skills { get; set; } = [];
    public Dictionary<string, int> Talents { get; set; } = [];
    public Dictionary<string, int> Upgrades { get; set; } = [];
    public Dictionary<string, double> PendingIntent { get; set; } = [];
    public Dictionary<string, double> IntentTimers { get; set; } = [];
    /// <summary>已解锁的系统 id，取值只允许 <see cref="Systems.All"/> 里的那几个。
    /// **旧档缺这个字段时反序列化得到空集 = 全部锁着**——这是刻意的：老玩家没有走过教学，
    /// 但他们的进度（关卡、法术、修行点数）一样不少，重新解锁一次只是点两下。
    /// 加字段**不需要升 `Version`**（`SaveStore.Validate` 只认 `Version == 1`）。</summary>
    public HashSet<string> UnlockedSystems { get; set; } = [];
    public HashSet<string> Pets { get; set; } = [];
    public List<string> EquippedPets { get; set; } = [];
    public Dictionary<string, List<string>> PetBuffs { get; set; } = [];
    public string Weapon { get; set; } = "";
    public int WeaponLevel { get; set; }
    public double WeaponRoll { get; set; } = 1;
    public bool LoopLevel { get; set; }
    public BattleState Battle { get; set; } = new();
    public double Amount(string item) => Wallet.GetValueOrDefault(item);
}

public sealed class BattleState
{
    public string LevelId { get; set; } = "";
    public double PlayerX { get; set; }
    public double PlayerHp { get; set; }
    public int Cell { get; set; }
    public double RespawnTimer { get; set; }
    public bool BossDefeated { get; set; }
    public bool PortalDestroyed { get; set; }
    public long NextEnemyId { get; set; } = 1;
    public Dictionary<int, SpawnState> Spawns { get; set; } = [];
    public List<EnemyState> Enemies { get; set; } = [];
    public Dictionary<string, double> Cooldowns { get; set; } = [];
}
public sealed class SpawnState
{
    public bool Passed { get; set; }
    public double Timer { get; set; }
    public int Wave { get; set; }
}
public sealed class EnemyState
{
    public long Id { get; set; }
    public string MonsterId { get; set; } = "";
    public string Kind { get; set; } = "normal";
    public double X { get; set; }
    public double Hp { get; set; }
    public double MaxHp { get; set; }
    public double Atk { get; set; }
    public double AttackTimer { get; set; }
    /// <summary>
    /// 挂在身上的状态与增益。**与玩家侧同一套** `BuffInstance`：配置在 `SkillBuff.csv`，
    /// "这只敌人身上这一份还剩多久"是运行态。本工程**不叠层**——同一个 kind 同时只有一份，
    /// 再挂一次是**刷新**（覆盖剩余时间与定格值），不是叠加；想严格区分同种状态的多个来源
    /// 得先有"可叠加的实例列表"，那是另一件事。
    /// </summary>
    /// <summary>
    /// **不入存档**：下面那几个状态属性就是它的持久化投影（`SlowUntil` / `SlowFactor` / `StunUntil` /
    /// `DotUntil` / `DotDps` / `DotSkill` / `VulnerableUntil` / `VulnerableFactor`）——存取格式与从前
    /// **一个字节都没变**，读档时由那些属性重新建出实例来。把这一列直接序列化会把配置（`BuffDef`）
    /// 一起写进存档，而且与那批属性互相覆盖，读回去的状态就不确定了。
    /// </summary>
    [JsonIgnore]
    public List<BuffInstance> Buffs { get; } = [];

    private BuffInstance? Status(string kind) => Buffs.FirstOrDefault(b => b.Def.Kind == kind);
    /// <summary>某一类状态在役那一份的剩余秒数；0 表示没有（"到期"与"从未有过"因此是同一种表示）。</summary>
    private double Until(string kind) => Status(kind)?.Remaining ?? 0;
    private double ValueOf(string kind, double fallback) => Status(kind)?.Value ?? fallback;
    /// <summary>
    /// 取在役的那一份，没有就造一份**临时定义**：`cast_root`（施放瞬间的全屏定身）、靶场摆位
    /// 与自检都会直接置状态，而那条路上没有配置行可依。真实来源（命中挂状态）走
    /// `GameSession.ApplyBuff`，用的是配置里的那一份。
    /// </summary>
    private BuffInstance Ensure(string kind)
    {
        if (Status(kind) is { } found) return found;
        found = new BuffInstance
        {
            Def = new BuffDef($"{kind}@runtime", kind, kind, "enemy", 1, 0, 0, false, 0),
            Source = "",
        };
        Buffs.Add(found);
        return found;
    }
    private void SetUntil(string kind, double seconds)
    {
        if (seconds <= 0) { if (Status(kind) is { } expired) Buffs.Remove(expired); return; }
        Ensure(kind).Remaining = seconds;
    }
    private void SetValue(string kind, double value) => Ensure(kind).Value = value;

    // 状态对外仍然是**同名属性**：`TickEnemies` / `BattleView` 的状态图标 / 自检都照旧读写，
    // 换掉的只是存法（五个散字段 → 实例清单）。
    public double SlowUntil { get => Until("slow"); set => SetUntil("slow", value); }
    /// <summary>移速乘数（0.75 = 移速 ×0.75）。**`slow` 与 `chill` 共用这一份**——移速只有一条判定路径，
    /// 两者互相覆盖时以最后一次命中为准（与从前共用一个字段时同义，见 `docs/data/fields.md` 的「寒冷」）。</summary>
    public double SlowFactor { get => ValueOf("slow", 1); set => SetValue("slow", value); }
    /// <summary>寒冷：减速之外多一个状态源，仅用于让表现层把受击角色染成冰蓝。</summary>
    public double ChillUntil { get => Until("chill"); set => SetUntil("chill", value); }
    public double StunUntil { get => Until("stun"); set => SetUntil("stun", value); }
    public double DotUntil { get => Until("dot"); set => SetUntil("dot", value); }
    /// <summary>灼烧每秒伤害。它是**施放那一刻定格的攻击者侧结算值**（出手快照），此后只有目标侧修正会变。</summary>
    public double DotDps { get => ValueOf("dot", 0); set => SetValue("dot", value); }
    /// <summary>施加这份灼烧的法术 id——伤害统计要按来源归因，而跳伤那条路上没有 `CombatEffect` 可查。
    /// 多个来源只留最后一次的施加者（刷新而非叠加，与 `DotDps` 同一条口径）。</summary>
    public string DotSkill { get => Status("dot")?.Source ?? ""; set => Ensure("dot").Source = value; }
    public double VulnerableUntil { get => Until("vulnerable"); set => SetUntil("vulnerable", value); }
    public double VulnerableFactor { get => ValueOf("vulnerable", 1); set => SetValue("vulnerable", value); }

    /// <summary>推进**非灼烧**状态的计时（归零即摘掉）。
    /// 灼烧单独走 <see cref="TickDot"/>：它的跳伤结算点在敌人行动之后，那个顺序是 DMG2 口径的一部分。</summary>
    public void TickStatuses(double dt)
    {
        // 倒着遍历、就地删（这是**每只怪每一步**都跑的热路径，`.ToArray()` 会在 80 只 × 20 步/秒下
        // 每秒生出一千多个小数组）。这里没有任何回调，所以原地删是安全的。
        for (int i = Buffs.Count - 1; i >= 0; i--)
        {
            var buff = Buffs[i];
            if (buff.Def.Kind == "dot") continue;
            buff.Remaining = Math.Max(0, buff.Remaining - dt);
            if (buff.Remaining <= 0) Buffs.RemoveAt(i);
        }
    }
    /// <summary>灼烧跳伤：**先扣时间再跳伤**（与从前的写法逐字一致，跳数因此不变）。
    /// 返回这一帧是否有灼烧在跳。</summary>
    public bool TickDot(double dt, out double damage, out string source)
    {
        damage = 0; source = "";
        if (Status("dot") is not { } dot) return false;
        dot.Remaining = Math.Max(0, dot.Remaining - dt);
        damage = dot.Value * dt;
        source = dot.Source;
        if (dot.Remaining <= 0) Buffs.Remove(dot);
        return true;
    }
}

/// <summary>短期效果不跨关卡；重进游戏清理表现效果，但保留已保存的敌人 HP 和技能冷却。</summary>
public sealed class CombatEffect
{
    public string Kind { get; init; } = "";
    public double X { get; set; }
    // 锁定的目标。追踪类弹丸在**落点重索敌**时会改指向新的敌人（见 GameSession.ReacquireTarget），所以是可写的。
    public long Target { get; set; }
    // 当前这一"段"的起点 X（逻辑坐标）。追踪弹被索敌重新指向之后，画面上那一段弧要从落点重新起算，
    // 否则第二段会继续用发射点的位置算进度，表现为凭空跳一下。非追踪形态不用它。
    public double LegX { get; set; }
    // 已经重新索敌过几次。追踪弹只允许有限次改换目标（`GameSession.MaxReacquire`），
    // 免得一只都追不到时无限接力、弹丸永远不消失。
    public int Reacquired { get; set; }
    // 追踪类弹道记住的"目标最后所在的位置"。目标中途死亡后，弹丸靠它把这一程飞完、到点即散，
    // 而不是改成朝发射方向一直飘出去（那会变成贴着地面平行右移，看着像脱靶）。
    public double TargetX { get; set; }
    /// <summary>
    /// **DMG1**：这一发的原始伤害（`攻击力 × 技能倍率 + 平值`），**不含任何条件乘区**。
    /// 暴击倍率、共享伤害倍率窗、斩杀、利用状态、易伤一律不在这里——它们在落地那一刻由
    /// `DamageFormula` 按目标当下的状态算（见 <see cref="Critical"/> 与 `GameSession.Hit`）。
    /// 所以读这个数做判断（例如"这一发有多重"）时要清楚它只是**出手时**的原始值。
    /// </summary>
    public double Damage { get; init; }
    /// <summary>
    /// **出手快照**：这一发产生时摇定的暴击结果。此后由它派生的一切结算共享这份快照
    /// （同一弹丸的其余命中、地面场每跳、天降落点、灼烧每跳），**不重新摇**——
    /// 期望值不变（线性期望），但一次出手的观感与账目对得上。见 `docs/design/combat.md` 的「出手快照」。
    /// </summary>
    public bool Critical { get; init; }
    /// <summary>出手那一刻的暴击倍率面板值（`fightattr.crit_damage`）。是否生效由 <see cref="Critical"/> 决定。</summary>
    public double CritDamage { get; init; } = 1;
    /// <summary>
    /// 出手那一刻生效的**共享伤害倍率窗**（**显式声明了伤害倍率窗**的增益类法术写入，见
    /// `SkillBuff.damage_window`），非生效期为 1。
    /// 它是攻击者侧的 **Build** 乘区因子，所以跟着快照走；召唤物射击与灼烧跳伤因此不再重算它。
    /// </summary>
    public double BuffPower { get; init; } = 1;
    public double Life { get; set; }
    public double Timer { get; set; }
    // 出场前的等待秒数：> 0 时这效果还没"发生"——不结算、表现层也不画。
    // 影分身那一式靠它晚一拍出现（与本体同帧落地会糊成一团），见 GameSession.TickEffects。
    public double Delay { get; set; }
    public bool Hostile { get; init; }
    // 影分身（剑二十三）放出的那一份：伤害按继承比例打折、弹道起点在分身身上。
    // 表现层据此不重复挂技能名标签，Core 据此不让它替本体缩冷却。Effects 不落盘，故不需要动存档格式。
    public bool Mirrored { get; init; }
    // 分身那一份**由哪个法术复制出来**（= 剑二十三的 id）。只给伤害统计归因用，见 `DamageSource`。
    public string MirrorSkill { get; init; } = "";
    /// <summary>
    /// 伤害统计归因用的来源 id。影分身那一份算在**复制它的那个法术**（剑二十三）名下，而不是被复制的这一式——
    /// 分身的价值要能单独看见，否则它永远藏在别人身上、永远查不出"剑二十三到底值多少"。
    /// 其余情况就是 `Skill` 本身。
    /// </summary>
    public string DamageSource => Mirrored && MirrorSkill != "" ? MirrorSkill : Skill;
    // **仅表现**：出生点 X。只在"生成带"模式下与 `X` 不同——剑从天上宽带里的出生点**斜落向** `X`，
    // 于是黑洞与飞行轨迹都画在 SpawnX 那边，而**判定一律用 `X`**（落点）。与 Arc / Jitter 同构：
    // Core 赋值一次、表现层只读，Effects 不落盘所以不用动存档格式。
    // 为 0 表示与 `X` 相同（默认的"原地垂直落下"）。
    public double SpawnX { get; init; }

    // 表现用来源标记：Skill 为产出该效果的法术/剑灵技能 ID，空表示敌方效果。
    // 仅次级效果无法区分来源（不同的持续伤害可能挂着同一种次级效果），故显式带上来源。
    public string Skill { get; init; } = "";
    // 初始时长，供界面计算渐隐与施法进度；不参与战斗判定。
    // 追踪弹重新索敌时会连同 Life 一起重置（那一段有自己的航程），所以是可写的。
    public double MaxLife { get; set; }
    // 命中时挂什么状态：**带那份状态的配置本身**（`SkillEffect.buff_id` 在加载期解析好），
    // 而不是把强度与寿命抄一份上来。从前效果实例上带着 `Secondary/SecondaryValue/SecondaryDuration`
    // 三个副本，读的时候再回到 `ApplySecondary` 里按字符串分派——同一件事有两个真相源。
    public BuffDef? Buff { get; init; }
    // 命中时的**条件型倍率**（DMG3 的 Build 乘区）：`target_hp_below`（斩杀）/ `target_has_state`（利用状态）。
    // 它是"这一发遇到的条件"，与"技能定义"分属两层，见 `docs/design/combat.md`。
    public string Condition { get; init; } = "";
    public double ConditionValue { get; init; }
    public double AoeRadius { get; init; }
    /// <summary>
    /// 周期结算的间隔（秒），来自效果行的 `tick_interval`。只有地面力场与天降火海会用到
    /// （见 `GameSession.TickEffects` 的 ground 分支）。它**跟着效果实例走**而不是去查全局设置——
    /// 同一份数值在模拟与期望模型里必须是同一个来源，而查全局设置会让"这一片场几秒跳一次"
    /// 没法按机制各自配（模型那边也要跟着读同一处）。
    /// </summary>
    public double TickInterval { get; init; }
    // 命中时把目标沿背离玩家的方向推开的逻辑距离；0 表示不击退。
    public double Knockback { get; init; }
    // 命中时把目标**朝本效果的中心**拉近的逻辑距离；0 表示不吸。
    // 是 Knockback 的反向孪生（一个推离玩家、一个吸向效果中心），同样不占 secondary——
    // 所以"吸 + 减速"能同时挂在一个技能上（寒冰龙卷）。收敛到中心即停，不会来回弹。
    public double Gather { get; init; }
    // 「各锁一敌」的剑陨（`SwordSkill.band > 0`）那一式。落地时 Core 要据此留一小段**剑气爆炸的余韵**
    // （一个 `Damage = 0` 的 ground 效果），表现层再按技能 ID 画扩散动画——所以这个旋钮得跟着效果走。
    public double Band { get; init; }
    // 落点/范围结算改为命中全体合法敌人（aoe_radius 退为表现层的落点预警圈）。
    public bool AoeAll { get; init; }
    // 这一发能打到哪一层（空 = both）。与 monster.layer 配对，飞行单位免疫 ground 定位的效果。
    public string Layer { get; init; } = "";
    // 飞行形态（空 = 直线弹道 bolt）：决定 X 是否冻结、是否先悬浮蓄势。逻辑与表现都读它。
    public string Trajectory { get; init; } = "";
    // 弹群内序号：Core 按选敌顺序编号，表现层据此错开站位与绘制层次（Core 不算显示位置）。
    public int Index { get; init; }
    // 仅表现：弧线形态的弧度，由 Core 用种子随机一次摇定；交给表现层逐帧摇会抖动且不可复现。
    // 落点重索敌时**取反**——画面上就是"划一道反向的弧追向新目标"，两段连起来像个波。
    public double Arc { get; set; }
    // 仅表现：0..1 的通用抖动，同样由 Core 摇定一次；天降形态拿它做出生高度的高低差。
    public double Jitter { get; init; }
    // 飞行速度（逻辑单位/秒）。只有法术会写入配置值；普攻、宠物弹与召唤弹一律用默认 1500，
    // 所以新增 speed 配置列不会连带改动它们。
    public double Speed { get; init; } = 1500;
    // 恒穿透：由形态自带（`line_pierce` 平射贯穿）。与下面的**概率穿透**分开。
    // 退役表里那个 `secondary = pierce` 与它走的是同一段代码，所以技能结构重构时合并成了一个形态
    // （"命中方式"归 Effect 之后，它不再需要占一个次级效果的名额）。
    public bool Pierce => Trajectory == "line_pierce";
    // 概率穿透（line_shot 专用）：首次命中时的一次判定机会，由配置给概率。
    public double PierceChance { get; init; }
    // 是否已经用掉过那次判定机会。用完置真，此后命中一律销毁——"概率穿透只在第一次击中触发"。
    public bool Pierced { get; set; }
    public List<long> Hit { get; } = [];
}
