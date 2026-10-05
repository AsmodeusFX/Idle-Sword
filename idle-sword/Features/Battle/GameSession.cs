using IdleSword.Core;

namespace IdleSword.Features;

/// <summary>固定步长的纯 C# 游戏会话；Godot 仅负责输入、表现和持久化调度。</summary>
public sealed partial class GameSession
{
    public GameConfig Config { get; }
    public PlayerState State { get; }
    public BattleState Battle => State.Battle;
    public LevelDef Level => Config.Levels.Single(l => l.Id == Battle.LevelId);
    public List<CombatEffect> Effects { get; } = [];
    public event Action? PersistRequested;
    public string Message { get; private set; } = "踏入青岚，问道长生。";
    public bool Moving { get; private set; }
    public double Elapsed { get; private set; }
    private readonly Random _random;
    private bool _persist;
    private double _buffTime;
    private double _buffPower = 1;
    // 玩家增益（护盾/回血/吸血）：跨关卡与死亡清除，不随存档持久化。
    private double _shield, _shieldUntil, _regenUntil, _regenRate, _lifestealUntil, _lifestealFactor;
    // 攻速与暴击增益：各自持有计时器与参数，互不覆盖（_buffTime/_buffPower 是共享的伤害倍率，与之无关）。
    private double _hasteUntil, _hasteFactor = 1;
    private double _critBonusUntil, _critBonus, _critReduceUntil, _critReduce;
    // 影分身（身外身）：_mirrorUntil > 0 期间，本体每放一个法术，分身同步再放一份（伤害 × _mirrorRatio）。
    // 与其它增益同生命周期（跨关卡 / 死亡 / 切预览清除，不写存档）。_mirrorRatio 在施放那一刻按等级与参悟算定，
    // 此后升级不会追溯改动正在生效的这一次——与"冷却写的是施放时的值"是同一条口径。
    private double _mirrorUntil, _mirrorRatio;
    // 复制出分身那一式的法术 id（= 身外身）。伤害统计要把分身那部分归到它名下，见 `CombatEffect.DamageSource`。
    private string _mirrorSkill = "";
    // 薯皮的"环绕飞剑"：护盾还在时按固定间隔自动还手。`_shieldSkill` 记住护盾是哪个法术给的
    // （出手范围要用它自己的 range），护盾不在时两者都无意义。
    private string _shieldSkill = "";
    private double _guardCooldown;
    // 神通的"概率累加"：技能 id → 已累加的概率。`trigger_chance_step > 0` 的法术每普攻一次就涨一点，
    // 摇中后清零。跨关卡 / 死亡 / 切预览一并清掉（挂在 ClearBuffs 上），与其它短时状态同生命周期。
    private readonly Dictionary<string, double> _triggerRamp = [];
    public bool RiftUnlocked => Battle.BossDefeated && !Battle.Enemies.Any(e => e.Hp > 0 && e.Kind != "rift");
    public double MaxHp => Config.Attr("hp") * (1 + Config.Attr("hp_percent") + TalentBonus("hp"));
    public double Attack => (Config.Attr("atk") + WeaponAttack) * (1 + Config.Attr("atk_percent") + TalentBonus("atk"));
    public double WeaponAttack => State.Weapon == "" ? 0 : Config.Row("Equip", State.Weapon).Number("base_atk") * (1 + .15 * State.WeaponLevel) * State.WeaponRoll;
    /// <summary>角色能打到的最大距离：取已习得法术的最大射程（不含 buff 类，它们不造成伤害）。
    /// **只算法术，不并入普攻射程**：接近裂隙的停步判定靠它，若改由普攻射程决定，
    /// 角色会停在裂隙射程外空转（见 Step 里裂隙解锁那段的说明）。普攻射程单列在 `fightattr.basic_range`，
    /// 它不小于停步距离 640，因此角色站定时普攻必定够得着。
    /// **一个法术都没学时回落到普攻射程**：开局不再白送御剑（见构造器），若不回落这里会是 0，
    /// 角色在 BOSS 格会一路走到底也不停——普攻虽然仍够得着裂隙，但"停下来打"的读法就没有了。
    /// 回落只在**完全没有**伤害型法术时生效，不是把普攻射程并进最大值。</summary>
    public double AttackRange
    {
        get
        {
            var ranges = State.Skills.Where(kv => kv.Value > 0 && Config.Skills.TryGetValue(kv.Key, out var s) && s.Kind != "buff")
                .Select(kv => Config.Skills[kv.Key].Range).ToList();
            return ranges.Count > 0 ? ranges.Max() : Config.Attr("basic_range");
        }
    }

    /// <summary>普攻占用的冷却键。放进 Battle.Cooldowns 是为了复用 Step 里那唯一的冷却衰减点：
    /// 攻速增益因此自动作用于普攻，键本身也随存档往返（SaveStore 不校验冷却键，旧存档缺该键即视为就绪）。
    /// **绝不能放进 State.Skills**：存档校验会用 SwordSkill 表核对技能键，且 CastSkills 对键是无保护索引。</summary>
    public const string BasicAttackKey = "basic_attack";

    /// <summary>普攻开关。开启时角色每 `fightattr.basic_interval` 秒向最近的合法目标平射一柄飞剑，
    /// 并在出手的那一刻摇各神通的触发概率（见 TickBasicAttack）。
    /// 自检里关掉它是为了给受控靶场保留确定的伤害预算——普攻每秒都在加伤害，也会消耗随机数。</summary>
    public bool BasicAttackEnabled { get; set; } = true;

    public GameSession(GameConfig config, PlayerState? state = null, int? seed = null)
    {
        Config = config; State = state ?? new(); _random = seed is null ? new Random() : new Random(seed.Value);
        if (state is null)
        {
            State.Wallet["gold"] = config.Setting("starting_gold");
            State.UnlockedLevels.Add(config.Levels[0].Id);
            foreach (var r in config.Rows("SwordLevel").Where(r => r.Flag("default_unlocked"))) State.Realms.Add(r.Text("id"));
            // 开局不附带任何法术：第 1 关先靠普攻（裸开局 88 ÷ 25 ≈ 3.5 下杀一只标准怪），
            // 让玩家自己点「习得」花 30 灵钱学御剑——第一次花灵钱换来"一支剑秒一只"的对比，
            // 比开局白送一个技能更能说明这个系统在干什么。数值锚点见 docs/design/balance_ttk.md。
            EnterLevel(config.Levels[0].Id);
        }
    }

    public void Step(double dt)
    {
        if (dt <= 0 || !double.IsFinite(dt)) return;
        Elapsed += dt;
        TickIntent(dt);
        if (Battle.RespawnTimer > 0)
        {
            Battle.RespawnTimer = Math.Max(0, Battle.RespawnTimer - dt);
            if (Battle.RespawnTimer == 0) { Battle.PlayerHp = MaxHp; _persist = true; }
            FinishStep(); return;
        }
        // 冷却流逝（唯一的衰减点）：攻速按倍数加速，但**不加速增益类法术**。
        // 若连增益一起加速，疾风（15s 冷却 / 6s 持续）会在持续期内就转好，等于自己给自己减冷却，
        // 变成 100% 常驻；两个 15s 增益还会互相锁死。剑灵的键不在 Config.Skills 里，照样加速。
        foreach (var key in Battle.Cooldowns.Keys.ToArray())
        {
            double factor = HasteFactor > 1 && Config.Skills.TryGetValue(key, out var hastened) && hastened.Kind == "buff" ? 1 : HasteFactor;
            Battle.Cooldowns[key] = Math.Max(0, Battle.Cooldowns[key] - dt * factor);
        }
        _buffTime = Math.Max(0, _buffTime - dt);
        TickPlayerBuffs(dt);
        ActivateCell();
        Moving = !Battle.Enemies.Any(e => e.Hp > 0 && e.Kind != "rift" && Math.Abs(e.X - Battle.PlayerX) <= Config.Attr("stop_range"));
        // 裂隙解锁后停步，但前提是已经打得到它。裂隙位于格内 1450+360 处，而 BOSS 格
        // 阵亡重生点在格首 80：两者相距 1730，远超任一法术射程（950）。若解锁瞬间直接冻结，
        // 玩家会停在射程外空转，挂机永久中断（取决于最后一个非裂隙敌人倒下时玩家站在哪，
        // 故表现为「有概率」）。因此射程外解锁时必须继续沿格推进。
        if (Battle.Cell == Level.Cells - 1 && RiftUnlocked)
            Moving = !InRange(Battle.Enemies.FirstOrDefault(e => e.Hp > 0 && e.Kind == "rift"), AttackRange);
        if (Moving)
        {
            Battle.PlayerX = Math.Min((Level.Cells - 1) * Config.Setting("cell_width") + SpawnOffset - 300,
                Battle.PlayerX + Config.Attr("move_speed") * dt);
            ActivateCell();
        }
        TickSpawns(dt);
        TickEnemies(dt);
        TickBasicAttack();
        CastSkills();
        TickSwordGuard(dt);
        TickEffects(dt);
        Battle.Enemies.RemoveAll(e => e.Hp <= 0);
        // 同帧击杀与死亡：奖励已结算；优先执行复活，传送留到存活后。
        if (Battle.PlayerHp <= 0) Die();
        else if (Battle.PortalDestroyed && RiftUnlocked) CompleteLevel();
        FinishStep();
    }

