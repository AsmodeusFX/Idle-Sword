using System.Globalization;
using IdleSword.Core;

// 关卡曲线生成器：按「数值主轴」的口径重算 level.csv 的倍率列。
//
// 为什么要有这个工具：曲线不是手调出来的，而是从主轴公式推出来的。以后每接入一套外挂养成
// 系统，都要按新的占比重新分摊总步长——那时改下面的参数重跑即可，不用手改 100 行。
// 手改会被 --check 抓出来。
//
// **养成口径（用户拍板）**：只看「修为」与「法术」两个系统；
// **武器 / 参悟 / 剑灵一律视为不存在**——所以本工具不读 `Equip.csv` / `SwordUpgrade.csv` / `Pet*.csv`。
//
// 口径与推导见 docs/design/balance_ttk.md；数值存档见 docs/design/skill_values.md。

const double BaseAttack = 10;      // fightattr.atk：主轴的起点，不含武器与剑意
const double BasicPower = 1;       // fightattr.basic_power
const double StandardHp = 30;      // 标准小怪（青苔妖）的基础 HP，SU 的载体
const double StandardHits = 3;     // 裸开局"N 下普攻打死后标准怪"的锚点
// **前几关是手抠的教学段**，不参与生成：它们的 `cells` 与 BOSS 血量都是按"点几下修为就能过"定的，
// 让生成器再算一遍只会把它冲掉。跳过之后 `--check` 也跳过同样的行，两边口径一致。
//
// 为什么要三段：**「BOSS = 75 只标准怪」这个比例是旧刻度下的设计**（那时玩家有整套法术链），
// 而前期玩家只会点击。所以前三关的 BOSS 按 5 / 10 / 20 SU 手抠成一条缓坡
// （`boss.hp × boss_hp` = 150 / 300 / 600），正好对上"拿到自动攻击 → 剑气 → 法术"那三个节奏。
const int HandTunedLevels = 3;
const double EliteSu = 10;         // 精英 = 10 SU（monster.csv 的 elite.hp 必须是 EliteSu × StandardHp）
const double BossSu = 75;          // BOSS 的**稳态** = 75 SU（boss.hp 同理）
// **BOSS 的强度是一条缓坡，不是一上来就 75 SU。**
// 教学段（前 3 关）的 BOSS 是手抠的，约 10 秒打完；而 75 SU 是"75 只标准怪"的量，
// 按期望 DPS 折算是 **~100 秒**——第 4 关一进曲线就是 2.3 秒 → 104 秒的十倍台阶。
// 所以从第 4 关起把 SU 从 12 爬到第 20 关的 75，之后恒定。
// （与「前 2 关 5 格、后面逐段拉长」是同一个思路：先让玩家在短场景里学会，再给全尺寸。）
const int BossRampStart = 4, BossRampEnd = 20;
const double BossSuStart = 12;
double BossSuAt(int order) => order <= BossRampStart ? BossSuStart
    : order >= BossRampEnd ? BossSu
    : BossSuStart * Math.Pow(BossSu / BossSuStart, (order - BossRampStart) / (double)(BossRampEnd - BossRampStart));
const double RiftRatio = 0.9;      // 裂隙 = 0.9 × 标准怪（它是一道门，不是一场战斗）
const double EliteAtkRatio = 0.95; // 精英攻击仍比同关普通怪略低，沿用原有关系
const double BladesStart = 1, BladesEnd = 5;      // 御剑术剑支数的成长轴（参悟，尚未接线）
const double RankStart = 1, RankEnd = 32;         // 期望技能等级（该关应有的练度）
const double NStart = 3, NEnd = 15;               // 波次怪物数
const double TEnd = 14;                           // 第 100 关的清波目标秒数（起点由第 1 关反推，见下）
const double AtkEnd = 2.5;                        // normal_atk 第 100 关的硬上界（见 balance_ttk.md §4.5）
const double CritDamage = 1.5;                    // fightattr.crit_damage，用于估暴击增益的收益
const double BaseCrit = 0.05;                     // fightattr.crit：**基础暴击**也要进模型（以前漏了，少算 ~2.5%）
const double BuffFloorRatio = 1.25;               // game_settings.buff_cooldown_floor_ratio
const double BuffCdPerLevel = 0.02;               // game_settings.buff_cooldown_per_level

