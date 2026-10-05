using System.Globalization;
using IdleSword.Core;

// 关卡曲线生成器：按「数值主轴」的口径重算 level.csv 的 normal_hp / normal_atk 两列。
//
// 为什么要有这个工具：曲线不是手调出来的，而是从主轴公式推出来的。以后每接入一套外挂养成
// 系统（武器 / 天赋扩展 / 剑意），都要按新的占比重新分摊总步长——那时改下面的参数重跑即可，
// 不用再手改 100 行。手改会被 --check 抓出来。
//
// 口径与推导见 docs/design/balance_ttk.md 与 docs/design/skill_realms.md。

const double BaseAttack = 25;      // fightattr.atk：主轴的起点，不含武器与剑意
const double BasicPower = 1;       // fightattr.basic_power
const double StandardHp = 88;      // 标准小怪（青苔妖）的基础 HP，SU 的载体
const double StandardHits = 3.5;   // 裸开局"N 下普攻打死后标准怪"的锚点
const double EliteSu = 10;         // 精英 = 10 SU（monster.csv 的 elite.hp 必须是 EliteSu × StandardHp）
const double BossSu = 75;          // BOSS = 75 SU（boss.hp 同理）
const double RiftRatio = 0.9;      // 裂隙 = 0.9 × 标准怪（它是一道门，不是一场战斗）
const double EliteAtkRatio = 0.95; // 精英攻击仍比同关普通怪略低，沿用原有关系
const double BladesStart = 1, BladesEnd = 5;      // 御剑剑支数的成长轴（参悟，尚未接线）
const double RankStart = 1, RankEnd = 32;         // 期望技能等级（该关应有的练度）
const double NStart = 3, NEnd = 15;               // 波次怪物数
const double TEnd = 14;                           // 第 100 关的清波目标秒数（起点由第 1 关反推，见下）
const double AtkEnd = 2.5;                        // normal_atk 第 100 关的硬上界（见 balance_ttk.md §4.5）
// **每 20 关解锁一个境界**（关 1/20/40/60/80）：模型里不能一开局就有 15 个法术。
// 这是一条新增假设，也是调优旋钮——玩家实际多快能攒够境界解锁灵钱，要等灵钱收入模型出来才能校准。
const int UnlockEvery = 20;
const double CritDamage = 1.5;                    // fightattr.crit_damage，用于估暴击增益的收益

// 天赋攻击加成：现有 5 个节点合计 +185%，按实际购买节奏分两段爬上来。
// 关 1–10 拿到 t_root + t_atk（+110%），关 11–30 补齐 t_end（+185%），之后恒定。
const int TalentFastEnd = 10, TalentFullEnd = 30;
const double TalentFast = 1.10, TalentFull = 1.85;

string dir = args.LastOrDefault(a => !a.StartsWith("--"))
    ?? Path.GetFullPath("idle-sword/Config/Tables");
bool check = args.Contains("--check");
string path = Path.Combine(dir, "level.csv");

double Talent(int order) => order <= TalentFastEnd
    ? TalentFast * (order - 1) / (TalentFastEnd - 1.0)
    : order <= TalentFullEnd
        ? TalentFast + (TalentFull - TalentFast) * (order - TalentFastEnd) / (TalentFullEnd - (double)TalentFastEnd)
        : TalentFull;

double Lerp(double a, double b, int order) => a + (b - a) * (order - 1) / 99.0;

double Attack(int order) => BaseAttack * (1 + Talent(order));
double WaveSize(int order) => Lerp(NStart, NEnd, order);

// ---- 技能链：全套 15 法术求和（口径见 docs/design/skill_values.md）----
// 旧版只建模御剑，于是①漏算了其余 14 个法术、②体现不出"新境界解锁新法术"。
var skills = CsvTable.Parse("SwordSkill.csv", File.ReadAllText(Path.Combine(dir, "SwordSkill.csv")));
int RealmOf(CsvRow s) => int.Parse(s.Text("realm_id").Replace("realm_", ""));
bool Unlocked(CsvRow s, int order) => order >= 1 + RealmOf(s) * UnlockEvery;

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
// 形状 → 期望命中数（这几条要与 docs/design/skill_values.md 第三节那张表逐条对应）：
//   单体 / 定点 / 平射   1
//   弧线 ×k               k            （各自选不重复的目标）
//   小范围天降 ×k          k × 参k      （落点小，通常只罩住一只）
//   各锁一敌 ×k（band>0）  k × 参k
//   天降火海              跳数 × 参k + 灼烧秒数 × 参k × 灼烧系数
//   持续力场（ground）     跳数 × 参k
//   贯穿（line_pierce）    wave         （一条线扫过整个战场，在场的都在内）
//   全体（aoe_all ×k）     k × wave     （每柄都打全场）
// 两个密度系数——参考波次约 10 只时，实测反推出来的值（见 docs/design/skill_values.md 第三节）：
// PerBlade 是一个「小落点」平均罩住几只（万剑实测每个落点 0.88、天陨 1.1~1.7，取 1.2）；
// PerTick 是一片「持续力场」每跳罩住几只（扎根实测每跳 2.3，取 2）。
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
        // 御剑的成长轴是剑支数（1 → 5），其余法术只吃技能等级。
        double hits = s.Text("id") == "skill_01" ? Lerp(BladesStart, BladesEnd, order) : Hits(s, WaveSize(order));
        chain += s.Number("power") * hits * (1 + 0.15 * (Lerp(RankStart, RankEnd, order) - 1)) / Cycle(s);
    }
    return chain;
}

