using IdleSword.Core;

namespace IdleSword.Features;

/// <summary>一个靶位。`X` 是世界落点，`Offset` 是喂给 `GameSession.Spawn` 的那个参数。</summary>
public sealed record TargetPlacement(string MonsterId, string Kind, string Layer, double Offset, double X);

/// <summary>
/// 一个**练度点**：要么是"模型认为打到这一关该有的样子"（<see cref="FromModel"/>），
/// 要么是一份真实存档。两者必须摆在同一批靶子上打，否则比值说明不了任何事。
/// </summary>
public sealed record ProbePoint
{
    public required string Name { get; init; }
    /// <summary>true = 按 <see cref="Talents"/> / <see cref="RealmUnlock"/> / <see cref="SkillLevel"/> 摆；
    /// false = 用 <see cref="State"/>（内核负责深拷贝）。</summary>
    public bool FromModel { get; init; }
    public PlayerState? State { get; init; }
    public IReadOnlyDictionary<string, int> Talents { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<int> RealmUnlock { get; init; } = [];
    public double SkillLevel { get; init; } = 1;
}

/// <summary>
/// 靶子的**供给口径**。三者量的是不同的东西，**量纲不同，不要混着比**——
/// 换口径等于换尺子，界面上必须写清当前用的是哪个。
/// </summary>
public enum FieldSupply
{
    /// <summary>
    /// **饱和**：靶子一死就在原地补一只同类。量的是「这个人每秒能打出多少」，
    /// 与 `LevelCurve.Model.Dps` 同口径（模型也是饱和口径），所以对账用这一档。
    /// </summary>
    Hold,
    /// <summary>
    /// **照引擎节拍**：每 `wave.interval` 秒从真实刷怪点再放一波，与 `GameSession.TickSpawns` 逐条相同。
    /// 量的是「这一关实际供得上多少」——它把**关卡节奏**也算了进去，所以它偏低不代表模型高估。
    /// </summary>
    WaveCadence,
    /// <summary>**只放一波**：量的是「清掉一波要多久」（清场口径）。要用它必须把预热期关掉。</summary>
    Single,
}

/// <summary>
/// 一次测量的全部参数。默认值就是这一轮的标定口径（**改默认值等于换尺子**，请连同
/// `tests/Program.cs` 里的基线带一起改）。
/// </summary>
public sealed record ProbeRequest
{
    public required int Order { get; init; }
    public required ProbePoint Point { get; init; }
    /// <summary>
    /// 预热秒数：**只推进、不记账**，让开局那两段暂态先过去，再开始量。暂态有两段：
    /// ① 靶子还在从刷怪点往里走的**爬坡期**（首只得走完 `40 + i×100` 那段路才进射程）——**这一段的量级大得多**；
    /// ② 起手齐射：沙盒是全新会话、`Battle.Cooldowns` 是空的，十五式在 t≈0 一起白得一次出手。
    ///
    /// ⚠️ 实测（第 30 关 60 秒窗口）不预热 **1456** / 预热后 **1618** SU/s——**预热是往上抬的**。
    /// 当初以为主要是②，会压低数值；探针把这个猜测纠正了：② 只占个位数百分比（十五式冷却多在 1~6 秒），
    /// ①才是主导。所以这一项**不是"越短越真"**，它必须长到爬坡走完。
    /// </summary>
    public double WarmupSeconds { get; init; } = 30;
    /// <summary>记账窗口秒数。</summary>
    public double WindowSeconds { get; init; } = 60;
    /// <summary>固定步长 = 工程的 50ms，**不是** 1/60——探针必须踩在引擎真实的推进节拍上。</summary>
    public double Dt { get; init; } = .05;
    public int Seed { get; init; } = 42;
    /// <summary>把每 `elite_every` 波来的那只精英也摆上。默认不摆：它是关卡节点，不是波次阵容的一部分。</summary>
    public bool IncludeElite { get; init; }
    /// <summary>true = 靶子钉在刷怪点；默认 false = 让它们自己走进射程（见 `Measure` 的 `Place`）。</summary>
    public bool FreezeTargets { get; init; }
    /// <summary>补靶的总次数上限（含第一波）——防死循环的安全帽，正常跑不到。</summary>
    public int MaxWaves { get; init; } = 4096;

