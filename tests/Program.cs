using IdleSword.Core;
using IdleSword.Features;

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("idle-sword/Config/Tables");
var source = GameConfig.Files.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(root, f)));
var config = GameConfig.Load(f => source[f]);
int passed = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
// 普攻默认关掉：受控用例要的是确定的伤害预算，而普攻每秒都在加伤害、也在消耗随机数。
// 需要验证真实循环的用例显式传 basic: true。
GameSession New(bool basic = false) { var g = new GameSession(config, seed: 42) { BasicAttackEnabled = basic }; return g; }
void Step(GameSession g, double seconds) { for (int i = 0; i < (int)Math.Ceiling(seconds / .05); i++) g.Step(.05); }
void ToBoss(GameSession g)
{
    g.Battle.PlayerX = (g.Level.Cells - 1) * config.Setting("cell_width") + 80;
    g.Battle.Enemies.Clear(); g.Battle.Spawns.Clear(); g.Battle.PlayerHp = g.MaxHp; g.Step(.05);
}
EnemyState Boss(GameSession g) => g.Battle.Enemies.Single(e => e.Kind == "boss");
void Reject(Action test) { try { test(); } catch (InvalidDataException) { return; } throw new Exception("Expected validation rejection"); }
// 按表头列名改写某一行的单元格：不再硬编码行尾字符串（列一多、值一改就会失效）。
string Cell(string text, string id, string field, string value)
{
    var lines = text.TrimStart('﻿').Split('\n');
    int at = Array.IndexOf(lines[0].TrimEnd('\r').Split(','), field);
    if (at < 0) throw new Exception("no such column: " + field);
    for (int i = 1; i < lines.Length; i++)
    {
        var cells = lines[i].TrimEnd('\r').Split(',');
        if (cells[0] != id) continue;
        if (cells.Length != lines[0].TrimEnd('\r').Split(',').Length) throw new Exception("column count mismatch on " + id);
        cells[at] = value; lines[i] = string.Join(',', cells);
        return string.Join('\n', lines);
    }
    throw new Exception("no such row: " + id);
}
// 单一剑诀 + 三个高血量靶子：靶子定身摆在 offset 处（不定身的话怪物会朝玩家走，弹道到达时机随之漂移）。
GameSession SalvoOn(GameConfig cfg, string skillId, double offset = 90)
{
    // 靶场一律关掉普攻：否则每秒多出一支飞行效果，既有"数得到几支剑"的断言全会被污染，
    // 而且普攻还要抽随机数，同 seed 的可复现序列也会跟着漂。
    var s = new GameSession(cfg, seed: 42) { BasicAttackEnabled = false }; s.Step(.05);
    foreach (var e in s.Battle.Enemies) { e.Atk = 0; e.Hp = e.MaxHp = 1e8; e.X = s.Battle.PlayerX + offset; e.StunUntil = 1e9; }
    s.State.Skills.Clear(); s.State.Skills[skillId] = 1; s.Battle.Cooldowns.Clear(); s.Effects.Clear();
    s.Step(.05);
    return s;
}
GameSession Salvo(string skillId, double offset = 90) => SalvoOn(config, skillId, offset);

