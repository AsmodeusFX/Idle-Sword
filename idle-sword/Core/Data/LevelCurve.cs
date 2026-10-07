namespace IdleSword.Core;

/// <summary>
/// **关卡曲线模型**：由「数值主轴」推出 `level.csv` 七个倍率列。
///
/// 为什么要有它：曲线不是手调出来的，而是从主轴公式推出来的（口径与推导见 `docs/design/balance_ttk.md`）。
/// 这个类**同时给两边用**：
/// - `tools/BalanceCurve`（生成 / 对比 level.csv）；
/// - 游戏侧的**关卡编辑器**（显示"模型建议值 + 偏差"，见 `UI/LevelEditor.cs`）。
///
/// ⚠️ **必须是同一份**。抄第二份的后果是两边静默分叉：编辑器里显示的建议值与工具真算出来的不一样，
/// 而两边都不会报错。工具那边用的是"链接源文件"（与它链 `CsvTable.cs` 同一手法），不是复制。
///
/// ⚠️ 它**不是闭式公式**：`NormalHp(order)` 由 `Su(order)` 推出，而 `Su` 依赖一条**经济模拟**——
/// 每关能赚多少灵石，就沿修行树贪心买多少，收入又反过来读 `level.csv` 的 `cells` 与 `boss_id`。
/// 所以改了关卡长度 / BOSS 种类，模型建议值会跟着变（这是对的，别当成 bug）。
/// </summary>
public static class LevelCurve
{
    // ── 主轴锚点（改任何一个都要同步改配置，见 balance_ttk.md）──
    public const double BaseAttack = 10;      // fightattr.atk：主轴的起点，不含武器与剑意
    public const double BasicPower = 1;       // fightattr.basic_power
    public const double StandardHp = 30;      // 标准小怪（青苔妖）的基础 HP，SU 的载体
    public const double StandardHits = 3;     // 裸开局"N 下普攻打死后标准怪"的锚点

    /// <summary>**前几关是手抠的教学段**，不参与生成：它们的 `cells` 与 BOSS 血量都是按"点几下修为就能过"定的，
    /// 让生成器再算一遍只会把它冲掉。调用方对 `order ≤ 它` 的行要**跳过 `boss_hp` / `boss_atk` 两列**
    /// （其它列照常按模型生成——整行跳过会出断崖：它们的 `normal_hp` 会冻在上一版曲线里）。</summary>
    public const int HandTunedLevels = 3;

    public const double EliteSu = 10;         // 精英 = 10 SU（monster.csv 的 elite.hp 必须是 EliteSu × StandardHp）
    public const double BossSu = 75;          // BOSS 的**稳态** = 75 SU（boss.hp 同理）

    // **BOSS 的强度是一条缓坡，不是一上来就 75 SU**：教学段是手抠的（约 10 秒打完），
    // 而 75 SU 按期望 DPS 折算是 ~100 秒——第 4 关一进曲线就是十倍台阶。
    // 所以从第 4 关起把 SU 从 12 爬到第 20 关的 75，之后恒定。
    public const int BossRampStart = 4, BossRampEnd = 20;
    public const double BossSuStart = 12;

    /// <summary>裂隙 = 0.9 × 标准怪（它是一道门，不是一场战斗）。</summary>
    public const double RiftRatio = 0.9;
    /// <summary>精英攻击仍比同关普通怪略低，沿用原有关系。</summary>
    public const double EliteAtkRatio = 0.95;