    /// <summary>
    /// 靶子供给口径。默认 <see cref="FieldSupply.Hold"/>——**因为要对比的 `LevelCurve.Model.Dps`
    /// 就是饱和口径**，两边必须同口径，否则比值里混着"关卡节奏"这一项，读不出来任何结论。
    /// 三种口径实测差得很远（第 60 关：饱和 1.50 / 照节拍 0.51 / 单波 0.11），选错一个结论就整个反过来。
    /// </summary>
    public FieldSupply Supply { get; init; } = FieldSupply.Hold;
}

/// <summary>一个技能在这一窗口里的出手与命中。`Casts` 由「冷却涨上去」判定（见 `Measure`）。</summary>
public sealed record ProbeSkillRow(string SkillId, int Casts, int Hits, double ModelHitsPerCast)
{
    /// <summary>实测每次出手命中数。没出手过就是 0（不是"0 命中"）。</summary>
    public double HitsPerCast => Casts > 0 ? (double)Hits / Casts : 0;

    /// <summary>
    /// 模型这一列有没有意义。增益 / 召唤类不产生直接命中，`HitsPerCast` 对它们返回 0——
    /// 并排显示会读成"实测远高于模型"，那是**误报**，界面上要显示成「—」。
    /// </summary>
    public bool Comparable => ModelHitsPerCast > 0;
}

/// <summary>一次测量的结果。所有比率都由 <see cref="HpPerSecond"/> 派生，SU 是唯一对外口径。</summary>
public sealed record ProbeResult
{
    public required int Order { get; init; }
    public required string PointName { get; init; }
    /// <summary>这一关**一只普通怪**的真实 SU——TTK 的载体。</summary>
    public required double TargetSu { get; init; }
    /// <summary>主指标：每秒打出几个 SU。</summary>
    public required double SuPerSecond { get; init; }
    /// <summary>= `TargetSu / SuPerSecond`，即"砍死这一关一只普通怪要几秒"。</summary>
    public required double Ttk { get; init; }
    /// <summary>
    /// = `FieldCount × TargetSu / SuPerSecond`，即"按饱和口径**清光一波**要几秒"。
    /// 与 `docs/design/balance_ttk.md` 的清波目标（第 100 关 `TEnd = 14` 秒）**同一量纲**，可以并排读；
    /// <see cref="Ttk"/> 是"一只"，这个才是"一波"。注意它不含供给节奏——照 `WaveCadence` 口径跑出来的
    /// 实际耗时由 `wave.interval` 封顶，不是这个数。
    /// </summary>
    public required double WaveTtk { get; init; }
    /// <summary>内部指标，只用来和 `LevelCurve.Model.Dps` 对账（界面上一律显示 SU/s）。</summary>
    public required double HpPerSecond { get; init; }
    /// <summary>记账窗口实际走了多少模拟秒（= `WindowSeconds`，取自账本自己的计时）。</summary>
    public required double Elapsed { get; init; }
    public required int Kills { get; init; }
    public required double ClearedSu { get; init; }
    /// <summary>一波摆几只（= `PlanWaveField` 的长度）。与模型的 `WaveSizeAt(order)` 并排看。</summary>
    public required int FieldCount { get; init; }
    /// <summary>这一次测量一共放了几波进来（含预热期）。</summary>
    public required int Waves { get; init; }
    public required IReadOnlyList<ProbeSkillRow> Skills { get; init; }
}

/// <summary>
/// 一次对账：模型期望练度点 + 当前存档练度点，两边都折成"相对模型 DPS 的倍率"。
/// 带内 = 实测/期望落在 <see cref="CombatProbe.BandLo"/> ~ <see cref="CombatProbe.BandHi"/>。
/// </summary>
public sealed record ProbeReconciliation
{
    public required int Order { get; init; }
    public required ProbeResult ModelPoint { get; init; }
    public required ProbeResult? SavePoint { get; init; }
    /// <summary>`LevelCurve.Model.Dps[order - 1]`，即"模型说这一关该有多少 DPS"。</summary>
    public required double ModelDps { get; init; }
    /// <summary>order = 1 为 false：只打印不判红（见 <see cref="CombatProbe.Reconcile"/> 的说明）。</summary>
    public required bool Judge { get; init; }
    public string Note { get; init; } = "";