    private void FinishStep() { if (_persist) { _persist = false; PersistRequested?.Invoke(); } }
    private void TickPlayerBuffs(double dt)
    {
        _shieldUntil = Math.Max(0, _shieldUntil - dt);
        _regenUntil = Math.Max(0, _regenUntil - dt);
        _lifestealUntil = Math.Max(0, _lifestealUntil - dt);
        _hasteUntil = Math.Max(0, _hasteUntil - dt);
        _critBonusUntil = Math.Max(0, _critBonusUntil - dt);
        _critReduceUntil = Math.Max(0, _critReduceUntil - dt);
        _mirrorUntil = Math.Max(0, _mirrorUntil - dt);
        if (_hasteUntil == 0) { _hasteFactor = 1; }
        if (_critBonusUntil == 0) { _critBonus = 0; _critReduce = 0; }
        if (_regenUntil > 0 && Battle.PlayerHp > 0) Battle.PlayerHp = Math.Min(MaxHp, Battle.PlayerHp + MaxHp * _regenRate * dt);
    }
    // 表现层读取的增益剩余时间：只读，不参与任何战斗判定，界面据此绘制各增益光环。
    public double BuffRemaining => _buffTime;
    public double ShieldRemaining => _shieldUntil;
    public double RegenRemaining => _regenUntil;
    public double LifestealRemaining => _lifestealUntil;
    public double HasteRemaining => _hasteUntil;
    public double CritBonusRemaining => _critBonusUntil;
    /// <summary>影分身剩余时间（身外身）。只读，供表现层决定要不要画那个半透明分身。</summary>
    public double MirrorRemaining => _mirrorUntil;
    /// <summary>该法术**当前**的触发概率（含每次普攻累加的那部分）。自检用它断言累加与清零，不必靠概率碰运气。</summary>
    internal double TriggerChanceNow(string id) => Config.Skills.TryGetValue(id, out var s)
        ? Math.Min(1, s.TriggerChance + _triggerRamp.GetValueOrDefault(id)) : 0;
    /// <summary>该法术在指定等级下的**实际冷却**（增益类会随等级缩短，见 `BuffCooldown`）。自检用。</summary>
    internal double BuffCooldownNow(string id, int rank) => Config.Skills.TryGetValue(id, out var s) ? BuffCooldown(s, rank) : 0;
    /// <summary>冷却流逝倍数：攻速增益生效时为 1 + secondary_value，否则 1。只有输出类法术与剑灵吃这个倍数（见 Step）。</summary>
    public double HasteFactor => _hasteUntil > 0 ? _hasteFactor : 1;
    /// <summary>暴击率加成（绝对值）：望月生效期间提高，直接叠在配置的基础暴击率上。</summary>
    public double CritBonus => _critBonusUntil > 0 ? _critBonus : 0;
    /// <summary>清除玩家短时增益（伤害倍率/护盾/回血/吸血/攻速/暴击）。关卡切换、死亡重生与技能预览切换时调用。</summary>
    public void ClearBuffs()
    {
        _buffTime = 0; _shield = 0; _shieldUntil = 0; _regenUntil = 0; _regenRate = 0; _lifestealUntil = 0; _lifestealFactor = 0;
        _hasteUntil = 0; _hasteFactor = 1; _critBonusUntil = 0; _critBonus = 0; _critReduceUntil = 0; _critReduce = 0;
        _mirrorUntil = 0; _mirrorRatio = 0; _mirrorSkill = "";
        _shieldSkill = ""; _guardCooldown = 0;
        _triggerRamp.Clear();
    }
    private double SpawnOffset => Config.Rows("spawn_point")[0].Number("offset");
    private void ActivateCell()
    {
        Battle.Cell = Math.Min(Level.Cells - 1, (int)(Battle.PlayerX / Config.Setting("cell_width")));
        if (Battle.Spawns.ContainsKey(Battle.Cell)) return;
        Battle.Spawns[Battle.Cell] = new();
        if (Battle.Cell == Level.Cells - 1)
        {
            Spawn(Level.Boss, Battle.Cell, 130);
            Spawn(Level.Rift, Battle.Cell, 360);
            Message = "妖王现身。击败群妖，方可破开裂隙。";
        }
    }
    /// <summary>
    /// **调试用**：每波额外多刷几只（GM 面板加减，跨关卡保留但不落盘）。用来测"怪物多的时候技能够不够爽"，
    /// 而不必把 `wave.csv` 的正式数值改掉。额外那几只**从普通怪原型里随机挑**——所以它同时能把地/空、
    /// 快/慢、远/近的比例搅乱，正好用来观察各技能的覆盖情况。
    /// 上限 20 是防手滑：刷怪落点是 `40 + i×100` 连续铺开的，堆太多会溢到下一格（那只是画面难看，不影响判定）。
    /// </summary>
    public int WaveBonus
    {
        get => _waveBonus;
        set => _waveBonus = Math.Clamp(value, 0, 20);
    }
    private int _waveBonus;

    /// <summary>
    /// **调试用**：怪物血量 / 攻击的额外倍率（GM 面板加减，1 = 不额外缩放）。测 debuff 与范围技能的堆积效率时，
    /// 怪太脆会什么都看不出来——把它们调厚一点，看"打死一波要多久、debuff 有没有机会生效"。
    /// 与 <see cref="WaveBonus"/> 一样是**会话态**：不落盘、不碰 `monster.csv`；乘在关卡倍率与波次系数**之后**，
    /// 精英 / BOSS / 裂隙同样吃（它们也是"怪"）。
    /// 改它**立刻按比例缩放场上已有的怪**（保持血量百分比不变），不必等下一波。
    /// </summary>
    public double MonsterHpScale
    {
        get => _monsterHpScale;
        set
        {
            double next = Math.Clamp(value, .1, 100), ratio = next / _monsterHpScale;
            if (Math.Abs(ratio - 1) > 1e-9)
                foreach (var e in Battle.Enemies) { e.MaxHp *= ratio; e.Hp *= ratio; }
            _monsterHpScale = next;
        }
    }
    /// <summary>怪物攻击的额外倍率，语义与 <see cref="MonsterHpScale"/> 相同。</summary>
    public double MonsterAtkScale
    {
        get => _monsterAtkScale;
        set
        {
            double next = Math.Clamp(value, .1, 100), ratio = next / _monsterAtkScale;
            if (Math.Abs(ratio - 1) > 1e-9)
                foreach (var e in Battle.Enemies) e.Atk *= ratio;
            _monsterAtkScale = next;
        }
    }
    private double _monsterHpScale = 1, _monsterAtkScale = 1;

    /// <summary>
    /// **调试用**：主角无敌——`HurtPlayer` 直接返回，不掉血也不死亡。
    /// 为什么需要它：死亡会 `ClearBuffs` 并重置冷却，于是**伤害统计里各技能的周期会断掉**、每秒数字失真；
    /// 想量准就得先让自己站得住。与其它 GM 开关一样是会话态：不写配置、不落盘。
    /// 注意它会改变随机数的消耗——`HurtPlayer` 里那次闪避判定被跳过了，所以开关前后同 seed 的随机序列不再一致。
    /// </summary>
    public bool PlayerInvincible { get; set; }
    private void TickSpawns(double dt)
    {
        var wave = Config.Waves[Level.Wave];
        foreach (var (cell, spawn) in Battle.Spawns)
        {
            if (Battle.PlayerX > cell * Config.Setting("cell_width") + SpawnOffset) spawn.Passed = true;
            if (spawn.Passed || Battle.BossDefeated) continue;
            spawn.Timer -= dt;
            if (spawn.Timer > 0) continue;
            spawn.Timer += wave.Interval; spawn.Wave++;
            // 混编：一条波次可以配多种怪，序号 i 是跨模板连续的，两种怪不会落在同一个点上。
            var units = Config.WaveUnits[wave.Id];
            var counts = WaveCounts(wave);
            int i = 0;
            for (int u = 0; u < units.Count; u++)
                for (int n = 0; n < counts[u]; n++) Spawn(units[u].Monster, cell, 40 + i++ * 100);
            // 调试加成（GM 面板）：额外那几只从普通怪里随机挑，见 WaveBonus 的说明。
            for (int n = 0; n < _waveBonus; n++) Spawn(RandomNormalMonster(), cell, 40 + i++ * 100);
            if (spawn.Wave % wave.EliteEvery == 0) Spawn(wave.Elite, cell, 40 + i * 100);
        }
    }
    /// <summary>
    /// 波次只数的关卡倍率：随关卡递增，且递增本身在加快（线性一项 + 平方一项，两项都在 `game_settings` 可调）。
    /// </summary>
    public double WaveScale => 1 + (Level.Order - 1) * Config.Setting("wave_growth") + Math.Pow(Level.Order - 1, 2) * Config.Setting("wave_accel");
    /// <summary>
    /// 这一波各模板刷几只。**只数先按关卡算，再按比例摊给模板**——所以"两只小怪配一只肉盾"永远是这个配比，
    /// 数量涨起来不会把阵容比例改掉（旧写法是各模板各自乘一个关卡倍率，`count = 1` 的模板会跳变：
    /// 第 2 关 `round(1.33)` 还是 1、第 3 关直接跳到 2）。
    /// 只数 = `round(count × 关卡倍率)`，到 `count_max` 封顶。
    /// </summary>
    /// <summary>随机挑一个普通怪原型（波次调试加成用）。走会话自己的 `_random`，所以同 seed 仍可复现。</summary>
    private string RandomNormalMonster()
    {
        var normal = Config.Monsters.Values.Where(m => m.Kind == "normal").ToArray();
        return normal[_random.Next(normal.Length)].Id;
    }
    private int[] WaveCounts(WaveDef wave) => WaveCountsFor(wave, Config.WaveUnits[wave.Id], WaveScale);
    /// <summary>上面那条的纯函数形态：只吃波次、模板与关卡倍率，自检可以直接断言（不必造一场战斗）。</summary>
    internal static int[] WaveCountsFor(WaveDef wave, List<WaveUnitDef> units, double scale)
    {
        int total = Math.Min(wave.CountMax, (int)Math.Round(wave.Count * scale, MidpointRounding.AwayFromZero));
        int sum = units.Sum(u => u.Weight);
        var counts = units.Select(u => total * u.Weight / sum).ToArray();     // 先按比例向下取整
        // 余数按"被舍掉的小数部分"从大到小补一只，保证合计**恰好**等于 total（并列时按表内顺序，结果确定可复现）。
        foreach (int i in Enumerable.Range(0, units.Count)
            .OrderByDescending(i => total * units[i].Weight % sum).ThenBy(i => i)
            .Take(total - counts.Sum()))
            counts[i]++;
        return counts;
    }

