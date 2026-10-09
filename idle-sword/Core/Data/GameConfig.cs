using System.Globalization;

namespace IdleSword.Core;

// Layer：怪物所在层，ground / air（空 = ground）。飞行单位免疫地面定位的技能，见 SwordSkill.hits。
public sealed record MonsterDef(string Id, string Name, string Kind, string Layer, double Hp, double Atk, double Range, double Interval, double Speed, string Attack, double Gold, string Visual);
public sealed record LevelDef(string Id, string Name, int Order, int Cells, double HpScale, double AtkScale, double EliteHp, double EliteAtk, double BossHp, double BossAtk, double RiftHp, string Wave, string Boss, string Rift, string FirstReward, string RepeatReward);
// 一条波次刷什么由子表 wave_unit 决定（一条波次可混编多种怪），这里只剩节奏、精英设置与强度系数。
// HpScale / AtkScale 是**波次自身**的强弱梯度，乘在 level.normal_hp / normal_atk 之上，只作用于 kind = normal。
// 精英 / BOSS / 裂隙不吃这两个系数：它们是关卡节点，不是波次阵容的一部分。
// Count 是这条波次在第 1 关的**总只数**，之后随关卡按全局倍率放大、到 `CountMax` 封顶（CountMax 必须不小于 Count）。
// 只数放在**波次**这一层而不是各模板上：模板只管"刷什么、按什么比例"，于是"这波多刷几只"不会顺手改掉阵容配比。
public sealed record WaveDef(string Id, double Interval, int EliteEvery, string Elite, double HpScale, double AtkScale, int Count, int CountMax);
// Weight 是该模板在这条波次里的**比例**（不是绝对只数）：整波只数由 wave.count / count_growth / count_max 决定。
public sealed record WaveUnitDef(string Monster, int Weight);
// TriggerChance：0 = 冷却到点自动释放（全部在役法术的默认）；> 0 = 不再自动释放，改为普攻出手时按此概率触发（神通）。
// CastRoot / Knockback：施放瞬间的全屏定身秒数、每次命中把目标推离玩家的逻辑距离，0 均表示无。
// Band：`sky_drop` 的"天上那排黑洞的铺开宽度"（0 = 各支在阵心两侧按 `spread` 对称铺开）。
// > 0 时**每支各锁一个（尽量不同的）目标、落在它当时的位置爆炸**，而黑洞以**目标群的中轴**为心铺开 band 宽。
// 锚在目标上而不是角色上：角色每秒走 340，一发 1.3 秒的轰炸若从角色量起，落点会甩到身后（18 苍穹剑陨）。
// 详见 docs/data/fields.md 的「各锁一敌」。
// Targeting：空 / nearest = 最近的合法目标（受射程限制）；highest_hp = 全场血量最高者，无视射程。
// AoeAll：落点/范围结算命中全体合法敌人（aoe_radius 退为表现用）。
// Hits：能打到哪一层。空 / both = 打地面也打空中；ground 只打地面；air 只打空中。与 monster.layer 配对判定。
// `SkillDef` 与技能三层（Effect / Buff / 时间轴 / 触发器）的定义与校验都在 `Core/Data/SkillTable.cs`——
// 期望模型（`LevelCurve`）必须与游戏侧读同一份解析器，否则两边静默分叉。见那个文件顶部的说明。
/// <summary>唯一配置入口。读取源 CSV 后校验并建立索引，运行时不修改配置对象。</summary>
public sealed class GameConfig
{
    public static readonly string[] Files = ["monster.csv", "level.csv", "item.csv", "fightattr.csv", "SwordLevel.csv", "SwordSkill.csv", "SkillEffect.csv", "SkillBuff.csv", "SkillBuffTimeline.csv", "SkillBuffTrigger.csv", "Talent.csv", "Equip.csv", "Pet.csv", "PetSkill.csv", "PetEquip.csv", "wave.csv", "wave_unit.csv", "drop.csv", "spawn_point.csv", "TalentLayout.csv", "game_settings.csv"];
    /// <summary>
    /// **开关类**天赋效果（含解锁类）：语义是"大于 0 即生效"，不是档位。
    ///
    /// 名单**只此一处**——加载期校验与节点编辑器都从它取。以前它是 `Load` 里的一个局部变量，
    /// 编辑器要用就得再抄一遍，而那正是这份配置一直在避免的重复（两处名单迟早分叉）。
    /// 解锁类那三个由 `Systems.ByEffect` 现取：**它才是解锁系统的唯一登记处**。
    /// </summary>
    public static readonly string[] SwitchEffects =
        ["auto_basic", "ranged_basic", .. Systems.ByEffect.Keys];