    public double ModelRatio => ModelDps > 0 ? ModelPoint.HpPerSecond / ModelDps : 0;
    public double SaveRatio => SavePoint is null || ModelDps <= 0 ? 0 : SavePoint.HpPerSecond / ModelDps;
    public bool ModelInBand => ModelRatio >= CombatProbe.BandLo && ModelRatio <= CombatProbe.BandHi;
    public bool SaveInBand => SaveRatio >= CombatProbe.BandLo && SaveRatio <= CombatProbe.BandHi;
}

/// <summary>
/// **战斗探针**：把「期望模型」和「逐拍模拟」摆到同一个练度点上打一次，报出 SU / TTK。
///
/// 它存在的理由只有一条：**模型的绝对值必须能被打脸**。
/// `LevelCurve` 算得出第 60 关该有多少 DPS，但那是它自己推自己——唯一能证伪它的东西，
/// 是让 `GameSession` 在**同一个练度点、同一批靶子**上真跑一遍。这个类就是那次对账。
///
/// 三条不许破的纪律：
/// - **不建第二个战斗引擎**。所有战斗规则只有 `GameSession` 一处，这里只摆场、推进、读数。
/// - **不抄第二份建场逻辑**。靶子的数量 / 血量 / 位置全部走引擎自己的纯函数
///   （`GameSession.WaveCountsFor` / `WaveScaleFor` / `Spawn`），自己写一遍就是静默分叉。
/// - **只有一把尺子**。所有指标必须能换算回 SU（`HpPerSecond / LevelCurve.StandardHp`），
///   模型侧的期望命中数也走 `LevelCurve.HitsPerCast`。
///
/// 纯 C#、不依赖 Godot。表读取靠注入的 <c>Func&lt;string,string&gt;</c>（参数是**文件名**）。
/// </summary>
public sealed class CombatProbe
{
    /// <summary>对账容差带：模型点实测/期望落在 <c>[0.75, 1.25]</c> 内算"带内"。</summary>
    public const double BandLo = .75, BandHi = 1.25;

    private readonly GameConfig _config;

    /// <summary>关卡曲线模型；配置读不出来时为 null（编辑器那一节据此显示"模型不可用"，而不是崩掉）。</summary>
    public LevelCurve.Model? Model { get; }

    public CombatProbe(GameConfig config, Func<string, string> source)
    {
        _config = config;
        Model = LevelCurve.TryCompute(source);
    }

    private LevelDef LevelOf(int order) =>
        _config.Levels.FirstOrDefault(l => l.Order == order)
        ?? throw new InvalidDataException($"没有 order = {order} 的关卡——探针只能对已有的关卡开火");

    // ─────────────────────────── 摆靶 ───────────────────────────