    private void Spawn(string monsterId, int cell, double offset)
    {
        var m = Config.Monsters[monsterId];
        // 普通怪吃两重倍率：关卡倍率（level.csv，随关卡递增）× 波次强度系数（wave.csv，同一关内的波次梯度）。
        // 精英 / BOSS / 裂隙只吃关卡倍率——它们是关卡节点而非波次阵容的一部分，不该被"这波更硬"影响。
        var wave = Config.Waves[Level.Wave];
        var (hp, atk) = m.Kind switch { "boss" => (Level.BossHp, Level.BossAtk), "elite" => (Level.EliteHp, Level.EliteAtk), "rift" => (Level.RiftHp, 0d), _ => (Level.HpScale * wave.HpScale, Level.AtkScale * wave.AtkScale) };
        // 最后的 `MonsterHpScale` / `MonsterAtkScale` 是 GM 的调试倍率（默认 1），乘在所有关卡与波次倍率**之后**。
        hp *= _monsterHpScale; atk *= _monsterAtkScale;
        Battle.Enemies.Add(new() { Id = Battle.NextEnemyId++, MonsterId = m.Id, Kind = m.Kind,
            X = cell * Config.Setting("cell_width") + SpawnOffset + offset,
            Hp = m.Hp * hp, MaxHp = m.Hp * hp, Atk = m.Atk * atk, AttackTimer = m.Interval });
    }
    private void TickEnemies(double dt)
    {
        foreach (var e in Battle.Enemies.Where(e => e.Hp > 0 && e.Kind != "rift"))
        {
            var m = Config.Monsters[e.MonsterId];
            e.SlowUntil = Math.Max(0, e.SlowUntil - dt);
            e.ChillUntil = Math.Max(0, e.ChillUntil - dt);
            e.StunUntil = Math.Max(0, e.StunUntil - dt);
            e.VulnerableUntil = Math.Max(0, e.VulnerableUntil - dt);
            if (e.StunUntil > 0) continue; // 眩晕：无法移动与攻击
            double distance = Math.Abs(e.X - Battle.PlayerX);
            double speed = m.Speed * (e.SlowUntil > 0 ? e.SlowFactor : 1);
            if (distance > m.Range)
                e.X += Math.Sign(Battle.PlayerX - e.X) * Math.Min(speed * dt, distance - m.Range);
            e.AttackTimer -= dt;
            if (Math.Abs(e.X - Battle.PlayerX) > m.Range + 1 || e.AttackTimer > 0) continue;
            e.AttackTimer = m.Interval;
            if (m.Attack == "melee") HurtPlayer(e.Atk);
            else Effects.Add(new() { Kind = m.Attack == "magic" ? "target" : "projectile", X = e.X, Life = m.Attack == "magic" ? .7 : 5, Damage = e.Atk, Hostile = true });
        }
        // 灼烧持续伤害：对非裂隙存活敌人按秒结算，不受眩晕影响。
        // 来源记在敌人身上（`DotSkill`）——跳伤这条路上没有 `CombatEffect` 可查，只能施放时先记下来。
        foreach (var e in Battle.Enemies.Where(e => e.Hp > 0 && e.Kind != "rift" && e.DotUntil > 0).ToArray())
        {
            e.DotUntil = Math.Max(0, e.DotUntil - dt);
            HurtEnemy(e, e.DotDps * dt, e.DotSkill);
        }
    }
    /// <summary>
    /// 按技能累计的伤害账本（GM 面板的「伤害统计」读它）。**纯运行时**：与 `Effects` / `Enemies` 同性质，不落盘。
    /// 口径与归因规则见 <see cref="DamageTally"/>。
    /// </summary>
    public DamageTally DamageStats { get; } = new();
    /// <summary>统计时长（自上一次重置起的模拟秒数）。会话的 `Elapsed` 从不重置，所以分母由账本自己记。</summary>
    public double DamageStatsSeconds => DamageStats.Seconds(Elapsed);
    /// <summary>清空伤害账本，并把统计起点挪到此刻。</summary>
    public void ResetDamageStats() => DamageStats.Reset(Elapsed);
    private bool Legal(EnemyState e) => e.Hp > 0 && (e.Kind != "rift" || RiftUnlocked);
    /// <summary>怪物所在层。层从模板推导而不是存进 `EnemyState`：存档格式一个字节都不用改，老存档的 MonsterId 照样能定位。</summary>
    private bool Flying(EnemyState e) => Config.Monsters[e.MonsterId].Layer == "air";
    /// <summary>技能定位与敌人所在层的匹配。空 / both 打两者，ground 只打地面，air 只打空中。</summary>
    private bool LayerHit(string hits, EnemyState e) => hits switch
    {
        "ground" => !Flying(e),
        "air" => Flying(e),
        _ => true,
    };
    /// <summary>能成为这一发的合法目标：目标本身合法，且层数对得上。地面招不会去锁飞行单位。</summary>
    private bool Legal(EnemyState e, string hits) => Legal(e) && LayerHit(hits, e);
    private bool InRange(EnemyState? e, double range) => e is not null && Math.Abs(e.X - Battle.PlayerX) <= range;
    /// <summary>最近的合法目标。hits 为空表示不限层（普攻、剑灵弹、召唤弹都走这一档）。</summary>
    private EnemyState? Target(double range, string hits = "") => Battle.Enemies.Where(e => Legal(e, hits) && Math.Abs(e.X - Battle.PlayerX) <= range).OrderBy(e => Math.Abs(e.X - Battle.PlayerX)).FirstOrDefault();

    private void CastSkills()
    {
        if (Battle.PlayerHp <= 0) return;
        foreach (var (id, rank) in State.Skills)
        {
            if (rank <= 0 || !Ready(id)) continue;
            var skill = Config.Skills[id];
            // 神通（trigger_chance > 0）不在冷却到点时自动释放，改为普攻出手时按概率触发，见 TickBasicAttack。
            if (skill.TriggerChance > 0) continue;
            if (Release(skill, SkillPower(skill, rank), rank)) Battle.Cooldowns[id] = BuffCooldown(skill, rank);   // 没有合法目标时不空放、也不消耗冷却
        }
        foreach (var pet in State.EquippedPets)
        {
            var row = Config.Row("PetSkill", Config.Row("Pet", pet).Text("skill_id"));
            var target = Target(row.Number("range"));
            if (target is null || !Ready(pet)) continue;
            var bonus = State.PetBuffs.GetValueOrDefault(pet, []).Sum(id => Config.Row("PetEquip", id).Number("power"));
            Launch(row.Text("kind"), target, Attack * row.Number("power") * (1 + bonus), .5, skill: row.Text("id"));
            Battle.Cooldowns[pet] = row.Number("cooldown");
        }
    }
    /// <summary>
    /// 薯皮的**环绕飞剑**：护盾还在时，按固定间隔自动向 `guard_range` 内最近的合法敌人射出一柄小剑。
    /// 它与护盾是同一件事的两面——护盾负责挡、飞剑负责还手，所以"护盾只在挨打时才有用"这条缺点被补上了。
    /// 三个参数全走 `game_settings`（`guard_blade_power` / `guard_interval` / `guard_range`）。
    /// **出手范围必须与法术自己的 `range` 分开**：`range` 是"能不能施放这个增益"（要够得着敌人才放），
    /// 若把它压短到近身距离，远程怪在场时就永远放不出来——预览页的靶子摆在 520，射程压到 400 之后
    /// 薯皮在预览里**连护盾都上不了**，画面上什么都没有。护盾不在时什么都不做。
    /// `index` 传 1：技能名标签与"暴击缩冷却"都只认第 0 支，环绕飞剑是持续输出、不该抢那个名额。
    /// </summary>
    private void TickSwordGuard(double dt)
    {
        _guardCooldown = Math.Max(0, _guardCooldown - dt);
        if (_shieldUntil <= 0 || _guardCooldown > 0) return;
        if (!Config.Skills.TryGetValue(_shieldSkill, out var skill)) return;
        var target = Target(Config.Setting("guard_range"));
        if (target is null) return;
        Launch("projectile", target, Attack * Config.Setting("guard_blade_power"), .5, skill: skill.Id, index: 1);
        _guardCooldown = Config.Setting("guard_interval");
    }

    /// <summary>法术威力：配置基础值 × 技能等级加成 × 参悟加成。普攻不走这里（它用 `fightattr.basic_power`）。
    /// 每级的加成走 `game_settings.skill_level_bonus` 而不是硬编码——技能页的「每级 +X%」提示读的是同一个值，
    /// 两边必须同源，否则提示会撒谎。</summary>
    private double SkillPower(SkillDef skill, int rank) =>
        skill.Power * (1 + Config.Setting("skill_level_bonus") * (rank - 1) + SkillBonus(skill.Id, "damage_percent"));