// 四个增益都是乘区：攻速与暴击作用于全链（普攻也吃），影分身只复制法术、不复制普攻。
CsvRow? Buff(string sec) => skills.FirstOrDefault(s => s.Text("kind") == "buff" && s.Text("secondary") == sec);
var settings = CsvTable.Parse("game_settings.csv", File.ReadAllText(Path.Combine(dir, "game_settings.csv")))
    .ToDictionary(r => r.Text("id"));
double Setting(string id) => settings[id].Number("value");
// 增益类的**实际冷却**随技能等级缩短（下限 = 持续 × floor_ratio，覆盖率封顶 80%）。
// 与 GameSession.BuffCooldown 是同一条口径，参数都从 game_settings 读——游戏与工具必须同源，
// 否则升级拉起来的覆盖率不会体现在曲线上。
double BuffCooldown(CsvRow s, int order) => Math.Max(
    s.Number("duration") * Setting("buff_cooldown_floor_ratio"),
    s.Number("cooldown") * (1 - Setting("buff_cooldown_per_level") * (Lerp(RankStart, RankEnd, order) - 1)));
double BuffUptime(CsvRow? s, int order) =>
    s is null || !Unlocked(s, order) ? 0 : Math.Min(1, s.Number("secondary_duration") / BuffCooldown(s, order));
var haste = Buff("haste"); var crit = Buff("crit_reduce"); var mirror = Buff("mirror");
double Haste(int order) => 1 + (haste is null ? 0 : haste.Number("secondary_value") * BuffUptime(haste, order));
double CritMul(int order) => 1 + (crit is null ? 0 : crit.Number("secondary_value") * (CritDamage - 1) * BuffUptime(crit, order));
double Mirror(int order) => mirror is null ? 0 : mirror.Number("secondary_value") * BuffUptime(mirror, order);

double Dps(int order) => Attack(order) * ((1 + Chain(order)) * Haste(order) * CritMul(order) + Chain(order) * Mirror(order));

// SU(1) = 标准小怪的基础 HP = 88（裸开局 88 ÷ 25 = 3.52 下普攻）；此后 SU 跟着期望总 DPS 走
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
var monsters = CsvTable.Parse("monster.csv", File.ReadAllText(Path.Combine(dir, "monster.csv"))).ToDictionary(r => r.Text("id"));
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
    // 精英 / BOSS / 裂隙的基础 HP 已经在 monster.csv 里按 SU 份额定好了（880 = 10 SU、6600 = 75 SU），
    // 所以它们的关卡倍率**就是 normal_hp 本身**——再乘一次 SU 份额就成了双重计数。
    // 裂隙保留原有的 0.9 折扣关系（它是一道门，不是一场战斗）。
    var want = new Dictionary<string, string>
    {
        ["normal_hp"] = F(hp),
        ["normal_atk"] = F(atk),
        ["elite_hp"] = F(hp),
        ["elite_atk"] = F(atk * EliteAtkRatio),
        ["boss_hp"] = F(hp),
        ["boss_atk"] = F(atk),
        ["rift_hp"] = F(hp * RiftRatio),
    };
    foreach (var (field, value) in want)
        if (check && row.Text(field) != value && mismatches++ < 10)
            Console.WriteLine($"  关 {order}: 文件 {field}={row.Text(field)} / 算出 {value}");
    output.Add(header.Select(c => want.TryGetValue(c, out var v) ? v : row.Text(c)));
}

static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

// `--sample`：把 balance_ttk.md 第 4.4 节那张采样表打出来（每 10 关 + 第 1 关）。
// 那张表的每一格都是本模型推的，改公式就要一起重生成，所以让它一键出，不靠手抄。
if (args.Contains("--sample"))
{
    Console.WriteLine("| 关 | 基础攻击 | 技能链 | 期望总 DPS | SU | 普攻下数 | 清波秒 | normal_hp | normal_atk |");
    Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
    foreach (int o in Enumerable.Range(1, 10).Select(i => i == 1 ? 1 : (i - 1) * 10).Concat([100]).Distinct())
        Console.WriteLine($"| {o} | {Attack(o):F0} | {Chain(o):F1} | {Dps(o):F0} | {Su(o):F0} | {Su(o) / Attack(o):F1} | "
            + $"{WaveSize(o) * Su(o) / Dps(o):F2} | {NormalHp(o):F2} | {NormalAtk(o):F2} |");
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