    /// <summary>
    /// 某个关卡「一波真实刷怪」的靶位清单。**纯计算**，不建会话——自检可以直接断言摆放，
    /// 不必真跑一场战斗。
    /// </summary>
    public IReadOnlyList<TargetPlacement> PlanWaveField(int order, bool includeElite = false)
    {
        var level = LevelOf(order);
        var wave = _config.Waves[level.Wave];
        var units = _config.WaveUnits[wave.Id];
        // 只数分摊走引擎的纯函数——在这儿重算一遍就是第二把尺子（见 WaveCountsFor 的说明）。
        var counts = GameSession.WaveCountsFor(wave, units, GameSession.WaveScaleFor(_config, order));
        double spawnOffset = _config.Rows("spawn_point")[0].Number("offset");
        var list = new List<TargetPlacement>();
        int i = 0;
        for (int u = 0; u < units.Count; u++)
            for (int n = 0; n < counts[u]; n++) Add(units[u].Monster, 40 + i++ * 100);
        // 精英每 elite_every 波来一只：它是关卡节点，不是波次阵容的一部分，所以默认不摆。
        if (includeElite) Add(wave.Elite, 40 + i * 100);
        return list;

        void Add(string monsterId, double offset)
        {
            var m = _config.Monsters[monsterId];
            list.Add(new(m.Id, m.Kind, m.Layer, offset, spawnOffset + offset));
        }
    }

    // ─────────────────────────── 测量 ───────────────────────────