    // 天赋节点的前置**不设条数上限**——从前的 2 条是拍脑袋定的，而语义改成"任意一条点亮即可"之后，
    // 多挂几条反而更好读（读者读的是"这几条里走通一条就行"，不是"这几条都得走通"）。
    // 节点编辑器仍然要镜像"根在 (0, rows/2)"与"只许向右延伸"这两条约束，否则它能写出加载期会拒绝的布局。

    public Dictionary<string, List<CsvRow>> Tables { get; } = [];
    public Dictionary<string, MonsterDef> Monsters { get; } = [];
    public List<LevelDef> Levels { get; } = [];
    public Dictionary<string, WaveDef> Waves { get; } = [];
    public Dictionary<string, List<WaveUnitDef>> WaveUnits { get; } = [];
    public Dictionary<string, SkillDef> Skills { get; } = [];
    /// <summary>技能三层（Effect / Buff / 时间轴 / 触发器）的全部定义。`Skills` 是它推导出来的
    /// 只读视图，**新代码请读这里**——见 `SkillTable.cs` 顶部。</summary>
    public SkillTable SkillTables { get; private set; } = null!;
    public List<CsvRow> Rows(string name) => Tables[name + ".csv"];
    public CsvRow Row(string name, string id) => Rows(name).Single(r => r.Text("id") == id);
    public double Setting(string id) => Row("game_settings", id).Number("value");
    public double Attr(string id) => Row("fightattr", id).Number("base_value");