Check("all tables / 100 stages / 15 skills / 60 intent upgrades", () => Assert(config.Levels.Count == 100 && config.Skills.Count == 15 && config.Rows("SwordUpgrade").Count == 60, "table counts"));
Check("skill roster: three per realm, rearranged as designed", () => {
    // 每境恰 3 个。本轮把青元剑芒下到炼气、万剑决与天剑下到筑基、剑气流云壁下到元婴，
    // 化神档腾出的位置给新技能剑二十三；剑侍归档，一进一出，在役数不变。
    var byRealm = config.Skills.Values.GroupBy(s => s.Realm).ToDictionary(g => g.Key, g => g.Select(s => s.Id).OrderBy(x => x).ToArray());
    Assert(byRealm.Count == 5 && byRealm.Values.All(v => v.Length == 3), "every realm holds exactly three skills");
    Assert(byRealm["realm_0"].SequenceEqual(new[] { "skill_01", "skill_04", "skill_11" }), "realm_0 = 御剑术 / 仙风云体术 / 青元剑芒");
    Assert(byRealm["realm_1"].SequenceEqual(new[] { "skill_06", "skill_14", "skill_17" }), "realm_1 = 万剑决 / 万剑归心 / 天剑");
    Assert(byRealm["realm_2"].SequenceEqual(new[] { "skill_02", "skill_07", "skill_12" }), "realm_2 = 焚天剑诀 / 御雷真诀 / 寒冰龙卷");
    Assert(byRealm["realm_3"].SequenceEqual(new[] { "skill_05", "skill_09", "skill_18" }), "realm_3 = 剑气流云壁 / 醉仙望月步 / 苍穹剑陨");
    Assert(byRealm["realm_4"].SequenceEqual(new[] { "skill_10", "skill_15", "skill_19" }), "realm_4 = 斩鬼神 / 诛仙剑阵 / 剑二十三");
    // 被挤出的四个已进归档表，不再参与加载（剑侍的跟随召唤机制留给以后的剑灵系统）。
    Assert(new[] { "skill_03", "skill_08", "skill_13", "skill_16" }.All(id => !config.Skills.ContainsKey(id)), "retired skills are gone");
});
Check("new and moved skills carry the intended effects", () => {
    // 天剑：改为**横向贯穿**（line_pierce），带斩杀；不锁层（空 hits = both）。
    // 它原来和御雷真诀、斩鬼神一样是"对最近的一只打一发"，三者撞车，这一改把它让了出来。
    var sky = config.Skills["skill_17"];
    Assert(sky.Secondary == "execute" && sky.SecondaryValue > 0, "天剑 executes");
    Assert(sky.Kind == "projectile" && sky.Trajectory == "line_pierce" && sky.Hits == "", "天剑 sweeps a line and hits both layers");
    // 青元剑芒：改为**收割**——取射程内血量最低的 3 个（御剑术打最前的，它打最脆的）。
    Assert(config.Skills["skill_11"].Targeting == "lowest_hp", "青元剑芒 reaps the weakest");
    // **金丹档只留一个真诀**：三个都做概率触发时整层不可控、成长反馈不明显，所以焚天剑诀与寒冰龙卷
    // 改回固定冷却，只留御雷真诀当"低频乱杀"的那一发。
    Assert(config.Skills["skill_07"].TriggerChance > 0, "御雷真诀 仍是真诀");
    Assert(config.Skills["skill_02"].TriggerChance == 0 && config.Skills["skill_12"].TriggerChance == 0,
        "焚天剑诀 / 寒冰龙卷 改回固定冷却");
    Assert(config.Skills.Values.Count(s => s.TriggerChance > 0) == 1, "全名单只留一个真诀");
    // 焚天剑诀：火海铺在**最靠前的那只**身上（迎宾位），不是脚边那只；火海也更持久。
    Assert(config.Skills["skill_02"].Targeting == "farthest" && config.Skills["skill_02"].SecondaryDuration >= 5,
        "焚天剑诀：落点在迎宾位、火海持久");
    // 寒冰龙卷：改成**聚怪**——形态从"推出去的平射贯穿"换成"落在目标位置的持续力场"，
    // 靠 `gather` 把周围的敌人朝风眼（阵心）拖近。`ground` 的落点本来就是选中目标的 X，所以
    // "在敌人位置生成"是现成的，不需要新形态。
    var tornado = config.Skills["skill_12"];
    Assert(tornado.Kind == "ground" && tornado.Trajectory == "" && tornado.AoeRadius > 0,
        "寒冰龙卷 改成落在目标位置的持续力场");
    Assert(tornado.Gather > 0 && tornado.Secondary == "chill",
        "寒冰龙卷 吸附与减速并存（两者都是正交旋钮，不占 secondary）");
    Assert(tornado.Knockback == 0, "寒冰龙卷 不再推人：聚怪与击退是反向的一对，同时挂着会互相抵消");
    // 预览靶场的靶子固定在 520 处（SkillPreview.PreviewTargetDistance）；射程比它还短的剑诀在预览页
    // **永远放不出来**（没有合法目标就不出手）——剑罡护体一度把射程压到 400，预览里连护盾都上不了，
    // 画面上什么都没有。这条断言就是为了拦住那种"配置上没错、但在预览里什么都看不到"的坑。
    Assert(config.Skills.Values.All(s => s.Range >= 520), "每个剑诀的射程都不短于预览靶距");
    // 斩鬼神：利用状态——对携带任意状态的目标增伤。
    Assert(config.Skills["skill_10"].Secondary == "bonus_vs_state" && config.Skills["skill_10"].SecondaryValue > 0,
        "斩鬼神 利用状态（目标带状态就增伤）");
    // 苍穹剑陨（原 18 大庚剑阵）：从"持续破甲地面"换成**爆炸类**——复用现成的 `sky_drop`，
    // 六柄剑在目标区域上空铺开后坠地，**靠寒冰龙卷把怪捏到一处才炸得满**。易伤（vulnerable）
    // 因此下线回到死配置（代码与自检覆盖保留），将来由剑意授予别的分支。
    var array = config.Skills["skill_18"];
    Assert(array.Kind == "projectile" && array.Trajectory == "sky_drop", "苍穹剑陨 是爆炸类的天降形态");
    Assert(array.ProjectileCount > 1 && array.AoeRadius > 0, "苍穹剑陨 多柄齐落、按落点半径结算");
    Assert(array.Secondary == "", "苍穹剑陨 不再挂易伤（易伤下线回到死配置）");
    Assert(array.Hits == "", "苍穹剑陨 hits both layers");
    // **生成带**：落点锚在玩家身上、等分铺在 `[玩家X + range − band, 玩家X + range]`（从身前一路铺到射程末端），
    // 与阵心在哪无关。数量初期是 3 支、爆炸只笼罩两三个身位——**靠怪聚成一堆才划算**，"加数量 / 加半径"
    // 留给剑意（见 sword_skills.md 第八节第 13 条）。
    Assert(array.Band > 0 && array.Band <= 1400, "苍穹剑陨 的黑洞铺开宽度配了、且夹得进画面");
    // 与诛仙剑阵的分工：后者是**无条件全屏**，前者只在射程带里稀疏落点。
    Assert(!array.AoeAll && config.Skills["skill_15"].AoeAll, "苍穹剑陨 打一条带、诛仙剑阵 打全场");
    // 各支按 `spread` 在阵心两侧铺开，最外侧那支离阵心 `(count-1)/2 × spread`——**大于落点半径就打不到阵心那只**。
    // 这条几何关系仍然要盯住：它是"一次施法铺多宽"的定义，改 `spread` / `aoe_radius` / `count` 都会动它。
    var wan = config.Skills["skill_06"];
    int wanLanes = Enumerable.Range(0, wan.ProjectileCount)
        .Count(i => Math.Abs((i - (wan.ProjectileCount - 1) / 2.0) * wan.Spread) < wan.AoeRadius);
    Assert(wanLanes == 3, $"万剑决 的落点里有 {wanLanes} 支压在阵心上（铺开宽度与落点半径的关系）");
    foreach (var s in config.Skills.Values.Where(s => s.Band > 0))
    {
        Assert(s.Trajectory == "sky_drop", $"{s.Id}：黑洞铺开宽度只对 sky_drop 有意义");
        // **别把剑开到画面外**：逻辑画布恒为 1920、角色恒在 x=330（`BattleView.X` 的锚点），黑洞整条带子
        // 可用的横向余量只有 1460（表现层会把它夹进 `[玩家X + 60, 玩家X + 1520]`）。超出去的那几支会被
        // 右边缘切掉——这正是这条断言存在的理由（远端那支曾经正好压在 1590 上）。
        Assert(s.Band <= 1400, $"{s.Id}：黑洞铺开宽度 {s.Band} 会把剑开到画面外（上限 1400）");
    }
    // 剑二十三：影分身是**自身状态**，必须是 buff + power 1（power != 1 会占用共享伤害倍率窗口）。
    var clone = config.Skills["skill_19"];
    Assert(clone.Kind == "buff" && clone.Secondary == "mirror", "剑二十三 is a self buff carrying the mirror effect");
    Assert(clone.Power == 1, "剑二十三 must not occupy the shared damage-multiplier window");
    Assert(clone.SecondaryValue > 0 && clone.SecondaryValue <= 1 && clone.SecondaryDuration > 0, "剑二十三 has a fractional inherit ratio and a positive lifetime");
    // 御雷真诀：下移时按设计文档把「雷」落成眩晕。
    Assert(config.Skills["skill_07"].Secondary == "stun" && config.Skills["skill_07"].SecondaryDuration > 0, "御雷真诀 stuns");
});
Check("影分身：本体每放一式，分身同步再放一份、伤害打折、不递归", () => {
    // 暴击是逐弹丸摇的，会把 70% 这个比值打乱，用改过配置的靶场把暴击关掉。
    var noCrit = GameConfig.Load(f => f == "fightattr.csv" ? Cell(source[f], "crit", "base_value", "0") : source[f]);
    var g = SalvoOn(noCrit, "skill_01", 400);      // 靶场只学了御剑术
    g.State.Skills["skill_19"] = 1;                // 再挂上影分身
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    Assert(g.ForceRelease("skill_19"), "剑二十三 released");
    Assert(g.MirrorRemaining > 0, "clone window opened");
    // 影分身自己不产生效果，也不该被自己镜像（否则会无限递归）。
    Assert(g.Effects.Count == 0, "影分身本身不产生效果、也不镜像自己");
    // 只清效果、不清冷却：ForceRelease 已经给剑二十三写上了 40 秒冷却，清掉的话下面 Step 时它会被再放一次。
    g.Effects.Clear();
    Assert(g.ForceRelease("skill_01"), "御剑术 released");
    var volley = g.Effects.Where(e => e.Skill == "skill_01").ToArray();
    Assert(volley.Length == 2, "本体与分身各出一支：" + volley.Length);
    Assert(volley.Count(e => e.Mirrored) == 1, "恰好一支来自分身");
    var real = volley.Single(e => !e.Mirrored);
    var copy = volley.Single(e => e.Mirrored);
    Assert(Math.Abs(copy.Damage / real.Damage - 0.7) < 1e-9, $"分身继承 70%：{copy.Damage:0.###}/{real.Damage:0.###}");
    // 分身的弹道从**身后**出发：玩家朝右推进，所以是更小的 X。
    Assert(copy.X < real.X, "分身那一支从身后出发");
    // 分身那一式晚一拍出现：本体立刻就走，分身要等 Delay 走完。
    Assert(copy.Delay > 0 && real.Delay == 0, $"只有分身那一份带延迟：{copy.Delay}/{real.Delay}");
    // 跨过起飞前停留（御剑术 hover_time 0.12）再看一眼：本体已经飞出去了，分身必须**仍然**在身后。
    // 这里曾经是个真 bug——停留期无条件 `effect.X = Battle.PlayerX`，把镜像的偏移抹掉，两支剑完全重叠。
    Step(g, .2);
    Assert(copy.Delay <= 0, "延迟已经走完");
    Assert(copy.X < real.X, $"跨过前摇后分身仍在身后：{copy.X:0.#}/{real.X:0.#}");
    // 窗口过期后不再复制。
    Step(g, 11);
    Assert(g.MirrorRemaining == 0, "clone window closed");
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    Assert(g.ForceRelease("skill_01"), "御剑术 released again");
    Assert(g.Effects.All(e => !e.Mirrored), "窗口过期后不再产生镜像效果");
});
Check("CSV BOM, quotes, multiline and write roundtrip", () => {
    var csv = CsvTable.Write(new[] { new[] { "id", "name" }, new[] { "1", "a,\"b\"\n中文" } });
    Assert(CsvTable.Parse("roundtrip", "\uFEFF" + csv)[0].Text("name") == "a,\"b\"\n中文", "roundtrip");
    Reject(() => CsvTable.Parse("bad", "id,name\n1,\"bad"));
});
Check("reject duplicate IDs and dangling references", () => {
    Reject(() => GameConfig.Load(f => f == "item.csv" ? source[f] + "gold,重复,currency,重复\n" : source[f]));
    // 波次刷什么怪现由子表 wave_unit 决定，悬空引用要在那里拦。
    Reject(() => GameConfig.Load(f => f == "wave_unit.csv" ? source[f].Replace("wave_1_slime,wave_1,slime", "wave_1_slime,wave_1,missing") : source[f]));
});
Check("reject repeat core and non-deterministic first-core quantity", () => {
    Reject(() => GameConfig.Load(f => f == "drop.csv" ? source[f].Replace("boss_repeat,gold,60", "boss_repeat,core,1") : source[f]));
    Reject(() => GameConfig.Load(f => f == "drop.csv" ? source[f].Replace("boss_first,core,1", "boss_first,core,2") : source[f]));
});
Check("reject talent cycles", () => Reject(() => GameConfig.Load(f => f == "TalentLink.csv" ? source[f] + "cycle,t_end,t_root\n" : source[f])));
Check("reject unknown secondary effect", () => Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "secondary", "bogus") : source[f])));
Check("reject invalid flight shape, count and arc band", () => {
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "trajectory", "warp") : source[f]));
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_07", "trajectory", "arc_homing") : source[f]));   // 非 projectile 不得带形态
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "projectile_count", "0") : source[f]));     // 弹数至少 1
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_07", "projectile_count", "3") : source[f]));     // 非 projectile 只能单发
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "arc_min", "200") : source[f]));            // 弧区间倒挂
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "arc_max", "400") : source[f]));            // 弧高越出战斗画面
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_06", "aoe_radius", "0") : source[f]));           // 天降必须有落点半径
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "arc_min", "-80") : source[f]));           // 下弧越出地面
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "speed", "10") : source[f]));              // 慢到几乎不动的弹道
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_01", "pierce_chance", "1.5") : source[f]));     // 概率不能超过 1
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_04", "secondary_extra", "-1") : source[f]));    // 附加参数不能为负
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_02", "trigger_chance", "1.5") : source[f]));     // 触发概率不能超过 1
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_02", "trigger_chance", "-0.1") : source[f]));    // 触发概率不能为负
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_12", "trigger_chance_step", "-0.1") : source[f])); // 概率累加的步进不能为负
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_10", "secondary_value", "0") : source[f]));      // 利用状态必须有正的增伤比例（0 就是一行死配置）
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_02", "secondary_duration", "0") : source[f]));   // 天降火海需要正的残留时长
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_12", "secondary", "freeze") : source[f]));       // chill 之外的名字仍然被拒
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_05", "targeting", "weakest") : source[f]));      // 未知的选敌方式
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_05", "cast_root", "-1") : source[f]));           // 定身时长不能为负
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_05", "knockback", "-1") : source[f]));           // 击退距离不能为负
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_12", "gather", "-1") : source[f]));              // 吸附距离不能为负
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_17", "band", "400") : source[f]));              // 黑洞铺开宽度只对 sky_drop 有意义
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_18", "band", "1600") : source[f]));             // 铺太宽就夹不进画面（上限 1400）
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_02", "aoe_all", "1") : source[f]));              // 天降火海与全体命中互斥
    Reject(() => GameConfig.Load(f => f == "monster.csv" ? Cell(source[f], "slime", "layer", "sky") : source[f]));                    // 未知层级
    Reject(() => GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "hits", "sky") : source[f]));              // 未知技能定位
    Reject(() => GameConfig.Load(f => f == "wave_unit.csv" ? Cell(source[f], "wave_3_hawk", "wave_id", "wave_99") : source[f]));      // 悬空波次引用
    Reject(() => GameConfig.Load(f => f == "wave_unit.csv" ? Cell(source[f], "wave_1_slime", "monster_id", "missing") : source[f]));   // 悬空的怪物引用
    // 某一波没有任何 wave_unit：那一波会刷不出怪、关卡直接空转，必须在加载期被拒。
    Reject(() => GameConfig.Load(f => f == "wave_unit.csv"
        ? Cell(Cell(Cell(source[f], "wave_3_bat", "wave_id", "wave_1"), "wave_3_hawk", "wave_id", "wave_1"), "wave_3_slime", "wave_id", "wave_1")
        : source[f]));
});
Check("only current cell activates; uncleared waves accumulate", () => {
    var g = New(); g.Step(.05);
    Assert(g.Battle.Spawns.Count == 1 && g.Battle.Enemies.Count == 3, "initial wave");
    foreach (var e in g.Battle.Enemies) { e.Hp = e.MaxHp = 1e8; e.Atk = 0; }
    Step(g, 6.2);
    Assert(g.Battle.Spawns.Count == 1 && g.Battle.Spawns[0].Wave >= 2 && g.Battle.Enemies.Count >= 6, "stack wave");
});
Check("walking can trigger a fresh wave before passing spawn", () => {
    var g = New(); g.Step(.05); g.Battle.Enemies.Clear();
    g.Battle.PlayerX = 1200; g.Battle.Spawns[0].Timer = .01; g.Step(.05);
    Assert(g.Moving && g.Battle.Enemies.Count == 3 && !g.Battle.Spawns[0].Passed, "walking spawn");
});
Check("passing stops spawn without removing existing enemies", () => {
    var g = New(); g.Step(.05); g.Battle.PlayerX = 1449;
    foreach (var e in g.Battle.Enemies) e.X = 3000;
    g.Battle.Spawns[0].Timer = .01; g.Step(.05);
    Assert(g.Battle.Spawns[0].Passed && g.Battle.Enemies.Count == 3, "pass lifecycle");
});
Check("waves mix several monster templates, spread across distinct spawn points", () => {
    var g = New(); g.Step(.05);
    Assert(config.WaveUnits["wave_1"].Count == 2, "wave_1 is configured from two templates");
    Assert(config.WaveUnits["wave_1"].Select(u => u.Weight).SequenceEqual([2, 1]), "并且是 2 : 1 的配比");
    Assert(g.Battle.Enemies.Count == 3, $"the wave still spawns three units in total: {g.Battle.Enemies.Count}");
    var kinds = g.Battle.Enemies.Select(e => e.MonsterId).OrderBy(id => id).ToArray();
    Assert(kinds.SequenceEqual(["slime", "slime", "tank"]), "the mix matches the table: " + string.Join(",", kinds));
    Assert(g.Battle.Enemies.Select(e => e.X).Distinct().Count() == 3, "no two units share a spawn point");
});
Check("the bare opening needs 3.5 basic attacks per standard monster", () => {
    // 开局不附带任何剑诀，也没有武器与天赋——这一条是 SU 刻度（标准怪 HP = 3.5 × 普攻）的唯一直接证据，
    // 也是整条关卡曲线的锚点。改 monster.csv 的 hp 列或 fightattr 的 atk 都会在这里断。
    var g = New(basic: true); g.Step(.05);
    Assert(g.State.Skills.Count == 0, "no skill is granted at the start");
    Assert(g.Battle.Enemies.Count == 3, "the first wave is still the 3-unit baseline");
    Assert(Math.Abs(g.AttackRange - config.Attr("basic_range")) < 1e-9,
        $"with no skill, the rift stop range falls back to the basic attack range: {g.AttackRange}");
    var slime = g.Battle.Enemies.First(e => e.MonsterId == "slime");
    Assert(Math.Abs(slime.MaxHp - 88) < 1e-9, $"the standard monster is 88 HP at stage 1: {slime.MaxHp}");
    Assert(Math.Abs(slime.MaxHp / g.Attack - 3.5) < .05, $"which is 3.5 basic attacks: {slime.MaxHp / g.Attack:F2}");
});
Check("伤害分级：多目标形态确实按波次人数结算，单体的只打一只", () => {
    // 这一条是 2026-10-05 那轮"把期望命中数写进模型"的**运行时**护栏：模型假定贯穿扫整波、单体只打一只，
    // 若哪天形态改坏了（例如 `line_pierce` 变成只打第一个），模型与实际就会脱节而没有任何东西报警。
    int Struck(string id, int enemies)
    {
        var g = Salvo(id, 300);
        g.Battle.Enemies.Clear();
        // 补到 N 只：用 GM 的波次加成多刷一波，比手搓 EnemyState 更贴近真实刷怪路径。
        g.WaveBonus = Math.Max(0, enemies - 3);
        g.Battle.Spawns[0].Timer = .01; g.Step(.05);
        foreach (var e in g.Battle.Enemies)
        { e.Hp = e.MaxHp = 1e8; e.X = g.Battle.PlayerX + 300; e.StunUntil = 1e9; }   // 叠在一处：只有形态在区分它们
        g.Battle.Cooldowns.Clear(); g.Effects.Clear();
        g.ResetDamageStats();
        Assert(g.ForceRelease(id), id + " released");
        g.Battle.Cooldowns[id] = 999;   // 定住冷却：这条用例量的是"**一次出手**打到几只"，不是几秒内的总命中
        Step(g, 3);
        return g.DamageStats.Rows[id].Hits;
    }
    // 贯穿（`line_pierce`）：一条线扫过整个战场，在场的都在内——命中数跟着人数走，不是恒定值。
    int few = Struck("skill_05", 5), many = Struck("skill_05", 9);
    Assert(few == 5, $"剑气流云壁 一次扫到整排：{few}");
    Assert(many > few, $"人多了就多打到几只：{few} → {many}");
    // 单体 / 定点：一次一只，人多也不变。
    Assert(Struck("skill_01", 5) == 1, "御剑术 只打最先遇到的那只");
    Assert(Struck("skill_10", 5) == 1, "斩鬼神 只打一只");
});
Check("伤害分级：同境界里功能越杂的水位越低、后一档高过前一档", () => {
    // 用户点名的设计意图：剑气流云壁（元婴·控场，功能最杂）的伤害必须低于同境界的范围技能苍穹剑陨。
    // 两个 N 取自 [skill_values.md](../docs/design/skill_values.md) 第三节的形态表（贯穿 = 参考波次 8、
    // 各锁一敌 ×3 ≈ 3.6）；改那张表就要同步改这里。这条护栏防的是"随手调 power 把某个技能调出档位"。
    var cloud = config.Skills["skill_05"];      // 元婴 · 控场
    var fall = config.Skills["skill_18"];       // 元婴 · 范围
    var god = config.Skills["skill_10"];        // 化神 · 主输出
    double cloudLevel = cloud.Power * 8 / (3.5 * cloud.Cooldown);
    double fallLevel = fall.Power * 3.6 / (3.5 * fall.Cooldown);
    double godLevel = god.Power * 1 / (3.5 * god.Cooldown);
    Assert(cloudLevel < fallLevel, $"元婴控场 {cloudLevel:0.##} 应低于同档范围 {fallLevel:0.##}");
    Assert(fallLevel < godLevel, $"后一档要更强：元婴范围 {fallLevel:0.##} vs 化神单体 {godLevel:0.##}");
});
Check("波次只数随关卡放大，并止步于 count_max（硬约束）", () => {
    int Spawned(int order)
    {
        var g = New();
        var id = config.Levels.Single(l => l.Order == order).Id;
        g.State.UnlockedLevels.Add(id); g.SelectLevel(id); g.Step(.05);
        return g.Battle.Enemies.Count;
    }
    Assert(Spawned(1) == 3, "第 1 关仍是 3 只（编制不变）");
    Assert(Spawned(20) > Spawned(1) && Spawned(50) > Spawned(20) && Spawned(100) > Spawned(50), "越到后面刷得越多");
    // 上限是硬约束：把倍率调到很大，数量照样止步在 count_max。
    var huge = GameConfig.Load(f => f == "game_settings.csv" ? Cell(source[f], "wave_growth", "value", "9") : source[f]);
    var capped = new GameSession(huge, seed: 42) { BasicAttackEnabled = false };
    capped.State.UnlockedLevels.Add("level_100"); capped.SelectLevel("level_100");
    capped.Step(.05);
    int cap = huge.Waves[huge.Levels.Single(l => l.Id == "level_100").Wave].CountMax;
    Assert(capped.Battle.Enemies.Count == cap, $"the ceiling holds: {capped.Battle.Enemies.Count} vs {cap}");
});
Check("数量按比例摊给各模板，阵容配比不随只数改变", () => {
    // "两只小怪配一只肉盾"在**任何倍数下**都该是这个配比——只数涨上去不会把它变成"一堆肉盾"。
    foreach (var wave in config.Waves.Values)
    {
        var units = config.WaveUnits[wave.Id];
        foreach (double scale in new[] { 1.0, 2.0, 3.5, 5.0 })
        {
            var counts = GameSession.WaveCountsFor(wave, units, scale);
            int want = Math.Min(wave.CountMax, (int)Math.Round(wave.Count * scale, MidpointRounding.AwayFromZero));
            Assert(counts.Sum() == want, $"{wave.Id}×{scale} 的合计应恰好等于整波只数（不丢不多）：{counts.Sum()} vs {want}");
            Assert(counts.All(n => n > 0), $"{wave.Id}×{scale} 每个模板都要刷得出怪：{string.Join(",", counts)}");
        }
    }
});
Check("GM 的主角无敌：不掉血、不死亡（死亡会重置冷却、让伤害统计断档）", () => {
    // 关掉闪避：不然"没掉血"可能是因为躲开了，而不是无敌挡的。
    var noDodge = GameConfig.Load(f => f == "fightattr.csv" ? Cell(source[f], "dodge", "base_value", "0") : source[f]);
    var g = new GameSession(noDodge, seed: 42) { BasicAttackEnabled = false };
    g.Step(.05);
    g.PlayerInvincible = true;
    g.Battle.PlayerHp = 1;                          // 只剩 1 点，随便挨一下就死
    Step(g, 10);                                    // 放怪物贴上来打
    Assert(g.Battle.PlayerHp == 1, $"无敌期间一点血都不掉：{g.Battle.PlayerHp}");
    Assert(g.Battle.RespawnTimer == 0, "也不会进复活倒计时");
    g.PlayerInvincible = false;
    Step(g, 10);
    Assert(g.Battle.PlayerHp != 1, "关掉之后伤害照常生效（死了就会满血复活，两者都不等于 1）");
});
Check("GM 的怪物倍率：立刻缩放场上的怪，之后刷出的也吃", () => {
    var g = New(); g.Step(.05);
    var e = g.Battle.Enemies[0];
    double hp = e.Hp, max = e.MaxHp, atk = e.Atk;
    g.MonsterHpScale = 3; g.MonsterAtkScale = 2;
    Assert(Math.Abs(e.MaxHp - max * 3) < 1e-9 && Math.Abs(e.Hp - hp * 3) < 1e-9 && Math.Abs(e.Atk - atk * 2) < 1e-9,
        "场上的怪立刻跟着变");
    Assert(Math.Abs(e.Hp / e.MaxHp - hp / max) < 1e-9, "血量按比例缩放，百分比不变");
    // 之后刷出的怪也吃这个倍率（乘在关卡倍率与波次系数**之后**）。
    g.Battle.Spawns[0].Timer = .01; g.Step(.05);
    var fresh = g.Battle.Enemies.First(x => x != e);
    var def = config.Monsters[fresh.MonsterId];
    double want = def.Hp * g.Level.HpScale * config.Waves[g.Level.Wave].HpScale * 3;
    Assert(Math.Abs(fresh.MaxHp - want) < 1e-6, $"新刷的怪也吃倍率：{fresh.MaxHp} vs {want}");
    // 上下限：手滑填 0 或天文数字都不至于把战斗弄崩。
    g.MonsterHpScale = 1e9; Assert(g.MonsterHpScale == 100, "上限 100");
    g.MonsterHpScale = 0; Assert(g.MonsterHpScale == .1, "下限 0.1");
});
Check("GM 的波次加成：只加不改配比，且封顶在 20", () => {
    // 调试开关：`wave.csv` 保持正式数值，测试时临时加几只（种类随机）。加成**只影响之后刷出的波次**。
    var g = New();
    Assert(g.WaveBonus == 0, "默认不加");
    g.WaveBonus = 5; g.Step(.05);
    Assert(g.Battle.Enemies.Count == 3 + 5, $"第一波就按加成刷：{g.Battle.Enemies.Count}");
    Assert(g.Battle.Enemies.Select(e => e.MonsterId).Distinct().Count() > 2, "额外那几只从普通怪里随机挑（不是清一色）");
    g.WaveBonus = 999;
    Assert(g.WaveBonus == 20, "上限 20：刷怪落点是 40 + i×100 铺开的，堆太多会溢到下一格");
    g.WaveBonus = -3;
    Assert(g.WaveBonus == 0, "不能为负");
});
Check("wave strength coefficients scale normal monsters, and only those", () => {
    var lvl = config.Levels.Single(l => l.Id == "level_051");
    var wave = config.Waves[lvl.Wave];
    // 取样前先证明"这一波确实带系数"——否则下面两条断言会退化成"乘了个 1"的空断言。
    Assert(wave.HpScale != 1 && wave.AtkScale != 1, "the sampled wave carries a coefficient to prove");
    var g = New(); g.State.UnlockedLevels.Add("level_051"); g.SelectLevel("level_051"); g.Step(.05);
    var slime = config.Monsters["slime"];
    var first = g.Battle.Enemies.First(e => e.MonsterId == "slime");
    Assert(Math.Abs(first.MaxHp - slime.Hp * lvl.HpScale * wave.HpScale) < 1e-6, $"stage × wave hp_scale is applied: {first.MaxHp}");
    Assert(Math.Abs(first.Atk - slime.Atk * lvl.AtkScale * wave.AtkScale) < 1e-6, $"stage × wave atk_scale is applied: {first.Atk}");
    // 基线波次系数为 1：第 1 关必须与关卡倍率一字不差，证明系数不是"总是叠了另一层什么东西"。
    var l1 = config.Levels.Single(l => l.Id == "level_001");
    Assert(config.Waves["wave_1"].HpScale == 1 && config.Waves["wave_1"].AtkScale == 1, "the baseline wave carries no extra coefficient");
    var b = New(); b.Step(.05);
    Assert(Math.Abs(b.Battle.Enemies[0].MaxHp - slime.Hp * l1.HpScale) < 1e-6, "the baseline stage keeps the plain stage multiplier");
    // 精英与 BOSS 是关卡节点、不是波次阵容的一部分，不该跟着"这波更硬"一起涨。
    var e = New(); e.Step(.05);
    foreach (var m in e.Battle.Enemies) { m.Hp = m.MaxHp = 1e8; m.Atk = 0; }
    while (e.Battle.Spawns[0].Wave < e.Config.Waves["wave_1"].EliteEvery) { e.Battle.Spawns[0].Timer = .01; e.Step(.05); }
    var elite = e.Battle.Enemies.Last(m => m.Kind == "elite");
    Assert(Math.Abs(elite.MaxHp - e.Config.Monsters["elite"].Hp * l1.EliteHp) < 1e-6, $"elites ignore the wave coefficient: {elite.MaxHp}");
    ToBoss(b);
    var boss = Boss(b);
    Assert(Math.Abs(boss.MaxHp - config.Monsters["boss"].Hp * l1.BossHp) < 1e-6, $"the boss ignores the wave coefficient: {boss.MaxHp}");
});
Check("stages 1-50 keep the base waves; later stages switch to the new set", () => {
    var early = config.Levels.Where(l => l.Order <= 50).Select(l => l.Wave).Distinct().OrderBy(x => x).ToArray();
    var late = config.Levels.Where(l => l.Order > 50).Select(l => l.Wave).Distinct().OrderBy(x => x).ToArray();
    // 前 50 关**六循环**：第 1~6 关各一套不同的阵容偏向，之后再重复。后 50 关仍是原来的"更硬一档"三循环。
    Assert(early.SequenceEqual(["wave_1", "wave_2", "wave_3", "wave_7", "wave_8", "wave_9"]),
        "early stages cycle six line-ups: " + string.Join(",", early));
    Assert(late.SequenceEqual(["wave_4", "wave_5", "wave_6"]), "late stages use the new trio: " + string.Join(",", late));
    Assert(config.WaveUnits.Keys.OrderBy(x => x).SequenceEqual(config.Waves.Keys.OrderBy(x => x)), "every wave has units");
});
Check("no wave's spawn ceiling runs past the next cell", () => {
    // GameSession.Spawn 的落点是 offset + 40 + i×100（i 跨模板连续），所以只数上限直接决定最末一只落在哪。
    // 精英每 elite_every 波跟着一只、同样占一个落点，所以算上限时要 +1。
    double cell = config.Setting("cell_width"), offset = config.Rows("spawn_point")[0].Number("offset");
    foreach (var wave in config.Waves.Values)
    {
        double far = offset + 40 + (wave.CountMax + 1) * 100;
        Assert(far <= 2 * cell, $"wave {wave.Id} spawns its last unit at {far}, past {2 * cell}");
    }
});
Check("reject bad wave coefficients and an over-long spawn line", () => {
    Reject(() => GameConfig.Load(f => f == "wave.csv" ? Cell(source[f], "wave_1", "hp_scale", "0") : source[f]));
    Reject(() => GameConfig.Load(f => f == "wave.csv" ? Cell(source[f], "wave_1", "atk_scale", "-1") : source[f]));
    Reject(() => GameConfig.Load(f => f == "wave.csv" ? Cell(source[f], "wave_1", "hp_scale", "10") : source[f]));   // 量级跳变须改关卡倍率，不是波次系数
    Reject(() => GameConfig.Load(f => f == "wave.csv" ? Cell(source[f], "wave_1", "count_max", "2") : source[f]));   // 上限低于第 1 关的基线只数
    Reject(() => GameConfig.Load(f => f == "wave.csv" ? Cell(source[f], "wave_1", "count_max", "100") : source[f])); // 上限抬到把最末一只刷进第 3 格
    Reject(() => GameConfig.Load(f => f == "wave_unit.csv" ? Cell(source[f], "wave_1_slime", "weight", "0") : source[f]));  // 比例必须为正，0 就是一行什么都不刷的死配置
});
Check("in-range targets stop movement", () => {
    var g = New(); g.Step(.05); g.Battle.Enemies[0].X = g.Battle.PlayerX + 300;
    double x = g.Battle.PlayerX; g.Step(.05); Assert(g.Battle.PlayerX == x && !g.Moving, "stop");
});
Check("normal death resets progress and keeps resources", () => {
    var g = New(); g.Battle.PlayerX = 5000; g.State.Wallet["gold"] = 999; g.Battle.PlayerHp = 0; g.Step(.05);
    Assert(g.Battle.Cell == 0 && g.Battle.PlayerX == 80 && g.State.Amount("gold") == 999 && g.Battle.RespawnTimer > 0, "normal death");
});
Check("boss-cell respawn preserves wounds and freezes encounter during respawn", () => {
    var g = New(); ToBoss(g); Boss(g).Hp = 123; g.Battle.PlayerHp = 0; g.Step(.05);
    int waves = g.Battle.Spawns[24].Wave; Step(g, 1);
    Assert(Boss(g).Hp == 123 && g.Battle.Cell == 24 && g.Battle.Spawns[24].Wave == waves, "boss persistence");
    Step(g, 1.05); Assert(g.Battle.PlayerHp == g.MaxHp, "full hp respawn");
});
Check("rift immune until every non-rift enemy dies", () => {
    var g = New(); ToBoss(g); var rift = g.Battle.Enemies.Single(e => e.Kind == "rift"); double hp = rift.Hp;
    g.HurtEnemy(rift, 1e9); Assert(rift.Hp == hp, "rift immune");
    g.HurtEnemy(Boss(g), 1e9); g.HurtEnemy(rift, 1e9); Assert(rift.Hp == hp, "adds gate");
    foreach (var e in g.Battle.Enemies.Where(e => e.Kind != "rift").ToArray()) g.HurtEnemy(e, 1e9);
    Assert(g.RiftUnlocked, "unlock"); g.HurtEnemy(rift, 1e9); g.Step(.05);
    Assert(g.Level.Id == "level_002", "next stage");
});
Check("rift unlocked beyond attack range still advances and clears the stage", () => {
    var g = New(); g.State.Skills["skill_01"] = 1; g.Battle.Cooldowns.Clear();   // 开局不再白送剑诀，用例自己给一个
    ToBoss(g); g.HurtEnemy(Boss(g), 1e9);
    foreach (var e in g.Battle.Enemies.Where(e => e.Kind != "rift").ToArray()) g.HurtEnemy(e, 1e9);
    // BOSS 格阵亡后的重生点距裂隙 1730，远超任一剑诀射程 950；旧逻辑在解锁瞬间冻结移动，挂机永久中断。
    g.Battle.PlayerX = (g.Level.Cells - 1) * config.Setting("cell_width") + 80;
    double rift = g.Battle.Enemies.Single(e => e.Kind == "rift").X;
    g.Step(.05);
    Assert(g.RiftUnlocked && rift - g.Battle.PlayerX > g.AttackRange, "unlocked while out of range");
    Assert(g.Moving, "advance instead of freezing out of range");
    Step(g, 3);
    Assert(rift - g.Battle.PlayerX <= g.AttackRange && !g.Moving, "closed to attack range");
    Step(g, 30);
    Assert(g.Level.Id == "level_002", "stage cleared");
});
Check("boss death stops adds; death keeps boss dead and first reward", () => {
    var g = New(); ToBoss(g); g.HurtEnemy(Boss(g), 1e9); g.Battle.PlayerHp = 0; g.Step(.05); Step(g, 2.1);
    Assert(g.Battle.BossDefeated && !g.Battle.Enemies.Any(e => e.Kind == "boss") && g.Battle.Spawns[24].Wave == 1 && g.State.Amount("core") == 1, "boss death state");
});
Check("repeat kill grants zero core; same template in another stage grants one", () => {
    var g = New(); ToBoss(g); g.HurtEnemy(Boss(g), 1e9);
    g.SelectLevel("level_001"); ToBoss(g); g.HurtEnemy(Boss(g), 1e9); Assert(g.State.Amount("core") == 1, "repeat");
    g.State.UnlockedLevels.Add("level_002"); g.SelectLevel("level_002"); ToBoss(g); g.HurtEnemy(Boss(g), 1e9);
    Assert(g.State.Amount("core") == 2 && g.State.FirstKills.Count == 2, "per-stage ledger");
});
Check("all 100 unique first kills issue exactly 100 cores", () => {
    var g = New(); foreach (var level in config.Levels) { g.State.UnlockedLevels.Add(level.Id); g.SelectLevel(level.Id); ToBoss(g); g.HurtEnemy(Boss(g), 1e9); }
    Assert(g.State.Amount("core") == 100 && g.State.FirstKills.Count == 100, "finite supply");
});
Check("old-stage loop and final-stage loop", () => {
    var g = New(); g.SelectLevel("level_001"); ToBoss(g);
    foreach (var e in g.Battle.Enemies.Where(e => e.Kind != "rift").ToArray()) g.HurtEnemy(e, 1e9);
    g.HurtEnemy(g.Battle.Enemies.Single(e => e.Kind == "rift"), 1e9); g.Step(.05);
    Assert(g.Level.Id == "level_001" && g.Battle.Cell == 0 && g.State.UnlockedLevels.Contains("level_002"), "old loop");
    g.State.UnlockedLevels.Add("level_100"); g.SelectLevel("level_100"); g.State.LoopLevel = false; ToBoss(g);
    foreach (var e in g.Battle.Enemies.Where(e => e.Kind != "rift").ToArray()) g.HurtEnemy(e, 1e9);
    g.HurtEnemy(g.Battle.Enemies.Single(e => e.Kind == "rift"), 1e9); g.Step(.05); Assert(g.Level.Id == "level_100", "last loop");
});
Check("no valid target consumes no skill cooldown", () => {
    var g = New(); g.Step(.05); Assert(g.Battle.Cooldowns.Count == 0, "empty range cooldown");
});
Check("intent auto-production, capacity and hover collection contract", () => {
    var g = New(); g.State.Talents["t_auto"] = 1; Step(g, 3.05);
    Assert(g.State.PendingIntent["intent_0"] == 1 && g.State.Amount("intent_0") == 0, "pending");
    for (int i = 0; i < 250; i++) g.ClickOre("ore_0");
    Assert(g.State.PendingIntent["intent_0"] == 200, "capacity");
    g.CollectIntent("intent_0"); g.CollectIntent("intent_0"); Assert(g.State.Amount("intent_0") == 200, "collect once");
});
Check("talent visibility, atomic costs and finite core spending", () => {
    var g = New(); Assert(!g.TalentVisible("t_auto") && !g.BuyTalent("t_auto"), "hidden node");
    g.State.Wallet["gold"] = 1000; Assert(g.BuyTalent("t_root") && g.TalentVisible("t_hp"), "adjacent");
    g.BuyTalent("t_hp"); double gold = g.State.Amount("gold"); Assert(!g.BuyTalent("t_auto") && g.State.Amount("gold") == gold, "atomic cost");
});
Check("all live effect types execute without invalid targets", () => {
    var g = New(); foreach (var realm in config.Rows("SwordLevel")) g.State.Realms.Add(realm.Text("id"));
    foreach (var id in config.Skills.Keys) g.State.Skills[id] = 1;
    g.Step(.05); foreach (var e in g.Battle.Enemies) { e.Hp = e.MaxHp = 1e8; e.Atk = 0; e.X = g.Battle.PlayerX + 200; }
    // 三个真诀只由普攻概率触发，不会在冷却到点自动释放；这里显式各放一次，保证四种在役 kind 都能被看到。
    foreach (var id in config.Skills.Keys.Where(id => config.Skills[id].TriggerChance > 0))
        Assert(g.ForceRelease(id), "trigger skill released: " + id);
    g.Step(.05);
    // 在役剑诀里会产生 CombatEffect 的 kind 有 projectile / ground / target 三种（buff 不产生效果）。
    // summon 随剑侍归档再次没有在役载体，覆盖率由"退休路径"那条用例用内存改配置维持。
    Assert(g.Effects.Any(e => e.Kind == "projectile") && g.Effects.Any(e => e.Kind == "ground") && g.Effects.Any(e => e.Kind == "target"), "effect kinds");
    Step(g, 15); Assert(g.Battle.Enemies.Any(e => e.Hp < e.MaxHp), "effects damage");
});
Check("secondary: vulnerable amplifies, stun freezes, slow halves, dot ticks", () => {
    var g = New(); g.Step(.05); var e = g.Battle.Enemies[0]; e.X = 5000; e.Atk = 0;
    e.Hp = e.MaxHp = 1000; e.VulnerableUntil = 1; e.VulnerableFactor = 1.5;
    g.HurtEnemy(e, 100); Assert(e.Hp == 850, "vulnerable");
    e.StunUntil = 1; double sx = e.X; g.Step(.05); Assert(e.X == sx, "stun");
    e.StunUntil = 0; e.SlowUntil = 1; e.SlowFactor = .5; sx = e.X; g.Step(.05);
    Assert(e.X < sx && e.X > sx - 6, "slow");
    e.Hp = e.MaxHp = 1000; e.VulnerableUntil = 0; e.VulnerableFactor = 1; e.DotUntil = 1; e.DotDps = 50; double hp = e.Hp; g.Step(1);
    Assert(e.Hp < hp - 40 && e.Hp > hp - 60, "dot");
});
// hover_homing 已无在役技能使用（御剑术改走 line_pierce），但形态留在词汇表里供日后配，故用改过的配置保住覆盖。
var hoverForm = GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_01", "trajectory", "hover_homing") : source[f]);
Check("hover_homing hovers above the caster, then homes and hits", () => {
    var g = SalvoOn(hoverForm, "skill_01");
    var blade = g.Effects.First(e => e.Trajectory == "hover_homing");   // 御剑术默认 1 支（剑支数是成长轴，不是初值）
    Assert(blade.Timer > 0 && blade.X == g.Battle.PlayerX, "hovers above the caster before flying");
    double hp = g.Battle.Enemies[0].Hp;
    Step(g, 1);                                     // 悬浮 0.12s + 飞行
    Assert(g.Battle.Enemies.Any(e => e.Hp < hp), "hits the target after hovering");
});
Check("sky_drop builds a fixed formation of distinct landing points", () => {
    var g = Salvo("skill_06");                      // 万剑决：5 支、间距 40、落点半径 60
    var blades = g.Effects.Where(e => e.Trajectory == "sky_drop").ToArray();
    Assert(blades.Length == 5, $"five blades: {blades.Length}");
    double center = g.Battle.PlayerX + 90;          // 三个靶子重叠在阵心
    var lands = blades.Select(b => b.X).OrderBy(x => x).ToArray();
    Assert(lands.Distinct().Count() == 5, "landing points are spread out, not stacked: " + string.Join(",", lands));
    Assert(lands[2] == center, "the middle blade lands on the formation centre");
    // 间距固定：只有落在半径 60 之内的三支（-40 / 0 / +40）够得到阵心的敌人，与"只有一个敌人"无关地铺开。
    Assert(blades.Count(b => Math.Abs(b.X - center) < 60) == 3, "exactly three landing points reach the centre");
    Assert(blades.Select(b => b.Timer).Distinct().Count() > 1, "blades appear staggered, not all at once");
    var xs = blades.Select(b => b.X).ToArray();
    Step(g, .1);                                    // 不追踪：位置必须冻结
    Assert(g.Effects.Where(e => e.Trajectory == "sky_drop").Select(b => b.X).OrderBy(x => x).SequenceEqual(xs.OrderBy(x => x)), "x stays frozen while falling");
    Step(g, .7);                                    // 停留 0.25s + 下落 0.5s 后落地：三个重叠单位都要挨打
    Assert(g.Battle.Enemies.Count(e => e.Hp < e.MaxHp) == 3, "every overlapping unit was hit");
});
Check("arc_homing fires a configurable salvo with distinct targets and reproducible arcs", () => {
    // 靶子放到 160：贴着 90 摆时剑芒会在同一个 Step 内就飞到并结算移除，观察不到编排信息。
    var g = Salvo("skill_11", 160);                 // 青元剑芒：3 支
    var arcs = g.Effects.Where(e => e.Trajectory == "arc_homing").Select(e => e.Arc).ToArray();
    Assert(arcs.Length == 3, $"three blades: {arcs.Length}");
    Assert(arcs.All(a => a >= -50 && a <= 140), "arc inside the configured band: " + string.Join(",", arcs));
    Assert(arcs.Distinct().Count() > 1, "arcs are randomised, not constant");
    Assert(g.Effects.Select(e => e.Target).Distinct().Count() == 3, "each blade picks its own target");
    Assert(g.Effects.All(e => e.Speed == 1200), "slower configured speed is used");
    Assert(g.Effects.Select(e => e.Timer).Distinct().Count() > 1, "blades are launched with a time gap");
    var again = Salvo("skill_11", 160);
    Assert(again.Effects.Where(e => e.Trajectory == "arc_homing").Select(e => e.Arc).SequenceEqual(arcs), "same seed reproduces the arcs");
});
Check("arc_homing can bend downwards, keeping both sides of the lane in play", () => {
    // 单个靶子 + 反复出手，收集足够多的弧度：区间带负值，应当既有上弧也有下弧（上方多、下方少）。
    var g = Salvo("skill_11", 160);
    var arcs = new List<double>();
    for (int cast = 0; cast < 60; cast++)
    {
        g.Battle.Cooldowns.Clear(); g.Effects.Clear(); g.Step(.05);
        arcs.AddRange(g.Effects.Where(e => e.Trajectory == "arc_homing").Select(e => e.Arc));
    }
    Assert(arcs.Count >= 60, "collected enough arcs to judge the band");
    Assert(arcs.Any(a => a > 0) && arcs.Any(a => a < 0), $"both up and down arcs occur: min={arcs.Min():0.#} max={arcs.Max():0.#}");
    Assert(arcs.Count(a => a < 0) < arcs.Count(a => a > 0), "downward arcs are the minority");
});
Check("line_shot strikes the first enemy on its path and is destroyed", () => {
    // 御剑术：肩侧横射、命中即散、不穿透。清空技能保证伤害只可能来自这些剑。
    var g = New(); g.Step(.05); g.State.Skills.Clear(); g.Battle.Cooldowns.Clear();
    var line = g.Battle.Enemies.ToArray();
    for (int i = 0; i < line.Length; i++) { line[i].Atk = 0; line[i].Hp = line[i].MaxHp = 1e8; line[i].X = g.Battle.PlayerX + 200 + i * 400; line[i].StunUntil = 1e9; }
    g.Effects.Clear();
    g.State.Skills["skill_01"] = 1; g.Battle.Cooldowns.Clear();
    g.Step(.05);
    var blades = g.Effects.Where(e => e.Trajectory == "line_shot").ToArray();
    Assert(blades.Length == 1, $"a single blade by default: {blades.Length}");
    Assert(blades.All(b => b.X == g.Battle.PlayerX && b.Speed == 1500), "start at the caster with the configured speed");
    Assert(blades.All(b => b.Timer > 0), "blades surface before being launched");
    Step(g, 1);
    // 三个敌人相隔 400（远超剑的命中半径），每支剑只可能打到最靠前的那个：最前面的挨打，后面两个毫发无损。
    Assert(line[0].Hp < line[0].MaxHp, "the nearest enemy was struck");
    Assert(line[1].Hp == line[1].MaxHp && line[2].Hp == line[2].MaxHp, "the shot is destroyed on impact and never pierces");
});
Check("line_shot pierces only on the first hit, and only when the chance allows", () => {
    // 概率穿透：一次判定机会。pierce_chance = 1 时第一击必穿，穿完标记下来，第二击照样销毁。
    var piercing = GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_01", "pierce_chance", "1") : source[f]);
    var g = SalvoOn(piercing, "skill_01", 200);
    var victim = g.Battle.Enemies[0];
    // 只留一个身前靶子：SalvoOn 把三个靶子放在同一个 X，剑穿过后会立刻撞上第二个同位置的靶子就销毁。
    foreach (var extra in g.Battle.Enemies.Skip(1).ToArray()) g.Battle.Enemies.Remove(extra);
    var behind = new EnemyState { Id = g.Battle.NextEnemyId++, MonsterId = victim.MonsterId, Kind = victim.Kind, X = victim.X + 300, Hp = 1e8, MaxHp = 1e8, Atk = 0, AttackTimer = 999, StunUntil = 1e9 };
    g.Battle.Enemies.Add(behind);
    g.Effects.Clear(); g.Battle.Cooldowns.Clear(); g.Step(.05);
    Step(g, .5);
    Assert(victim.Hp < victim.MaxHp && behind.Hp < behind.MaxHp, "the pierce carried the blade into the enemy behind");
    behind.Hp = behind.MaxHp; victim.Hp = victim.MaxHp;
    g.Effects.Clear(); g.Battle.Cooldowns.Clear(); g.Step(.05);
    var blade = g.Effects.First(e => e.Trajectory == "line_shot");
    Assert(!blade.Pierced, "a fresh blade has not spent its pierce yet");
    for (int i = 0; i < 12 && !blade.Pierced; i++) g.Step(.05);
    Assert(blade.Pierced, "the first hit marked the blade as already pierced, so it can never pierce again");
});
Check("line_pierce sweeps every enemy along the line", () => {
    // 恒穿透形态现在由剑气流云壁与寒冰龙卷使用；这里仍用改过的配置单独跑一遍，作为形态自身的对照
    // （两个在役技能各自还叠了击退与寒冷，混在一起就看不清形态本身的行为）。
    var sweeping = GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(Cell(source[f], "skill_01", "trajectory", "line_pierce"), "skill_01", "projectile_count", "1") : source[f]);
    var g = SalvoOn(sweeping, "skill_01", 200);
    var line = g.Battle.Enemies.ToArray();
    for (int i = 0; i < line.Length; i++) { line[i].X = g.Battle.PlayerX + 200 + i * 100; line[i].Hp = line[i].MaxHp = 1e8; }
    g.Effects.Clear(); g.Battle.Cooldowns.Clear(); g.Step(.05);
    Step(g, .5);
    Assert(line.All(e => e.Hp < e.MaxHp), "an always-piercing blade sweeps through every enemy along the line");
});
Check("basic attack fires one flat shot per interval, and stays silent without a legal target", () => {
    // 关掉暴击：普攻伤害要能被精确断言，暴击会把数字乘 1.5，抽签结果还随 seed 漂。
    var plain = GameConfig.Load(f => f == "fightattr.csv" ? Cell(source[f], "crit", "base_value", "0") : source[f]);
    var g = new GameSession(plain, seed: 42) { BasicAttackEnabled = true };
    g.State.Skills.Clear(); g.Battle.Cooldowns.Clear();
    g.Step(.05);
    // 初始波在普攻射程 950 之外：不空放，也不写下间隔。
    Assert(g.Effects.Count == 0 && !g.Battle.Cooldowns.ContainsKey(GameSession.BasicAttackKey), "out of range means no basic attack");
    var target = g.Battle.Enemies[0];
    foreach (var e in g.Battle.Enemies.Skip(1).ToArray()) g.Battle.Enemies.Remove(e);
    target.X = g.Battle.PlayerX + 500; target.Hp = target.MaxHp = 1e6;
    target.Atk = 0; target.AttackTimer = 999; target.StunUntil = 1e9;
    g.Step(.05);
    var shot = g.Effects.Single(e => e.Skill == "");
    Assert(shot.Kind == "projectile" && Math.Abs(shot.Damage - g.Attack * plain.Attr("basic_power")) < 1e-9,
        $"the shot carries attack x basic_power: {shot.Damage}");
    Assert(Math.Abs(g.Battle.Cooldowns[GameSession.BasicAttackKey] - plain.Attr("basic_interval")) < 1e-9, "the interval starts ticking");
    Step(g, 1);
    Assert(target.Hp < target.MaxHp, "the shot lands on the target");
    // 节奏：冷却从 0 起跑 2.55 秒正好三手（0.05 / 1.05 / 2.05），间隔由配置决定。
    g.Battle.Cooldowns[GameSession.BasicAttackKey] = 0;
    int fired = 0; double previous = 0;
    for (int i = 0; i < 51; i++)
    {
        g.Step(.05);
        double now = g.Battle.Cooldowns.GetValueOrDefault(GameSession.BasicAttackKey);
        if (now > previous + .5) fired++;   // 间隔被顶回满值 = 出了一手
        previous = now;
    }
    Assert(fired == 3, $"one shot per configured interval: {fired} in 2.55s");
});
Check("trigger skills never auto-cast, only fire on a basic attack, and stay gated by cooldown", () => {
    // 御雷真诀（触发概率 4%）：靶场关了普攻，冷却到点也绝不会自己放出来。
    var idle = Salvo("skill_07");
    Step(idle, 30);
    Assert(!idle.Effects.Any(e => e.Skill == "skill_07"), "a trigger skill never auto-casts on cooldown");
    // 概率拉到 1：出手的普攻必定带出这一手。
    var certain = GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_07", "trigger_chance", "1") : source[f]);
    // 靶子放远（>99）：贴脸摆的话普攻飞剑会在同一个 Step 内就命中并被移除，看不到它还留在场上。
    var g = SalvoOn(certain, "skill_07", 200); g.BasicAttackEnabled = true;
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    g.Step(.05);
    Assert(g.Effects.Any(e => e.Kind == "target" && e.Skill == "skill_07"), "the basic attack is what pulls the trigger");
    // 冷却仍是硬门槛：把它顶满，概率再高也不出手（同时验证这一手普攻确实出去了，不是空断言）。
    g.Effects.Clear(); g.Battle.Cooldowns["skill_07"] = 30; g.Battle.Cooldowns[GameSession.BasicAttackKey] = 0;
    g.Step(.05);
    Assert(g.Effects.Any(e => e.Skill == ""), "the basic attack itself still happens");
    Assert(!g.Effects.Any(e => e.Skill == "skill_07"), "a cooling trigger skill cannot fire");
});
Check("焚天剑诀 lands a flame sword that leaves a lingering fire sea refreshing the burn", () => {
    var g = Salvo("skill_02", 90);                  // 三个靶子重叠在阵心
    // 焚天剑诀现在是**固定冷却**：Salvo 那一步已经自动放过一次了，先清干净再显式放，
    // 否则下面按"只有一个天降效果"写的断言会撞上两个。
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    Assert(g.ForceRelease("skill_02"), "the flame sword is released");
    g.Battle.Cooldowns["skill_02"] = 999;           // 用例跑得比冷却长，钉住它，免得中途又自动放一片火海
    var blade = g.Effects.Single(e => e.Trajectory == "sky_drop");
    Assert(blade.Secondary == "dot" && Math.Abs(blade.AoeRadius - 120) < 1e-9, "the sword carries dot and the landing radius");
    Assert(!g.Effects.Any(e => e.Kind == "ground"), "the sea does not exist before the sword lands");
    Step(g, .8);                                    // 停留 0.25s + 下落 0.5s
    var sea = g.Effects.SingleOrDefault(e => e.Kind == "ground" && e.Skill == "skill_02");
    Assert(sea is not null, "landing leaves a fire sea");
    // 火海寿命取配置的 secondary_duration（不写死数字——它会随"火海要更持久"这类调整而变）。
    Assert(Math.Abs(sea!.MaxLife - config.Skills["skill_02"].SecondaryDuration) < 1e-9,
        $"the sea burns for the configured duration: {sea.MaxLife}");
    Assert(Math.Abs(sea.AoeRadius - 120) < 1e-9, "the sea keeps the landing radius");
    Step(g, .1);                                    // 火海下一步才第一次结算
    Assert(g.Battle.Enemies.All(e => e.DotUntil > 0), "everything inside the sea is set alight");
    double hp = g.Battle.Enemies[0].Hp;
    Step(g, 1);
    Assert(g.Battle.Enemies[0].Hp < hp && g.Battle.Enemies[0].DotUntil > 2.5, "the sea keeps damaging and refreshing the burn");
    Step(g, config.Skills["skill_02"].SecondaryDuration);
    Assert(!g.Effects.Any(e => e.Kind == "ground" && e.Skill == "skill_02"), "the sea burns out after its duration");
});
Check("寒冰龙卷 gathers the pack toward its eye, and chills it", () => {
    var g = Salvo("skill_12", 200);                 // 寒冰龙卷：ground + gather + chill
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();  // 它是固定冷却，先清掉自动放的那一次
    // 三只怪摆进力场半径内、彼此隔开：只有吸附才会把它们拉近，其它机制（减速/伤害）都不改变 X 的散布。
    // 靶场把敌人定死了，所以 X 的变化只可能来自 gather（或将来误配回来的 knockback）。
    var pack = g.Battle.Enemies.ToArray();
    for (int i = 0; i < pack.Length; i++) pack[i].X = g.Battle.PlayerX + 300 + i * 100;
    Assert(g.ForceRelease("skill_12"), "the tornado is released");
    var eye = g.Effects.Single(e => e.Skill == "skill_12");
    Assert(eye.Kind == "ground" && eye.Gather > 0, "it lands a field carrying the pull distance");
    double center = eye.X;
    Assert(pack.All(e => Math.Abs(e.X - center) < eye.AoeRadius), "the pack starts inside the eye");
    var x0 = pack.Select(e => e.X).ToArray();
    var spread0 = x0.Max() - x0.Min();
    // 一跳（0.6 秒）走一步：最远的那只一次只被拉 `gather` 那么远，而不是一帧贴到中心——
    // 那是 knockback 的读法，不是聚怪。力场的第一次结算发生在施放后的第一帧。
    Step(g, .05);
    for (int i = 0; i < pack.Length; i++)
    {
        double moved = x0[i] - pack[i].X;
        Assert(moved >= 0 && moved <= eye.Gather + 1e-9, $"一跳最多拉 gather 那么远：{moved:0.#}");
    }
    Assert(pack.Any(e => Math.Abs(e.X - center) > 0), "one tick narrows the pack without collapsing it");
    // 逐跳走完整个力场寿命：收敛**单调且不越过中心**——`min()` 把步长夹在剩余距离上，所以不会来回弹。
    for (int tick = 0; tick < 6; tick++)
    {
        Step(g, .6);
        for (int i = 0; i < pack.Length; i++)
            Assert(pack[i].X <= x0[i] + 1e-9 && pack[i].X >= center - 1e-9,
                $"the pull only ever moves enemies toward the eye, never past it: {pack[i].X:0.#} vs eye {center:0.#}");
    }
    Assert(pack.Max(e => e.X) - pack.Min(e => e.X) < spread0 * .6, $"the pack is pulled together: {spread0:0.#} → {pack.Max(e => e.X) - pack.Min(e => e.X):0.#}");
    Assert(pack.All(e => Math.Abs(e.X - center) < 1e-9), "given enough ticks the pack settles exactly on the eye");
    Assert(pack.All(e => e.ChillUntil > 0 && Math.Abs(e.SlowFactor - .75) < 1e-9), "everything in the eye is chilled by 25%");
    Assert(pack.All(e => e.Hp < e.MaxHp), "and struck");
    Step(g, 5);
    Assert(!g.Effects.Any(x => x.Skill == "skill_12"), "the field dies once its configured duration runs out");
});

