using System.Globalization;
using IdleSword.Core;

// 关卡曲线工具：按「数值主轴」的口径**对比 / 重算** level.csv 的倍率列。
//
// 口径与推导见 docs/design/balance_ttk.md；数值存档见 docs/design/skill_values.md。
//
// ⚠️ **默认不写盘**（2026-10-07 改）。原因：关卡编辑器已经把 level.csv 的倍率列接管成"人可以直接调"的，
// 而本工具原来的默认行为是**重写那七列**——跑一下就把编辑器的改动覆盖了，而且不报错。
// 现在默认只**打印模型建议 + 与文件的偏差**（正是编辑器里那张"建议 vs 实际"表的数据源）；
// 要按模型整条重算，显式传 `--write`。
//
// 模型本身在 `idle-sword/Core/Data/LevelCurve.cs`，与本工具**共用同一份**（见那里的注释：抄第二份会静默分叉）。
//
// 参数：最后一个不以 `--` 开头的参数 = 表目录（默认 `idle-sword/Config/Tables`）。
//   --check   逐行比对文件值与模型值，不一致就非零退出（守卫"手改"；现在改由编辑器改，这条成了"偏差报告"）
//   --write   按模型重算并写回 level.csv（除非你确实想整条重算，否则别用）
//   --early   打印前 12 关的「前期体检表」（BOSS 打几秒、小怪几刀）
//   --sample  打印 balance_ttk.md §4.4 的采样表

string dir = args.LastOrDefault(a => !a.StartsWith("--"))
    ?? Path.GetFullPath("idle-sword/Config/Tables");
bool check = args.Contains("--check"), write = args.Contains("--write");
string path = Path.Combine(dir, "level.csv");

string Source(string file) => File.ReadAllText(Path.Combine(dir, file));
var model = LevelCurve.Compute(Source);   // 配置有问题就响亮抛错（工具要的就是这个）

string text = File.ReadAllText(path).TrimStart('﻿');
// 表头原样保留：只替换倍率列的单元格，列序与列名都不由本工具决定。
var header = text.Split('\n')[0].TrimEnd('\r').Split(',');
var rows = CsvTable.Parse("level.csv", text);
static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

// 教学段（前 HandTunedLevels 关）**只锁手抠的那两列**：BOSS 血量是照"点几下修为就能过"定的。
// 其它列照常按模型算——**整行跳过会出断崖**：它们的 `normal_hp` 会冻在上一版曲线里，
// 而第 4 关一进模型就从 1.58 跳到 16.6（实测到的十倍断崖）。
IEnumerable<(string Field, string Value)> Wanted(CsvRow row)
{
    int order = row.Int("order");
    foreach (var (field, v) in model.Suggested(order))
    {
        if (order <= LevelCurve.HandTunedLevels && field is "boss_hp" or "boss_atk") continue;
        yield return (field, F(v));
    }
}

int mismatches = 0;
var output = new List<IEnumerable<string>> { header };
foreach (var row in rows)
{
    var want = Wanted(row).ToDictionary(x => x.Field, x => x.Value);
    foreach (var (field, value) in want)
        if (row.Text(field) != value && mismatches++ < 10)
            Console.WriteLine($"  关 {row.Int("order")}: 文件 {field}={row.Text(field)} / 模型 {value}");
    output.Add(header.Select(c => want.TryGetValue(c, out var v) ? v : row.Text(c)));
}

int handTuned = rows.Count(r => r.Int("order") <= LevelCurve.HandTunedLevels);
Console.WriteLine($"模型：{rows.Count} 关；normal_hp 第 1/50/100 关 = "
    + $"{model.NormalHp[0]:F4} / {model.NormalHp[Math.Min(49, model.Count - 1)]:F4} / {model.NormalHp[^1]:F4}，"
    + $"normal_atk 第 100 关 = {model.NormalAtk[^1]:F4}"
    + (handTuned > 0 ? $"（前 {handTuned} 关的 boss_hp / boss_atk 是手抠的，不参与对比）" : ""));

// `--early`：**前期体检表**（前 12 关）。前期是手抠的，改任何一个数都该回来看这张表——
// 它把"这一关的 BOSS 要打几秒、小怪要几刀"直接算出来，而不是拿 SU 反推。
if (args.Contains("--early"))
{
    var levels = CsvTable.Parse("level.csv", text).ToDictionary(r => r.Int("order"));
    var monsters = CsvTable.Parse("monster.csv", Source("monster.csv")).ToDictionary(r => r.Text("id"));
    Console.WriteLine("| 关 | BOSS | BOSS血 | 普通怪血 |");
    Console.WriteLine("| --- | --- | --- | --- |");
    for (int o = 1; o <= 12; o++)
    {
        var lv = levels[o];
        double bossHp = monsters[lv.Text("boss_id")].Number("hp") * lv.Number("boss_hp");
        double mobHp = monsters["slime"].Number("hp") * lv.Number("normal_hp");
        Console.WriteLine($"| {o} | {monsters[lv.Text("boss_id")].Text("name")} | {bossHp:F0} | {mobHp:F0} |");
    }
    return 0;
}

// `--sample`：把 balance_ttk.md 第 4.4 节那张采样表打出来（每 10 关 + 第 1 关）。
if (args.Contains("--sample"))
{
    Console.WriteLine("| 关 | normal_hp | normal_atk | elite_hp | boss_hp | rift_hp |");
    Console.WriteLine("| --- | --- | --- | --- | --- | --- |");
    foreach (int o in Enumerable.Range(1, 10).Select(i => i == 1 ? 1 : (i - 1) * 10).Concat([100]).Distinct())
    {
        var s = model.Suggested(o);
        Console.WriteLine($"| {o} | {s["normal_hp"]:F2} | {s["normal_atk"]:F2} | {s["elite_hp"]:F2} | {s["boss_hp"]:F2} | {s["rift_hp"]:F2} |");
    }
    return 0;
}

if (check)
{
    if (mismatches > 0)
    {
        Console.WriteLine($"LEVEL CURVE 有偏差: {mismatches} 行与主轴公式不一致（共 {rows.Count} 行）");
        Console.WriteLine("这是**报告**不是错——倍率列现在归关卡编辑器。要按模型整条重算，传 `--write`。");
        return 1;
    }
    Console.WriteLine($"LEVEL CURVE OK: {rows.Count} 行全部与主轴公式一致");
    return 0;
}

if (!write)
{
    if (mismatches == 0)
    {
        Console.WriteLine("与模型一致，未写盘。（默认不写盘：见文件顶部的说明）");
        return 0;
    }
    Console.WriteLine($"与模型有 {mismatches} 处偏差（上面最多列 10 条）。**未写盘**——"
        + "要按模型整条重算，显式传 `--write`。");
    return 0;
}

File.WriteAllText(path, CsvTable.Write(output), new System.Text.UTF8Encoding(false));
Console.WriteLine($"已重写 {rows.Count} 行的倍率列。");
return 0;