    /// <summary>
    /// 增益类法术的**实际冷却**：每升一级缩短 `buff_cooldown_per_level`，但**下限是持续时长 × `buff_cooldown_floor_ratio`**。
    /// 增益的峰值强度不随等级变（它的效果走 `secondary_value`，那是个定值），所以若升级什么都不给，
    /// 玩家花灵钱点「强化」就什么都没发生——疾风与望月原本就是这种零收益。
    /// 改成缩冷却之后，成长体现在**覆盖率**上，而峰值不变（「小妖档不该给满」那条口径因此保住）。
    /// 下限把覆盖率封在 80%，避免它变成常驻——与「攻速不加速增益类法术」是同一条护栏。
    /// 非增益类返回配置冷却，行为不变。
    /// </summary>
    private double BuffCooldown(SkillDef skill, int rank)
    {
        if (skill.Kind != "buff") return skill.Cooldown;
        double floor = skill.Duration * Config.Setting("buff_cooldown_floor_ratio");
        return Math.Max(floor, skill.Cooldown * (1 - Config.Setting("buff_cooldown_per_level") * (rank - 1)));
    }

    /// <summary>真的出手一次。返回 false 表示场上没有合法目标、这一手没有放出去，调用方据此决定不写冷却。</summary>
    private bool Release(SkillDef skill, double power, int rank)
    {
        if (!LaunchShape(skill, power, rank)) return false;
        // 影分身（身外身）：本体这一手放出去之后，分身同步再放一份，伤害按继承比例。
        // 镜像走 LaunchShape 而不是 Release —— 冷却由 Release 的**调用方**写，镜像这一份因此不写冷却，
        // 于是不会多发一次释放音（TrackSkillCasts 只认"冷却 0 → 正"的边沿），也不会把 CastRoot 的定身又结算一遍。
        // 跳过增益类（对分身没有意义）与召唤类（召唤物会活过分身寿命），并跳过影分身自身，避免无限递归。
        if (_mirrorUntil > 0 && skill.Secondary != "mirror" && skill.Kind is not ("buff" or "summon"))
            LaunchShape(skill, power * _mirrorRatio, rank, mirrored: true);
        // 施放瞬间的全屏定身。放在这里而不是 Launch 里：Release 已经把 buff / multi / volley 三条释放路径收口，
        // 一次施法只结算一次；定身先于剑气出手，配置把 hover_time 也配成同长，读起来就是"定住一秒再放剑气"。
        // 复用 StunUntil，于是"无法移动与攻击"与表现层的眩晕电弧都是现成的，不必另加状态。
        // 排除裂隙：它本来就不移动、不攻击，定身对它没有意义；而 TickEnemies 只在非裂隙敌人上衰减状态，
        // 给它挂上 StunUntil 会永远减不掉，表现层会一直画着一圈电弧。
        if (skill.CastRoot > 0)
            foreach (var e in Battle.Enemies.Where(e => e.Hp > 0 && e.Kind != "rift"))
                e.StunUntil = Math.Max(e.StunUntil, skill.CastRoot);
        return true;
    }

    /// <summary>按法术的形态真正把这一手放出去。返回 false 表示没有合法目标、没有空放。</summary>
    private bool LaunchShape(SkillDef skill, double power, int rank, bool mirrored = false)
    {
        if (skill.Kind == "buff")
        {
            // Buff 以自身为目标，仅在交战时触发，避免无敌人时浪费冷却。
            if (Target(skill.Range) is null) return false;
            CastBuff(skill, power, rank);
            return true;
        }
        if (skill.Secondary == "multi")
        {
            // 历史次级效果：当前已无技能使用（原疾风剑专用），退休配置日后可能复活，勿删。
            var targets = Targets(skill.Range, (int)skill.SecondaryValue);
            if (targets.Count == 0) return false;
            for (int i = 0; i < targets.Count; i++) LaunchSkill(skill, targets[i], Attack * power, i, 0, mirrored);   // 序号照传，暴击缩冷却才不会被多发放大
            return true;
        }
        return CastVolley(skill, Attack * power, mirrored);
    }

    /// <summary>
    /// 无视冷却与触发概率，立刻释放一次指定法术。技能预览与自检专用，不参与正常战斗流程。
    /// 照常写冷却：释放音是靠"冷却 0 → 正"的边沿触发的（见 UI/Audio.cs 的 TrackSkillCasts），
    /// 这里不写的话神通在预览里出手了却没有声音，而那时唯一出得了手的就只剩它。
    /// </summary>
    internal bool ForceRelease(string id)
    {
        if (!Config.Skills.TryGetValue(id, out var skill)) return false;
        int rank = State.Skills.GetValueOrDefault(id);
        if (!Release(skill, SkillPower(skill, rank), rank)) return false;
        Battle.Cooldowns[id] = skill.Cooldown;
        return true;
    }

    /// <summary>
    /// 普通攻击：每 `basic_interval` 秒向最近的合法目标平射一柄飞剑，并在出手的这一刻摇各神通的触发概率。
    /// 形态用默认 bolt（Trajectory 为空）——表现层已把它画成肩部高度横飞的剑，正是「平射」；
    /// Skill 传空串，于是技能名标签、形态参数回退、以及「效果来源必须是已知法术」的自检都不受影响。
    /// 没有合法目标时不空放、也不消耗间隔，与法术同口径。
    /// </summary>
    private void TickBasicAttack()
    {
        if (!BasicAttackEnabled || Battle.PlayerHp <= 0 || !Ready(BasicAttackKey)) return;
        var target = Target(Config.Attr("basic_range"));
        if (target is null) return;
        Battle.Cooldowns[BasicAttackKey] = Config.Attr("basic_interval");
        Launch("projectile", target, Attack * Config.Attr("basic_power"), 4, skill: "");
        RollTriggerSkills();
    }

    /// <summary>
    /// 神通触发：普攻出手时，对每个「已习得、当前冷却就绪、且配了触发概率」的法术各摇一次。
    /// 冷却未就绪的不摇——省下一次随机数，也让「冷却就绪才可能触发」这句话成立。
    /// 冷却对神通而言是「最短触发间隔」：实际间隔由触发概率主导（5% 每次普攻 ≈ 平均 20 秒一次）。
    /// </summary>
    private void RollTriggerSkills()
    {
        foreach (var (id, rank) in State.Skills)
        {
            if (rank <= 0 || !Ready(id) || !Config.Skills.TryGetValue(id, out var skill) || skill.TriggerChance <= 0) continue;
            // 概率可以是"每次普攻累加"的形态（`trigger_chance_step > 0`）：起步低、越打越近，摇中后回到起步值。
            // 它既不会"冷却一好就放"（起步只有 15%），也不会连十几次摇不中——把"看脸"换成"越打越近"。
            // `step = 0` 时这一路退化成原来的固定概率，行为不变。
            double acc = _triggerRamp.GetValueOrDefault(id);
            if (_random.NextDouble() >= Math.Min(1, skill.TriggerChance + acc))
            {
                _triggerRamp[id] = acc + skill.TriggerChanceStep;
                continue;
            }
            _triggerRamp[id] = 0;
            if (Release(skill, SkillPower(skill, rank), rank)) Battle.Cooldowns[id] = skill.Cooldown;
        }
    }

    private bool Ready(string key) => Battle.Cooldowns.GetValueOrDefault(key) <= 0;
    /// <summary>
    /// 弹群编排：数量、落点/排列间距与错时都在这里决定，返回是否真的出手了（false 表示没有合法目标）。
    /// </summary>
    private bool CastVolley(SkillDef skill, double damage, bool mirrored = false)
    {
        int count = skill.Kind == "projectile" ? skill.ProjectileCount : 1;
        if (skill.Trajectory == "sky_drop")
        {
            // 固定剑阵：阵心取选中的合法敌人，各支按 spread 在阵心两侧铺开，**与敌人数无关**——
            // 只有一个敌人时不会缩成一束。落点由 Core 算进 X，因为它直接参与落地判定。
            // 各锁一敌（`band > 0`，18 天陨）：每支**各锁一个（尽量不同的）目标、落在它当时的位置爆炸**，
            // 目标中途死了也照炸那个位置（"击中谁是谁"，与火海同一条契约）。天上的黑洞则以**目标群的中轴**为心
            // 铺开 `band` 宽、再夹进画面——**锚在目标上而不是角色上**：角色每秒走 340，一发 1.3 秒的轰炸若从
            // 角色量起，落点会甩到身后，一整排剑全落在空地上。敌人不足时 `Picks` 会循环重复，所以打 BOSS
            // 就是 count 支全砸在它身上；聚怪把怪捏成一簇时，count 支落在同一片、爆炸叠起来。
            if (skill.Band > 0)
            {
                var volley = Picks(skill, count);
                if (volley.Count == 0) return false;
                double mid = volley.Average(e => e.X);
                // 夹取：角色锚点在屏幕 330、逻辑画布 1920，所以黑洞整条带子要落在 [玩家X + 60, 玩家X + 1520] 内。
                double near = Math.Clamp(mid - skill.Band / 2, Battle.PlayerX + 60, Battle.PlayerX + 1520 - skill.Band);
                for (int i = 0; i < count; i++)
                {
                    double slot = count == 1 ? .5 : i / (double)(count - 1);
                    LaunchSkill(skill, volley[i % volley.Count], damage, i, 0, mirrored, near + slot * skill.Band);
                }
                return true;
            }
            var center = Pick(skill);
            if (center is null) return false;
            for (int i = 0; i < count; i++) LaunchSkill(skill, center, damage, i, (i - (count - 1) / 2.0) * skill.Spread, mirrored);
            return true;
        }
        // 其余形态各自选敌、尽量不重复（敌人不足才循环重复）。
        var picks = Picks(skill, count);
        if (picks.Count == 0) return false;
        for (int i = 0; i < picks.Count; i++) LaunchSkill(skill, picks[i], damage, i, 0, mirrored);
        return true;
    }