Check("苍穹剑陨 各锁一敌：只剩一只时三支全砸在它身上（打 BOSS 不丢伤害）", () => {
    var g = Salvo("skill_18", 200);
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    var s = config.Skills["skill_18"];
    var lone = g.Battle.Enemies[0];
    g.Battle.Enemies.RemoveAll(e => e != lone);                 // 场上只剩一只：正是 E1 的口径
    lone.X = g.Battle.PlayerX + 500;
    Assert(g.ForceRelease("skill_18"), "the volley is released");
    var blades = g.Effects.Where(e => e.Skill == "skill_18").ToArray();
    Assert(blades.Length == s.ProjectileCount && blades.Length > 1, $"the configured volley is raised: {blades.Length}");
    Assert(blades.All(e => e.Trajectory == "sky_drop"), "they all come down from above");
    // 敌人不足时 `Picks` 循环重复，于是三支全锁同一只、全落在它身上——**不再有"落在空地上"这回事**。
    Assert(blades.All(e => Math.Abs(e.X - lone.X) < 1e-9), "每一支的落点都在它身上");
    // 黑洞仍然铺得开：出生点互不重叠，且宽度取配置。
    var spawn = blades.Select(e => e.SpawnX).OrderBy(v => v).ToArray();
    Assert(spawn.Distinct().Count() == spawn.Length, "黑洞铺开、不叠在一点");
    Assert(Math.Abs((spawn[^1] - spawn[0]) - s.Band) < 1e-6, $"铺开宽度取配置：{spawn[^1] - spawn[0]:0} / {s.Band}");
    // 整条带子夹在画面内（角色锚点 330、逻辑宽 1920）。落点可以离得很远，但黑洞不能开出屏幕。
    Assert(spawn[0] > g.Battle.PlayerX + 60 - 1e-6 && spawn[^1] < g.Battle.PlayerX + 1520 + 1e-6,
        $"黑洞夹在画面内：{spawn[0] - g.Battle.PlayerX:0}..{spawn[^1] - g.Battle.PlayerX:0}");
    Assert(lone.Hp == lone.MaxHp, "落地前不掉血");
    // **错时**：`volley_interval` 给第 i 支额外延迟 i × 间隔（再叠 ±jitter），所以三支的出现 / 落下有先后，
    // 不是"同一刻齐放"。倒计时因此互不相同。
    Assert(blades.Select(e => e.Timer).Distinct().Count() == blades.Length, "三支错时出现，不是同一刻齐放");
    double flight = s.HoverTime + s.Duration;      // 探出 + 停顿 + 下落：这一版砍到了原来的一半
    // 逐帧走完整个窗口：余韵只活 `BurstLife` 秒，而三支是**错时**落的，一把抓不到三处。
    // 按**实例**去重（三支落在同一个位置，位置分不开），数一数一共留下几处爆炸余韵。
    var bursts = new HashSet<CombatEffect>();
    for (int i = 0; i < 40; i++)
    {
        Step(g, .05);
        foreach (var e in g.Effects.Where(e => e.Skill == "skill_18" && e.Kind == "ground")) bursts.Add(e);
    }
    Assert(lone.Hp < lone.MaxHp, "三支全砸在它身上");
    // 落地留下"剑气爆炸"的余韵：一个 `Damage = 0` 的 ground 效果，**只走寿命与绘制**，
    // 好让表现层有 0→1 的进度去画真正的扩散动画（否则效果在落地当帧就被回收，只剩坠落最后一帧闪一下）。
    Assert(bursts.Count == s.ProjectileCount, $"每支各留一下爆炸余韵：{bursts.Count}");
    Assert(bursts.All(e => e.Damage == 0 && Math.Abs(e.AoeRadius - s.AoeRadius) < 1e-9), "余韵不带伤害、半径取配置");
    double afterBurst = lone.Hp;
    Step(g, 1);
    Assert(lone.Hp == afterBurst, "余韵一次伤害都不造成（它不是第二段伤害）");
    Assert(!g.Effects.Any(e => e.Skill == "skill_18"), "余韵超时自散");
});
Check("苍穹剑陨 有多只时各锁一只，尽量不重复", () => {
    var g = Salvo("skill_18", 300);
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    var pack = g.Battle.Enemies.ToArray();
    for (int i = 0; i < pack.Length; i++) pack[i].X = g.Battle.PlayerX + 300 + i * 200;
    Assert(g.ForceRelease("skill_18"), "the volley is released");
    var blades = g.Effects.Where(e => e.Skill == "skill_18").ToArray();
    // 三只怪、三支剑：各锁一只，落点因此互不相同、且每支都压在**某只怪当时的站位**上。
    var lands = blades.Select(e => e.X).OrderBy(x => x).ToArray();
    Assert(lands.Distinct().Count() == blades.Length, "三支落在三个不同的位置");
    Assert(pack.All(e => lands.Any(x => Math.Abs(x - e.X) < 1e-9)), "每支都落在某只怪身上");
    // 关键：**落点跟着怪走，不跟着角色走**。把角色往前挪一大截（模拟 1.3 秒的下落期间边走边打），
    // 落点必须纹丝不动——这是"人在走路，打击位置跑到人身后去了"那条的回归护栏。
    var before = blades.Select(e => e.X).ToArray();
    g.Battle.PlayerX += 440;
    Step(g, 1.2);
    foreach (var b in blades) Assert(b.X == before[Array.IndexOf(blades, b)], "落点不随角色移动");
    Step(g, 1);
    Assert(pack.All(e => e.Hp < e.MaxHp), "三只各挨一支");
    Assert(!g.Effects.Any(e => e.Skill == "skill_18"), "the volley is gone once it has landed");
});