string dir = args.LastOrDefault(a => !a.StartsWith("--"))
    ?? Path.GetFullPath("idle-sword/Config/Tables");
bool check = args.Contains("--check");
string path = Path.Combine(dir, "level.csv");

List<CsvRow> Load(string file) => CsvTable.Parse(file, File.ReadAllText(Path.Combine(dir, file)));

var skills = Load("SwordSkill.csv");
var monsters = Load("monster.csv").ToDictionary(r => r.Text("id"));
var levelRows = Load("level.csv");
var levelByOrder = levelRows.ToDictionary(r => r.Int("order"));
var talent = Load("Talent.csv").ToDictionary(r => r.Text("id"));
var layout = Load("TalentLayout.csv").ToDictionary(r => r.Text("id"));
var skillLevelBonus = Load("game_settings.csv").First(r => r.Text("id") == "skill_level_bonus").Number("value");
// 境界解锁价：realm_0 默认开，其余按 `SwordLevel.cost_gold` 花灵石解锁。
var realmCost = Load("SwordLevel.csv").ToDictionary(r => r.Text("id"), r => r.Number("cost_gold"));

double Lerp(double a, double b, int order) => a + (b - a) * (order - 1) / 99.0;
double WaveSize(int order) => Lerp(NStart, NEnd, order);

// ══════════════════════════════════════════════════════════════════════════
//  修为（天赋树）：**按收入做经济模拟**——每关能赚多少灵石，就沿树买多少
// ══════════════════════════════════════════════════════════════════════════
//
// 以前这里是一条手调的 `TalentFast/TalentFull`（+110% → +185%，注释写"现有 5 个节点"），
// 而真实树有 55 个节点、且效果全改成了**固定值**——模型把满配攻击算成 28.5，真实是 150.5，
// **差 5.3 倍，形状也不对**（真实成长几乎全是那 45 个链节点的 +133 平攻）。
//
// 现在改成模拟：
//   每关收入 ≈ 格数 × 波次只数 × 平均单只掉落 + 那一关 BOSS 的掉落与首杀奖励
//   沿树**贪心买最便宜的**（前置全部点过才能点）
//   灵核门：灵核只由 BOSS 首杀产出 ⇒ 打第 N 关时手上最多 N−1 颗
//
// ⚠️ **"贪心买最便宜的"是一个可调旋钮**（见 balance_ttk.md）。改它必须重跑曲线。
const string Gold = "gold";
var bought = talent.Keys.ToDictionary(id => id, _ => 0);
double goldLeft = 0, coresLeft = 0;
var attackByOrder = new double[levelRows.Count + 2];

double AvgNormalGold = monsters.Values.Where(m => m.Text("kind") == "normal").Average(m => m.Number("gold"));
double BossFirstGold = Load("drop.csv").Where(r => r.Text("group_id") == "boss_first" && r.Text("item_id") == "gold")
    .Sum(r => r.Number("amount"));

// ══════════════════════════════════════════════════════════════════════════
//  法术：境界解锁**跟着修行树与境界价走**，不是"每 20 关一个"
// ══════════════════════════════════════════════════════════════════════════
//
// 旧模型写死 `UnlockEvery = 20`（还差一关：实际落在 21/41/61/81，`level.csv` 在 20→21 有明显跳变）。
// 现在：境界 0 由修行树的「法术」节点（`realm_system`）打开——那一格买到了就开；
// 之后每个境界花 `SwordLevel.cost_gold` 解锁，是**灵石驱动**的，与关卡无关。
// 所以模拟时要一起记账：**先买树、再用剩下的钱开境界**（同一步里树优先——「法术」那格本身就在树上）。
var realmDefs = Load("SwordLevel.csv").OrderBy(r => r.Int("order")).ToArray();
var realmUnlockOrder = new int[realmDefs.Length];
Array.Fill(realmUnlockOrder, int.MaxValue);
bool hasSpellSystem = false;