    public static GameConfig Load(Func<string, string> read)
    {
        var c = new GameConfig();
        foreach (string file in Files)
        {
            var rows = CsvTable.Parse(file, read(file));
            var ids = new HashSet<string>();
            foreach (var r in rows)
                if (string.IsNullOrWhiteSpace(r.Text("id")) || !ids.Add(r.Text("id"))) throw r.Error("id", "为空或重复");
            c.Tables[file] = rows;
        }
        foreach (var r in c.Rows("monster"))
        {
            Choice(r, "kind", "normal", "elite", "boss", "rift"); Choice(r, "attack_type", "melee", "ranged", "magic", "none");
            if (r.Text("layer") != "") Choice(r, "layer", "ground", "air");
            Positive(r, "hp"); Positive(r, "attack_interval"); Nonnegative(r, "atk", "attack_range", "move_speed", "gold");
            if (r.Text("kind") == "rift" && (r.Number("atk") != 0 || r.Number("move_speed") != 0 || r.Text("attack_type") != "none")) throw r.Error("kind", "裂隙不能移动或攻击");
            var m = new MonsterDef(r.Text("id"), r.Text("name"), r.Text("kind"), r.Text("layer"), r.Number("hp"), r.Number("atk"), r.Number("attack_range"), r.Number("attack_interval"), r.Number("move_speed"), r.Text("attack_type"), r.Number("gold"), r.Text("visual"));
            c.Monsters.Add(m.Id, m);
        }
        foreach (var r in c.Rows("wave"))
        {
            c.Ref(r, "elite_id", "monster"); Positive(r, "interval"); Positive(r, "elite_every");
            Positive(r, "hp_scale"); Positive(r, "atk_scale");
            Positive(r, "count"); Positive(r, "count_max");
            // 上限低于基线就等于关卡越高刷怪越少，那是配错了，不是设计。
            if (r.Int("count_max") < r.Int("count")) throw r.Error("count_max", "不能小于第 1 关的基线只数 count");
            // 上界只是防手滑（例如想写 1.2 写成 12）——强度系数是"同一关内的波次梯度"，不该是量级跳变。
            foreach (var f in new[] { "hp_scale", "atk_scale" })
                if (r.Number(f) > 5) throw r.Error(f, "波次强度系数不应超过 5，量级调整请改 level.csv 的关卡倍率");
            c.Waves.Add(r.Text("id"), new(r.Text("id"), r.Number("interval"), r.Int("elite_every"), r.Text("elite_id"), r.Number("hp_scale"), r.Number("atk_scale"), r.Int("count"), r.Int("count_max")));
        }
        // 波次混编：一条波次可以配多行，每行一种怪与它在整波里的**比例**；偏移按跨行的连续序号铺开，不会两只叠在一起。
        foreach (var r in c.Rows("wave_unit"))
        {
            c.Ref(r, "wave_id", "wave"); c.Ref(r, "monster_id", "monster"); Positive(r, "weight");
            if (!c.WaveUnits.TryGetValue(r.Text("wave_id"), out var units)) c.WaveUnits[r.Text("wave_id")] = units = [];
            units.Add(new(r.Text("monster_id"), r.Int("weight")));
        }
        // 没有配任何单位的波次会刷不出怪，关卡直接空转——这是新子表最容易配错的地方，在加载期就拦住。
        foreach (var wave in c.Waves.Values)
            if (!c.Rows("wave_unit").Any(u => u.Text("wave_id") == wave.Id)) throw new InvalidDataException($"wave_unit.csv: 波次 {wave.Id} 没有配置任何怪物");
        // 一条波次的**只数上限**决定最末一只落在哪里：落点是 40 + i×100 横向铺开（i 跨模板连续），
        // 所以上限抬高不是免费的。超过下一格末尾就落到第 3 格——画面外、要走很久才进场，还会与下一格的怪交错。
        // 精英每 `elite_every` 波跟着一只、同样占一个落点，所以 +1。
        // 与 GameSession.Spawn 的落点算法是同一份约定；改那边要同步改这里。
        // （spawn_point 为空时下面那条 spawn_point 校验会给出更好的报错，这里先让路。）
        var spawn = c.Rows("spawn_point").FirstOrDefault();
        if (spawn is not null)
        {
            double cellWidth = c.Setting("cell_width"), spawnOffset = spawn.Number("offset");
            foreach (var wave in c.Waves.Values)
            {
                int cap = wave.CountMax + 1;
                double far = spawnOffset + 40 + cap * 100;
                if (far > 2 * cellWidth) throw new InvalidDataException($"wave.csv: 波次 {wave.Id} 的只数上限 {wave.CountMax}（含精英 {cap} 只）会把最末一只刷到第 3 格（落点 {far} > {2 * cellWidth}）");
            }
        }
        foreach (var r in c.Rows("drop")) { c.Ref(r, "item_id", "item"); Nonnegative(r, "amount"); }
        foreach (var r in c.Rows("level"))
        {
            c.Ref(r, "wave_id", "wave"); c.Ref(r, "boss_id", "monster"); c.Ref(r, "rift_id", "monster");
            foreach (var key in new[] { "cells", "normal_hp", "normal_atk", "elite_hp", "elite_atk", "boss_hp", "boss_atk", "rift_hp" }) Positive(r, key);
            if (r.Int("cells") < 2) throw r.Error("cells", "至少两格");
            if (c.Monsters[r.Text("boss_id")].Kind != "boss" || c.Monsters[r.Text("rift_id")].Kind != "rift") throw r.Error("boss_id", "BOSS 或裂隙分类错误");
            var first = c.Rows("drop").Where(d => d.Text("group_id") == r.Text("first_reward")).ToList();
            var repeat = c.Rows("drop").Where(d => d.Text("group_id") == r.Text("repeat_reward")).ToList();
            if (first.Count == 0 || repeat.Count == 0) throw r.Error("first_reward", "奖励组不存在");
            if (first.Where(d => d.Text("item_id") == "core").Sum(d => d.Number("amount")) != 1) throw r.Error("first_reward", "每关首杀必须恰好给 1 个灵核");
            if (repeat.Any(d => d.Text("item_id") == "core")) throw r.Error("repeat_reward", "重复奖励不得包含灵核");
            c.Levels.Add(new(r.Text("id"), r.Text("name"), r.Int("order"), r.Int("cells"), r.Number("normal_hp"), r.Number("normal_atk"), r.Number("elite_hp"), r.Number("elite_atk"), r.Number("boss_hp"), r.Number("boss_atk"), r.Number("rift_hp"), r.Text("wave_id"), r.Text("boss_id"), r.Text("rift_id"), r.Text("first_reward"), r.Text("repeat_reward")));
        }
        c.Levels.Sort((a, b) => a.Order.CompareTo(b.Order));
        if (c.Levels.Count == 0 || c.Levels.Select(l => l.Order).Distinct().Count() != c.Levels.Count) throw new InvalidDataException("level.csv: 关卡为空或排序重复");
        foreach (var r in c.Rows("SwordLevel")) { Nonnegative(r, "cost_gold"); r.Flag("default_unlocked"); }
        // ── 技能三层：Skill / Effect / Buff ──────────────────────────────────
        // 解析、校验与"派生视图"全部收在 `SkillTable.Parse`：期望模型（`LevelCurve`）与游戏侧
        // 必须读同一份实现，否则两边静默分叉（本工程栽过一次，见 `SkillTable.cs` 顶部）。
        // 这里只剩一件本文件才知道的事：境界引用，那要查 `SwordLevel.csv`。
        foreach (var r in c.Rows("SwordSkill")) c.Ref(r, "realm_id", "SwordLevel");
        c.SkillTables = SkillTable.Parse(c.Rows("SwordSkill"), c.Rows("SkillEffect"), c.Rows("SkillBuff"),
            c.Rows("SkillBuffTimeline"), c.Rows("SkillBuffTrigger"));
        foreach (var (id, skill) in c.SkillTables.Skills) c.Skills.Add(id, skill);
        // ── 修行星图：内容表 + 布局表 ──────────────────────────────────────
        // 刻意拆成两张表：**几何与拓扑由工具整份拥有**（下一轮的节点编辑器），数值与文案人工维护。
        // 位置是画出来的、数值不是，让工具去猜数值只会帮倒忙。两表 id 必须一一对应，缺哪一边都拒绝加载。
        int gridRows = (int)c.Setting("talent_grid_rows");
        foreach (var r in c.Rows("Talent"))
        {
            if (r.Text("name").Length == 0) throw r.Error("name", "不能为空");
            Positive(r, "max_level"); Nonnegative(r, "effect_per_level");
            // `cost` 是每级消耗列表，第 i 项 = 从 i-1 级点到 i 级的价。**项数必须等于 max_level**——
            // 补出来的价格没人认得出是错的。
            var costs = r.NumberList("cost");
            if (costs.Count != r.Int("max_level")) throw r.Error("cost", $"必须写全 {r.Int("max_level")} 项（每级一项），实际 {costs.Count} 项");
            if (costs.Any(v => v < 0)) throw r.Error("cost", "不能小于 0");
            // 消耗必须是 item 表里 kind=currency 的道具（灵石 / 灵核），不能填成装备之类。
            var currency = c.Rows("item").SingleOrDefault(i => i.Text("id") == r.Text("cost_currency"))
                ?? throw r.Error("cost_currency", $"引用的道具不存在: {r.Text("cost_currency")}");
            if (currency.Text("kind") != "currency") throw r.Error("cost_currency", "必须引用 item.csv 里 kind=currency 的道具");
            Choice(r, "icon", "attack", "defense", "utility", "special");
            // 取值分两类：**数值**（atk / hp / atk_flat / hp_flat）按 `effect_per_level × 等级` 求和；
            // **开关**（auto_basic / ranged_basic，以及 Systems.ByEffect 里那几个解锁类）
            // 只认">0 即已生效"，语义是开关而不是档位。
            string effect = r.Text("effect");
            // 约定：解锁类效果一律以 `_system` 结尾，且**必须在 Systems.ByEffect 里登记**。
            // 漏登记的后果是那个系统**永久锁死且不报任何错**，所以在这里当场拦住。
            if (effect.EndsWith("_system") && !Systems.ByEffect.ContainsKey(effect))
                throw r.Error("effect", $"解锁类效果 {effect} 没有在 Systems.ByEffect 里登记（那个系统将永远打不开）");
            // 开关类的名单**只此一处**（见 `SwitchEffects`）：加载期校验与节点编辑器都从它取。
            var switches = SwitchEffects.ToList();
            var choices = new List<string> { "none", "atk", "hp", "atk_flat", "hp_flat", "drop_flat" };
            choices.AddRange(switches);
            Choice(r, "effect", [.. choices]);
            // 开关类节点满级只有 1 级：写成多级的话"点第二级"什么都不会发生，是典型的静默失效。
            if (switches.Contains(effect) && r.Int("max_level") != 1)
                throw r.Error("max_level", "解锁 / 开关类效果只能是 1 级");
            // 开关类的 `effect_per_level` **必须大于 0**：判据是"大于 0 即生效"，
            // 填 0 的话这个节点**买了也不生效**——而且不报任何错（系统永远打不开、普攻永远不转远程）。
            // 这条特别容易踩：编辑器补的骨架行 `effect_per_level` 就是 0，给它换个 `*_system` 效果
            // 却忘了改这一格，加载期会照常放行，玩起来只是"点了没反应"。
            if (switches.Contains(effect) && r.Number("effect_per_level") <= 0)
                throw r.Error("effect_per_level", "开关 / 解锁类效果的每级效果量必须大于 0（判据是\"大于 0 即生效\"，填 0 等于买了不生效）");
            // `none` 是编辑器给新节点补的占位行：可购买却没有效果 = 让玩家白花钱，所以必须免费。
            if (r.Text("effect") == "none" && costs.Any(v => v != 0)) throw r.Error("cost", "占位节点（effect=none）的每级消耗必须为 0");
        }
        foreach (var r in c.Rows("TalentLayout"))
        {
            int row = r.Int("row"), col = r.Int("col");
            if (row < 0 || row >= gridRows) throw r.Error("row", $"必须为 0～{gridRows - 1}");
            // `col` **可以为负**：根的行列都由设计摆，而"想在起点左边再长一节"就必须能往左。
            // 从前这里卡着 `col < 0` 拒绝——根一旦被推到第 0 列左边就**再也存不下来**，
            // 等于把"向左扩展"整条路堵死。向右那一侧本来就没有上限，左边也不该有。
            var prereqs = r.TextList("prereq");
            // **不设条数上限**，也**没有"需满级"那一档**：前置的语义是"任意一条点亮即可"（见 `TalentVisible`），
            // 这里只管结构合法性——不自指、不重复、引用存在、不成环、只能向右。
            if (prereqs.Distinct().Count() != prereqs.Count) throw r.Error("prereq", "同一个节点被写了两遍");
            if (prereqs.Contains(r.Text("id"))) throw r.Error("prereq", "不能把自己设为前置");
        }
        var layout = c.Rows("TalentLayout").ToDictionary(r => r.Text("id"));
        var content = c.Rows("Talent").Select(r => r.Text("id")).ToHashSet();
        var missingContent = layout.Keys.Where(id => !content.Contains(id)).ToList();
        if (missingContent.Count > 0) throw new InvalidDataException("Talent.csv 缺少 TalentLayout.csv 里这些节点的内容行: " + string.Join(", ", missingContent));
        var missingLayout = content.Where(id => !layout.ContainsKey(id)).ToList();
        if (missingLayout.Count > 0) throw new InvalidDataException("TalentLayout.csv 缺少 Talent.csv 里这些节点的位置: " + string.Join(", ", missingLayout));
        if (layout.Values.Select(r => (r.Int("col"), r.Int("row"))).Distinct().Count() != layout.Count)
            throw new InvalidDataException("TalentLayout.csv: 节点坐标重复");
        // 恰好一个根 = **唯一那个没有前置的节点**。它的**行列都不限制**：
        // 从前要求固定 `(0, 2)`，那是排版偏好而不是技术要求——而它让"想在起点前面插几个节点"变得很难做
        // （新起点只能挤在同一格）。放开之后，根落在哪一行由设计决定（左上、左下都行），
        // 而"它一定在最左一列"是**推导出来的**：每个非根节点都有前置，前置的列 ≤ 自己，
        // 顺着追下去必然落到这个没有前置的节点上，所以它的列号天生就是全图最小。
        var roots = layout.Values.Where(r => r.TextList("prereq").Count == 0).ToList();
        if (roots.Count != 1) throw new InvalidDataException($"TalentLayout.csv: 必须恰好有一个根节点（没有前置的那个），实际 {roots.Count} 个");
        foreach (var r in layout.Values)
            foreach (var p in r.TextList("prereq"))
            {
                if (!layout.TryGetValue(p, out var up)) throw r.Error("prereq", $"引用了不存在的节点: {p}");
                // **列号不再约束前置**：从前要求"前置必须在左边或同列"（星图只能向右延伸），
                // 那让"把右边那个节点当前置"做不成，摆图不够灵活。
                // 去掉它是安全的——**成环由上面那段 DFS 单独拦**，不依赖列号；
                // 代价只是"列号不再等于拓扑序"，也就是连线可能往回拐（好不好看交给设计判断）。
            }
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidDataException($"TalentLayout.csv: 前置连成了一个环 {id}");
            foreach (var p in layout[id].TextList("prereq")) Visit(p);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var id in layout.Keys) Visit(id);
        foreach (var r in c.Rows("PetSkill")) { Positive(r, "cooldown"); Positive(r, "power"); Positive(r, "range"); }
        foreach (var r in c.Rows("Pet")) { c.Ref(r, "skill_id", "PetSkill"); Positive(r, "weight"); }
        foreach (var r in c.Rows("PetEquip")) { Positive(r, "power"); Positive(r, "cost_gold"); }
        foreach (var r in c.Rows("Equip")) { Positive(r, "base_atk"); Positive(r, "craft_cost"); Positive(r, "upgrade_cost"); Positive(r, "refine_cost"); }
        foreach (var r in c.Rows("spawn_point"))
        {
            Positive(r, "offset"); if (r.Number("offset") >= c.Setting("cell_width")) throw r.Error("offset", "必须在格内");
        }
        // 属性表的每一行自带**上下限**（`min_value` / `max_value`），所以"哪个属性不能为 0"写在它自己那一行上，
        // 不再是一份与表并行维护的硬编码 id 名单——那种名单迟早会与表分叉。
        // 例：`basic_interval = 0` 会让普攻永不出手、`melee_range = 0` 会让近战永远够不着，
        // 两者都不报错、只让一整个形态不能用，所以它们自己的 min_value 就是 0.01。
        //
        // `max_value` **允许留空 = 不设上限**（攻击力这类属性本来就该无上限）。
        // 这里校验的是**基础值**；各项加成之后的**合计值**由 `GameSession` 用同一对上下限夹取
        // （暴击率合计不得超过 1 之类），两处读的是同一列，不会各写一个数。
        foreach (var r in c.Rows("fightattr"))
        {
            Nonnegative(r, "base_value");
            double min = Optional(r, "min_value") ?? throw r.Error("min_value", "不能留空（只允许 max_value 留空）");
            double? max = Optional(r, "max_value");
            if (max is double high && high < min) throw r.Error("max_value", "不能小于 min_value");
            if (r.Number("base_value") < min) throw r.Error("base_value", $"低于下限 {min}");
            if (max is double cap && r.Number("base_value") > cap) throw r.Error("base_value", $"超出上限 {cap}");
        }
        // 全局设置一律必须为正……**除了 `starting_gold`**：开局不给钱是合法的设计选择
        // （第一点修为必须靠杀怪换来），而"必须大于 0"会把它拦在加载期。
        foreach (var r in c.Rows("game_settings"))
            if (r.Text("id") == "starting_gold") Nonnegative(r, "value");
            else Positive(r, "value");
        // 灵核奖励组只能被关卡首杀入口引用；不能通过通用奖励调用入账。
        var firstGroups = c.Levels.Select(l => l.FirstReward).ToHashSet();
        foreach (var r in c.Rows("drop").Where(r => r.Text("item_id") == "core"))
            if (!firstGroups.Contains(r.Text("group_id"))) throw r.Error("group_id", "灵核只允许出现在首杀奖励组");
        return c;
    }
    private void Ref(CsvRow r, string field, string table)
    {
        if (!Rows(table).Any(t => t.Text("id") == r.Text(field))) throw r.Error(field, $"引用不存在: {r.Text(field)}");
    }
    private static void Positive(CsvRow r, string f) { if (r.Number(f) <= 0) throw r.Error(f, "必须大于 0"); }
    /// <summary>可留空的数值列：空串 / 纯空白 → null。
    /// 不能用 <see cref="CsvRow.Number"/> 读——它会把留空直接判成"需要有限数值"，
    /// 而"上限留空 = 不设上限"是属性表里最常见的写法。</summary>
    private static double? Optional(CsvRow r, string f)
    {
        string text = r.Text(f).Trim();
        if (text.Length == 0) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw r.Error(f, "需要有限数值（留空表示不设上限）");
        return value;
    }
    private static void Nonnegative(CsvRow r, params string[] fields) { foreach (var f in fields) if (r.Number(f) < 0) throw r.Error(f, "不能小于 0"); }
    private static void Choice(CsvRow r, string f, params string[] choices) { if (!choices.Contains(r.Text(f))) throw r.Error(f, "未知枚举值"); }
}
