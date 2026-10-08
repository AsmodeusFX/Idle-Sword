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

    /// <summary>
    /// 该关的「**期望技能等级**」（`RankStart` → `RankEnd` 线性）。
    ///
    /// 这是模型对"打到这一关时，玩家手上的法术大概几级"的假设，与 <see cref="Model.Talents"/> 一样
    /// 属于**练度**的一部分。对外露出是为了让"期望模型 vs 逐拍模拟"能把 `GameSession` 摆到同一个点上——
    /// 两边各写一份插值就等于又开了一条会脱钩的接线（这正是本文件反复在防的那种事）。
    /// </summary>
    public static double SkillLevelAt(int order) => RankStart + (RankEnd - RankStart) * (order - 1) / 99.0;

    /// <summary>该关的「**期望波次只数**」（`NStart` → `NEnd` 线性）。多目标场景摆几只靶子要照它来。</summary>
    public static double WaveSizeAt(int order) => NStart + (NEnd - NStart) * (order - 1) / 99.0;

    /// <summary>
    /// 一次施法的**期望命中数**（口径见 `docs/design/skill_values.md` 第三节）。`wave` = 参考波次下的在场只数。
    ///
    /// 对外露出只有一个理由，和 <see cref="SkillLevelAt"/> 一样：**不许有第二把尺子**。
    /// 「期望模型 vs 逐拍模拟」的对账要把模型的期望命中数与**实测命中/次**并排，
    /// 两边各写一份形态分派就是又开了一条会脱钩的接线——而且这条分叉特别隐蔽：
    /// 它不会报错，只会让对账表看起来"模型就是比实测低一点"。
    ///
    /// ⚠️ **形态与类别必须穷举**：新增一种形态却没进模型，会让它的命中数**静默算成 1**——
    /// 那是系统性低估（`combat.md` §12.2 记过：模型给天剑按单体算，实测一次出手平均贯穿 18.6 只）。
    /// 所以没识别的取值在这里响亮失败，与 `SkillText` 的穷举是同一套纪律。
    /// </summary>
    public static double HitsPerCast(SkillDef s, double wave)
    {
        if (s.Kind is not ("projectile" or "target" or "ground" or "buff" or "summon"))
            throw new InvalidDataException($"技能类别 '{s.Kind}' 没有进期望模型——新增类别时要在这里补一条");
        if (s.Trajectory is not ("" or "bolt" or "line_shot" or "line_pierce" or "sky_drop" or "arc_homing" or "hover_homing"))
            throw new InvalidDataException($"飞行形态 '{s.Trajectory}' 没有进期望模型——新增形态时要在这里补一条");
        var effect = s.Effects.Count > 0 ? s.Effects[0] : null;
        // 周期结算的间隔**读效果行**（`tick_interval`），与 `GameSession.TickEffects` 同源——
        // 从前两处各读 `game_settings.ground_tick_interval`，写死一个数就会变成静默的模型脱钩。
        double tick = effect?.TickInterval ?? 0;
        if (s.AoeAll) return s.ProjectileCount * wave;
        if (s.Kind == "ground") return Math.Round(s.Duration / tick) * PerTick;
        if (s.Trajectory == "sky_drop")
        {
            var buff = effect?.Buff;
            // 天降火海：落地不结算，只有火海在跳 —— 直伤按跳数，灼烧按"着火秒数 × 灼烧系数"折算成等效命中。
            if (buff is { Kind: "dot" })
            {
                // ⚠️ 先问覆盖登记表：`dot` 若被登记成「不计」，这里就成了"登记说不管、代码却在算"。
                // 那种分叉必须当场炸——它正是"模型静默漏算/多算一个乘区"的入口。
                if (!CountedKind("dot")) throw new InvalidOperationException(
                    $"覆盖登记表把 'dot' 记成「{Fate(KindFate, "Buff kind", "dot")}」，但命中数在按它折算火海——两者必须一致");
                return Math.Round(buff.Duration / tick) * PerTick + buff.Duration * PerTick * buff.Value;
            }
            return s.ProjectileCount * PerBlade;
        }
        if (s.Trajectory == "line_pierce") return wave;
        if (s.Trajectory == "arc_homing") return s.ProjectileCount;
        return 1;
    }

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
        /// 每一关的**期望总 DPS**（伤害 / 秒，已含通用增伤 × 暴击期望 × 伤害倍率窗 × 三个来源的频率）。
        ///
        /// 它和 <see cref="NormalHp"/> 是同一份推导的两面：`SU(order)` 就是拿它比出来的
        /// （`su1 × Dps(order) ÷ Dps(1) × …`），所以这一列**不是新算法**，只是把中间量露出来。
        ///
        /// 露出来的用途只有一个：让"期望模型 vs 逐拍模拟"能摆在**同一个练度点**上对账
        /// （见 <see cref="Talents"/>）。没有它，两边只能比一个被 `<see cref="NormalHp"/>`
        /// 归一化过的形状，而"模型漏了一个乘区"恰恰表现为**绝对值**偏差。
        /// </summary>
        public required IReadOnlyList<double> Dps { get; init; }

        /// <summary>
        /// 每一关结束时，模型认为玩家**已经买到手**的修行节点等级（`id → 等级`）。
        ///
        /// 这是那条经济模拟（每关收入 → 贪心买最便宜的 → 灵核门封顶）的直接产物，
        /// 从前只喂给内部的 `TotalAttack()`。对账要用它把 `GameSession` 摆到同一个练度点上——
        /// 否则"模型说该有多少 DPS"和"模拟跑出来多少"根本不是在说同一个人。
        /// </summary>
        public required IReadOnlyList<IReadOnlyDictionary<string, int>> Talents { get; init; }

        /// <summary>
        /// 每个境界（索引 = `realm_0`…`realm_4` 的尾号）被打开的关数；`int.MaxValue` = 到最后一关都没开。
        /// 法术的可用性由它决定（`Unlocked`），所以对账时也要照它摆。
        /// </summary>
        public required IReadOnlyList<int> RealmUnlock { get; init; }

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

    // ══════════════════════════════════════════════════════════════════════════════
    //  覆盖登记表：模型对每一种取值**算不算**，明写在这里
    // ══════════════════════════════════════════════════════════════════════════════
    //  为什么要有它：模型只按名字去找它要算的那几种增益，所以**新增一种影响 DPS 的 kind 会被静默忽略**——
    //  那是系统性低估，而 `--check` 对"模型变没变"完全不敏感（`combat.md` §12.2 明确它改造前后都是 394）。
    //  有了这张表，"没算"就成了一条**显式决定**而不是遗漏：词表里多一个取值却没人登记，`Compute` 当场抛；
    //  反过来，登记为「不计」的取值被代码算到了，也会抛（见 `CountedBuff`）。**两边都要对得上**。
    //
    //  取值必须以 `算` 或 `不计` 开头——那是判据，后面的话是给人读的理由。
    private static readonly Dictionary<string, string> KindFate = new()
    {
        ["dot"] = "算：天降火海的等效命中（Hits 里按 buff.kind 折算跳数）",
        ["slow"] = "不计：目标侧控场，不改玩家的 DPS",
        ["chill"] = "不计：同上（它的移速与 slow 同源）",
        ["stun"] = "不计：目标侧控场",
        ["vulnerable"] = "不计：目标条件——要不要进模型取决于「目标处在什么状态」的建模口径（combat.md §12.4 第 3 条）",
        ["haste"] = "算：普攻与法术两条频率轴（SpeedBonus）",
        ["crit_reduce"] = "算：暴击期望（CritRate）",
        ["shield"] = "算：环绕飞剑（GuardBlade 按 shield 找那一式）",
        ["regen"] = "不计：续航，不改输出",
        ["lifesteal"] = "不计：续航，不改输出",
        ["mirror"] = "算：法术链的复制系数（Mirror）",
    };
    private static readonly Dictionary<string, string> EffectTypeFate = new()
    {
        ["attack"] = "算：技能链（Chain）",
        ["auto_attack"] = "算：环绕飞剑（GuardBlade 读时间轴上那条效果）",
        ["shorten_cooldown"] = "不计：只改频率，而且它的下限已经并进 skill_cdr 那条护栏",
        ["mirror_cast"] = "不计：它的量记在 `mirror` 那个 kind 上（一份量只算一次）",
    };

    private static string Fate(Dictionary<string, string> table, string what, string key) =>
        table.TryGetValue(key, out string? fate) ? fate
        : throw new InvalidOperationException(
            $"模型没有登记 {what} '{key}' 的归宿——新增取值时要在 `LevelCurve` 的覆盖登记表里说清它**算不算**"
            + "（漏一个就是静默低估，而 --check 对此不敏感）");

    /// <summary>词表里的每一种取值都必须在覆盖登记表里有归宿，且登记表里不许留词表已经没有的取值。</summary>
    public static void CheckCoverage(IReadOnlyList<string> buffKinds, IReadOnlyList<string> effectTypes)
    {
        foreach (string kind in buffKinds) Fate(KindFate, "Buff kind", kind);
        foreach (string type in effectTypes) Fate(EffectTypeFate, "effect_type", type);
        var stale = KindFate.Keys.Where(k => !buffKinds.Contains(k))
            .Concat(EffectTypeFate.Keys.Where(k => !effectTypes.Contains(k))).ToList();
        if (stale.Count > 0)
            throw new InvalidOperationException($"覆盖登记表里有词表已经不存在的取值：{string.Join(", ", stale)}（表会烂，跟 headers.json 那一课一样）");
    }
    /// <summary>用配置当前的词表跑一遍覆盖检查（`Compute` 开头会调它）。</summary>
    public static void CheckCoverage() => CheckCoverage(SkillTable.BuffKinds, SkillTable.EffectTypes);

    /// <summary>登记表说这一种 kind **模型要算**吗？没登记就抛。</summary>
    private static bool CountedKind(string kind) => Fate(KindFate, "Buff kind", kind).StartsWith("算");

    /// <summary>
    /// 跑一遍模型。<paramref name="source"/> 与 `GameConfig.Load` 同一形状：给文件名、返回表内容。
    /// 配置有问题时**抛异常**（工具要的就是响亮失败）；游戏侧请用 <see cref="TryCompute"/>。
    /// </summary>
    public static Model Compute(Func<string, string> source)
    {
        // 先确认"模型对每种取值算不算"这件事本身是完整的——见上面的覆盖登记表。
        CheckCoverage();
        List<CsvRow> Load(string file) => CsvTable.Parse(file, source(file));

        // 技能三层走**与游戏侧同一个解析器**（`SkillTable.Parse`）：模型与模拟读同一份口径，
        // 这是"漏算一个乘区就是静默低估"唯一的结构性防线。见 `SkillTable.cs` 顶部。
        var skillTables = SkillTable.Parse(Load("SwordSkill.csv"), Load("SkillEffect.csv"), Load("SkillBuff.csv"),
            Load("SkillBuffTimeline.csv"), Load("SkillBuffTrigger.csv"));
        var skills = skillTables.Skills.Values.ToList();
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

        // ⚠️ 期望练度的两条轴**必须与对外露出的那两个方法同源**：它们是要拿去和模拟对账的，
        // 各写一份插值就会在"有人调了 RankEnd"时静默分叉（对账两边摆不到同一个点上，
        // 而偏差看起来像模型不准）。
        double WaveSize(int order) => WaveSizeAt(order);
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
        // 逐关留档：这一关的期望练度（`Model.Talents` 的载体）。期望 DPS 最后统一取一遍。
        var talentByOrder = new Dictionary<string, int>[count + 2];

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
            // 练度也要**逐关快照**（`bought` 是就地累加的，只留一份就等于每关都记最后一关的）。
            // `Dps(order)` 留到循环之后再取——它依赖的那几个局部函数在下面才声明，
            // 而 `Attack(order)` 读的正是上面这一行刚写进去的攻击力，所以两趟结果一致。
            talentByOrder[order] = new Dictionary<string, int>(bought);
        }

        double Attack(int order) => attackByOrder[Math.Clamp(order, 1, count)];
        // 境界号取 `realm_id` 的尾号（`realm_0` … `realm_4`），与 `SwordLevel.csv` 的 order 一一对应。
        int RealmOf(SkillDef s) => int.Parse(s.Realm.Replace("realm_", ""));
        bool Unlocked(SkillDef s, int order) => order >= realmUnlockOrder[RealmOf(s)];

        // 期望出手周期：冷却制 = 冷却；神通 = 冷却 + 期望等待（冷却未就绪时 RollTriggerSkills 直接跳过）。
        // 叠加形态（trigger_chance_step > 0）用生存积求期望普攻次数。
        double Cycle(SkillDef s)
        {
            double cd = s.Cooldown, p = s.TriggerChance, step = s.TriggerChanceStep;
            if (p <= 0) return cd;
            if (step <= 0) return cd + 1 / p;
            double sum = 0, survive = 1;
            for (int k = 0; k < 200 && survive > 1e-12; k++) { sum += survive; survive *= 1 - Math.Min(1, p + step * k); }
            return cd + sum;
        }

        // 命中数走 `LevelCurve.HitsPerCast`（公开静态）——这里是本地函数的话，探针那边就得再抄一份形态分派。
        // 三个增益都是乘区：加速作用于普攻与法术（两条轴各算各的）、暴击作用于全链，影分身只复制法术、不复制普攻。
        // **按自身增益的 kind 找**（不再按 `secondary` 字符串猜）：一个 kind 只属于一个增益类法术，
        // 找不到就是"这个乘区没有载体"，返回 null 让各调用方回落到中性值。
        // ⚠️ 只许取**登记表说"要算"**的那些 kind——登记为"不计"的走到这里就是接线错了。
        SkillDef? CountedBuff(string kind)
        {
            if (!CountedKind(kind)) throw new InvalidOperationException($"'{kind}' 在覆盖登记表里是「不计入模型」，但代码在算它——两者必须一致");
            return skills.FirstOrDefault(s => s.Kind == "buff" && s.Buffs.Count > 0 && s.Buffs[0].Kind == kind);
        }
        double BuffCooldown(SkillDef s, int order) => Math.Max(
            s.Duration * settings["buff_cooldown_floor_ratio"],
            s.Cooldown * (1 - settings["buff_cooldown_per_level"] * (SkillLevelAt(order) - 1)));
        double BuffUptime(SkillDef? s, int order) =>
            s is null || !Unlocked(s, order) ? 0 : Math.Min(1, s.Buffs[0].Duration / BuffCooldown(s, order));
        var haste = CountedBuff("haste"); var critBuff = CountedBuff("crit_reduce"); var mirror = CountedBuff("mirror");

        // ── 频率：**普攻与法术是两条独立的轴**（见 docs/design/combat.md）──
        // 普攻吃 `attack_speed`、法术吃 `skill_cdr`，两者都不能进伤害乘区。
        // 加速类增益（仙风云体术）**两样一起给**——它的效果口径就是"普攻与法术一起加速"，
        // 与 `GameSession.CastBuff` 的 `haste` 分支同源；只给一边这条曲线整体就变形了。
        double SpeedBonus(int order) => haste is null ? 0 : haste.Buffs[0].Value * BuffUptime(haste, order);
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
        double SkillRateAt(SkillDef s, int order) =>
            s.Power * (1 + settings["skill_level_bonus"] * (SkillLevelAt(order) - 1));

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
                if (s.Kind is "buff" or "summon" || !Unlocked(s, order)) continue;
                // ⚠️ 这里从前给御剑术单开了一条成长轴（剑支 1 → 5），而**模拟根本不产生多支**
                // （`projectile_count = 1` 写死、选敌只取一支）⇒ 模型按一条不存在的成长轴多算了最多 5 倍
                // 命中数。删掉它，模型回到"只算真的会发生的事"。
                // 参悟把剑支涨上去是**设计里写过、但没接线**的东西（见 `docs/design/combat.md` §12.4 第 3 条），
                // 接线时要连同这条一起加回来。
                chain += DamageFormula.Dmg1(Attack(order), SkillRateAt(s, order), s.SkillFlat)
                    * HitsPerCast(s, WaveSize(order)) / Cycle(s);
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
                // ⚠️ 判据是 Buff 上的**显式标记** `damage_window`，**不是** `power != 1`。
                // 从前这里写的是 `power == 1`，而模拟侧（`GameSession.CastBuff`）用的是显式标记——
                // 两边**已经分叉**，只是当前 15 式都没声明窗、所有增益的威力又都是 1，结论恰好相同。
                // 那正是本工程反复踩的"错得对称、伪装成正确"（见 combat.md §12）：这一轮收到同一口径。
                if (s.Kind != "buff" || !s.DamageWindow || !Unlocked(s, order)) continue;
                // `CastBuff` 写进窗里的是**算上等级与参悟**的威力（`SkillPower`），不是配置的幂。
                double power = SkillRateAt(s, order);
                // 窗的寿命取增益自己的时长（`CastBuff` 用的是它），与 `secondary_duration` 那类效果量是两件事。
                double uptime = Math.Min(1, s.Duration / BuffCooldown(s, order));
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
            var shield = CountedBuff("shield");
            if (shield is null || !Unlocked(shield, order)) return 0;
            // 威力与间隔**读机制自己的行**：护盾增益的时间轴上挂的那条效果（威力 ÷ 间隔）。
            // 从前这两项散在 `game_settings` 里（`guard_blade_power` / `guard_interval`），
            // 是"机制参数不在机制旁边"的典型——改结构时一起下放到 SkillBuffTimeline.csv。
            var tick = skillTables.Timelines.FirstOrDefault(t => t.BuffId == shield.Buffs[0].Id);
            if (tick is null) return 0;
            double uptime = Math.Min(1, shield.Duration / BuffCooldown(shield, order));
            return DamageFormula.Dmg1(Attack(order), skillTables.Effects[tick.EffectId].Power / tick.Interval, 0) * uptime;
        }

        /// <summary>该关的暴击率合计（基础 + 增益那一份）。暴击的**判定**在 DMG2，这里只解它的期望。</summary>
        double CritRate(int order) => attrs["crit_rate"]
            + (critBuff is null ? 0 : critBuff.Buffs[0].Value * BuffUptime(critBuff, order));
        /// <summary>暴击的**期望**倍率 `1 + C × (M − 1)`——共用 `DamageFormula` 那一份实现，别在这里重写一遍。</summary>
        double CritMul(int order) => DamageFormula.ExpectedCritMultiplier(CritRate(order), attrs["crit_damage"]);
        /// <summary>影分身的**期望继承总量** = 继承比例 × 覆盖率。它复制的是法术，所以只乘法术链。</summary>
        double Mirror(int order) => mirror is null ? 0 : mirror.Buffs[0].Value * BuffUptime(mirror, order);
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
        var dpsList = new List<double>(count);
        var talentList = new List<IReadOnlyDictionary<string, int>>(count);
        for (int order = 1; order <= count; order++)
        {
            hpList.Add(NormalHpOf(order)); atkList.Add(NormalAtkOf(order));
            dpsList.Add(Dps(order)); talentList.Add(talentByOrder[order]);
        }
        return new Model
        {
            NormalHp = hpList, NormalAtk = atkList, Dps = dpsList, Talents = talentList,
            RealmUnlock = realmUnlockOrder.ToArray(),
        };
    }

    /// <summary>跑一遍模型，配置有问题时**返回 null 而不是抛**——游戏侧（关卡编辑器）要能优雅降级。</summary>
    public static Model? TryCompute(Func<string, string> source)
    {
        try { return Compute(source); }
        catch (Exception) { return null; }
    }
}
