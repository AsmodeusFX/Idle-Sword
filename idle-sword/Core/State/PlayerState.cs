namespace IdleSword.Core;

/// <summary>版本化存档 DTO。永久进度与本轮战斗分离，保存未收取参悟产物，无离线时间戳。</summary>
public sealed class PlayerState
{
    public int Version { get; set; } = 1;
    public Dictionary<string, double> Wallet { get; set; } = new() { ["gold"] = 0, ["core"] = 0 };
    // GM 调试发放记录：按货币 id 累计由调试入口发出的数量。存档校验据此区分
    // 「玩法产出」与「调试产出」，否则调试发出的妖核会被当成篡改而拒绝整份存档。
    public Dictionary<string, double> DebugGranted { get; set; } = [];
    public HashSet<string> FirstKills { get; set; } = [];
    public HashSet<string> UnlockedLevels { get; set; } = [];
    public HashSet<string> Realms { get; set; } = [];
    public Dictionary<string, int> Skills { get; set; } = [];
    public Dictionary<string, int> Talents { get; set; } = [];
    public Dictionary<string, int> Upgrades { get; set; } = [];
    public Dictionary<string, double> PendingIntent { get; set; } = [];
    public Dictionary<string, double> IntentTimers { get; set; } = [];
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
    // 次级效果状态：Until 为剩余秒数，0 表示未生效；Factor 为生效期间的乘数。
    public double SlowUntil { get; set; }
    public double SlowFactor { get; set; } = 1;
    // 寒冷：减速之外多一个状态源，仅用于表现层把受击角色染成冰蓝。
    // 移速仍由 SlowUntil/SlowFactor 决定（chill 会同时置位那两个字段），所以战斗判定只有一条路径。
    public double ChillUntil { get; set; }
    public double StunUntil { get; set; }
    public double DotUntil { get; set; }
    public double DotDps { get; set; }
    // 施加这份灼烧的剑诀 id——伤害统计要按来源归因，而跳伤那条路上没有 `CombatEffect` 可查。
    // **多个来源的灼烧只留最后一次的施加者**（`ApplySecondary` 是赋值而非叠加，与 DotDps 同一条口径）；
    // 想要严格区分得给灼烧建模成"可叠加的多个实例"，那是另一件事，这里不额外建模。
    public string DotSkill { get; set; } = "";
    public double VulnerableUntil { get; set; }
    public double VulnerableFactor { get; set; } = 1;
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
    public double Damage { get; init; }
    public double Life { get; set; }
    public double Timer { get; set; }
    // 出场前的等待秒数：> 0 时这效果还没"发生"——不结算、表现层也不画。
    // 影分身那一式靠它晚一拍出现（与本体同帧落地会糊成一团），见 GameSession.TickEffects。
    public double Delay { get; set; }
    public bool Hostile { get; init; }
    // 影分身（剑二十三）放出的那一份：伤害按继承比例打折、弹道起点在分身身上。
    // 表现层据此不重复挂技能名标签，Core 据此不让它替本体缩冷却。Effects 不落盘，故不需要动存档格式。
    public bool Mirrored { get; init; }
    // 分身那一份**由哪个剑诀复制出来**（= 剑二十三的 id）。只给伤害统计归因用，见 `DamageSource`。
    public string MirrorSkill { get; init; } = "";
    /// <summary>
    /// 伤害统计归因用的来源 id。影分身那一份算在**复制它的那个剑诀**（剑二十三）名下，而不是被复制的这一式——
    /// 分身的价值要能单独看见，否则它永远藏在别人身上、永远查不出"剑二十三到底值多少"。
    /// 其余情况就是 `Skill` 本身。
    /// </summary>
    public string DamageSource => Mirrored && MirrorSkill != "" ? MirrorSkill : Skill;
    // **仅表现**：出生点 X。只在"生成带"模式下与 `X` 不同——剑从天上宽带里的出生点**斜落向** `X`，
    // 于是黑洞与飞行轨迹都画在 SpawnX 那边，而**判定一律用 `X`**（落点）。与 Arc / Jitter 同构：
    // Core 赋值一次、表现层只读，Effects 不落盘所以不用动存档格式。
    // 为 0 表示与 `X` 相同（默认的"原地垂直落下"）。
    public double SpawnX { get; init; }

    // 表现用来源标记：Skill 为产出该效果的剑诀/剑灵技能 ID，空表示敌方效果。
    // 仅次级效果无法区分来源（不同的持续伤害可能挂着同一种次级效果），故显式带上来源。
    public string Skill { get; init; } = "";
    // 初始时长，供界面计算渐隐与施法进度；不参与战斗判定。
    // 追踪弹重新索敌时会连同 Life 一起重置（那一段有自己的航程），所以是可写的。
    public double MaxLife { get; set; }
    // 次级效果与命中参数：Secondary 为空表示无；Pierce 由 Secondary == "pierce" 推导。
    public string Secondary { get; init; } = "";
    public double SecondaryValue { get; init; }
    public double SecondaryDuration { get; init; }
    public double ExecuteThreshold { get; init; }
    public double AoeRadius { get; init; }
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
    // 飞行速度（逻辑单位/秒）。只有剑诀会写入配置值；普攻、宠物弹与召唤弹一律用默认 1500，
    // 所以新增 speed 配置列不会连带改动它们。
    public double Speed { get; init; } = 1500;
    // 穿透：既可以是次级效果（退役配置用），也可以由形态自带（line_pierce 平射贯穿），
    // 后者让"形态决定怎么命中"而不必占用次级效果列。这是**恒穿透**，与下面的概率穿透分开。
    public bool Pierce => Secondary == "pierce" || Trajectory == "line_pierce";
    // 概率穿透（line_shot 专用）：首次命中时的一次判定机会，由配置给概率。
    public double PierceChance { get; init; }
    // 是否已经用掉过那次判定机会。用完置真，此后命中一律销毁——"概率穿透只在第一次击中触发"。
    public bool Pierced { get; set; }
    public List<long> Hit { get; } = [];
}
