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
    // ⚠️ `atk_base` / `basic_power` / `crit_rate` / `crit_damage` / `attack_speed` / `skill_cdr` **不在这里**：
    // 它们**直接从 fightattr.csv 读**。从前是抄一份常量在代码里、注释写上"同 fightattr.xxx"，
    // 而两份之间没有任何东西对账——改了表不改常量，模型与游戏就静默分叉（`--check` 只会说"文件与模型不符"，
    // 不会告诉你是哪一边错了）。现在只有下面这几条**模型自身的设计决策**留作常量。
    public const double StandardHp = 30;      // 标准小怪（青苔妖）的基础 HP，SU 的载体（与 monster.csv 对账，见自检）
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
    private const double RankStart = 1, RankEnd = 32;       // 期望技能等级（该关应有的练度）
    private const double NStart = 3, NEnd = 15;             // 波次怪物数
    private const double TEnd = 14;                         // 第 100 关的清波目标秒数（起点由第 1 关反推）
    private const double AtkEnd = 2.5;                      // normal_atk 第 100 关的硬上界（balance_ttk.md §4.5）
    // 增益类法术的冷却成长：**读 game_settings 而不是抄常量**（`buff_cooldown_floor_ratio` / `_per_level`）。
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
        // 战斗属性**从表里读**，不再抄一份常量在代码里（see the anchor comment above）。缺行会当场抛，
        // 这正是想要的：改名/删行之后模型不该"接着按旧值算下去"。
        var attrs = Load("fightattr.csv").ToDictionary(r => r.Text("id"), r => r.Number("base_value"));
        // 地面持续效果的结算间隔：与 `GameSession.TickEffects` 读的是**同一个配置项**。
        double groundTick = settings["ground_tick_interval"];

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
            return attrs["atk_base"] * (1 + percent) + flat;
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
            // 地面持续效果的跳数 = 时长 ÷ 结算间隔。间隔**读 game_settings**，与 GameSession 同源——
            // 写死 0.6 的旧写法会让"把间隔调成 0.5"变成一次静默的模型脱钩（模型仍按 0.6 算命中数）。
            if (s.Text("kind") == "ground") return Math.Round(s.Number("duration") / groundTick) * PerTick;
            if (s.Text("trajectory") == "sky_drop")
            {
                if (s.Text("secondary") == "dot")
                    // 天降火海：落地不结算，只有火海在跳 —— 直伤按跳数，灼烧按"着火秒数 × 灼烧系数"折算成等效命中。
                    return Math.Round(s.Number("secondary_duration") / groundTick) * PerTick
                        + s.Number("secondary_duration") * PerTick * s.Number("secondary_value");
                return s.Int("projectile_count") * PerBlade;
            }
            if (s.Text("trajectory") == "line_pierce") return wave;
            if (s.Text("trajectory") == "arc_homing") return s.Int("projectile_count");
            return 1;
        }

        // 三个增益都是乘区：加速作用于普攻与法术（两条轴各算各的）、暴击作用于全链，影分身只复制法术、不复制普攻。
        CsvRow? Buff(string sec) => skills.FirstOrDefault(s => s.Text("kind") == "buff" && s.Text("secondary") == sec);
        double BuffCooldown(CsvRow s, int order) => Math.Max(
            s.Number("duration") * settings["buff_cooldown_floor_ratio"],
            s.Number("cooldown") * (1 - settings["buff_cooldown_per_level"] * (Lerp(RankStart, RankEnd, order) - 1)));
        double BuffUptime(CsvRow? s, int order) =>
            s is null || !Unlocked(s, order) ? 0 : Math.Min(1, s.Number("secondary_duration") / BuffCooldown(s, order));
        var haste = Buff("haste"); var critBuff = Buff("crit_reduce"); var mirror = Buff("mirror");

        // ── 频率：**普攻与法术是两条独立的轴**（见 docs/design/combat.md）──
        // 普攻吃 `attack_speed`、法术吃 `skill_cdr`，两者都不能进伤害乘区。
        // 加速类增益（仙风云体术）**两样一起给**——它的效果口径就是"普攻与法术一起加速"，
        // 与 `GameSession.CastBuff` 的 `haste` 分支同源；只给一边这条曲线整体就变形了。
        double SpeedBonus(int order) => haste is null ? 0 : haste.Number("secondary_value") * BuffUptime(haste, order);
        /// <summary>普攻频率乘区 = `1 + attack_speed + 增益那一份`（普攻间隔是 `basic_interval ÷(1+它)`）。</summary>
        double BasicSpeed(int order) => 1 + attrs["attack_speed"] + SpeedBonus(order);
        /// <summary>法术频率乘区 = `1 + skill_cdr + 增益那一份`（冷却是 `cooldown ÷(1+它)`）。</summary>
        double SkillSpeed(int order) => 1 + attrs["skill_cdr"] + SpeedBonus(order);

        /// <summary>
        /// 该关这一式的 **SkillRate**（威力）：`power × (1 + 技能等级加成 [+ 参悟])`。
        ///
        /// 🚨 **参悟（剑意）这一项还没算，而它不是小项**：`SwordUpgrade.csv` 给每个法术配了 4 行
        /// `damage_percent`（`intent_0..3`），每行 20 级 × 8% ⇒ 满配 **+640%**（`SkillRate ×7.4`）。
        /// 模拟侧（`GameSession.SkillPower`）**是算的**，所以模型现在系统性低估。
        /// `balance_ttk.md` §6.2 的三步走也明确要求把剑意算进满配效率，所以这是**已经脱钩的前提**，
        /// 不是"有意简化的近似"。补它要先定一条"期望参悟练度"曲线（与 `RankStart/RankEnd` 同类）——
        /// 那是设计决定，见 `docs/design/combat.md` §12.4 第 2 条。
        /// **提取成一处**是为了让它只有一个该被改的地方，而不是散在两个函数里。
        /// </summary>
        double SkillRateAt(CsvRow s, int order) =>
            s.Number("power") * (1 + settings["skill_level_bonus"] * (Lerp(RankStart, RankEnd, order) - 1));

        /// <summary>普攻**未加速**的每发 DMG1（`AttackPower × basic_power + 0`）。</summary>
        double BasicDmg1(int order) => DamageFormula.Dmg1(Attack(order), attrs["basic_power"], 0);

        /// <summary>
        /// 法术链**未加速**的每秒 DMG1 总量 = `Σ(DMG1(单发) × 期望命中数 ÷ 出手周期)`。
        /// **每一项都经 `DamageFormula.Dmg1`**：DMG1 里将来加的组成部分（`skill_flat` 等）会自动被算进来，
        /// 不会出现"公式里加了一项、模型没跟着算，而 `--check` 照样绿"。
        /// </summary>
        double Chain(int order)
        {
            double chain = 0;
            foreach (var s in skills)
            {
                if (s.Text("kind") is "buff" or "summon" || !Unlocked(s, order)) continue;
                // ⚠️ 这里从前给御剑术单开了一条成长轴（剑支 1 → 5），而**模拟根本不产生多支**
                // （`projectile_count = 1` 写死、选敌只取一支）⇒ 模型按一条不存在的成长轴多算了最多 5 倍
                // 命中数。删掉它，模型回到"只算真的会发生的事"。
                // 参悟把剑支涨上去是**设计里写过、但没接线**的东西（见 `docs/design/combat.md` §12.4 第 3 条），
                // 接线时要连同这条一起加回来。
                chain += DamageFormula.Dmg1(Attack(order), SkillRateAt(s, order), s.Number("skill_flat"))
                    * Hits(s, WaveSize(order)) / Cycle(s);
            }
            return chain;
        }

        /// <summary>
        /// **共享伤害倍率窗**（DMG3 的 Build 乘区）：`power != 1` 的增益类法术在生效期内把**所有**伤害
        /// 乘上它的威力（`GameSession.CastBuff` 写入、`Hit` 读取）。期望值 = `1 + (威力 − 1) × 覆盖率`，
        /// 多个窗**相乘**（Build = `Π`，与 `GameSession._windows` 同一口径）。
        ///
        /// ⚠️ 从前模型**完全没算这一项**：`剑罡护体`（`power` 1.5，且威力随技能等级成长）在模拟里是一个
        /// 大乘区，而在模型里等于不存在——中后期 DPS 被系统性低估。见 `docs/design/combat.md` §12。
        /// </summary>
        double DamageWindow(int order)
        {
            double product = 1;
            foreach (var s in skills)
            {
                if (s.Text("kind") != "buff" || s.Number("power") == 1 || !Unlocked(s, order)) continue;
                // `CastBuff` 写进窗里的是**算上等级与参悟**的威力（`SkillPower`），不是配置的 power。
                double power = SkillRateAt(s, order);
                // 窗的寿命取 `duration`（`CastBuff` 用的是它），与 `secondary_duration` 那类效果量是两件事。
                double uptime = Math.Min(1, s.Number("duration") / BuffCooldown(s, order));
                product *= 1 + (power - 1) * uptime;
            }
            return product;
        }

        /// <summary>
        /// **环绕飞剑**（`secondary = shield` 的剑罡护体）：护盾在时每 `guard_interval` 秒还手一柄，
        /// 威力 = `guard_blade_power`。它是模拟里一个**确定的**伤害源（除了暴击与倍率窗不吃别的条件），
        /// 所以模型要算它。返回"未加速的每秒 DMG1"。
        /// </summary>
        double GuardBlade(int order)
        {
            var shield = skills.FirstOrDefault(s => s.Text("kind") == "buff" && s.Text("secondary") == "shield");
            if (shield is null || !Unlocked(shield, order)) return 0;
            double uptime = Math.Min(1, shield.Number("duration") / BuffCooldown(shield, order));
            return DamageFormula.Dmg1(Attack(order), settings["guard_blade_power"] / settings["guard_interval"], 0) * uptime;
        }

        /// <summary>该关的暴击率合计（基础 + 增益那一份）。暴击的**判定**在 DMG2，这里只解它的期望。</summary>
        double CritRate(int order) => attrs["crit_rate"]
            + (critBuff is null ? 0 : critBuff.Number("secondary_value") * BuffUptime(critBuff, order));
        /// <summary>暴击的**期望**倍率 `1 + C × (M − 1)`——共用 `DamageFormula` 那一份实现，别在这里重写一遍。</summary>
        double CritMul(int order) => DamageFormula.ExpectedCritMultiplier(CritRate(order), attrs["crit_damage"]);
        /// <summary>影分身的**期望继承总量** = 继承比例 × 覆盖率。它复制的是法术，所以只乘法术链。</summary>
        double Mirror(int order) => mirror is null ? 0 : mirror.Number("secondary_value") * BuffUptime(mirror, order);
        /// <summary>DMG3 的通用增伤（加算池）。当前 `generic_damage = 0`，但**结构上必须在**。</summary>
        double GenericMul(int order) => DamageFormula.GenericMultiplier(attrs["generic_damage"]);

        // 期望总 DPS：**全部走同一套模板**（`combat.md` 的 `DMG1 × Generic × Critical × Build × Vulnerability`）：
        //   通用增伤 × 暴击期望 × 共享倍率窗 × [ 三个来源各自的"每发 DMG1 × 频率" ]
        //     · 普攻吃**普攻频率**（attack_speed），法术链吃**法术频率**（skill_cdr）——两条独立轴
        //     · **影分身复制的是法术**，所以它同时吃法术频率与暴击期望。它**没有特殊待遇**：
        //       每一个被复制出去的都是一个独立的 Attack Event（各自摇暴击），期望上就是 × 暴击期望。
        //     · 环绕飞剑不走频率轴（它的节奏是固定的 `guard_interval`），但同样吃暴击与倍率窗
        // 口径与差异见 `docs/design/combat.md` §12。
        double Dps(int order) => GenericMul(order) * DamageWindow(order) * CritMul(order) *
            (BasicDmg1(order) * BasicSpeed(order)
             + Chain(order) * SkillSpeed(order) * (1 + Mirror(order))
             + GuardBlade(order));

        // SU(1) = 标准小怪的基础 HP = 30（裸开局 30 ÷ 10 = 3 下普攻）；此后 SU 跟着期望总 DPS 走。
        // 第 1 关的清波秒数不写死，而是从模型反推（N × SU ÷ DPS）——写死近似值会让 normal_hp(1) 不是恰好 1.0。
        // 这条锚点是本模型唯一的"外部输入"：改 StandardHp 就必须同步改 monster.csv 的 hp 列。
        const int First = 1;
        // 裸开局的每发伤害 = `DMG1 = atk_base × basic_power + 0`（无武器、无天赋、无剑诀）。
        double bareShot = DamageFormula.Dmg1(attrs["atk_base"], attrs["basic_power"], 0);
        double bareHits = StandardHp / bareShot;
        if (bareHits < StandardHits - 0.5 || bareHits > StandardHits + 0.5)
            throw new InvalidOperationException(
                $"标准怪 {StandardHp} HP ÷ 裸开局普攻 {bareShot} = {bareHits:F2} 下，已偏离 {StandardHits} 下的设计锚");
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