double TotalAttack()
{
    double percent = 0, flat = 0;
    foreach (var (id, lv) in bought)
    {
        if (lv == 0) continue;
        var r = talent[id];
        double amount = r.Number("effect_per_level") * lv;
        switch (r.Text("effect"))
        {
            case "atk": percent += amount; break;      // 百分比：进乘区
            case "atk_flat": flat += amount; break;    // 平攻：加在乘区之外
        }
    }
    return BaseAttack * (1 + percent) + flat;
}

for (int order = 1; order <= levelRows.Count; order++)
{
    var lv = levelByOrder[order];
    goldLeft += lv.Int("cells") * WaveSize(order) * AvgNormalGold
        + monsters[lv.Text("boss_id")].Number("gold") + BossFirstGold;
    coresLeft = order - 1;   // 打过前 N−1 关的 BOSS，手上就有这么多灵核

    // ① 沿树贪心买最便宜的：前置齐、没满级、且用当前货币买得起。
    for (int guard = 0; guard < 10000; guard++)
    {
        string? pick = null; double best = double.MaxValue;
        foreach (var id in talent.Keys)
        {
            var r = talent[id];
            if (r.Text("effect") == "none" || bought[id] >= r.Int("max_level")) continue;  // `none` 是占位节点，游戏里不可购
            if (!layout[id].TextList("prereq").All(p => bought[p] >= 1)) continue;
            double cost = r.NumberList("cost")[bought[id]];
            if (cost >= best) continue;
            if (r.Text("cost_currency") == Gold ? cost > goldLeft : cost > coresLeft) continue;
            pick = id; best = cost;
        }
        if (pick is null) break;
        if (talent[pick].Text("cost_currency") == Gold) goldLeft -= best; else coresLeft -= best;
        if (talent[pick].Text("effect") == "realm_system") hasSpellSystem = true;
        bought[pick]++;
    }

    // ② 再用剩下的钱开境界（按 order 顺序；前面没开就轮不到后面）。
    for (int i = 0; i < realmDefs.Length; i++)
    {
        if (realmUnlockOrder[i] != int.MaxValue) continue;
        if (!hasSpellSystem) break;
        if (i == 0) { realmUnlockOrder[0] = order; continue; }
        if (realmUnlockOrder[i - 1] == int.MaxValue) break;
        double cost = realmDefs[i].Number("cost_gold");
        if (goldLeft < cost) break;
        goldLeft -= cost;
        realmUnlockOrder[i] = order;
    }

    attackByOrder[order] = TotalAttack();
}

double Attack(int order) => attackByOrder[Math.Clamp(order, 1, levelRows.Count)];

int RealmOf(CsvRow s) => int.Parse(s.Text("realm_id").Replace("realm_", ""));
bool Unlocked(CsvRow s, int order) => order >= realmUnlockOrder[RealmOf(s)];

// 期望出手周期：冷却制 = 冷却；神通 = 冷却 + 期望等待（冷却未就绪时 RollTriggerSkills 直接跳过）。
// 叠加形态（trigger_chance_step > 0）用生存积求期望普攻次数。
double Cycle(CsvRow s)
{
    double cd = s.Number("cooldown"), p = s.Number("trigger_chance"), step = s.Number("trigger_chance_step");
    if (p <= 0) return cd;
    if (step <= 0) return cd + 1 / p;
    double sum = 0, survive = 1;
    for (int k = 0; k < 200 && survive > 1e-12; k++) { sum += survive; survive *= 1 - Math.Min(1, p + step * k); }
    return cd + sum;
}