Check("伤害统计：按技能归因，有效伤害与实际掉血一致", () => {
    var g = Salvo("skill_01", 200);
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    var target = g.Battle.Enemies[0];
    double before = target.Hp;
    g.ResetDamageStats();
    Assert(g.ForceRelease("skill_01"), "released");
    Step(g, .5);
    var row = g.DamageStats.Rows["skill_01"];
    Assert(row.Hits == 1, $"御剑术单发打中一次：{row.Hits}");
    Assert(Math.Abs(row.Effective - (before - target.Hp)) < 1e-9,
        $"有效伤害就是实际掉的血：{row.Effective} vs {before - target.Hp}");
});
Check("伤害统计：溢出单列，有效 + 溢出 = 名义伤害", () => {
    var g = New(); g.Step(.05);
    var e = g.Battle.Enemies[0];
    e.X = 5000; e.Atk = 0; e.Hp = e.MaxHp = 100;    // 挪远停手，只留我这一发
    g.ResetDamageStats();
    g.HurtEnemy(e, 250, "skill_test");              // 一发远超剩余血量
    var row = g.DamageStats.Rows["skill_test"];
    Assert(Math.Abs(row.Effective - 100) < 1e-9 && Math.Abs(row.Overkill - 150) < 1e-9,
        $"有效 = 剩余血量、溢出 = 差额：{row.Effective} / {row.Overkill}");
});
Check("伤害统计：记的是**乘过易伤之后**的值", () => {
    // 这条专门锁"取值点在易伤乘区之后"——若在 `Hit` 里读 damage，数字会偏小。
    var g = New(); g.Step(.05);
    var e = g.Battle.Enemies[0];
    e.X = 5000; e.Atk = 0; e.Hp = e.MaxHp = 1e6;
    e.VulnerableUntil = 5; e.VulnerableFactor = 1.5;
    g.ResetDamageStats();
    g.HurtEnemy(e, 100, "skill_v");
    Assert(Math.Abs(g.DamageStats.Rows["skill_v"].Effective - 150) < 1e-9,
        $"易伤要算进去：{g.DamageStats.Rows["skill_v"].Effective}");
});
Check("伤害统计：灼烧跳伤归到施加它的那个技能名下", () => {
    var g = Salvo("skill_02", 90);                  // 焚天剑诀：天降火海
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    g.ResetDamageStats();
    Assert(g.ForceRelease("skill_02"), "released");
    Step(g, 2);                                     // 落地 + 火海烧几跳
    var row = g.DamageStats.Rows["skill_02"];
    Assert(row.Hits > 0 && row.Effective > 0, "火海的直伤与跳伤都记在 skill_02 名下");
    Assert(g.Battle.Enemies[0].DotSkill == "skill_02", "灼烧记下了来源剑诀");
});
Check("伤害统计：普攻单列一行（来源是空串）", () => {
    var g = New(basic: true); g.Step(.05);
    g.ResetDamageStats();
    Step(g, 4);
    Assert(g.DamageStats.Rows.ContainsKey("") && g.DamageStats.Rows[""].Effective > 0, "普攻记在空串名下");
});
Check("伤害统计：影分身那一份归到剑二十三名下（它复制出来的东西就是它的价值）", () => {
    var g = Salvo("skill_01", 400);
    g.State.Skills["skill_19"] = 1;                 // 剑二十三
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    Assert(g.ForceRelease("skill_19"), "分身窗口开了");
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    g.ResetDamageStats();
    Assert(g.ForceRelease("skill_01"), "released");
    Step(g, 1);
    Assert(g.DamageStats.Rows["skill_01"].Hits == 1, $"本体那一份记在御剑术名下：{g.DamageStats.Rows["skill_01"].Hits}");
    Assert(g.DamageStats.Rows["skill_19"].Hits == 1, $"分身那一份记在剑二十三名下：{g.DamageStats.Rows["skill_19"].Hits}");
    // 分身那一份按继承比例打折，所以更小——"剑二十三到底值多少"因此能单独看出来。
    Assert(g.DamageStats.Rows["skill_19"].Effective < g.DamageStats.Rows["skill_01"].Effective,
        "分身按继承比例打折，伤害小于本体");
});
Check("伤害统计：零伤害的爆炸余韵不记账", () => {
    // 苍穹剑陨落地派生的那个 `Damage = 0` 的余韵照样会走 `Hit`，不拦的话会凭空加一次命中。
    var g = Salvo("skill_18", 200);
    var lone = g.Battle.Enemies[0];
    g.Battle.Enemies.RemoveAll(e => e != lone);     // 只留一只：命中数应当恰等于支数
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    g.ResetDamageStats();
    Assert(g.ForceRelease("skill_18"), "released");
    Step(g, 3);
    Assert(g.DamageStats.Rows["skill_18"].Hits == config.Skills["skill_18"].ProjectileCount,
        $"命中数 = 支数（余韵不记账）：{g.DamageStats.Rows["skill_18"].Hits}");
    Assert(g.DamageStats.Rows["skill_18"].Effective > 0, "而且确实打出了伤害");
});
Check("伤害统计：重置清空账本，并把统计时长从此刻重新算", () => {
    var g = Salvo("skill_01", 200);
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    Assert(g.ForceRelease("skill_01"), "released");
    Step(g, 1);
    Assert(g.DamageStats.Rows.Count > 0 && g.DamageStatsSeconds > 0, "先有数据");
    g.ResetDamageStats();
    Assert(g.DamageStats.Rows.Count == 0 && g.DamageStatsSeconds < 1e-9, "重置清空并把起点挪到此刻");
    Step(g, 1);
    Assert(Math.Abs(g.DamageStatsSeconds - 1) < .06, $"统计时长从重置那一刻算起：{g.DamageStatsSeconds:0.###}");
});