    /// <summary>跑一次测量。同 <see cref="ProbeRequest"/> 两次得到**逐位相同**的结果（种子固定、步长固定）。</summary>
    public ProbeResult Measure(ProbeRequest r)
    {
        var level = LevelOf(r.Order);
        var wave = _config.Waves[level.Wave];

        // 存档点必须**深拷贝**：沙盒会推进、会结算（靶子掉血、写冷却），共享引用会把真实进度改坏。
        var state = r.Point.FromModel ? new PlayerState() : SaveStore.Clone(r.Point.State ?? new PlayerState());
        var session = new GameSession(_config, state, seed: r.Seed) { SandboxMode = true, BasicAttackEnabled = true };
        // 练度摆成模型假设的那一份（不给武器——模型也算不了它，给了比值就不可归因）。
        if (r.Point.FromModel) GrantModelPoint(session, r.Order, r.Point);
        // 探针量的是**输出上限**：自动出手与远程是"普攻每一发都稳定出去"这条口径的前提。
        // **两个练度点都补上**，否则存档点会因为没点这两项而变成"站着不还手"，两点的差就失去意义了。
        session.State.Talents["t_auto"] = 1;
        session.State.Talents["t_ranged"] = 1;
        // 场地自建：沙盒不推进关卡，所以直接把 Battle 换成一个干净的靶场（与 `SkillPreview.BuildSandbox` 同一手法）。
        session.State.Battle = new BattleState { LevelId = level.Id, PlayerX = GameSession.LevelStartX };
        session.State.Battle.PlayerHp = session.MaxHp;

        var placements = PlanWaveField(r.Order, r.IncludeElite);
        // id → MaxHp：死亡时用它把 id 折成 SU。id 由 `NextEnemyId` 单调递增，跨多波不会撞。
        var maxHp = new Dictionary<long, double>();
        var slotOf = new Dictionary<long, TargetPlacement>();   // 活着的靶子 → 它顶的那个槽位
        var lastX = new Dictionary<long, double>();             // 每只靶子**死前最后**的位置
        int waves = 0;
        double waveClock = 0;

        // 出手次数：**冷却"涨上去"就是放了一次**。
        //
        // ⚠️ 不能照抄 UI/Audio.cs 那条「冷却 0 → 正」，那条从我们这个观测点看是**哑的**：
        //    冷却衰减写的是 `Math.Max(0, cd − dt×factor)`（`Step` 顶部），而 `CastSkills` 在**同一步**的后半段
        //    一起手就把冷却写回正值。每步只在 `Step` 之后观察一次的话，那个 0 永远观察不到——
        //    实测下来只有"想放但没目标、冷却停在 0"的技能偶尔能被记到（第 30 关只认出一式）。
        //    冷却只会**减**（衰减）或**跳增**（出手时写入），所以"比上一步大"就等价于"这一步出手了"。
        // ⚠️ 也**不能比 `Effects` 集合差**：增益类法术走 `CastBuff`，根本不产生 `CombatEffect`，差集法会永久漏掉它们。
        // ⚠️ 基线在摆完靶之后才建（下面 `lastCd` 初值 0）：否则开局那一片写入会被误记成"窗口内出手"。
        var casts = new Dictionary<string, int>();
        var lastCd = new Dictionary<string, double>();
        foreach (string id in _config.Skills.Keys) { casts[id] = 0; lastCd[id] = 0; }

        int kills = 0;
        double clearedSu = 0;
        // 复用逐拍差分容器。旧写法对每个旧 ID 再扫描整个场面，密集波次会退化成平方开销。
        var before = new List<long>();
        var surviving = new HashSet<long>();

        void SpawnAt(TargetPlacement p, double x)
        {
            session.Spawn(p.MonsterId, cell: 0, p.Offset);
            var e = session.Battle.Enemies[^1];
            e.X = x;
            // 靶子不反击。`Atk = 0` 之外再把出手计时器顶满，连一次 `HurtPlayer` 都不走
            //（`HurtPlayer` 会摇一次闪避用的随机数，走了就白白扰动随机流）。
            e.Atk = 0; e.AttackTimer = 999;
            // 默认**不定身**：让它们照自己的速度走进 `m.Range`。真实波次就是这么聚到玩家身边的；
            // 定身会把远处的靶子永远留在射程外（`40 + i×100` 会一直铺到 1440，
            // 而玩家站在 `LevelStartX = 80`，远程射程也就够到一千出头）。
            if (r.FreezeTargets) e.StunUntil = 1e9;
            slotOf[e.Id] = p;
            maxHp[e.Id] = e.MaxHp;
        }

        /// <summary>放一波进来。**只追加不清场**——清场等于让上一波凭空消失，那不是引擎的规则。</summary>
        void PlaceWave()
        {
            // `SandboxMode` 保证的是"世界不动"，**不是**"靶子常驻"——所以靶场要在这里自己摆。
            session.Battle.Spawns.Clear();
            foreach (var p in placements) SpawnAt(p, p.X);
            waves++;
        }

        int Steps(double seconds) => (int)Math.Ceiling(seconds / r.Dt);

        void Run(double seconds, bool tally)
        {
            for (int n = 0; n < Steps(seconds); n++)
            {
                // 照节拍补波：**`GameSession.TickSpawns` 的规则**——每 `wave.interval` 秒放一波，
                // 不看场上还剩几只。
                if (r.Supply == FieldSupply.WaveCadence
                    && (waveClock += r.Dt) >= wave.Interval && waves < r.MaxWaves)
                {
                    waveClock -= wave.Interval;
                    PlaceWave();
                }
                // （`Single` 口径不补：第一波打光就没了，量的是"清一波要多久"。）
                before.Clear();
                foreach (var e in session.Battle.Enemies) { lastX[e.Id] = e.X; before.Add(e.Id); }
                session.Step(r.Dt);
                surviving.Clear();
                foreach (var e in session.Battle.Enemies) surviving.Add(e.Id);
                // 沙盒里唯一的移除路径是 `Step` 末尾那句 `RemoveAll(e => e.Hp <= 0)`，
                // 所以"刚才还在、现在没了"就是这一步死了。不改 `DamageTally`——它记的是有效伤害，不是死亡。
                foreach (long id in before)
                {
                    if (surviving.Contains(id)) continue;
                    if (tally)
                    {
                        kills++;
                        clearedSu += maxHp.GetValueOrDefault(id) / LevelCurve.StandardHp;
                    }
                    double fellAt = lastX.GetValueOrDefault(id, double.NaN);
                    lastX.Remove(id);
                    if (r.Supply != FieldSupply.Hold || !slotOf.Remove(id, out var slot)) continue;
                    // 饱和口径：**原地**补一只同类的满血靶（位置取它倒下的地方，不是刷怪点）——
                    // 挪回刷怪点的话它又得从几百像素外走回来，窗口里会周期性出现"没人可打"的空档，
                    // 量到的就成了"波次供得上供不上"，与 `Model.Dps` 不再是同一个量。
                    if (waves >= r.MaxWaves) continue;
                    waves++;
                    SpawnAt(slot, double.IsNaN(fellAt) ? slot.X : fellAt);
                }
                foreach (string id in _config.Skills.Keys)
                {
                    double now = session.Battle.Cooldowns.GetValueOrDefault(id);
                    if (tally && now > lastCd[id]) casts[id]++;
                    lastCd[id] = now;
                }
            }
        }

        PlaceWave();   // 第一波：`TickSpawns` 也是激活那一刻就放

        // 预热期：丢掉起手齐射。沙盒开局冷却表为空，十五式在 t≈0 一起白得一次出手——
        // 在 60 秒窗口里，30 秒冷却的技能会出手 3 次而不是 2 次（+50%）。不预热，这一份白得的伤害
        // 会全算进"每秒输出"，而模型那边根本没这回事。
        Run(r.WarmupSeconds, tally: false);
        session.ResetDamageStats();
        foreach (string id in _config.Skills.Keys) casts[id] = 0;

        Run(r.WindowSeconds, tally: true);

        var rows = session.DamageStats.Rows;
        double secondsMeasured = Math.Max(1e-9, session.DamageStatsSeconds);
        // **有效 + 溢出 = 这一笔的最终伤害**（见 `GameSession.ApplyDamage`：溢出 = 伤害 − 有效）。
        //
        // ⚠️ 只取「有效」是错的，而且错得很有方向性：靶子用的是**真实血量**，高级关卡一击就把人打没了，
        // 于是几乎每一笔的绝大部分都被记成"溢出"，实测值会被系统性地砍掉一大块——
        // 量到的成了"这一关的怪一共多少血"，而不是"这个人每秒能打多少"。
        // 模型的 `Dps` 是**输出口径**（不含任何目标血量上限），所以这边也必须取输出口径。
        double hpPerSecond = rows.Values.Sum(x => x.Effective + x.Overkill) / secondsMeasured;
        double suPerSecond = hpPerSecond / LevelCurve.StandardHp;
        // 参照靶 = 这一关**普通怪**的真实 SU。TTK 因此就是"砍死这一关一只普通怪要几秒"，
        // 与模型那边 SU(order) 用的是同一个载体，能直接并排读。
        double targetSu = ReferenceTargetSu(level, wave);

        return new ProbeResult
        {
            Order = r.Order,
            PointName = r.Point.Name,
            TargetSu = targetSu,
            SuPerSecond = suPerSecond,
            Ttk = suPerSecond > 0 ? targetSu / suPerSecond : double.PositiveInfinity,
            WaveTtk = suPerSecond > 0 ? placements.Count * targetSu / suPerSecond : double.PositiveInfinity,
            HpPerSecond = hpPerSecond,
            Elapsed = secondsMeasured,
            Kills = kills,
            ClearedSu = clearedSu,
            FieldCount = placements.Count,
            Waves = waves,
            Skills = SkillRows(r.Order, rows, casts),
        };
    }