    /// <summary>
    /// 选敌。默认最近的合法目标（受射程限制），即法术一直以来的行为；
    /// `highest_hp` = 全场血量最高者、**无视射程**（斩鬼）；
    /// `lowest_hp` = **射程内**血量最低者（须芒的收割，与前者不同：它仍受射程约束）。
    /// 同血量 / 同距离按 Id 升序，自检才有确定结果。
    /// </summary>
    private EnemyState? Pick(SkillDef skill) => skill.Targeting switch
    {
        "highest_hp" => Battle.Enemies.Where(e => Legal(e, skill.Hits)).OrderByDescending(e => e.Hp).ThenBy(e => e.Id).FirstOrDefault(),
        "lowest_hp" => Targets(skill.Range, 1, skill.Hits, "lowest_hp").FirstOrDefault(),
        // 取射程内**最靠前**的那只（X 最大）。给"在地上留一片持久灼烧"的技能用：怪从右边来，
        // 那条路是必经之路——落在最靠前的位置等于铺在"迎宾位"，后面进来的都要穿过去；
        // 落在最近敌人身上等于落在脚边，怪几乎立刻就走过它了，收益最低（妖火的火海尤其吃亏）。
        "farthest" => Battle.Enemies.Where(e => Legal(e, skill.Hits) && Math.Abs(e.X - Battle.PlayerX) <= skill.Range)
            .OrderByDescending(e => e.X).ThenBy(e => e.Id).FirstOrDefault(),
        _ => Target(skill.Range, skill.Hits),
    };

    /// <summary>
    /// 多发取目标。`highest_hp` 取全场血量最高的前 n 个（不重复，斩鬼用）；
    /// `lowest_hp` 取**射程内**血量最低的前 n 个，**敌人不足时循环重复打同一个**——
    /// 须芒因此"场上只有一只时三段全打在它身上"，打 BOSS 不吃亏。
    /// 将来要"依次出现多个剑光"时这里天然支持，只需放宽"非 projectile 只能单发"那条校验。
    /// </summary>
    private List<EnemyState> Picks(SkillDef skill, int n) => skill.Targeting switch
    {
        "highest_hp" => Battle.Enemies.Where(e => Legal(e, skill.Hits)).OrderByDescending(e => e.Hp).ThenBy(e => e.Id).Take(n).ToList(),
        "lowest_hp" => TargetsWithRepeats(skill.Range, n, skill.Hits, "lowest_hp"),
        "farthest" => TargetsWithRepeats(skill.Range, n, skill.Hits, "farthest"),
        _ => TargetsWithRepeats(skill.Range, n, skill.Hits),
    };
    // ThenBy(Id) 只为同 X 多敌时排序确定：自检常把多个敌人重叠放到同一坐标。
    // order 由 `SwordSkill.targeting` 传进来：空 = **离玩家最近**（默认口径）、`lowest_hp` = 血量升序（收割）、
    // `farthest` = 最靠前（给"在地上留一片持久灼烧"的技能用，见 `Pick`）。
    private List<EnemyState> Targets(double range, int n, string hits = "", string order = "")
    {
        var inRange = Battle.Enemies.Where(e => Legal(e, hits) && Math.Abs(e.X - Battle.PlayerX) <= range);
        var ordered = order switch
        {
            "lowest_hp" => inRange.OrderBy(e => e.Hp).ThenBy(e => e.Id),
            "farthest" => inRange.OrderByDescending(e => e.X).ThenBy(e => e.Id),
            _ => inRange.OrderBy(e => Math.Abs(e.X - Battle.PlayerX)).ThenBy(e => e.Id),
        };
        return ordered.Take(n).ToList();
    }
    /// <summary>多发弹群选敌：先取 n 个不同敌人（各自选敌、尽量不重复）；敌人不足时才按序循环重复打同一个。</summary>
    private List<EnemyState> TargetsWithRepeats(double range, int n, string hits = "", string order = "")
    {
        var distinct = Targets(range, n, hits, order);
        if (distinct.Count == 0) return [];
        var picks = new List<EnemyState>(n);
        for (int i = 0; i < n; i++) picks.Add(distinct[i % distinct.Count]);
        return picks;
    }
    // 影分身在本体**身后**的站位偏移（逻辑单位）。玩家朝右推进，所以身后是更小的 X。
    private const double MirrorOffset = -110;
    // 分身那一式比本体晚多久出现。要短到仍读作"同一式跟着放"，又长到不会被看成同一发——
    // 少了它就永远是同帧落地，画面上糊成一团（尤其是 target / ground / sky_drop 那三类没有发射点的形态）。
    public const double MirrorDelay = .18;
    // 目标中途死亡时，"落点重判定"的搜索半径（逻辑单位，约一个身位）。
    // 追踪弹与定点弹在**发射时**锁定目标，而目标很可能在弹丸飞到之前就被别的技能打死——
    // 这时若什么都不做，这一发就白飞了（须芒专挑残血，于是它系统性白飞）。
    // 补救只在**落点附近**挑一个合法敌人结算，**不改追远处**：改追会让多发齐射收敛到同一只
    // （Picks 刻意"尽量不重复"就是为了铺开），剑气也会明显拐弯打远处的怪。
    private const double FallbackRadius = 80;
    // "各锁一敌"的剑陨落地后留下的**爆炸余韵**时长（秒）。它只有寿命、不造成伤害（`Damage = 0`），
    // 纯粹是给表现层一个 0→1 的进度去画扩散的剑气爆炸——否则效果在落地当帧就被回收，画不出"炸开"。
    // 定得比 ground 的 0.6 秒结算间隔短，于是它连一次结算都不会走到。
    public const double BurstLife = .3;
    // 追踪弹最多改换几次目标。到顶之后即使落点附近还有人也不再接力，弹丸自然消散——
    // 否则一只都追不到时它会一段接一段地追下去，永远不消失。**索不到敌就是本次伤害丢失**，这是有意的。
    private const int MaxReacquire = 2;