Check("默认（无生成带）的天降剑雨仍以阵心对称铺开，命中数按几何算", () => {
    // 单体口径的前提是"一次施法的 k 支全部命中同一目标"，但它**不是配置写完就自动成立的**：
    // 各支按 spread 在阵心两侧铺开，最外侧那支离阵心 (count-1)/2 × spread，超过 aoe_radius 就打不到阵心那只。
    // 这里用**真实会话**数一遍，而不是在自检里把工具那条公式再抄一遍——它同时验证了 Core 的落点算法
    // （`CastVolley` 把 lane 算进 effect.X）与配置文件是同一条口径。
    var g = Salvo("skill_06", 300);          // 万剑决：5 支 / 间距 40 / 落点半径 60 → 只有中间 3 支够得着
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    var s = config.Skills["skill_06"];
    var lone = g.Battle.Enemies[0];
    g.Battle.Enemies.RemoveAll(e => e != lone);     // 场上只剩一只：正是 E1 的口径
    Assert(g.ForceRelease("skill_06"), "released");
    int reaching = g.Effects.Count(e => e.Skill == "skill_06" && Math.Abs(e.X - lone.X) < s.AoeRadius);
    int want = Enumerable.Range(0, s.ProjectileCount)
        .Count(i => Math.Abs((i - (s.ProjectileCount - 1) / 2.0) * s.Spread) < s.AoeRadius);
    Assert(reaching == want && reaching < s.ProjectileCount,
        $"{s.ProjectileCount} 支里 {reaching} 支够得着阵心（口径记 {want} 支）");
});
Check("剑气流云壁 roots the whole field at cast, then a ground wave shoves each enemy exactly once", () => {
    // 定身：射程 950 之外的敌人也要被定住——"全屏"不看射程。
    var rooted = Salvo("skill_05", 200);
    var line = rooted.Battle.Enemies.ToArray();
    for (int i = 0; i < line.Length; i++) { line[i].X = rooted.Battle.PlayerX + 200 + i * 900; line[i].StunUntil = 0; }
    rooted.Effects.Clear(); rooted.Battle.Cooldowns.Clear();
    Assert(rooted.ForceRelease("skill_05"), "the wave is released");
    Assert(line.All(e => Math.Abs(e.StunUntil - 1) < 1e-9), "every enemy is rooted at cast, range ignored");
    var wave = rooted.Effects.Single(e => e.Skill == "skill_05");
    Assert(wave.Timer > 0 && wave.X == rooted.Battle.PlayerX, "the wave waits out its wind-up at the caster");
    Step(rooted, .4);                                   // 前摇只有 0.5 秒：这里还在里面
    Assert(wave.X == rooted.Battle.PlayerX, "still winding up before it launches");
    // 前摇期间角色还在前进：发射点必须跟着人走。冻结在施放那一刻的话，起飞时剑气已经被甩到身后，
    // 表现为"冲击波从画面左边冒出来"——这条是那个 bug 的回归护栏。
    rooted.Battle.PlayerX += 200;
    Step(rooted, .05);
    Assert(wave.X == rooted.Battle.PlayerX, "the launch point follows the caster during the wind-up");
    Step(rooted, .3);                                   // 前摇 0.5 秒走完，剑气开始推进
    Assert(wave.X > rooted.Battle.PlayerX, "the wave is on its way once the wind-up ends");
    // 击退：靶子被定死（不还手、不移动），位置变化只可能来自剑气。
    var pushed = Salvo("skill_05", 200);
    foreach (var e in pushed.Battle.Enemies) e.X = pushed.Battle.PlayerX + 600;
    var victim = pushed.Battle.Enemies[0];
    pushed.Effects.Clear(); pushed.Battle.Cooldowns.Clear();
    Assert(pushed.ForceRelease("skill_05"), "second release");
    double x0 = victim.X, hp0 = victim.Hp;
    Step(pushed, 2.5);                                  // 0.5s 前摇 + 600/700 ≈ 0.86s 推进
    Assert(victim.Hp < hp0, "the wave damaged it");
    // 正好推开一个 knockback 的距离：多挨一次就会是两倍，这一条同时证明了"每个敌人只算一次"。
    Assert(Math.Abs(victim.X - (x0 + 160)) < 1e-9, $"pushed exactly once: {x0} -> {victim.X}");
    Step(pushed, 3.5);                                  // duration 4 + 前摇 0.5 = 寿命 4.5 秒
    Assert(!pushed.Effects.Any(e => e.Skill == "skill_05"), "the wave dies after its configured duration");
});
Check("斩鬼神 strikes the field's healthiest enemy however far away, and only after the wind-up", () => {
    var g = Salvo("skill_10", 200);
    var line = g.Battle.Enemies.ToArray();
    foreach (var e in line) { e.X = g.Battle.PlayerX + 200; e.Hp = e.MaxHp = 1e6; }
    var near = line[0]; near.Hp = near.MaxHp = 1e3;               // 身前残血
    var far = line[1]; far.X = g.Battle.PlayerX + 4000; far.Hp = far.MaxHp = 1e9;   // 射程外满血
    g.Battle.Enemies.Remove(line[2]);
    g.Effects.Clear(); g.Battle.Cooldowns.Clear();
    Assert(g.ForceRelease("skill_10"), "the strike is released");
    var strike = g.Effects.Single(e => e.Skill == "skill_10");
    Assert(strike.Target == far.Id, "the healthiest enemy is picked, range ignored");
    Assert(far.Hp == far.MaxHp && near.Hp == near.MaxHp, "nothing has landed yet — the sword is still winding up");
    Step(g, 1);                                                    // duration 0.9s
    Assert(far.Hp < far.MaxHp, "the chosen enemy was struck");
    Assert(near.Hp == near.MaxHp, "and the one in front was left alone");
});
Check("诛仙剑阵 drops four swords in sequence, each one striking every enemy on the field", () => {
    var g = Salvo("skill_15", 400);
    var line = g.Battle.Enemies.ToArray();
    // 一个在身前、一个在身后、一个远到任何落点半径都够不着：aoe_all 必须三个全打。
    line[0].X = g.Battle.PlayerX + 400;
    line[1].X = g.Battle.PlayerX - 500;
    line[2].X = g.Battle.PlayerX + 9000;
    g.Effects.Clear(); g.Battle.Cooldowns.Clear();
    Assert(g.ForceRelease("skill_15"), "the array is released");
    var swords = g.Effects.Where(e => e.Trajectory == "sky_drop").ToArray();
    Assert(swords.Length == 4, $"four swords: {swords.Length}");
    Assert(swords.Select(s => s.X).Distinct().Count() == 4, "four distinct landing points: " + string.Join(",", swords.Select(s => s.X)));
    // 四柄同时浮现、同时落地（volley_interval 为 0）：同一批的起飞前停留必须完全一致。
    // 初始高度的差别是表现层的事（SkyDropTop 分层 + spawn_jitter），Core 不参与。
    Assert(swords.Select(s => s.Timer).Distinct().Count() == 1, "the swords appear and land together: " + string.Join(",", swords.Select(s => s.Timer)));
    Assert(swords.All(s => s.AoeAll), "the array is configured to strike the whole field");
    Step(g, 2);                                                    // 浮现停顿 1 + 下落 0.5，四柄同时
    Assert(line.All(e => e.Hp < e.MaxHp), "every enemy was struck, wherever it stands");
});
Check("ground-only skills cannot touch flying monsters, while everything else still reaches them", () => {
    var g = New(); g.Step(.05);
    g.State.Skills.Clear(); g.Battle.Enemies.Clear();
    EnemyState Add(string monster, double x)
    {
        var e = new EnemyState { Id = g.Battle.NextEnemyId++, MonsterId = monster, Kind = "normal", X = x, Hp = 1e6, MaxHp = 1e6, Atk = 0, AttackTimer = 999, StunUntil = 1e9 };
        g.Battle.Enemies.Add(e); return e;
    }
    var ground = Add("slime", g.Battle.PlayerX + 200);
    var flyer = Add("bat", g.Battle.PlayerX + 260);   // 就在地面靶旁边：没有层判定的话它一定会被波及
    // 焚天剑诀定位是 ground（天降火海），且是触发类真诀，必须显式放一次：地面靶掉血，空中靶毫发无损。
    g.State.Skills["skill_02"] = 1; g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    Assert(g.ForceRelease("skill_02"), "焚天剑诀 released");
    Step(g, 2);
    Assert(ground.Hp < ground.MaxHp, "the ground target was struck");
    Assert(flyer.Hp == flyer.MaxHp, $"the flying target was left alone: {flyer.Hp}");
    // 不限层的技能（御剑术 / 普攻）照样打得到它——这是"只练地面招也不会卡死"的护栏。
    g.Battle.Enemies.Remove(ground);
    g.State.Skills.Clear(); g.State.Skills["skill_01"] = 1; g.Battle.Cooldowns.Clear();
    flyer.Hp = flyer.MaxHp; flyer.X = g.Battle.PlayerX + 300;
    Step(g, 3);
    Assert(flyer.Hp < flyer.MaxHp, "a layer-agnostic skill still reaches the flyer");
});
Check("a skill aimed only at the air never picks a ground target", () => {
    // hits = air 目前没有在役技能，用改过的配置保住这条路径的覆盖（与 pierce / hover_homing 同一做法）。
    var airOnly = GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_18", "hits", "air") : source[f]);
    var g = SalvoOn(airOnly, "skill_18", 200);         // 靶子全是青苔妖（地面）
    g.Effects.Clear(); g.Battle.Cooldowns.Clear();
    Step(g, 2);
    Assert(g.Battle.Enemies.All(e => e.Hp == e.MaxHp), "an air-only skill left the ground targets alone");
    Assert(g.Effects.Count == 0, "and it never even placed its field");
    Assert(g.Battle.Cooldowns.GetValueOrDefault("skill_18") == 0, "no legal target means no cooldown consumed");
});
Check("haste speeds up attack cooldowns but never buff cooldowns", () => {
    // 攻速若连增益类剑诀一起加速，仙风云体术（15s 冷却 / 6s 持续）会在持续期内转好，变成 100% 常驻。
    var g = Salvo("skill_04");                       // 仙风云体术：以自身为目标的增益，交战即可放
    Assert(g.HasteRemaining > 0 && Math.Abs(g.HasteFactor - 1.25) < 1e-9, "haste 25% means cooldowns tick 1.25x");
    g.Battle.Cooldowns.Clear();
    g.Battle.Cooldowns["skill_01"] = 10;              // 输出类剑诀：应被加速
    g.Battle.Cooldowns["skill_04"] = 10;              // 增益类剑诀：不该被加速
    g.Step(.05);
    Assert(Math.Abs(g.Battle.Cooldowns["skill_01"] - (10 - .0625)) < 1e-9, $"attack cooldown ticked 1.25x: {g.Battle.Cooldowns["skill_01"]}");
    Assert(Math.Abs(g.Battle.Cooldowns["skill_04"] - (10 - .05)) < 1e-9, $"buff cooldown ticked 1x: {g.Battle.Cooldowns["skill_04"]}");
});
Check("a power-1 buff leaves the shared damage window alone", () => {
    // 剑罡护体（power 1.5，持续走配置）生效中再放仙风云体术（power 1，6 秒）：
    // 若纯功能向的增益也占用共享窗口，剩余时间会被顶成 6 秒。
    // 持续秒数从配置读，别写死——增益时长是会调的。
    double window = config.Skills["skill_14"].Duration;
    var g = Salvo("skill_14");                       // 靶子在身前 90，增益才有合法目标可放
    Assert(Math.Abs(g.BuffRemaining - window) < 1e-9, $"damage buff window is {window}s: {g.BuffRemaining}");
    g.State.Skills["skill_04"] = 1; g.Battle.Cooldowns.Clear();
    g.Step(.05);
    Assert(g.HasteRemaining > 0, "haste came up");
    Assert(g.BuffRemaining <= window, $"the power-1 buff did not reset the damage window: {g.BuffRemaining}");
});
Check("crit_reduce raises the crit rate while it lasts", () => {
    // 固定 seed 下确定性可比：叠伤害合计，加成期间应明显高于基础暴击率。
    // 每轮清冷却 → 该轮必定出手；每轮跑 0.4 秒，够肩侧的三支剑飞到 200 处的靶子。
    // 清冷却顺带让醉仙望月步每轮重新上，保证整个统计窗口内加成都生效。
    double Harvest(GameSession s, EnemyState target, int rounds)
    {
        double total = 0;
        for (int i = 0; i < rounds; i++) { s.Battle.Cooldowns.Clear(); double before = target.Hp; Step(s, .4); total += before - target.Hp; }
        return total;
    }
    var plain = Salvo("skill_01", 200);
    var plainTarget = plain.Battle.Enemies[0];
    var blessed = Salvo("skill_01", 200);
    var blessedTarget = blessed.Battle.Enemies[0];
    blessed.State.Skills["skill_09"] = 1; blessed.Battle.Cooldowns.Clear();
    blessed.Step(.05);
    Assert(blessed.CritBonusRemaining > 0 && blessed.CritBonus > .29, "crit buff is up");
    double without = Harvest(plain, plainTarget, 60), with = Harvest(blessed, blessedTarget, 60);
    Assert(with > without * 1.05, $"crit bonus raised damage: {without:0} -> {with:0}");
});
Check("a crit shortens one cooling skill's cooldown", () => {
    // 两次跑同一个 seed，唯一差别是增益是否在生效：多余的那部分冷却缩减只能来自暴击抽签。
    GameSession Run(bool withBuff)
    {
        var s = Salvo("skill_01", 200);
        s.Battle.Enemies[0].Hp = s.Battle.Enemies[0].MaxHp = 1e9;
        s.State.Skills["skill_06"] = 1;                       // sink：习得但冷却很长，不会出手
        if (withBuff) s.State.Skills["skill_09"] = 1;         // 醉仙望月步
        s.Battle.Cooldowns.Clear();
        s.Battle.Cooldowns["skill_06"] = 30;
        s.Battle.Cooldowns["skill_09"] = 0;
        // 窗口要够长：缩冷却改成"每次施法最多一次"之后，触发密度降到 1/3（御剑术一轮 3 支只由第一支触发），
        // 3 秒里等不到暴击就会变成假失败。10 秒足够，增益自身 6 秒持续也会在这段时间里覆盖大部分。
        Step(s, 10);
        return s;
    }
    double with = Run(true).Battle.Cooldowns["skill_06"], without = Run(false).Battle.Cooldowns["skill_06"];
    Assert(with < without, $"crits shortened a cooling skill: {without:0.##} -> {with:0.##}");
});
Check("crits never shorten buff cooldowns, and only fire once per cast", () => {
    // 两条口径合起来看：一个增益冷却 + 一个非增益"沉槽"冷却都不出手，30 秒后自然衰减 30。
    // 增益必须正好剩 30（暴击碰不到它）；沉槽必须低于 30（暴击确实在缩短它，否则这条是空断言）。
    var g = Salvo("skill_01", 200);
    foreach (var id in new[] { "skill_04", "skill_06", "skill_09" }) g.State.Skills[id] = 1;
    g.State.Skills["skill_01"] = 1;
    g.Battle.Cooldowns.Clear();
    g.Battle.Cooldowns["skill_04"] = 60; g.Battle.Cooldowns["skill_06"] = 60;
    Step(g, 30);
    Assert(Math.Abs(g.Battle.Cooldowns["skill_04"] - 30) < 1e-6,
        $"a buff cooldown only decays naturally: {g.Battle.Cooldowns["skill_04"]:0.##}");
    Assert(g.Battle.Cooldowns["skill_06"] < 29, $"crits did shorten a non-buff cooldown: {g.Battle.Cooldowns["skill_06"]:0.##}");

    // 每次施法最多一次：把暴击率拉满，御剑术一轮 3 支，旧写法会一轮触发 3 次。
    var alwaysCrit = GameConfig.Load(f => f == "fightattr.csv" ? Cell(source[f], "crit", "base_value", "1") : source[f]);
    var b = SalvoOn(alwaysCrit, "skill_01", 200);
    b.State.Skills["skill_06"] = 1; b.State.Skills["skill_09"] = 1;
    b.Battle.Cooldowns.Clear();
    b.Battle.Cooldowns["skill_06"] = 300;      // 沉槽：冷却远长于观察窗口，不会被自然衰减清空
    double prev = 0; int volleys = 0;
    for (int i = 0; i < 600; i++)              // 30 秒
    {
        b.Step(.05);
        double now = b.Battle.Cooldowns.GetValueOrDefault("skill_01");
        if (now > prev + .5) volleys++;        // 冷却被顶回满值 = 出了一轮手
        prev = now;
    }
    double extra = (300 - 30) - b.Battle.Cooldowns["skill_06"];   // 超出自然衰减的部分全是缩冷却
    Assert(extra >= 5, $"crits did shorten the cooling skill: {extra:0.##}s");
    Assert(extra <= volleys / 2 + 2, $"at most one shortening per cast: {extra:0.##}s over {volleys} casts");
});
Check("homing blade finishes its flight to the dead target's last position", () => {
    var g = SalvoOn(hoverForm, "skill_01", 900);    // 放远些，飞行 0.45 秒时还在半路
    var blade = g.Effects.First(e => e.Trajectory == "hover_homing");
    // 目标位置在发射那一刻就记下了。必须**在 Step 之前**取：靶子在停步距离之外，角色会边走边打，
    // 拿走动之后的 PlayerX 去比会误判。
    double targetX = blade.TargetX;
    Assert(targetX > g.Battle.PlayerX + 800, "target position was remembered at launch");
    Step(g, .45);                                   // 越过悬浮期，进入飞行
    g.Battle.Enemies.Clear();                       // 目标中途死亡
    double before = blade.X;
    Step(g, .1);
    Assert(g.Effects.Contains(blade), "blade survives instead of vanishing");
    // 仍然朝**目标最后所在的位置**走：旧写法是沿发射方向一直飘到出界，画面上就是贴着地面平行右移。
    Assert(blade.X > before && blade.X <= targetX, $"still closing on the remembered point: {blade.X:0.#}/{targetX:0.#}");
    Step(g, 1);
    Assert(!g.Effects.Contains(blade), "gone once it reaches that point");
});
Check("青元剑芒 keeps closing on the dead target's position instead of drifting right", () => {
    var g = Salvo("skill_11", 700);                 // 青元剑芒：3 支弧线追踪
    var blades = g.Effects.Where(e => e.Trajectory == "arc_homing").ToArray();
    Assert(blades.Length == 3, "three blades");
    double targetX = blades[0].TargetX;
    Step(g, .1);
    g.Battle.Enemies.Clear();                       // 目标中途死亡
    double before = blades[0].X;
    Step(g, .1);
    Assert(blades[0].X > before && blades[0].X <= targetX, $"still closing on the remembered point: {blades[0].X:0.#}/{targetX:0.#}");
    Step(g, 1);
    Assert(blades.All(b => !g.Effects.Contains(b)), "every blade is gone once it reaches that point");
});
Check("真诀的概率可以逐次累加，摇中后清零", () => {
    // 0.01 起步 + 每次 +0.99：最多两次普攻就必中，于是"累加"和"清零"都是确定行为，不靠运气。
    // 断言的是**不变量**（没中会涨、中了会落回起步值），因此不受第一次是否撞上 1% 影响。
    var ramp = GameConfig.Load(f => f == "SwordSkill.csv"
        ? Cell(Cell(source[f], "skill_07", "trigger_chance", "0.01"), "skill_07", "trigger_chance_step", "0.99")
        : source[f]);
    var g = new GameSession(ramp, seed: 7) { BasicAttackEnabled = true };
    g.State.Skills["skill_07"] = 1;
    g.Battle.Cooldowns.Clear();
    Assert(Math.Abs(g.TriggerChanceNow("skill_07") - 0.01) < 1e-9, "起步概率等于配置值");
    double prev = g.TriggerChanceNow("skill_07");
    bool grew = false, reset = false;
    for (int i = 0; i < 40; i++)
    {
        Step(g, 1);                                   // 一次普攻摇一次
        double now = g.TriggerChanceNow("skill_07");
        if (now > prev + 1e-9) grew = true;           // 涨了 = 上一次没摇中
        if (now < prev - 1e-9) reset = true;          // 落回起步值 = 上一次摇中了
        prev = now;
    }
    Assert(grew, "没摇中时会逐次累加");
    Assert(reset, "摇中之后回到起步值");
    // step = 0 的剑诀（焚天剑诀 / 御雷真诀）永远是固定概率，行为与改动前一致。
    Assert(Math.Abs(g.TriggerChanceNow("skill_02") - ramp.Skills["skill_02"].TriggerChance) < 1e-9, "未配置累加的剑诀不涨");
});
Check("利用状态：目标带状态时增伤，不带时不变", () => {
    var noCrit = GameConfig.Load(f => f == "fightattr.csv" ? Cell(source[f], "crit", "base_value", "0") : source[f]);
    var g = SalvoOn(noCrit, "skill_10", 400);          // 斩鬼神：target 类，延迟 0.9 秒结算
    var foe = g.Battle.Enemies.OrderBy(e => e.Id).First();
    // SalvoOn 为了定住靶子给了 StunUntil = 1e9——那本身就算「带状态」。先清掉，否则两组都吃加成、比不出差异。
    foe.StunUntil = 0;
    foe.Hp = foe.MaxHp = 1e9;                          // 拉高血量，确保 highest_hp 一定选中它
    double HitOnce()
    {
        g.Battle.Cooldowns.Clear(); g.Effects.Clear();
        Assert(g.ForceRelease("skill_10"), "斩鬼神 released");
        Step(g, 1.2);
        double dealt = foe.MaxHp - foe.Hp;
        foe.Hp = foe.MaxHp;
        return dealt;
    }
    double clean = HitOnce();
    Assert(clean > 0, "干净目标也吃到了伤害");
    foe.DotUntil = 5;                                  // 挂个状态（DPS 为 0，只当"有状态"的标记）
    double marked = HitOnce();
    Assert(Math.Abs(marked / clean - 1.3) < 1e-9, $"带状态的目标多挨 30%：{marked:0.#}/{clean:0.#}");
});
Check("剑罡护体：护盾吸收伤害，敌人近身时环绕飞剑还手", () => {
    // 关掉闪避：不然"血没掉"可能是因为躲开了，而不是护盾挡的。
    var noDodge = GameConfig.Load(f => f == "fightattr.csv" ? Cell(source[f], "dodge", "base_value", "0") : source[f]);
    var g = SalvoOn(noDodge, "skill_14", 200);
    Assert(g.ForceRelease("skill_14"), "剑罡护体 released");
    Assert(g.ShieldRemaining > 0, "护盾生效");
    // 还手：护盾一上身就开始按 guard_interval 出手。抓"刚射出那一帧"——它飞得很快，一步之后就已经命中了。
    // Index = 1 是刻意的：技能名标签与"暴击缩冷却"都只认第 0 支，环绕飞剑不该抢那个名额。
    // 扫一段窗口而不是抓某一帧：飞剑出手后很快就命中消失，单帧捕捉太脆。
    bool guardFired = false;
    for (int i = 0; i < 40 && !guardFired; i++)
    {
        Step(g, .05);
        guardFired = g.Effects.Any(e => e.Skill == "skill_14" && e.Index == 1);
    }
    Assert(guardFired, "环绕飞剑自行出手，且不抢技能名的名额");
    // 让一只怪贴身打一下：护盾量 = 攻击 × 2（25×2 = 50），足以把这一击整个吃掉。
    var foe = g.Battle.Enemies[0];
    foe.X = g.Battle.PlayerX + 60; foe.StunUntil = 0; foe.Atk = 40; foe.AttackTimer = 0;
    double hp = g.Battle.PlayerHp;
    Step(g, .1);
    Assert(g.Battle.PlayerHp == hp, $"护盾挡下了这一击：{hp} → {g.Battle.PlayerHp}");
    // 靶场关掉了普攻，所以下面这一秒里除环绕飞剑之外没有任何伤害来源。
    g.Effects.Clear();
    double foeHp = foe.Hp;
    Step(g, 1);
    Assert(foe.Hp < foeHp, "环绕飞剑自行出手并打中了敌人");
});
Check("焚天剑诀的火海铺在最靠前的那只身上（而不是脚边）", () => {
    var g = Salvo("skill_02", 400);
    var foes = g.Battle.Enemies.OrderBy(e => e.Id).ToArray();
    foes[0].X = g.Battle.PlayerX + 200;      // 脚边那只
    foes[1].X = g.Battle.PlayerX + 500;
    foes[2].X = g.Battle.PlayerX + 800;      // 最靠前的那只
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    Assert(g.ForceRelease("skill_02"), "焚天剑诀 released");
    Step(g, 1);                              // 天降要 0.75 秒才落地成火海
    var sea = g.Effects.FirstOrDefault(e => e.Kind == "ground" && e.Skill == "skill_02");
    Assert(sea is not null, "落点留下一片火海");
    Assert(Math.Abs(sea!.X - foes[2].X) < 1e-6, $"火海落在最靠前那只身上：{sea.X:0} vs {foes[2].X:0}");
});
Check("追踪弹的目标中途死亡时，飞完这一程再落点重索敌（而不是白飞）", () => {
    // 单发版本：让"改追了谁"没有别的解释（三支的话，另外两支追了谁说不清）。
    var single = GameConfig.Load(f => f == "SwordSkill.csv" ? Cell(source[f], "skill_11", "projectile_count", "1") : source[f]);
    var g = SalvoOn(single, "skill_11", 400);
    var foes = g.Battle.Enemies.OrderBy(e => e.Id).ToArray();
    foes[0].X = g.Battle.PlayerX + 400;      // 会被锁定的那只
    foes[1].X = g.Battle.PlayerX + 440;      // 落点 80 以内
    foes[2].X = g.Battle.PlayerX + 900;      // 远处，够不到（不该被改追）
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    Assert(g.ForceRelease("skill_11"), "青元剑芒 released");
    var blade = g.Effects.Single(e => e.Trajectory == "arc_homing");
    Assert(blade.Target == foes[0].Id, "同血量按 Id 升序锁定");
    double arcBefore = blade.Arc;
    g.Battle.Enemies.Remove(foes[0]);        // 模拟"它被别的技能打死了"
    double before = foes[1].Hp;
    Step(g, 1);
    Assert(foes[1].Hp < before, "落点附近的敌人挨到了伤害（没有白飞）");
    // 中途确实改索过敌：目标换成落点附近那只、弧度取反（画面上是一道反向的波）。
    Assert(blade.Target == foes[1].Id && blade.Reacquired == 1, "落点重索敌：改追附近那只");
    Assert(Math.Abs(blade.Arc + arcBefore) < 1e-9, "反向弧度（弧度取反）");
    Assert(!g.Effects.Contains(blade), "命中后消散");
    // 落点附近没人 → 直接消失，**本次伤害丢失**（用户明确要的行为，不做兜底命中）。
    var lonely = SalvoOn(single, "skill_11", 400);
    var prey = lonely.Battle.Enemies.OrderBy(e => e.Id).First();
    lonely.Battle.Enemies.RemoveAll(e => e.Id != prey.Id);
    lonely.Battle.Cooldowns.Clear(); lonely.Effects.Clear();
    Assert(lonely.ForceRelease("skill_11"), "青元剑芒 released");
    var alone = lonely.Effects.Single(e => e.Trajectory == "arc_homing");
    lonely.Battle.Enemies.Remove(prey);      // 目标死了，附近也没有别人
    Step(lonely, 1);
    Assert(!lonely.Effects.Contains(alone), "索不到敌就消失");
});
Check("定点弹的目标中途死亡时，同样改打落点附近的敌人", () => {
    var g = Salvo("skill_10", 400);          // 斩鬼神：target 类，延迟 0.9 秒才结算
    var foes = g.Battle.Enemies.OrderBy(e => e.Id).ToArray();
    foes[0].Hp = foes[0].MaxHp = 1e9;        // 拉高，确保 highest_hp 锁定它
    foes[1].X = foes[0].X + 60;              // 落点 80 以内
    foes[2].X = foes[0].X + 900;
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    Assert(g.ForceRelease("skill_10"), "斩鬼神 released");
    double before = foes[1].Hp;
    g.Battle.Enemies.Remove(foes[0]);        // 目标在延迟期死掉
    Step(g, 1.2);
    Assert(foes[1].Hp < before, "落点附近的那只挨到了这一剑");
});
Check("增益类剑诀的升级缩短冷却，且有覆盖率下限", () => {
    var g = Salvo("skill_04", 200);          // 仙风云体术：增益，冷却 15 / 持续 6
    Assert(Math.Abs(g.BuffCooldownNow("skill_04", 1) - 15) < 1e-9, "1 级等于配置冷却");
    double high = g.BuffCooldownNow("skill_04", 32);
    Assert(high < 15, $"等级高了冷却变短：{high:0.#}");
    Assert(high >= 6 * 1.25 - 1e-9, $"但不低于 持续×1.25：{high:0.#}");
    Assert(Math.Abs(g.BuffCooldownNow("skill_01", 32) - 1.2) < 1e-9, "输出类冷却不随等级变");
});
Check("青元剑芒 reaps the weakest and focuses fire when outnumbered", () => {
    // 阶梯血量：三只靶子分别 300 / 200 / 100，让"最低血"有唯一解。
    var g = Salvo("skill_11", 400);
    var foes = g.Battle.Enemies.OrderBy(e => e.Id).ToArray();
    foes[0].Hp = foes[0].MaxHp = 300; foes[1].Hp = foes[1].MaxHp = 200; foes[2].Hp = foes[2].MaxHp = 100;
    g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    Assert(g.ForceRelease("skill_11"), "青元剑芒 released");
    // 三段分别指向最脆、次脆、最厚——而不是"离玩家最近的三个"。
    Assert(g.Effects.Select(e => e.Target).SequenceEqual(new[] { foes[2].Id, foes[1].Id, foes[0].Id }),
        "aims at the weakest first: " + string.Join(",", g.Effects.Select(e => e.Target)));
    // 场上只剩一只时，三段全打在它身上（打 BOSS 不吃亏）——这是 TargetsWithRepeats 的现成行为。
    var solo = Salvo("skill_11", 400);
    solo.Battle.Enemies.RemoveRange(1, solo.Battle.Enemies.Count - 1);
    long only = solo.Battle.Enemies[0].Id;
    solo.Battle.Cooldowns.Clear(); solo.Effects.Clear();
    Assert(solo.ForceRelease("skill_11"), "青元剑芒 released solo");
    var shots = solo.Effects.Where(e => e.Skill == "skill_11").ToArray();
    Assert(shots.Length == 3 && shots.All(e => e.Target == only), "all three blades focus the only enemy");
});
Check("summon and pet bolts keep the default speed", () => {
    // speed 列只作用于剑诀：召唤弹与宠物弹走同一条默认路径，不能连带被配置改动（回归护栏）。
    Assert(new CombatEffect().Speed == 1500, "default bullet speed is unchanged");
    // 已无在役召唤（剑侍归档，跟随召唤机制留给以后的剑灵系统），用内存改配置把一个剑诀改回召唤，保住这条护栏。
    // 改 kind 必须同时清掉天降形态并把弹数压回 1，否则过不了校验。
    var summoning = GameConfig.Load(f => f == "SwordSkill.csv"
        ? Cell(Cell(Cell(Cell(source[f], "skill_06", "kind", "summon"), "skill_06", "trajectory", ""),
            "skill_06", "projectile_count", "1"), "skill_06", "duration", "7")
        : source[f]);
    // 靶子必须放远：召唤物的锚点在玩家身前 110，贴脸摆的话弹丸生成时就已经贴着目标、同一步内命中并被移除。
    var g = SalvoOn(summoning, "skill_06", 900);    // 召唤物每秒发射一枚弹丸（索敌距离 1100）
    Step(g, 2.2);
    var bolt = g.Effects.FirstOrDefault(e => e.Kind == "projectile");
    Assert(bolt is not null && bolt.Speed == 1500,
        "summon-fired bolt keeps the default speed: " + string.Join(" | ", g.Effects.Select(e => $"{e.Kind}/life={e.Life:0.##}/spd={e.Speed}")));
});
Check("retired pierce / multi paths stay covered by config-driven effects", () => {
    // 两个次级效果已无在役技能使用（原御气飞剑/疾风剑），退休配置日后可能复活，故用改过的配置保住覆盖。
    // skill_02 现在是带天降形态的固定冷却技能，要把它改成"普通的多发弹"必须一并清掉形态与触发概率，
    // 否则它既不会自动释放、也不会从玩家身前飞出，这条用例就变成空断言。
    var multi = GameConfig.Load(f => f == "SwordSkill.csv"
        ? Cell(Cell(Cell(Cell(Cell(source[f],
            "skill_02", "kind", "projectile"),
            "skill_02", "secondary", "multi"),
            "skill_02", "secondary_value", "3"),
            "skill_02", "trajectory", ""),
            "skill_02", "trigger_chance", "0")
        : source[f]);
    var g = new GameSession(multi, seed: 42) { BasicAttackEnabled = false }; g.Step(.05);
    // 靶子放远并定身：贴脸摆的话多发弹丸会在同一个 Step 内命中并被移除，数不到编排。
    foreach (var e in g.Battle.Enemies) { e.Atk = 0; e.Hp = e.MaxHp = 1e8; e.X = g.Battle.PlayerX + 160; e.StunUntil = 1e9; }
    g.State.Skills.Clear(); g.State.Skills["skill_02"] = 1; g.Battle.Cooldowns.Clear(); g.Effects.Clear();
    g.Step(.05);
    Assert(g.Effects.Count(e => e.Kind == "projectile" && !e.Hostile) >= 3, "multi still fires several projectiles");
    // pierce：直接构造一枚穿透弹，验证沿途每个敌人各挨一次；清空技能以保证伤害只可能来自它。
    var p = New(); p.Step(.05); p.State.Skills.Clear(); p.Battle.Cooldowns.Clear();
    var targets = p.Battle.Enemies.ToArray();
    for (int i = 0; i < targets.Length; i++) { targets[i].Atk = 0; targets[i].Hp = targets[i].MaxHp = 1e8; targets[i].X = p.Battle.PlayerX + 200 + i * 100; }
    p.Effects.Clear();
    p.Effects.Add(new() { Kind = "projectile", X = p.Battle.PlayerX, Damage = 1000, Life = 4, MaxLife = 4, Secondary = "pierce" });
    Step(p, .5);
    Assert(targets.All(e => e.Hp < e.MaxHp), "pierce hits every enemy along the line");
});
Check("retired shield / regen effects stay covered", () => {
    // 护心剑罡 / 归元护法 已搬进退役配置，shield / regen 当前无技能使用，故用改过的配置保住消费路径的覆盖。
    GameConfig BuffCfg(string secondary, string value) => GameConfig.Load(f => f == "SwordSkill.csv"
        ? Cell(Cell(Cell(Cell(source[f], "skill_04", "secondary", secondary), "skill_04", "secondary_value", value), "skill_04", "secondary_duration", "5"), "skill_04", "power", "1.3")
        : source[f]);
    // 护盾：厚盾扛住近战攻击，掉的是盾不是血。靶子解除定身并调成低伤害，便于观察。
    var guarded = SalvoOn(BuffCfg("shield", "100"), "skill_04");
    Assert(guarded.ShieldRemaining > 0, "the shield branch set a shield");
    foreach (var e in guarded.Battle.Enemies) { e.StunUntil = 0; e.Atk = 5; e.AttackTimer = .1; }
    double hp = guarded.Battle.PlayerHp;
    Step(guarded, 3);
    Assert(guarded.Battle.PlayerHp == hp, $"the shield absorbed the damage instead of hp: {guarded.Battle.PlayerHp:0}/{hp:0}");
    // 回血：气血被压低后靠增益回升（靶子伤害为 0，排除干扰）。
    var healing = SalvoOn(BuffCfg("regen", "0.05"), "skill_04");
    Assert(healing.RegenRemaining > 0, "the regen branch set a regen");
    healing.Battle.PlayerHp = healing.MaxHp * .5;
    double low = healing.Battle.PlayerHp;
    Step(healing, 1);
    Assert(healing.Battle.PlayerHp > low, $"regen restored hp: {low:0} -> {healing.Battle.PlayerHp:0}");
});
Check("GM grant covers every configured currency without touching the first-kill ledger", () => {
    var g = New();
    var ids = config.Rows("item").Where(r => r.Text("kind") == "currency").Select(r => r.Text("id")).ToList();
    Assert(ids.Count >= 2, "currency count");
    foreach (var id in ids) g.State.Wallet[id] = 0;
    Assert(g.GrantAllCurrencies(), "gm grant");
    Assert(ids.All(id => g.State.Amount(id) == 10000), "every currency");
    // 妖核来自 GM 而非首杀，故首杀账本必须保持为空，定量投放口径不被污染。
    Assert(g.State.FirstKills.Count == 0, "ledger untouched");
    g.GrantAllCurrencies(5); Assert(g.State.Amount("gold") == 10005, "accumulate");
    Assert(g.State.DebugGranted["core"] == 10005, "debug grant recorded");
    SaveStore.Validate(g.State, config); // 调试发放被校验承认，不再报「妖核数量与首杀账本不符」
    g.State.DebugGranted["core"] = 0; Reject(() => SaveStore.Validate(g.State, config)); // 抹掉记录后仍判为不一致
});
Check("legacy save with untracked GM cores is adopted instead of rejected", () => {
    var dir = Path.GetFullPath("artifacts/checks/" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(dir, "save.json"); var store = new SaveStore(path); var g = New();
    g.State.Wallet["core"] = 500; store.Save(g.State); // 旧版存档：有妖核余额、无调试记录、无首杀
    var state = store.Load(config)!;
    Assert(state.Amount("core") == 500 && state.DebugGranted["core"] == 500, "adopted as debug grant");
    store.Save(state); Assert(store.Load(config)!.Amount("core") == 500 && store.Warning is null, "stable after adoption");
});
Check("effects carry their source skill so the view can tell the 15 skills apart", () => {
    // 唯一的真诀（御雷真诀）只由普攻概率触发：用触发概率为 1 的改过配置跑，让"非增益剑诀一律留痕"这条断言是确定的，
    // 不必依赖 10% 在有限步数里摇中。其余剑诀都是固定冷却，冷却一到自然出手。
    var certain = GameConfig.Load(f => f == "SwordSkill.csv"
        ? Cell(source[f], "skill_07", "trigger_chance", "1")
        : source[f]);
    var g = new GameSession(certain, seed: 42) { BasicAttackEnabled = true };
    foreach (var realm in config.Rows("SwordLevel")) g.State.Realms.Add(realm.Text("id"));
    foreach (var id in certain.Skills.Keys) g.State.Skills[id] = 1;
    g.Step(.05); foreach (var e in g.Battle.Enemies) { e.Hp = e.MaxHp = 1e8; e.Atk = 0; }
    var fired = new HashSet<string>();
    for (int i = 0; i < 400; i++) { g.Step(.05); foreach (var effect in g.Effects) if (effect.Skill != "") fired.Add(effect.Skill); }
    // buff 类（仙风云体术 / 醉仙望月步 / 万剑归心 / 剑二十三）不产生飞行/地面效果，普攻的 Skill 为空也在这里被排除；
    // 其余每一个都该带着来源标记出手。条数从配置推出来，别再写死——编制每调一次就会错一次。
    Assert(fired.Count >= certain.Skills.Values.Count(s => s.Kind != "buff"), $"distinct skills fired: {string.Join(",", fired.Order())}");
    Assert(fired.All(certain.Skills.ContainsKey), "skill ids resolve to SwordSkill rows");
    Assert(g.Effects.All(e => e.MaxLife > 0), "max life present for fading");
});
Check("three pet slots and unique buff categories", () => {
    var g = New(); g.State.Wallet["gold"] = 10000;
    foreach (var r in config.Rows("Pet")) { g.State.Pets.Add(r.Text("id")); g.TogglePet(r.Text("id")); }
    foreach (var r in config.Rows("PetEquip")) Assert(g.EquipPetBuff("pet_azure", r.Text("id")), "equip");
    Assert(g.State.EquippedPets.Count == 3 && !g.EquipPetBuff("pet_azure", "pet_edge"), "slots");
});
Check("atomic save, reload without offline gains, and corrupt-primary backup recovery", () => {
    var dir = Path.GetFullPath("artifacts/checks/" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(dir, "save.json"); var store = new SaveStore(path); var g = New();
    ToBoss(g); g.HurtEnemy(Boss(g), 1e9); g.ClickOre("ore_0"); store.Save(g.State); store.Save(g.State);
    var state = store.Load(config)!; Assert(state.Amount("core") == 1 && state.PendingIntent["intent_0"] == 1 && state.Battle.BossDefeated, "reload");
    var loaded = new GameSession(config, state); loaded.SelectLevel("level_001"); ToBoss(loaded); loaded.HurtEnemy(Boss(loaded), 1e9); Assert(loaded.State.Amount("core") == 1, "no duplicate reload");
    File.WriteAllText(path, "{broken"); Assert(store.Load(config)!.Amount("core") == 1 && store.Warning is not null, "backup recovery");
});
Check("base character can reach boss and obtain first core in a sustained run", () => {
    // 开局不再白送剑诀，所以第一件事是习得御剑术（它属默认解锁的炼气档）。
    // 这一趟验证的是完整的推进循环，而不是"什么都不买"的裸角色：BOSS 格的小怪在 BOSS 死前不会停刷，
    // DPS 不涨就会越堆越多、陷入复活循环——边打边把灵钱投回武器与剑诀等级，才是设计上的正常打法。
    // 预算 90000 步（4500 秒）：标准怪抬到 88、BOSS 抬到 75 SU 之后，这一趟比调数值前长得多。
    var g = New(basic: true);
    g.UpgradeSkill("skill_01");
    int steps = 0;
    for (; steps < 90000 && g.State.FirstKills.Count == 0; steps++)
    {
        if (g.State.Weapon == "") g.Craft("sword_wood");
        else if (g.SkillCost("skill_01") <= g.State.Amount("gold")) g.UpgradeSkill("skill_01");
        g.Step(.05);
    }
    Assert(g.State.FirstKills.Count > 0,
        $"stalled at {g.Battle.Cell + 1} hp={g.Battle.PlayerHp} after {steps * .05:F0}s");
});
Console.WriteLine($"ALL {passed} CHECKS PASSED");

// CSV roundtrip export is explicit and writes to a separate output directory.
int export = Array.IndexOf(args, "--export");
if (export >= 0 && export + 1 < args.Length)
{
    string output = Path.GetFullPath(args[export + 1]); Directory.CreateDirectory(output);
    foreach (string file in GameConfig.Files)
    {
        var header = source[file].TrimStart('\uFEFF').Split('\n')[0].TrimEnd('\r').Split(',');
        var records = new List<IEnumerable<string>> { header }; records.AddRange(config.Tables[file].Select(r => header.Select(r.Text)));
        File.WriteAllText(Path.Combine(output, file), CsvTable.Write(records), new System.Text.UTF8Encoding(false));
    }
    GameConfig.Load(file => File.ReadAllText(Path.Combine(output, file)));
    Console.WriteLine("CSV EXPORT VERIFIED " + output);
}
