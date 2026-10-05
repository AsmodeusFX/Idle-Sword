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
// 锚在目标上而不是角色上：角色每秒走 340，一发 1.3 秒的轰炸若从角色量起，落点会甩到身后（18 天陨）。
// 详见 docs/data/fields.md 的「各锁一敌」。
// Targeting：空 / nearest = 最近的合法目标（受射程限制）；highest_hp = 全场血量最高者，无视射程。
// AoeAll：落点/范围结算命中全体合法敌人（aoe_radius 退为表现用）。
// Hits：能打到哪一层。空 / both = 打地面也打空中；ground 只打地面；air 只打空中。与 monster.layer 配对判定。
public sealed record SkillDef(string Id, string Name, string Realm, string Kind, double Cooldown, double Range, double Power, double Duration, int MaxLevel, double Cost, double CostGrowth, string Description, string Secondary, double SecondaryValue, double SecondaryDuration, double AoeRadius, string Trajectory, int ProjectileCount, double HoverTime, double ArcMin, double ArcMax, double Speed, double Spread, double VolleyInterval, double VolleyJitter, double SpawnJitter, double PierceChance, double SecondaryExtra, double TriggerChance, double TriggerChanceStep, double CastRoot, double Knockback, string Targeting, bool AoeAll, string Hits, double Gather, double Band);