    private void CastBuff(SkillDef skill, double power, int rank)
    {
        // 伤害倍率只在配置里 power != 1 时占用：纯功能向的增益（疾风/望月）不该把正在
        // 生效的伤害倍率重置掉。判断用配置的基础 power 而不是算上等级与参悟之后的 power——
        // 否则技能一升级，倍率就会被激活，等于偷偷取消了这条规则。
        if (skill.Power != 1) { _buffPower = power; _buffTime = skill.Duration; }
        Battle.PlayerHp = Math.Min(MaxHp, Battle.PlayerHp + MaxHp * .05);
        switch (skill.Secondary)
        {
            case "shield": _shield = Math.Max(_shield, Attack * skill.SecondaryValue); _shieldUntil = skill.SecondaryDuration; _shieldSkill = skill.Id; break;
            case "regen": _regenUntil = skill.SecondaryDuration; _regenRate = skill.SecondaryValue; break;
            case "lifesteal": _lifestealUntil = skill.SecondaryDuration; _lifestealFactor = skill.SecondaryValue; break;
            case "haste": _hasteFactor = 1 + skill.SecondaryValue; _hasteUntil = skill.SecondaryDuration; break;
            case "crit_reduce": _critBonus = skill.SecondaryValue; _critBonusUntil = skill.SecondaryDuration; _critReduce = skill.SecondaryExtra; _critReduceUntil = skill.SecondaryDuration; break;
            // 影分身（身外身）：继承比例 = 配置基础值 + 每级 +1% + 参悟那 4 行的加成，**不封顶**（用户定）。
            // 算的是施放这一刻的 rank —— 与 SkillPower 同一条口径：正在生效的这一次不随后续升级追溯变动。
            case "mirror":
                _mirrorUntil = skill.SecondaryDuration;
                _mirrorRatio = skill.SecondaryValue + .01 * (rank - 1) + SkillBonus(skill.Id, "inherit_percent");
                _mirrorSkill = skill.Id;
                break;
        }
    }
    /// <summary>
    /// 暴击后的"缩短一个随机技能的冷却"：只从已习得且**当前冷却 &gt; 0** 的非增益技能里抽
    /// （抽到冷却为 0 的技能等于白给；抽到增益会让攻速覆盖率自涨，见下）。没有候选就什么都不做。
    /// </summary>
    private void CritShortenCooldown()
    {
        if (_critReduceUntil <= 0 || _critReduce <= 0) return;
        // 候选里排除增益类法术：否则暴击会不断给疾风减冷却，它的覆盖率从标称的 6/15=40%
        // 实测涨到 50%+，形成"暴击 → 增益来得更勤 → 出手更快 → 更多暴击"的正反馈。
        // 与"攻速不加速增益类法术的冷却"是同一条口径：增益之间的冷却不该互相喂。
        // 神通（trigger_chance > 0）同样排除：它们的冷却只是"最短触发间隔"，实际由概率主导，
        // 缩几秒几乎等于白给，还会稀释这次暴击本该给输出法术的收益。
        var cooling = State.Skills.Where(kv => kv.Value > 0 && Battle.Cooldowns.GetValueOrDefault(kv.Key) > 0
                && Config.Skills.TryGetValue(kv.Key, out var s) && s.Kind != "buff" && s.TriggerChance <= 0)
            .Select(kv => kv.Key).ToArray();
        if (cooling.Length == 0) return;
        string pick = cooling[_random.Next(cooling.Length)];
        Battle.Cooldowns[pick] = Math.Max(0, Battle.Cooldowns[pick] - _critReduce);
    }
    private void LaunchSkill(SkillDef skill, EnemyState target, double damage, int index, double lane = 0, bool mirrored = false, double spawnX = 0)
    {
        // 随机量在这里一次摇定，表现层只读结果：逐帧重摇会让弧线/高度抖动，也让自检无法复现。
        // 弧度只有弧线形态抽；Jitter 是 0..1 的通用抖动，由表现层按形态解释（天降形态拿它做出生高度）。
        double arc = skill.Trajectory == "arc_homing"
            ? skill.ArcMin + _random.NextDouble() * (skill.ArcMax - skill.ArcMin) : 0;
        double jitter = _random.NextDouble();
        // 弹群错时：第 i 支额外延迟 i × interval，再叠加 ±jitter 的随机；与 hover_time 一起折算成"起飞前停留"。
        double delay = Math.Max(0, index * skill.VolleyInterval + (_random.NextDouble() * 2 - 1) * skill.VolleyJitter);
        Launch(skill.Kind, target, damage, skill.Duration, true, skill.Secondary, skill.SecondaryValue,
            skill.SecondaryDuration, skill.Secondary == "execute" ? skill.SecondaryValue : 0, skill.AoeRadius, skill.Id,
            skill.Trajectory, index, skill.HoverTime + delay, arc, skill.Speed > 0 ? skill.Speed : 1500, lane, jitter,
            skill.PierceChance, skill.Knockback, skill.AoeAll, skill.Hits, mirrored, skill.Gather, spawnX, skill.Band);
    }
    private void Launch(string kind, EnemyState target, double damage, double duration, bool applyModifiers = true,
        string secondary = "", double secondaryValue = 0, double secondaryDuration = 0, double executeThreshold = 0, double aoeRadius = 0, string skill = "",
        string trajectory = "", int index = 0, double hold = 0, double arc = 0, double speed = 1500, double lane = 0, double jitter = 0,
        double pierceChance = 0, double knockback = 0, bool aoeAll = false, string layer = "", bool mirrored = false, double gather = 0, double spawnX = 0, double band = 0)
    {
        if (applyModifiers && _buffTime > 0) damage *= _buffPower;
        // 暴击判定全游戏只有这一处，逐弹丸各摇一次（召唤弹 applyModifiers=false 不参与）。
        // 抽签消耗 _random，会让后续随机序列偏移（同 seed 仍可复现）。
        if (applyModifiers && _random.NextDouble() < Config.Attr("crit") + CritBonus)
        {
            damage *= Config.Attr("crit_damage");
            // "暴击缩短一个随机技能的冷却"按**每次施法**最多触发一次：多发齐射是 3～5 支各摇一次暴击的，
            // 若每支都触发，实际触发密度会放大数倍（实测能把增益冷却吃到覆盖率自涨）。
            // 由该次施法的第一支负责，单发/剑灵弹丸的 index 本就是 0。
            // 普攻（skill 为空）不参与缩冷却：它每秒一次，若也算，望月的覆盖率会自涨——
            // 与"攻速不加速增益类法术"是同一条护栏口径：缩冷却只挂在法术出手上。
            // 影分身那一份不替本体缩冷却：它不是玩家亲手放的那一手，否则分身窗口内缩冷却会凭空翻倍。
            if (index == 0 && skill != "" && !mirrored) CritShortenCooldown();
        }
        // 生命期分两种：普通弹道沿用硬编码 4 秒——召唤弹传 2、剑灵弹只传 0.5，
        // 若把 duration 当通用寿命会缩短剑灵弹丸、使其飞不到目标；自定义飞行形态才用 duration（本列对该形态即飞行/下坠时长）。
        // 自定义形态还要加上起飞前停留（hold）：停留与飞行各自计时，落地/命中的时刻才会随错时而变化。
        bool customFlight = trajectory is "hover_homing" or "sky_drop" or "arc_homing" or "line_pierce" or "line_shot";
        double life = kind != "projectile" ? duration
            : customFlight ? duration + hold
            : 4;
        // 起始 X：天降形态生成在目标上空（lane 是剑阵里的落点偏移，落点由 Core 定、不交给表现层），此后冻结；
        // 其余弹道从玩家处出发。
        // 影分身那一份从**分身**身上出发（本体身后 MirrorOffset）。target / ground / sky_drop 三类的起点本就是目标的 X，
        // 分身一式因此与本体落点完全重合——这正是"分身同放一式"该有的样子，不需要偏移。
        double startX = kind is "ground" or "target" || trajectory == "sky_drop"
            ? target.X + lane
            : Battle.PlayerX + (mirrored ? MirrorOffset : 0);
        Effects.Add(new() { Kind = kind, X = startX, LegX = startX,
            // 出生点只给表现层用（生成带模式下的黑洞与斜落轨迹）；为 0 表示与 X 相同。
            SpawnX = spawnX,
            Target = target.Id, TargetX = target.X, Damage = damage, Life = life, MaxLife = life,
            // 自定义形态复用 Timer 作"起飞前停留"倒计时：TickEffects 每步已经 Timer -= dt，停留期只读 Timer > 0。
            Timer = customFlight ? hold : 0,
            Skill = skill, Secondary = secondary, SecondaryValue = secondaryValue, SecondaryDuration = secondaryDuration,
            ExecuteThreshold = executeThreshold, AoeRadius = aoeRadius, Trajectory = trajectory, Index = index, Arc = arc,
            Speed = speed, Jitter = jitter, PierceChance = pierceChance,
            Knockback = knockback, Gather = gather, AoeAll = aoeAll, Layer = layer, Mirrored = mirrored, Band = band,
            MirrorSkill = mirrored ? _mirrorSkill : "",
            // 分身那一式晚一拍出现：同帧落地两边会完全重叠，尤其 target / ground / sky_drop 三类根本没有发射点可看。
            Delay = mirrored ? MirrorDelay : 0 });
    }
    private void TickEffects(double dt)
    {
        foreach (var effect in Effects.ToArray())
        {
            // 影分身那一式晚一拍出现。拦在 Life / Timer 递减**之前**，于是延迟期间它的寿命与起飞倒计时都不流逝，
            // 到点后按原有逻辑照常走——不必给每个 kind 各打一个补丁（bolt 没有 Timer 通路、ground 的 Timer 又是 tick 间隔）。
            if (effect.Delay > 0) { effect.Delay -= dt; continue; }
            effect.Life -= dt; effect.Timer -= dt;
            if (effect.Hostile)
            {
                if (effect.Kind == "projectile") effect.X += Math.Sign(Battle.PlayerX - effect.X) * Math.Min(900 * dt, Math.Abs(Battle.PlayerX - effect.X));
                if ((effect.Kind == "projectile" && Math.Abs(effect.X - Battle.PlayerX) < 20) || (effect.Kind == "target" && effect.Life <= 0))
                { HurtPlayer(effect.Damage); effect.Life = 0; }
                continue;
            }
            // 锁定型（target / 追踪弹）在这里取目标：层数对不上就当它不存在，后面各分支自然不结算。
            var target = Battle.Enemies.FirstOrDefault(e => e.Id == effect.Target && Legal(e, effect.Layer));
            switch (effect.Kind)
            {
                case "projectile":
                    // 起飞前停留（悬浮蓄势 / 浮现 / 剑阵各支的错时）：所有自定义形态共用同一个倒计时，
                    // 所以 bolt 以外的形态不必各自实现时序。bolt 与退役的 secondary == "pierce" 传 0，行为不变。
                    if (effect.Timer > 0)
                    {
                        // 肩侧/身前发射的形态（平射与平推）在停留期要**跟着施法者走**：这一秒里角色可能还在前进，
                        // 发射点若冻结在施放那一刻，等它起飞时已经被甩到角色身后——寒潮的前摇有整整 1 秒，
                        // 表现为"冲击波从画面左边冒出来"。落点类形态（sky_drop）不在此列：它的 X 是阵心，不能跟人走。
                        // 影分身那一式跟着**分身**走，不是跟着玩家：少了这个偏移，镜像会被这一行拽回本体身上、
                        // 与本体那一支完全重叠（御剑前摇 0.12 秒、寒潮 0.5 秒，都够把偏移抹掉）。
                        if (effect.Trajectory is "line_shot" or "line_pierce")
                            effect.X = Battle.PlayerX + (effect.Mirrored ? MirrorOffset : 0);
                        break;
                    }
                    // 自定义飞行形态。bolt（Trajectory 为空）不走这里，下面两条旧路径原样保留。
                    if (effect.Trajectory == "sky_drop")
                    {
                        // 垂直落下：X 在发射时已冻结（不追踪，"击中谁是谁"），落地时对落点半径内所有敌人结算，可命中重叠单位。
                        if (effect.Life > 0) break;
                        double radius = effect.AoeRadius; // 校验保证 > 0，绝不回退 ground 的 220
                        // 天降火海：带 dot 的天降不做一次性结算，改为在落点留下一片火海。
                        // 火海本身就是一个 ground 效果（寿命 = secondary_duration），于是"每 0.6 秒对半径内全体结算并刷新灼烧"
                        // 这套现成逻辑原样复用；伤害走 Damage，灼烧是它附在敌人身上的持续伤害。校验保证这里的 duration > 0。
                        if (effect.Secondary == "dot")
                        {
                            double life = effect.SecondaryDuration;
                            Effects.Add(new()
                            {
                                Kind = "ground", X = effect.X, Damage = effect.Damage, Life = life, MaxLife = life,
                                // Index 非 0：技能名标签只由本次施法的第一支负责，火海是那把剑派生出来的，不该再挂一次名字。
                                Index = 1, Skill = effect.Skill, Secondary = "dot", SecondaryValue = effect.SecondaryValue,
                                SecondaryDuration = effect.SecondaryDuration, AoeRadius = radius, Layer = effect.Layer,
                            });
                            break;
                        }
                        // 各锁一敌的剑陨（`band > 0`）：落地时**原地留一小段"剑气爆炸"的余韵**。
                        // 它是个 `Damage = 0` 的 ground 效果——所以只走寿命与绘制，一次结算也伤不到人
                        // （`HurtEnemy(e, 0)` 什么都不会改），却让爆炸有了 0→1 的进度可以画真正的扩散动画。
                        // 不留这一下的话，效果在落地当帧就被回收，表现层只剩"坠落最后一帧闪一下"。
                        if (effect.Band > 0)
                        {
                            Effects.Add(new()
                            {
                                Kind = "ground", X = effect.X, Damage = 0, Life = BurstLife, MaxLife = BurstLife,
                                Index = 1,                       // 技能名标签只由本次施法的第一支负责
                                Skill = effect.Skill, AoeRadius = radius, Layer = effect.Layer,
                            });
                        }
                        // 全体命中（aoe_all）：无视位置与半径，对这一把剑落地时的所有合法敌人各结算一次。
                        // radius 在配置里仍要求为正，但此时只作表现层的落点预警圈用。
                        if (effect.AoeAll)
                        {
                            foreach (var e in Battle.Enemies.Where(e => Legal(e, effect.Layer)).ToArray()) Hit(e, effect.Damage, effect);
                            break;
                        }
                        foreach (var e in Battle.Enemies.Where(e => Legal(e, effect.Layer) && Math.Abs(e.X - effect.X) < radius).ToArray())
                            Hit(e, effect.Damage, effect);
                        break;
                    }
                    if (effect.Trajectory == "line_shot")
                    {
                        // 平射、不追踪：命中路径上最先遇到的那个敌人即结算并销毁（与 line_pierce 的恒穿透区分开）。
                        // 用扫过区间拾取，避免一帧跨过多个身位时漏判；只取最靠前的一个，不是全部。
                        double from = effect.X;
                        effect.X += effect.Speed * dt;
                        if (effect.X > Level.Cells * Config.Setting("cell_width")) { effect.Life = 0; break; }
                        var victim = Battle.Enemies.Where(e => Legal(e, effect.Layer) && !effect.Hit.Contains(e.Id) && e.X >= from - 30 && e.X <= effect.X + 30)
                            .OrderBy(e => e.X).ThenBy(e => e.Id).FirstOrDefault();
                        if (victim is null) break;
                        effect.Hit.Add(victim.Id);
                        Hit(victim, effect.Damage, effect);
                        // 概率穿透只有第一次击中时判定一次：没穿就销毁，穿了也标记下来，之后命中一律销毁。
                        if (!effect.Pierced && effect.PierceChance > 0 && _random.NextDouble() < effect.PierceChance) effect.Pierced = true;
                        else effect.Life = 0;
                        break;
                    }
                    if (effect.Trajectory is "hover_homing" or "arc_homing")
                    {
                        if (target is null)
                        {
                            // 目标已死：**先把这一程飞完**（飞到它最后所在的位置），不半路消失。
                            // 旧写法是沿发射方向一直飘到出界，画面上是"剑芒贴着地面平行右移才没"；
                            // 但真正难看的不是这个——是表现层拿不到目标时把进度钉在弧顶，剑芒一直飘在半空。
                            effect.X += Math.Sign(effect.TargetX - effect.X) * Math.Min(effect.Speed * dt, Math.Abs(effect.TargetX - effect.X));
                            if (Math.Abs(effect.X - effect.TargetX) >= 24) break;
                            // 到落点了：**就地索敌**。换一个附近的目标接着追，弧度取反——画面上是一道反向的弧，
                            // 两段连起来像个波。找不到人就到此为止。
                            var next = ReacquireTarget(effect);
                            if (next is null) { effect.Life = 0; break; }
                            effect.Target = next.Id;
                            effect.LegX = effect.X;          // 新的一段从落点起算，第二段弧才不会跳
                            effect.Arc = -effect.Arc;        // 反向弧度：波浪的由来
                            effect.Reacquired++;
                            // 新的一段要有自己的寿命，否则总航程超过原寿命、剑芒在第二段半路被回收。
                            double leg = Math.Abs(next.X - effect.X) / Math.Max(1, effect.Speed) + .2;
                            effect.Life = effect.MaxLife = leg;
                            break;
                        }
                        effect.TargetX = target.X;   // 记住目标最后的位置，供上面那条分支用
                        effect.X += Math.Sign(target.X - effect.X) * Math.Min(effect.Speed * dt, Math.Abs(target.X - effect.X));
                        if (Math.Abs(effect.X - target.X) < 24) { Hit(target, effect.Damage, effect); effect.Life = 0; }
                        break;
                    }
                    if (effect.Pierce)
                    {
                        // 穿透：沿前进方向飞行，命中沿途每个敌人一次（用扫过区间避免单帧跳过）。
                        // line_pierce（御剑平射）与退役配置的 secondary == "pierce" 共用这一段。
                        double from = effect.X;
                        effect.X += effect.Speed * dt;
                        if (effect.X > Level.Cells * Config.Setting("cell_width")) { effect.Life = 0; break; }
                        foreach (var e in Battle.Enemies.Where(e => Legal(e, effect.Layer) && !effect.Hit.Contains(e.Id) && e.X >= from - 30 && e.X <= effect.X + 30).ToArray())
                        { effect.Hit.Add(e.Id); Hit(e, effect.Damage, effect); }
                        break;
                    }
                    if (target is null) { effect.Life = 0; break; }
                    effect.X += Math.Sign(target.X - effect.X) * Math.Min(effect.Speed * dt, Math.Abs(target.X - effect.X));
                    if (Math.Abs(effect.X - target.X) < 24) { Hit(target, effect.Damage, effect); effect.Life = 0; }
                    break;
                case "target":
                    if (effect.Life > 0) break;
                    // 目标已死就落点重判定——不然 15~30 秒冷却的大招（落雷 / 斩鬼）会白白打空。
                    if (target is not null) Hit(target, effect.Damage, effect);
                    else FallbackHit(effect);
                    break;
                case "ground":
                    if (effect.Timer <= 0)
                    {
                        effect.Timer = .6;
                        double radius = effect.AoeRadius > 0 ? effect.AoeRadius : 220;
                        foreach (var e in Battle.Enemies.Where(e => Legal(e, effect.Layer) && Math.Abs(e.X - effect.X) < radius).ToArray()) Hit(e, effect.Damage, effect);
                    }
                    break;
                case "summon":
                    effect.X = Battle.PlayerX + 110;
                    if (effect.Timer <= 0) { effect.Timer = 1; var next = Target(1100); if (next is not null) Launch("projectile", next, effect.Damage, 2, false, executeThreshold: effect.ExecuteThreshold, skill: effect.Skill); }
                    break;
            }
        }
        Effects.RemoveAll(e => e.Life <= 0);
    }
    // 命中结算：应用斩杀与吸血，再附带次级效果状态；易伤在 HurtEnemy 内统一处理。
    private void Hit(EnemyState e, double damage, CombatEffect fx)
    {
        // 层数不匹配就直接不结算：这是"飞行单位免疫地面技能"的最后一道防线，
        // 任何绕过选敌的调用路径（比如 aoe_all 的全场扫描）都在这里被拦住。
        if (!Legal(e) || !LayerHit(fx.Layer, e)) return;
        if (fx.ExecuteThreshold > 0 && e.Hp / e.MaxHp < fx.ExecuteThreshold) damage *= 2; // 斩杀：低于阈值气血伤害翻倍（暂定）
        // 利用状态（法术的 secondary = bonus_vs_state）：目标身上带着**任意状态**就增伤。
        // 必须在 ApplySecondary 之前读——那一句在函数末尾，会把本次施加的状态覆盖上去，晚读就会把"刚挂上的"也算成"已有的"。
        if (fx.Secondary == "bonus_vs_state" && fx.SecondaryValue > 0 && CarriesAnyState(e)) damage *= 1 + fx.SecondaryValue;
        // 来源在这里是现成的：宠物弹 / 召唤弹 / 剑罡飞剑都带着自己的 id；影分身那一份走 `DamageSource`，
        // 归到**复制它的法术**（身外身）名下，而不是被复制的这一式。
        HurtEnemy(e, damage, fx.DamageSource);
        if (damage > 0 && _lifestealUntil > 0) Battle.PlayerHp = Math.Min(MaxHp, Battle.PlayerHp + damage * _lifestealFactor); // 吸血
        // 击退：沿背离玩家的方向推开。死在本次伤害上的敌人不再后退，免得"尸体会滑动"。
        // 推离战力范围会让角色随即继续前进，正是"剑气推着敌人走"该有的结果，不需要额外处理。
        // 裂隙不可移动，绝不推它——它的位置是关卡与停步判定的锚点。
        if (fx.Knockback > 0 && e.Hp > 0 && e.Kind != "rift")
            e.X += (e.X >= Battle.PlayerX ? 1 : -1) * fx.Knockback;
        // 吸附（聚怪）：与击退互为反向的一对——它把目标朝**本效果的中心**（`fx.X`，ground 的阵心就是施放时选中的那只）
        // 拉近，而不是背离玩家推开。取 `Min` 是刻意的：不会把敌人一口气拽过中心再来回弹，**收敛到中心就停**。
        // 同样的两条护栏：死在这次伤害上的不再动（免得尸体滑动），裂隙永不移动。
        if (fx.Gather > 0 && e.Hp > 0 && e.Kind != "rift")
            e.X += Math.Sign(fx.X - e.X) * Math.Min(fx.Gather, Math.Abs(fx.X - e.X));
        ApplySecondary(e, fx);
    }
    /// <summary>
    /// 追踪弹飞完一程、原目标已死时的**落点重索敌**：在落点附近挑一个合法敌人接着追。
    /// 挑**血量最低**的而不是最近的，是为了契合"收割"——落地那一刻谁最脆就追谁。
    /// 只在 `FallbackRadius` 内找，**不改追远处**（多发齐射若都能改追远处会收敛到同一只，
    /// `Picks` 刻意"尽量不重复"就是为了铺开）；也限制改换次数，免得一只都追不到时无限接力。
    /// </summary>
    private EnemyState? ReacquireTarget(CombatEffect effect) => effect.Reacquired >= MaxReacquire ? null
        : Battle.Enemies
            .Where(e => Legal(e, effect.Layer) && Math.Abs(e.X - effect.X) < FallbackRadius)
            .OrderBy(e => e.Hp).ThenBy(e => e.Id).FirstOrDefault();