    // ── 模型内部的成长轴（不对外，调用方用不到）──
    private const double BladesStart = 1, BladesEnd = 5;    // 御剑术剑支数（参悟，尚未接线）
    private const double RankStart = 1, RankEnd = 32;       // 期望技能等级（该关应有的练度）
    private const double NStart = 3, NEnd = 15;             // 波次怪物数
    private const double TEnd = 14;                         // 第 100 关的清波目标秒数（起点由第 1 关反推）
    private const double AtkEnd = 2.5;                      // normal_atk 第 100 关的硬上界（balance_ttk.md §4.5）
    private const double CritDamage = 1.5;                  // fightattr.crit_damage
    private const double BaseCrit = 0.05;                   // fightattr.crit：**基础暴击**也要进模型
    private const double BuffFloorRatio = 1.25;             // game_settings.buff_cooldown_floor_ratio
    private const double BuffCdPerLevel = 0.02;             // game_settings.buff_cooldown_per_level
    private const double PerBlade = 1.2, PerTick = 2;       // 命中数密度系数（见 skill_values.md 第三节）
    private const string Gold = "gold";

    /// <summary>某关 BOSS 的 SU 份额（缓坡，见 <see cref="BossRampStart"/>）。</summary>
    public static double BossSuAt(int order) => order <= BossRampStart ? BossSuStart
        : order >= BossRampEnd ? BossSu
        : BossSuStart * Math.Pow(BossSu / BossSuStart, (order - BossRampStart) / (double)(BossRampEnd - BossRampStart));

    /// <summary>一条关卡曲线：每个 order 一行建议值。索引就是 `order - 1`。</summary>
    public sealed class Model
    {
        /// <summary>模型算出来的 `normal_hp` / `normal_atk`（按 order 升序）。</summary>
        public required IReadOnlyList<double> NormalHp { get; init; }
        public required IReadOnlyList<double> NormalAtk { get; init; }
        /// <summary>关数（= 读到的 level 行数）。</summary>
        public int Count => NormalHp.Count;

        /// <summary>
        /// 某一关七个倍率列的**模型建议值**。`order ≤ HandTunedLevels` 时 `boss_hp` / `boss_atk`
        /// 仍返回模型值——**要不要用它由调用方决定**（工具那边是"不写、不校验"）。
        /// </summary>
        public IReadOnlyDictionary<string, double> Suggested(int order)
        {
            double hp = NormalHp[order - 1], atk = NormalAtk[order - 1];
            // 精英 / BOSS / 裂隙的基础 HP 已经在 monster.csv 里按 SU 份额定好了（300 = 10 SU、2250 = 75 SU），
            // 所以它们的关卡倍率**就是 normal_hp 本身**——再乘一次 SU 份额就成了双重计数。
            // 注意 `BossSuAt` 里的 SU 是**该关的**标准怪血量（StandardHp × normal_hp），不是基础的 30；
            // 按 30 算会把第 4 关的 BOSS 写成 360 血（1 秒就死）。
            return new Dictionary<string, double>
            {
                ["normal_hp"] = hp,
                ["normal_atk"] = atk,
                ["elite_hp"] = hp,
                ["elite_atk"] = atk * EliteAtkRatio,
                ["boss_hp"] = BossSuAt(order) / BossSu * hp,
                ["boss_atk"] = atk,
                ["rift_hp"] = hp * RiftRatio,
            };
        }
    }