/// <summary>唯一配置入口。读取源 CSV 后校验并建立索引，运行时不修改配置对象。</summary>
public sealed class GameConfig
{
    public static readonly string[] Files = ["monster.csv", "level.csv", "item.csv", "fightattr.csv", "SwordLevel.csv", "SwordSkill.csv", "Talent.csv", "Equip.csv", "SwordUpgrade.csv", "Pet.csv", "PetSkill.csv", "PetEquip.csv", "wave.csv", "wave_unit.csv", "drop.csv", "spawn_point.csv", "TalentLink.csv", "contemplation.csv", "game_settings.csv"];
    public Dictionary<string, List<CsvRow>> Tables { get; } = [];
    public Dictionary<string, MonsterDef> Monsters { get; } = [];
    public List<LevelDef> Levels { get; } = [];
    public Dictionary<string, WaveDef> Waves { get; } = [];
    public Dictionary<string, List<WaveUnitDef>> WaveUnits { get; } = [];
    public Dictionary<string, SkillDef> Skills { get; } = [];
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
        foreach (var r in c.Rows("SwordSkill"))
        {
            c.Ref(r, "realm_id", "SwordLevel"); Choice(r, "kind", "projectile", "target", "ground", "buff", "summon");
            foreach (var key in new[] { "cooldown", "range", "power", "duration", "max_level", "cost", "cost_growth" }) Positive(r, key);
            var secondary = r.Text("secondary");
            if (secondary != "") Choice(r, "secondary", "pierce", "multi", "slow", "stun", "dot", "vulnerable", "chill", "lifesteal", "execute", "shield", "regen", "haste", "crit_reduce", "mirror", "bonus_vs_state");
            Nonnegative(r, "secondary_value", "secondary_duration", "aoe_radius", "secondary_extra", "pierce_chance", "trigger_chance", "trigger_chance_step");
            // 利用状态（bonus_vs_state）：对**携带任意状态**的目标增伤。它不写任何状态字段，只在 Hit 里当乘区用，
            // 所以值必须为正——配成 0 就是一行什么都不做的死配置。
            if (secondary == "bonus_vs_state" && r.Number("secondary_value") <= 0)
                throw r.Error("secondary_value", "利用状态（bonus_vs_state）需要正的增伤比例");
            // 影分身（mirror，身外身）：它是**自身的短时状态**，不分敌、也不生成单位，所以必须是 buff。
            // secondary_value 是继承比例的小数（0.7 = 七成），误填成 70 不会报任何错、只会静默变成 7000%，故在这里拦下。
            if (secondary == "mirror")
            {
                if (r.Text("kind") != "buff") throw r.Error("kind", "影分身（mirror）必须是 buff 类：它是自身的状态，不是单位");
                if (r.Number("secondary_duration") <= 0) throw r.Error("secondary_duration", "影分身需要正的持续时间");
                if (r.Number("secondary_value") > 1) throw r.Error("secondary_value", "影分身的继承比例是 0～1 的小数（0.7 = 七成）");
            }
            if (r.Number("pierce_chance") > 1) throw r.Error("pierce_chance", "是概率，取值 0～1");
            // 触发概率：0 表示沿用「冷却到点自动释放」，> 0 表示改为普攻出手时按概率触发（神通）。
            // 两者互斥，不需要额外的触发方式列：0 就是自动释放那一档。
            if (r.Number("trigger_chance") > 1) throw r.Error("trigger_chance", "是概率，取值 0～1");
            // gather 是 knockback（推开）的反向孪生：每次命中把目标**朝效果中心**拉近，0 = 不吸。
            // 两者都是正交旋钮，不占 secondary，所以"吸 + 减速"能同时挂在同一个技能上（扎根）。
            Nonnegative(r, "cast_root", "knockback", "gather", "band");
            // 黑洞铺开宽度：只有 sky_drop 会读它——别的形态配了就是一行什么都不做的死配置，拦下来别让它静默失效。
            if (r.Number("band") > 0 && r.Text("trajectory") != "sky_drop")
                throw r.Error("band", "只有 sky_drop 形态支持黑洞铺开宽度（band）");
            // 铺得太宽就夹不住画面了（表现层要把这条带子夹在 1920 逻辑画布里，可用的横向余量只有 1460）。
            if (r.Number("band") > 1400)
                throw r.Error("band", "黑洞铺开宽度超过 1400 就夹不进画面（角色锚点在 330、逻辑宽 1920）");
            if (r.Text("targeting") != "") Choice(r, "targeting", "nearest", "highest_hp", "lowest_hp", "farthest");
            r.Flag("aoe_all");
            // 技能定位：空 = both（打地面也打空中）。飞行单位只吃 air / both。
            if (r.Text("hits") != "") Choice(r, "hits", "ground", "air", "both");
            // 飞行形态：空 = bolt（原直线弹道，行为不变）。形态与数量都落在配置里，
            // 同类弹道的新技能只改 CSV，不必新增 kind（音效映射与五 kind 自检因此不受影响）。
            var trajectory = r.Text("trajectory");
            var count = r.Int("projectile_count");
            if (trajectory != "")
            {
                Choice(r, "trajectory", "bolt", "hover_homing", "sky_drop", "arc_homing", "line_pierce", "line_shot");
                if (r.Text("kind") != "projectile") throw r.Error("trajectory", "仅 projectile 可使用自定义飞行形态");
            }
            if (count < 1) throw r.Error("projectile_count", "至少 1");
            if (r.Text("kind") != "projectile" && count != 1) throw r.Error("projectile_count", "非 projectile 只能为 1");
            Nonnegative(r, "hover_time", "arc_max", "spread", "volley_interval", "volley_jitter", "spawn_jitter");
            // 弧度允许为负：负值表示从下方掠过（上方空间多、下方少）。下限 -60，再大就会插进地面与下方 UI。
            if (r.Number("arc_min") < -60) throw r.Error("arc_min", "下弧不得超过 -60（会越出地面与下方界面）");
            if (r.Number("arc_min") > r.Number("arc_max")) throw r.Error("arc_min", "不能大于 arc_max");
            if (trajectory == "arc_homing")
            {
                // 弧顶 = 发射高度约 300 − 弧高；弧高超过 300 会顶出战斗区上沿（表现层另有夹取兜底，此处提前拦住手改配置）。
                if (r.Number("arc_max") <= 0) throw r.Error("arc_max", "弧线形态需要正的弧度上界");
                if (r.Number("arc_max") > 300) throw r.Error("arc_max", "弧度超过 300 会越出战斗画面");
            }
            // 落点判定半径复用 aoe_radius：ground 在 0 时回退 220，天降形态绝不允许回退（否则会变成来路不明的大范围）。
            if (trajectory == "sky_drop" && r.Number("aoe_radius") <= 0) throw r.Error("aoe_radius", "天降形态需要正的落点判定半径");
            // 天降 + 灼烧 = 落地后残留一片火海（见 GameSession.TickEffects）：火海寿命取 secondary_duration，
            // 为 0 会变成落地即灭、DOT 也无从刷新，故此处提前拦住手改配置。
            if (trajectory == "sky_drop" && secondary == "dot" && r.Number("secondary_duration") <= 0)
                throw r.Error("secondary_duration", "天降火海需要正的残留时长");
            // 天降火海优先于全体结算（见 TickEffects），两者同配会让 aoe_all 静默失效——正是这套校验要拦的东西。
            if (trajectory == "sky_drop" && secondary == "dot" && r.Flag("aoe_all"))
                throw r.Error("aoe_all", "天降火海与全体命中互斥");
            // 弹速：0 表示沿用默认 1500（宠物弹与召唤弹也走那个默认值，不受本列影响）。
            var speed = r.Number("speed");
            if (speed != 0 && speed < 100) throw r.Error("speed", "0 表示默认 1500；显式配置时必须 ≥ 100");
            c.Skills.Add(r.Text("id"), new(r.Text("id"), r.Text("name"), r.Text("realm_id"), r.Text("kind"), r.Number("cooldown"), r.Number("range"), r.Number("power"), r.Number("duration"), r.Int("max_level"), r.Number("cost"), r.Number("cost_growth"), r.Text("description"), secondary, r.Number("secondary_value"), r.Number("secondary_duration"), r.Number("aoe_radius"), trajectory, count, r.Number("hover_time"), r.Number("arc_min"), r.Number("arc_max"), speed, r.Number("spread"), r.Number("volley_interval"), r.Number("volley_jitter"), r.Number("spawn_jitter"), r.Number("pierce_chance"), r.Number("secondary_extra"), r.Number("trigger_chance"), r.Number("trigger_chance_step"),
                r.Number("cast_root"), r.Number("knockback"), r.Text("targeting"), r.Flag("aoe_all"), r.Text("hits"), r.Number("gather"), r.Number("band")));
        }
        foreach (var r in c.Rows("Talent"))
        {
            if (r.Int("row") is < 1 or > 5) throw r.Error("row", "必须为 1～5");
            Positive(r, "column"); Positive(r, "max_level"); Nonnegative(r, "cost_gold", "cost_core", "value");
            Choice(r, "effect", "atk", "hp", "auto_intent");
        }
        foreach (var r in c.Rows("TalentLink")) { c.Ref(r, "from_id", "Talent"); c.Ref(r, "to_id", "Talent"); }
        if (c.Rows("Talent").Select(r => (r.Int("column"), r.Int("row"))).Distinct().Count() != c.Rows("Talent").Count)
            throw new InvalidDataException("Talent.csv: 节点坐标重复");
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidDataException($"TalentLink.csv: 存在环路 {id}");
            foreach (var link in c.Rows("TalentLink").Where(x => x.Text("from_id") == id)) Visit(link.Text("to_id"));
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var r in c.Rows("Talent")) Visit(r.Text("id"));
        foreach (var r in c.Rows("SwordUpgrade"))
        {
            c.Ref(r, "skill_id", "SwordSkill"); c.Ref(r, "currency_id", "item");
            Positive(r, "max_level"); Positive(r, "cost"); Positive(r, "value");
            // effect 决定"这一行加的是哪种东西"，界面按它渲染文案——填错会显示成别的东西，必须校验。
            // damage_percent = 技能威力；inherit_percent = 影分身的继承比例（身外身）。
            Choice(r, "effect", "damage_percent", "inherit_percent");
        }
        foreach (var r in c.Rows("contemplation"))
        {
            c.Ref(r, "item_id", "item"); if (r.Text("item_id") == "core") throw r.Error("item_id", "参悟不能产出灵核");
            Positive(r, "capacity"); Positive(r, "click_amount"); Positive(r, "auto_interval");
        }
        foreach (var r in c.Rows("PetSkill")) { Positive(r, "cooldown"); Positive(r, "power"); Positive(r, "range"); }
        foreach (var r in c.Rows("Pet")) { c.Ref(r, "skill_id", "PetSkill"); Positive(r, "weight"); }
        foreach (var r in c.Rows("PetEquip")) { Positive(r, "power"); Positive(r, "cost_gold"); }
        foreach (var r in c.Rows("Equip")) { Positive(r, "base_atk"); Positive(r, "craft_cost"); Positive(r, "upgrade_cost"); Positive(r, "refine_cost"); }
        foreach (var r in c.Rows("spawn_point"))
        {
            Positive(r, "offset"); if (r.Number("offset") >= c.Setting("cell_width")) throw r.Error("offset", "必须在格内");
        }
        // 普攻三个参数参与每次出手，取 0 会让普攻永不出手或零伤害，故按「必须为正」校验（其余属性可以为 0）。
        foreach (var r in c.Rows("fightattr"))
        {
            Nonnegative(r, "base_value");
            if (r.Text("id") is "basic_interval" or "basic_power" or "basic_range") Positive(r, "base_value");
        }
        foreach (var r in c.Rows("game_settings")) Positive(r, "value");
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
    private static void Nonnegative(CsvRow r, params string[] fields) { foreach (var f in fields) if (r.Number(f) < 0) throw r.Error(f, "不能小于 0"); }
    private static void Choice(CsvRow r, string f, params string[] choices) { if (!choices.Contains(r.Text(f))) throw r.Error(f, "未知枚举值"); }
}