    /// <summary>
    /// 定点弹（`target` 类）目标中途死亡时的补救：在落点附近挑一个合法敌人**当场结算一次**。
    /// 它没有飞行过程、没有表现可保，所以直接打，不像追踪弹那样"飞过去再追"。
    /// 不这么做的话，落雷 / 斩鬼 这类 15~30 秒冷却的大招会白白打空。
    /// </summary>
    private void FallbackHit(CombatEffect effect)
    {
        var victim = Battle.Enemies
            .Where(e => Legal(e, effect.Layer) && Math.Abs(e.X - effect.TargetX) < FallbackRadius)
            .OrderBy(e => e.Hp).ThenBy(e => e.Id).FirstOrDefault();
        if (victim is not null) Hit(victim, effect.Damage, effect);
    }

    /// <summary>
    /// 目标身上是否带着任一状态。判定集合与 <see cref="ApplySecondary"/> 的 switch 对齐（slow / chill / stun / dot / vulnerable）。
    /// 注意 `StunUntil` 也被 `cast_root`（寒潮的全屏定身）写入，所以被定身的目标也算"有状态"——这是有意的。
    /// </summary>
    private static bool CarriesAnyState(EnemyState e) =>
        e.SlowUntil > 0 || e.ChillUntil > 0 || e.StunUntil > 0 || e.DotUntil > 0 || e.VulnerableUntil > 0;