    /// <summary>
    /// 跑一遍模型。<paramref name="source"/> 与 `GameConfig.Load` 同一形状：给文件名、返回表内容。
    /// 配置有问题时**抛异常**（工具要的就是响亮失败）；游戏侧请用 <see cref="TryCompute"/>。
    /// </summary>
    public static Model Compute(Func<string, string> source)
    {
        List<CsvRow> Load(string file) => CsvTable.Parse(file, source(file));

        var skills = Load("SwordSkill.csv");
        var monsters = Load("monster.csv").ToDictionary(r => r.Text("id"));
        var levelRows = Load("level.csv");
        var levelByOrder = levelRows.ToDictionary(r => r.Int("order"));
        var talent = Load("Talent.csv").ToDictionary(r => r.Text("id"));
        var layout = Load("TalentLayout.csv").ToDictionary(r => r.Text("id"));
        var settings = Load("game_settings.csv").ToDictionary(r => r.Text("id"), r => r.Number("value"));
        var realmDefs = Load("SwordLevel.csv").OrderBy(r => r.Int("order")).ToArray();

        double Lerp(double a, double b, int order) => a + (b - a) * (order - 1) / 99.0;
        double WaveSize(int order) => Lerp(NStart, NEnd, order);
        int count = levelRows.Count;

        // ══════════════════════════════════════════════════════════════════════════
        //  修为（天赋树）：**按收入做经济模拟**——每关能赚多少灵石，就沿树买多少
        // ══════════════════════════════════════════════════════════════════════════
        //  以前这里是一条手调的成长曲线，而真实树有 55 个节点、效果全是固定值——
        //  模型把满配攻击算成 28.5、真实是 150.5，差 5.3 倍，形状也不对。
        //  现在改成模拟：每关收入 ≈ 格数 × 波次只数 × 平均单只掉落 + 那一关 BOSS 的掉落与首杀奖励；
        //  沿树**贪心买最便宜的**（任意一条前置点过就能点）；灵核只由 BOSS 首杀产出 ⇒ 打第 N 关时手上最多 N−1 颗。
        //  ⚠️ "贪心买最便宜的"是一个可调旋钮（见 balance_ttk.md）。改它必须重跑曲线。
        var bought = talent.Keys.ToDictionary(id => id, _ => 0);
        double goldLeft = 0, coresLeft = 0;
        var attackByOrder = new double[count + 2];

        double avgNormalGold = monsters.Values.Where(m => m.Text("kind") == "normal").Average(m => m.Number("gold"));
        double bossFirstGold = Load("drop.csv")
            .Where(r => r.Text("group_id") == "boss_first" && r.Text("item_id") == Gold)
            .Sum(r => r.Number("amount"));

        // ══════════════════════════════════════════════════════════════════════════
        //  法术：境界解锁**跟着修行树与境界价走**，不是"每 20 关一个"
        // ══════════════════════════════════════════════════════════════════════════
        //  境界 0 由修行树的「法术」节点（`realm_system`）打开；之后每个境界花 `SwordLevel.cost_gold` 解锁，
        //  是**灵石驱动**的，与关卡无关。所以模拟时要一起记账：**先买树、再用剩下的钱开境界**。
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

        for (int order = 1; order <= count; order++)
        {
            var lv = levelByOrder[order];
            goldLeft += lv.Int("cells") * WaveSize(order) * avgNormalGold
                + monsters[lv.Text("boss_id")].Number("gold") + bossFirstGold;
            coresLeft = order - 1;   // 打过前 N−1 关的 BOSS，手上就有这么多灵核

            // ① 沿树贪心买最便宜的：前置齐、没满级、且用当前货币买得起。
            for (int guard = 0; guard < 10000; guard++)
            {
                string? pick = null; double best = double.MaxValue;
                foreach (var id in talent.Keys)
                {
                    var r = talent[id];
                    if (r.Text("effect") == "none" || bought[id] >= r.Int("max_level")) continue;  // `none` 是占位节点，游戏里不可购
                    // 前置与 `TalentVisible` **同一口径：任意一条点亮即可**。这里是 AND 语义的第三处实现
                    // （另两处在玩法逻辑与测试里）——只改那两处不改这里，模型会按"全部满足"推算玩家能走多深，
                    // 与游戏**静默脱钩**，而 `--check` 照样绿。
                    // 没有前置的（根）当然可买：`Any` 对空集返回 false，漏掉这一条根节点永远买不到，整条曲线会崩。
                    var prereqs = layout[id].TextList("prereq");
                    if (prereqs.Count > 0 && !prereqs.Any(p => bought[p] >= 1)) continue;
                    var costList = r.NumberList("cost");
                    if (bought[id] >= costList.Count) continue;   // 价目没配全：这道具买不了，别越界
                    double cost = costList[bought[id]];
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

        double Attack(int order) => attackByOrder[Math.Clamp(order, 1, count)];
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
        // 只记单体口径会让多目标技能白拿 N 倍（实测里天剑一个人占 39%），所以按形态给期望命中数。
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
                chain += s.Number("power") * hits * (1 + settings["skill_level_bonus"] * (Lerp(RankStart, RankEnd, order) - 1)) / Cycle(s);
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
        // 暴击乘区 = **基础暴击** + 增益给的那一份。基础那 5% 以前漏了，少算约 +2.5% DPS。
        double CritMul(int order) => 1 + BaseCrit * (CritDamage - 1)
            + (critBuff is null ? 0 : critBuff.Number("secondary_value") * (CritDamage - 1) * BuffUptime(critBuff, order));
        double Mirror(int order) => mirror is null ? 0 : mirror.Number("secondary_value") * BuffUptime(mirror, order);

        double Dps(int order) => Attack(order) * ((1 + Chain(order)) * Haste(order) * CritMul(order) + Chain(order) * Mirror(order));

        // SU(1) = 标准小怪的基础 HP = 30（裸开局 30 ÷ 10 = 3 下普攻）；此后 SU 跟着期望总 DPS 走。
        // 第 1 关的清波秒数不写死，而是从模型反推（N × SU ÷ DPS）——写死近似值会让 normal_hp(1) 不是恰好 1.0。
        // 这条锚点是本模型唯一的"外部输入"：改 StandardHp 就必须同步改 monster.csv 的 hp 列。
        const int First = 1;
        double bareHits = StandardHp / (BaseAttack * BasicPower);
        if (bareHits < StandardHits - 0.5 || bareHits > StandardHits + 0.5)
            throw new InvalidOperationException(
                $"标准怪 {StandardHp} HP ÷ 裸开局普攻 {BaseAttack * BasicPower} = {bareHits:F2} 下，已偏离 {StandardHits} 下的设计锚");
        double su1 = StandardHp;
        double t1 = WaveSize(First) * su1 / Dps(First);
        double TargetSeconds(int order) => t1 + (TEnd - t1) * (order - 1) / 99.0;

        double Su(int order) => su1 * Dps(order) / Dps(First) * WaveSize(First) / WaveSize(order) * TargetSeconds(order) / t1;
        double NormalHpOf(int order) => Su(order) / StandardHp;
        double NormalAtkOf(int order) => 1 + (AtkEnd - 1) * (order - 1) / 99.0;

        // 精英与 BOSS 的基础 HP 必须与这里的 SU 份额一致，否则关卡倍率再对也白搭——在这里就拦住。
        foreach (var (id, su) in new[] { ("elite", EliteSu), ("boss", BossSu) })
        {
            double want = su * StandardHp, have = monsters[id].Number("hp");
            if (Math.Abs(have - want) > 1e-6) throw new InvalidOperationException(
                $"monster.csv 的 {id}.hp = {have}，但 curve 要求 {su} SU = {want}；先改 monster.csv 再生成曲线");
        }

        // 关卡数不足 100 时（比如有人删到 60 关）曲线照样能算——`Lerp` 的除数仍是 99，
        // 因为它是"第 100 关"的设计锚，不是"最后一关"。这里只把结果截到实际关数。
        var hpList = new List<double>(count);
        var atkList = new List<double>(count);
        for (int order = 1; order <= count; order++) { hpList.Add(NormalHpOf(order)); atkList.Add(NormalAtkOf(order)); }
        return new Model { NormalHp = hpList, NormalAtk = atkList };
    }

    /// <summary>跑一遍模型，配置有问题时**返回 null 而不是抛**——游戏侧（关卡编辑器）要能优雅降级。</summary>
    public static Model? TryCompute(Func<string, string> source)
    {
        try { return Compute(source); }
        catch (Exception) { return null; }
    }
}