// 一次施法的命中数——**按参考波次下的期望命中数**（口径见 docs/design/skill_values.md 第三节）。
//
// 2026-10-05 改的：以前只记**单体口径**（`line_pierce` 记 1、`aoe_all` 记弹数），而实战里前者一条线
// 扫掉整波（实测 10~15 只）、后者每个弹丸打全场——**多目标技能白拿 N 倍**，实测里天剑一个人占 39%。
// 现在按形态给期望命中数，`wave` 就是当时的在场只数（曲线自己的 `WaveSize(order)`）。
//
// 两个密度系数——参考波次约 10 只时，实测反推出来的值（见 docs/design/skill_values.md 第三节）：
// PerBlade 是一个「小落点」平均罩住几只；PerTick 是一片「持续力场」每跳罩住几只。
const double PerBlade = 1.2, PerTick = 2;
double Hits(CsvRow s, double wave)
{
    if (s.Flag("aoe_all")) return s.Int("projectile_count") * wave;
    if (s.Text("kind") == "ground") return Math.Round(s.Number("duration") / 0.6) * PerTick;
    if (s.Text("trajectory") == "sky_drop")
    {
        if (s.Text("secondary") == "dot")
            // 天降火海：落地不结算，只有火海在跳 —— 直伤按跳数，灼烧按"着火秒数 × 灼烧系数"折算成等效命中。
            return Math.Round(s.Number("secondary_duration") / 0.6) * PerTick
                + s.Number("secondary_duration") * PerTick * s.Number("secondary_value");
        return s.Int("projectile_count") * PerBlade;
    }
    if (s.Text("trajectory") == "line_pierce") return wave;
    if (s.Text("trajectory") == "arc_homing") return s.Int("projectile_count");
    return 1;
}

double Chain(int order)
{
    double chain = 0;
    foreach (var s in skills)
    {
        if (s.Text("kind") is "buff" or "summon" || !Unlocked(s, order)) continue;
        // 御剑术的成长轴是剑支数（1 → 5），其余法术只吃技能等级。
        double hits = s.Text("id") == "skill_01" ? Lerp(BladesStart, BladesEnd, order) : Hits(s, WaveSize(order));
        chain += s.Number("power") * hits * (1 + skillLevelBonus * (Lerp(RankStart, RankEnd, order) - 1)) / Cycle(s);
    }
    return chain;
}

// 四个增益都是乘区：攻速与暴击作用于全链（普攻也吃），影分身只复制法术、不复制普攻。
CsvRow? Buff(string sec) => skills.FirstOrDefault(s => s.Text("kind") == "buff" && s.Text("secondary") == sec);
double BuffCooldown(CsvRow s, int order) => Math.Max(
    s.Number("duration") * BuffFloorRatio,
    s.Number("cooldown") * (1 - BuffCdPerLevel * (Lerp(RankStart, RankEnd, order) - 1)));
double BuffUptime(CsvRow? s, int order) =>
    s is null || !Unlocked(s, order) ? 0 : Math.Min(1, s.Number("secondary_duration") / BuffCooldown(s, order));
var haste = Buff("haste"); var critBuff = Buff("crit_reduce"); var mirror = Buff("mirror");
double Haste(int order) => 1 + (haste is null ? 0 : haste.Number("secondary_value") * BuffUptime(haste, order));
/// <summary>暴击乘区 = **基础暴击** + 增益给的那一份。基础那 5% 以前漏了，少算约 +2.5% DPS。</summary>
double CritMul(int order) => 1 + BaseCrit * (CritDamage - 1)
    + (critBuff is null ? 0 : critBuff.Number("secondary_value") * (CritDamage - 1) * BuffUptime(critBuff, order));
double Mirror(int order) => mirror is null ? 0 : mirror.Number("secondary_value") * BuffUptime(mirror, order);

double Dps(int order) => Attack(order) * ((1 + Chain(order)) * Haste(order) * CritMul(order) + Chain(order) * Mirror(order));