    /// <summary>
    /// 一次对账：模型期望练度点 + 当前存档练度点，两边都折成"相对模型 DPS 的倍率"。
    /// 模型读不出来时返回 null（调用方显示"模型不可用"，不炸）。
    /// </summary>
    public ProbeReconciliation? Reconcile(int order, PlayerState? saveState)
    {
        if (Model is null || order < 1 || order > Model.Count) return null;
        double dps = Model.Dps[order - 1];

        var modelPoint = Measure(new ProbeRequest
        {
            Order = order,
            Point = new ProbePoint
            {
                Name = "模型期望",
                FromModel = true,
                Talents = Model.Talents[order - 1],
                RealmUnlock = Model.RealmUnlock,
                SkillLevel = LevelCurve.SkillLevelAt(order),
            },
        });
        ProbeResult? savePoint = saveState is null ? null : Measure(new ProbeRequest
        {
            Order = order,
            Point = new ProbePoint { Name = "当前存档", State = saveState },
        });

        // order = 1 只打印不判：模型第 1 关的期望波次是 3 只，而 `level_001` 引用的是一条单只教学波
        //（`count_max = 1`）——那是既有的表债务，探针把它照出来了，但不该把旧账变成红灯。
        bool judge = order > 1;

        return new ProbeReconciliation
        {
            Order = order,
            ModelPoint = modelPoint,
            SavePoint = savePoint,
            ModelDps = dps,
            Judge = judge,
            Note = judge ? "" : "第 1 关只打印不判定：模型期望波次 3 只，实际是 count_max = 1 的单只教学波（既有表债务）。",
        };
    }