    private void ApplySecondary(EnemyState e, CombatEffect fx)
    {
        switch (fx.Secondary)
        {
            case "slow": e.SlowUntil = fx.SecondaryDuration; e.SlowFactor = 1 - fx.SecondaryValue; break;
            // 寒冷：移速仍走 SlowUntil/SlowFactor（战斗判定只有一条路径），ChillUntil 只是多出来的状态源，
            // 供表现层把受击角色染成冰蓝。与减速互相覆盖时，谁都可能后写，故两者都按"最后一次命中"为准。
            case "chill": e.SlowUntil = fx.SecondaryDuration; e.SlowFactor = 1 - fx.SecondaryValue; e.ChillUntil = fx.SecondaryDuration; break;
            case "stun": e.StunUntil = fx.SecondaryDuration; break;
            // 灼烧：一并记下**来源法术**，供伤害统计在跳伤时归因（那条路上没有 fx 可查）。
            case "dot": e.DotUntil = fx.SecondaryDuration; e.DotDps = fx.Damage * fx.SecondaryValue; e.DotSkill = fx.DamageSource; break;
            case "vulnerable": e.VulnerableUntil = fx.SecondaryDuration; e.VulnerableFactor = 1 + fx.SecondaryValue; break;
        }
    }
    private void HurtPlayer(double amount)
    {
        if (PlayerInvincible) return;   // GM 调试开关，见 PlayerInvincible 的说明
        if (Battle.RespawnTimer > 0 || _random.NextDouble() < Config.Attr("dodge")) return;
        if (_shieldUntil > 0 && _shield > 0) { double absorbed = Math.Min(_shield, amount); _shield -= absorbed; amount -= absorbed; if (_shield <= 0) _shieldUntil = 0; }
        Battle.PlayerHp = Math.Max(0, Battle.PlayerHp - amount);
    }
    /// <summary>
    /// 扣血。**全游戏唯一给敌人造成伤害的地方**，伤害统计（<see cref="DamageStats"/>）就挂在这里。
    /// `source` 是来源技能 id（空串 = 普通攻击），只有两个生产调用方：`Hit` 传 `fx.Skill`、
    /// 灼烧跳伤传 `EnemyState.DotSkill`；测试里的直接调用不传，落到"普通攻击"那一档。
    /// </summary>
    internal void HurtEnemy(EnemyState e, double damage, string source = "")
    {
        if (!Legal(e)) return;
        if (e.VulnerableUntil > 0) damage *= e.VulnerableFactor; // 易伤：目标受击伤害提高
        double before = e.Hp;
        e.Hp = Math.Max(0, before - damage);
        // 记账必须在这里、**在易伤乘区之后**：在 `Hit` 里读 damage 会漏掉 `VulnerableFactor`，数字会偏小。
        // `Math.Max(0, …)` 已经把过量击杀吃掉了，所以有效 = before − after、溢出 = damage − 有效。
        DamageStats.Add(source, before - e.Hp, damage - (before - e.Hp));
        if (e.Hp > 0) return;
        AddCurrency("gold", Config.Monsters[e.MonsterId].Gold);
        if (e.Kind == "boss")
        {
            Battle.BossDefeated = true;
            bool first = State.FirstKills.Add(Level.Id);
            foreach (var reward in Config.Rows("drop").Where(r => r.Text("group_id") == (first ? Level.FirstReward : Level.RepeatReward)))
            {
                if (reward.Text("item_id") != "core") AddCurrency(reward.Text("item_id"), reward.Number("amount"));
            }
            // 灵核只在这一处入账，账本与余额随整个快照原子保存。
            if (first) State.Wallet["core"] = State.Amount("core") + 1;
            Message = first ? "妖王伏诛 · 首杀灵核 +1！" : "妖王伏诛 · 已领取过本关灵核。";
            _persist = true;
        }
        if (e.Kind == "rift") { Battle.PortalDestroyed = true; _persist = true; }
    }
    private void Die()
    {
        bool bossCell = Battle.Cell == Level.Cells - 1;
        Effects.Clear(); ClearBuffs();
        if (!bossCell) EnterLevel(Level.Id);
        else Battle.PlayerX = (Level.Cells - 1) * Config.Setting("cell_width") + 80;
        Battle.PlayerHp = 0;
        Battle.RespawnTimer = Config.Setting("respawn_seconds");
        Moving = false; _persist = true;
        Message = bossCell ? "气血耗尽 · 本格重生，敌人伤势保留。" : "气血耗尽 · 返回本关起点，资源全部保留。";
    }
    private void CompleteLevel()
    {
        int index = Config.Levels.FindIndex(l => l.Id == Level.Id);
        if (index + 1 < Config.Levels.Count) State.UnlockedLevels.Add(Config.Levels[index + 1].Id);
        var next = !State.LoopLevel && index + 1 < Config.Levels.Count ? Config.Levels[index + 1] : Level;
        EnterLevel(next.Id); Message = "裂隙已破 · 抵达 " + next.Name; _persist = true;
    }
    private void EnterLevel(string id)
    {
        State.Battle = new() { LevelId = id, PlayerHp = MaxHp, PlayerX = 80 };
        Effects.Clear(); ClearBuffs();
    }
    public bool SelectLevel(string id)
    {
        if (!State.UnlockedLevels.Contains(id)) return Say("尚未解锁此关。");
        State.LoopLevel = true; EnterLevel(id); return Changed("已切换为整关循环。");
    }
    public void ToggleLoop() { State.LoopLevel = !State.LoopLevel; Changed(State.LoopLevel ? "正在循环本关。" : "已开启自动推进。"); }
    private bool Say(string message) { Message = message; return false; }
    private bool Changed(string message) { Message = message; PersistRequested?.Invoke(); return true; }
    private void AddCurrency(string id, double amount)
    {
        if (id == "core") throw new InvalidOperationException("灵核只能由关卡首杀发放");
        State.Wallet[id] = State.Amount(id) + amount;
    }

    /// <summary>
    /// GM 调试入口：为 item.csv 中每一种货币（kind = currency）各增加固定数量。
    /// 遍历配置表而非硬编码名单，因此新增货币后无需改动此处即可自动纳入发放范围。
    /// 这是开发调试通道，不属于五大玩法系统，也不是任何奖励组的产出路径；
    /// 灵核的玩法产出口仍然只有「关卡首杀」一处，此方法不写入首杀账本（FirstKills），
    /// 而是把发放量记入 DebugGranted，使存档校验能区分调试产出与篡改。
    /// </summary>
    public bool GrantAllCurrencies(double amount = 10000)
    {
        var currencies = Config.Rows("item").Where(r => r.Text("kind") == "currency").ToList();
        if (currencies.Count == 0) return Say("配置中没有可发放的货币。");
        foreach (var r in currencies)
        {
            string id = r.Text("id");
            State.Wallet[id] = State.Amount(id) + amount;
            State.DebugGranted[id] = State.DebugGranted.GetValueOrDefault(id) + amount;
        }
        return Changed($"GM 调试 · {currencies.Count} 种货币各 +{amount:0}");
    }
}