// SU(1) = 标准小怪的基础 HP = 30（裸开局 30 ÷ 10 = 3 下普攻）；此后 SU 跟着期望总 DPS 走
// （口径见 balance_ttk.md §4.2）。第 1 关的清波秒数不写死，而是从模型反推（N × SU ÷ DPS）——
// 写死一个近似值会让 normal_hp(1) 不是恰好 1.0，曲线在起点就偏。
// 这条锚点是本工具唯一的"外部输入"：改 StandardHp 就必须同步改 monster.csv 的 hp 列。
const int First = 1;
double bareHits = StandardHp / (BaseAttack * BasicPower);
if (bareHits < StandardHits - 0.5 || bareHits > StandardHits + 0.5) throw new InvalidOperationException(
    $"标准怪 {StandardHp} HP ÷ 裸开局普攻 {BaseAttack * BasicPower} = {bareHits:F2} 下，已偏离 {StandardHits} 下的设计锚");
double Su1 = StandardHp;
double T1 = WaveSize(First) * Su1 / Dps(First);
double TargetSeconds(int order) => T1 + (TEnd - T1) * (order - 1) / 99.0;

double Su(int order) => Su1 * Dps(order) / Dps(First) * WaveSize(First) / WaveSize(order) * TargetSeconds(order) / T1;
double NormalHp(int order) => Su(order) / StandardHp;
double NormalAtk(int order) => 1 + (AtkEnd - 1) * (order - 1) / 99.0;

// 精英与 BOSS 的基础 HP 必须与这里的 SU 份额一致，否则关卡倍率再对也白搭——在生成期就拦住。
foreach (var (id, su) in new[] { ("elite", EliteSu), ("boss", BossSu) })
{
    double want = su * StandardHp, have = monsters[id].Number("hp");
    if (Math.Abs(have - want) > 1e-6) throw new InvalidOperationException(
        $"monster.csv 的 {id}.hp = {have}，但 curve 要求 {su} SU = {want}；先改 monster.csv 再生成曲线");
}

string text = File.ReadAllText(path).TrimStart('﻿');
// 表头原样保留：只替换倍率列的单元格，列序与列名都不由本工具决定。
var header = text.Split('\n')[0].TrimEnd('\r').Split(',');
var rows = CsvTable.Parse("level.csv", text);

int mismatches = 0;
var output = new List<IEnumerable<string>> { header };
foreach (var row in rows)
{
    int order = row.Int("order");
    double hp = NormalHp(order), atk = NormalAtk(order);
    // 精英 / BOSS / 裂隙的基础 HP 已经在 monster.csv 里按 SU 份额定好了（300 = 10 SU、2250 = 75 SU），
    // 所以它们的关卡倍率**就是 normal_hp 本身**——再乘一次 SU 份额就成了双重计数。
    // 裂隙保留原有的 0.9 折扣关系（它是一道门，不是一场战斗）。
    var want = new Dictionary<string, string>
    {
        ["normal_hp"] = F(hp),
        ["normal_atk"] = F(atk),
        ["elite_hp"] = F(hp),
        ["elite_atk"] = F(atk * EliteAtkRatio),
        // BOSS 单独走 SU 缓坡（见 `BossSu(order)`），**不再等于 `normal_hp`**——
        // 那一条等价于"BOSS 恒为 75 只标准怪"，在教学段之后是个断崖。
        // 注意 SU 是**该关的**标准怪血量（`StandardHp × normal_hp(order)`），不是基础的 30——
        // 按 30 算会把第 4 关的 BOSS 写成 360 血（1 秒就死）。
        ["boss_hp"] = F(BossSuAt(order) / BossSu * hp),
        ["boss_atk"] = F(atk),
        ["rift_hp"] = F(hp * RiftRatio),
    };
    // 教学段（前 HandTunedLevels 关）**只锁手抠的那两列**：BOSS 血量是照"点几下修为就能过"定的。
    // 其它列（`normal_hp` 等）照常按模型生成——**整行跳过会出断崖**：它们的 `normal_hp` 会冻在
    // 上一版曲线里，而第 4 关一进模型就从 1.58 跳到 16.6（实测到的十倍断崖）。
    if (order <= HandTunedLevels)
    {
        want["boss_hp"] = row.Text("boss_hp");
        want["boss_atk"] = row.Text("boss_atk");
    }
    foreach (var (field, value) in want)
        if (check && row.Text(field) != value && mismatches++ < 10)
            Console.WriteLine($"  关 {order}: 文件 {field}={row.Text(field)} / 算出 {value}");
    output.Add(header.Select(c => want.TryGetValue(c, out var v) ? v : row.Text(c)));
}