    // ─────────────────────────── 内部 ───────────────────────────

    /// <summary>
    /// 把模型对**这一关该有的练度**摆到会话上：境界、天赋、技能等级。
    /// **不给武器**——模型也算不了它，给了比值就不可归因（差异要能指到具体的乘区上）。
    /// </summary>
    private void GrantModelPoint(GameSession session, int order, ProbePoint point)
    {
        session.UnlockAllSystems();
        foreach (var (id, lv) in point.Talents) if (lv > 0) session.State.Talents[id] = lv;
        int rank = Math.Max(1, (int)Math.Round(point.SkillLevel));
        foreach (var s in _config.Skills.Values)
        {
            int realm = RealmIndexOf(s);
            if (realm >= 0 && realm < point.RealmUnlock.Count && point.RealmUnlock[realm] <= order)
                session.State.Skills[s.Id] = rank;
        }
    }

    /// <summary>`skill.realm`（形如 `realm_3`）→ 境界序号；形态不对时返回 -1（当作"没解锁"）。</summary>
    private static int RealmIndexOf(SkillDef s)
    {
        const string prefix = "realm_";
        return s.Realm.StartsWith(prefix) && int.TryParse(s.Realm[prefix.Length..], out int i) ? i : -1;
    }

    /// <summary>这一关"一只普通怪值几个 SU"。找不到普通怪（纯精英阵容）时退回第一只。</summary>
    private double ReferenceTargetSu(LevelDef level, WaveDef wave)
    {
        var units = _config.WaveUnits[wave.Id];
        var pick = units.Select(u => _config.Monsters[u.Monster]).FirstOrDefault(m => m.Kind == "normal")
                   ?? _config.Monsters[units[0].Monster];
        // 与 `GameSession.Spawn` 的普通怪分支逐条同源：`m.hp × level.hp_scale × wave.hp_scale`。
        return pick.Hp * level.HpScale * wave.HpScale / LevelCurve.StandardHp;
    }

    /// <summary>
    /// 「模型期望命中/次」与「实测命中/次」并排。这一列是整张报表的关键：
    /// 比值偏高时看它就知道是哪个形态的密度系数（`PerBlade` / `PerTick`）不准，还是别处漏了乘区。
    /// </summary>
    private IReadOnlyList<ProbeSkillRow> SkillRows(int order, IReadOnlyDictionary<string, DamageTally.Row> rows, Dictionary<string, int> casts)
    {
        // 普攻：模型里恒为单体一发（`TickBasicAttack` 只锁最近的合法目标），所以期望命中/次就是 1。
        var list = new List<ProbeSkillRow>
        {
            new(GameSession.BasicAttackKey,
                casts.GetValueOrDefault(GameSession.BasicAttackKey),
                rows.GetValueOrDefault(GameSession.BasicAttackKey)?.Hits ?? 0,
                1),
        };
        foreach (var s in _config.Skills.Values.OrderBy(s => s.Id))
            list.Add(new(s.Id, casts.GetValueOrDefault(s.Id), rows.GetValueOrDefault(s.Id)?.Hits ?? 0,
                LevelCurve.HitsPerCast(s, LevelCurve.WaveSizeAt(order))));
        return list;
    }
}