static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

// `--early`：**前期体检表**（前 12 关）。前期是手抠的，改任何一个数都该回来看这张表——
// 它把"这一关的 BOSS 要打几秒、小怪要几刀"直接算出来，而不是拿 SU 反推。
if (args.Contains("--early"))
{
    Console.WriteLine("| 关 | 修为攻击 | 技能链 | 总DPS | 境界 | BOSS | BOSS血 | BOSS秒 | 普通怪血 | 普通怪刀 |");
    Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
    for (int o = 1; o <= 12; o++)
    {
        var lv = levelByOrder[o];
        double bossHp = monsters[lv.Text("boss_id")].Number("hp") * lv.Number("boss_hp");
        double mobHp = monsters["slime"].Number("hp") * lv.Number("normal_hp");
        int realm = realmUnlockOrder.Count(x => x <= o) - 1;
        Console.WriteLine($"| {o} | {Attack(o):F0} | {Chain(o):F1} | {Dps(o):F0} | {realm} | {monsters[lv.Text("boss_id")].Text("name")} | "
            + $"{bossHp:F0} | {bossHp / Dps(o):F1} | {mobHp:F0} | {mobHp / Attack(o):F2} |");
    }
    return 0;
}

// `--sample`：把 balance_ttk.md 第 4.4 节那张采样表打出来（每 10 关 + 第 1 关）。
// 那张表的每一格都是本模型推的，改公式就要一起重生成，所以让它一键出，不靠手抄。
if (args.Contains("--sample"))
{
    Console.WriteLine("| 关 | 修为攻击 | 技能链 | 期望总 DPS | SU | 普攻下数 | 清波秒 | 境界 | normal_hp | normal_atk |");
    Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
    foreach (int o in Enumerable.Range(1, 10).Select(i => i == 1 ? 1 : (i - 1) * 10).Concat([100]).Distinct())
    {
        int realm = realmUnlockOrder.Count(x => x <= o) - 1;
        Console.WriteLine($"| {o} | {Attack(o):F0} | {Chain(o):F1} | {Dps(o):F0} | {Su(o):F0} | {Su(o) / Attack(o):F1} | "
            + $"{WaveSize(o) * Su(o) / Dps(o):F2} | {realm} | {NormalHp(o):F2} | {NormalAtk(o):F2} |");
    }
    return 0;
}

if (check)
{
    if (mismatches > 0)
    {
        Console.WriteLine($"LEVEL CURVE OUT OF DATE: {mismatches} 行与主轴公式不一致（共 {rows.Count} 行）");
        Console.WriteLine("跑 `dotnet run --project tools/BalanceCurve -- idle-sword/Config/Tables` 重新生成。");
        return 1;
    }
    Console.WriteLine($"LEVEL CURVE OK: {rows.Count} 行全部与主轴公式一致");
    return 0;
}

File.WriteAllText(path, CsvTable.Write(output), new System.Text.UTF8Encoding(false));
Console.WriteLine($"已重写 {rows.Count} 行：normal_hp 第 1/50/100 关 = "
    + $"{NormalHp(1):F4} / {NormalHp(50):F4} / {NormalHp(100):F4}，"
    + $"normal_atk 第 100 关 = {NormalAtk(100):F4}");
return 0;
