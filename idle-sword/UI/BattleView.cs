using Godot;
using IdleSword.Core;
using IdleSword.Features;

namespace IdleSword.UI;

/// <summary>
/// 战斗表现适配器。像素占位精灵统一 64×64，底部中心锚点，素材替换不改变战斗判定。
/// 表现层只读不写：命中反馈由血条差分推断，状态图标直接读取 EnemyState 的公开字段，
/// 不在 Core 里添加任何仅供显示的状态。
/// </summary>
public partial class BattleView : Control
{
    /// <summary>玩家在屏幕上的基准 x。正常玩法里它同时也是"镜头原点"，所以玩家永远钉在这里。</summary>
    public const float PlayerAnchor = 330f;

    public GameSession Session { get; set; } = null!;

    // ── 镜头与玩家位置的**分离**（序章要用）────────────────────────────────
    // 原先 `PlayerX` 同时兼任"玩家的世界位置"和"镜头原点"两件事：世界坐标靠 `- PlayerX` 平移，
    // 而玩家自己被写死在屏幕 330 上。序章要求"镜头锁死、土豆在画面里走"，这两件事必须能分开。
    // 下面四个覆盖量**默认全为 null / 与旧值相等**，此时 CameraX == PlayerX、
    // PlayerScreenX == 330、MovingNow == Session.Moving、MotionClock == Session.Elapsed，
    // 每一处表达式都退化回改动前的写法——正常玩法的渲染因此逐像素不变（冒烟里有断言钉住）。
    /// <summary>镜头原点覆盖。null = 跟随玩家（默认，等于旧行为）。</summary>
    public float? CameraOverride { get; set; }
    /// <summary>玩家的屏幕 x。默认为 <see cref="PlayerAnchor"/>。</summary>
    public float PlayerScreenX { get; set; } = PlayerAnchor;
    /// <summary>离地高度：只抬本体，影子留在地面线，于是读成"悬空/下落"。</summary>
    public float PlayerAirHeight { get; set; }
    /// <summary>步伐开关覆盖。序章不推进会话，但要走出台步与扬尘，所以需要从外面给。</summary>
    public bool? MovingOverride { get; set; }
    private float CameraX => CameraOverride ?? (float)Session.Battle.PlayerX;
    private bool MovingNow => MovingOverride ?? Session.Moving;
    // 步伐节拍用哪个时钟：正常玩法必须留在 Session.Elapsed（模拟时间），序章才切到渲染时钟。
    private double MotionClock => MovingOverride is null ? Session.Elapsed : _clock;
    // 落地挤压的一次性脉冲时刻。
    private double _landAt = double.NegativeInfinity;
    /// <summary>
    /// 资源缺失记录。Godot 会吞掉 _Ready 中抛出的异常（转换为控制台错误而不中断进程），
    /// 因此缺图不能靠抛异常暴露——那会让"资源没导入"表现为"游戏照常跑但画面空白"。
    /// 这里显式记录，交给启动流程提示，并由 QA 自检断言。
    /// </summary>
    public string? LoadError { get; private set; }
    /// <summary>命中信号，参数为是否重击。音频由 Main 订阅后播放，表现层只负责把差分结果报出去。</summary>
    public event Action<bool>? HitLanded;
    /// <summary>敌人死亡信号。死亡的敌人已在同一次 Step 里被移除，血量差分看不到 Hp&lt;=0，只能靠"从列表消失"判定。</summary>
    public event Action? EnemyDefeated;
    // 元素配色：风青碧、雷金电、霜冰蓝、炎赤橙、太虚紫、血赤。与 UiKit 的靛青底/暖金强调共存。
    private static readonly Color Wind = new("#9fdcc4"), Thunder = new("#f5ea9a"), Frost = new("#9ed4ee"),
        Flame = new("#ff8a3d"), Void = new("#b49ae0"), Blood = new("#d96a72"), Hostile = new("#e99885");
    private readonly Dictionary<string, Texture2D> _textures = [];
    // _lastHp 用于逐帧差分出"命中"，_popups 为飘字，_flash 记录受击闪动截止时刻。
    private readonly Dictionary<long, double> _lastHp = [], _flash = [];
    private readonly List<Popup> _popups = [];
    // ── 手感表演（juice）的瞬时状态：只影响绘制，不参与任何判定，也不写存档 ──
    // 上一帧的玩家血量，差分出"挨打了"。回血（归元、吸血）不算，只有掉血才闪；NaN = 本场还没采样过。
    private double _lastPlayerHp = double.NaN;
    // 受击白闪的截止时刻。
    private double _playerFlash;
    // 落地扬尘。列表封顶，免得长时间挂机堆到几千个。
    private readonly List<Puff> _puffs = [];
    // 上一次迈步的相位，用来在相位穿过 0（跨出半步）时冒一次尘。
    private double _lastStepPhase;

    /// <summary>落地扬尘。纯表现，带一点重力和淡出。</summary>
    private sealed class Puff
    {
        public float X, Y, Vx, Vy, Size;
        public double Life, MaxLife;
    }
    // 上一帧看到的 BattleState 对象。换场判定必须用对象身份，见 TrackHits 的说明。
    private BattleState? _lastBattle;
    private double _clock;
    // 上一次观测到的模拟时钟，用来算"这一帧到底模拟了多少秒"，见 TrackHits 与 AddPopup。
    private double _lastElapsed;
    // 召唤/跟随单位占用的位次。Core 每步把召唤物的 X 统一钉回玩家身后（那只是逻辑锚点），
    // 具体站在哪、怎么错开由表现层分配，见 AssignCompanionSlots。
    private readonly Dictionary<CombatEffect, int> _companionSlots = [];

    private sealed class Popup
    {
        public long Id; public bool Burn; public float X; public double Total, Life, MaxLife;
        public Color Color = Colors.White; public int Size = 22;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore; ClipContents = true;
        TextureFilter = TextureFilterEnum.Nearest;
        var manifest = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(Godot.FileAccess.GetFileAsString("res://Assets/visuals.json"))
            ?? throw new InvalidDataException("美术资源映射为空");
        var missing = new List<string>();
        foreach (var (id, path) in manifest)
        {
            var texture = GD.Load<Texture2D>(path);
            if (texture is null) missing.Add(path); else _textures[id] = texture;
        }
        // 怪物与剑灵的 `visual` 是 CSV 里的自由文本，GameConfig **不校验**它——资源清单的知识属于表现层，
        // 不该塞进 Core。不查的话，ID 写错只会"画面上少一只怪"，静默无报错；这里补上，
        // 让它变成启动期错误（冒烟测试对 LoadError 直接抛异常）。
        if (Session is not null)
        {
            foreach (var monster in Session.Config.Monsters.Values)
                if (!_textures.ContainsKey(monster.Visual))
                    missing.Add($"monster {monster.Id} 的 visual '{monster.Visual}'");
            foreach (var row in Session.Config.Rows("Pet"))
                if (!_textures.ContainsKey(row.Text("visual")))
                    missing.Add($"pet {row.Text("id")} 的 visual '{row.Text("visual")}'");
        }
        if (missing.Count > 0) LoadError = "缺少美术资源（未导入、路径错误或 visuals.json 里没有该 ID）: " + string.Join(", ", missing);
    }
    /// <summary>世界坐标 → 屏幕坐标。镜头跟随玩家时（默认），它等于改动前的 `330 + (world - PlayerX)`。</summary>
    private float X(double world) => PlayerScreenX + (float)(world - CameraX);
    /// <summary>同上，公开给自检断言"默认路径没被改动"。见 <see cref="CameraOverride"/> 的说明。</summary>
    public float ScreenX(double world) => X(world);
    public override void _Process(double delta)
    {
        TrackHits(delta);
        TrackPlayerJuice(delta);
        AssignCompanionSlots();
        QueueRedraw();
    }
    /// <summary>清空飘字与受击记录。切换预览目标时调用，避免上一场的残留浮在画面上。</summary>
    public void ResetTransient()
    {
        _lastHp.Clear(); _flash.Clear(); _popups.Clear(); _companionSlots.Clear();
        _puffs.Clear(); _playerFlash = 0; _lastPlayerHp = double.NaN;
    }

    /// <summary>
    /// 玩家一侧的手感表演：受击白闪 + 迈步扬尘。
    /// **只读** <c>PlayerHp</c> 与 <c>Moving</c> 做差分，不回写任何玩法数据——表现层不碰 Core 的状态。
    /// </summary>
    private void TrackPlayerJuice(double delta)
    {
        double hp = Session.Battle.PlayerHp;
        // 只有掉血才闪：归元与吸血都是回血，闪起来会让玩家以为挨打了。
        if (!double.IsNaN(_lastPlayerHp) && hp < _lastPlayerHp) _playerFlash = _clock + .12;
        _lastPlayerHp = hp;

        // 迈步扬尘：复用移动时那个 sin 相位（见 _Draw 的 bob），每跨出半步（相位过零）在脚下冒一小撮。
        double phase = MovingNow ? Math.Sin(MotionClock * 14) : 0;
        if (MovingNow && _lastStepPhase != 0 && Math.Sign(phase) != Math.Sign(_lastStepPhase) && _puffs.Count < 40)
            for (int i = 0; i < 2; i++)
                _puffs.Add(new Puff
                {
                    X = PlayerScreenX + (float)(Random.Shared.NextDouble() * 44 - 22),
                    Y = 366,
                    Vx = (float)(Random.Shared.NextDouble() * 40 - 50),   // 前进方向身后
                    Vy = (float)(-Random.Shared.NextDouble() * 26 - 6),
                    Size = (float)(Random.Shared.NextDouble() * 6 + 5),
                    Life = .38, MaxLife = .38,
                });
        _lastStepPhase = phase;

        foreach (var puff in _puffs)
        {
            puff.Life -= delta;
            puff.X += puff.Vx * (float)delta;
            puff.Y += puff.Vy * (float)delta;
            puff.Vy += 46 * (float)delta;   // 一点重力：扬起来再落回去
        }
        _puffs.RemoveAll(p => p.Life <= 0);
    }

    /// <summary>
    /// 召唤/跟随单位当前占用的位次。站位完全由表现层决定（Core 只把它们钉在玩家身后的
    /// 逻辑锚点上，站位不影响伤害），公开此只读视图仅供自检核对"多个单位确实错开"。
    /// </summary>
    public IReadOnlyDictionary<CombatEffect, int> CompanionSlots => _companionSlots;

    /// <summary>
    /// 累计判定为灼烧跳伤的次数。公开仅供自检核对判据——跳伤被误判成命中时，
    /// 声音本身可能被音效抑制机制吃掉而看不出来，这个计数不会。
    /// </summary>
    public int BurnTicks { get; private set; }

    /// <summary>
    /// 给每个召唤/跟随单位分配一个稳定位次。新单位补当前最小的空位，已有单位不会因为
    /// 别人到期而整体挪位——那会让画面上的单位无端平移。位次最大不超过并发召唤数。
    /// </summary>
    private void AssignCompanionSlots()
    {
        var live = Session.Effects.Where(e => e.Kind == "summon").ToHashSet();
        foreach (var gone in _companionSlots.Keys.Where(e => !live.Contains(e)).ToArray()) _companionSlots.Remove(gone);
        var used = _companionSlots.Values.ToHashSet();
        foreach (var effect in live)
        {
            if (_companionSlots.ContainsKey(effect)) continue;
            int slot = 0;
            while (used.Contains(slot)) slot++;
            used.Add(slot);
            _companionSlots[effect] = slot;
        }
    }

    /// <summary>
    /// 召唤/跟随单位的站位（屏幕坐标，玩家恒在 x=330）。
    /// 左右交替、逐层向外并抬高，避免所有单位叠在同一点，也避免全堆在同一侧——
    /// 单侧一字排开在单位变多时会甩出画面。slot 越大越靠外，之后的跟随单位沿用这套位次即可。
    /// </summary>
    public static (float X, float Y) FollowerSlot(int slot)
    {
        int ring = slot / 2;
        // 左侧让得近、右侧让得开：玩家朝右，身后更空，身前要避开挥剑与弹丸的主轴。
        float dx = (slot % 2 == 0 ? -96f : 116f) + (slot % 2 == 0 ? -1f : 1f) * ring * 92;
        return (330 + dx, 250 - ring * 46);
    }

    /// <summary>
    /// 头顶剑阵位次（悬浮形态用）：以玩家(x=330)为基准水平错开、中央略高，多支剑不重叠。
    /// 与 FollowerSlot 同样是纯函数：只吃序号与总数，不含随机、不读会话状态，因此可被自检断言。
    /// 站位只是绘制偏移，Core 里每支剑的 X 仍是各自锁定的逻辑锚点，不参与命中判定。
    /// </summary>
    public static (float X, float Y) HoverSlot(int index, int count)
    {
        float spacing = Math.Min(38f, 300f / Math.Max(1, count - 1));
        float dx = Math.Clamp((index - (count - 1) / 2f) * spacing, -300f, 300f);
        // 越靠中央抬得越高：读起来像一列斜张的剑阵，重叠的两支也自然分出层次。
        float lift = 1 - Math.Abs(dx) / 300f;
        return (330 + dx, Math.Clamp(198 - lift * 14 - index % 2 * 6, 10f, 230f));
    }

    /// <summary>天降剑的起飞高度：按序号分层，避免同一落点的多支剑完全重叠。落点 X 由 Core 冻结，表现层不改。</summary>
    public static float SkyDropTop(int index) => 10 + index % 5 * 20;

    /// <summary>
    /// 平射剑的排列位次（御剑）：以玩家为原点，身前身后交替、同侧逐层上抬，横向间距由 spread 配置。
    /// 纯函数，只吃序号／总数／间距，可被自检断言；y 落在肩部一带（268 上下），不遮挡下方界面。
    /// </summary>
    public static (float X, float Y) VolleySlot(int index, int count, float spread)
    {
        float toward = index % 2 == 0 ? 1f : -1f;          // 偶数位在身前、奇数位在身后
        float rank = index / 2;
        // 身后偏低、身前偏高，同侧再逐层上抬：只按 rank 错高会挤在一条线上，看不出是"排开的一列剑"。
        float y = 258 - rank * 26 + (index % 2 == 0 ? 0f : 18f);
        return (330 + toward * (26 + rank * Math.Max(12f, spread)), y);
    }

    /// <summary>
    /// 剑形多边形：按给定角度手算旋转。本文件刻意不用 DrawSetTransform——
    /// 变换状态会残留给后续的 DrawString / 精灵绘制，且手算版是纯函数、可被自检断言。
    /// 角度 0 为剑尖朝右，+90° 为剑尖朝下。剑格另用 DrawBlade 画一条横档。
    /// </summary>
    public static Vector2[] BladePolygon(float cx, float cy, double angle, float length, float width)
    {
        float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
        Vector2 At(float along, float across) => new(cx + along * cos - across * sin, cy + along * sin + across * cos);
        float tip = length * .5f, tail = -length * .5f, w = width * .5f;
        return [At(tip, 0), At(tip - length * .3f, -w), At(tail, -w * .5f), At(tail, w * .5f), At(tip - length * .3f, w)];
    }

    /// <summary>取出某效果的形态参数（数量／停留／飞行时长／排列间距／出生高度随机）。查不到配置时按单发直线处理。</summary>
    private (int Count, double Hold, double Duration, double Spread, double SpawnJitter) ShapeOf(CombatEffect effect)
        => Session.Config.Skills.TryGetValue(effect.Skill, out var s)
            ? (s.ProjectileCount, s.HoverTime, s.Duration, s.Spread, s.SpawnJitter) : (1, 0d, effect.MaxLife, 0d, 0d);

    /// <summary>
    /// 飞行进度按"逻辑 X 走了多远"算，而不是按寿命：逻辑飞行远快于 duration，
    /// 按寿命算会让剑在命中前看起来还没飞到位（命中当帧 effect 就被移除了）。
    /// </summary>
    private float FlightProgress(CombatEffect effect, float x, float from)
    {
        var target = Session.Battle.Enemies.FirstOrDefault(e => e.Id == effect.Target);
        // 目标死了就飞向**它最后所在的位置**（"沿原轨道把这一程飞完"）。
        // 旧写法这里回退成 `x + 200`，于是进度被顶到 ≈0.95、剑芒被钉在弧顶附近贴着高空平移，
        // 飘到落点再凭空消失——正是"在半空中消失了"。Core 那边会飞完这一程再落点重索敌。
        float to = target is null ? X(effect.TargetX) : X(target.X);
        return Math.Clamp((x - from) / Math.Max(1, to - from), 0, 1);
    }

    /// <summary>
    /// 判断一次掉血是否属于灼烧跳伤。阈值按"这段时间里模拟了多少秒"算，不能用真实帧间隔——
    /// 跳伤是按固定步长成块结算的（每步 DotDps*fixed_step），而真实帧间隔通常小于固定步长，
    /// 用帧间隔做阈值会低于单步跳伤，判据永不成立，于是每一步都播一次命中音。
    /// </summary>
    public static bool IsBurnTick(EnemyState enemy, double damage, double simSeconds)
        => enemy.DotUntil > 0 && damage <= enemy.DotDps * simSeconds * 1.5;
    /// <summary>模拟层不发命中事件，表现层用血量差分还原命中、飘字与受击闪动。</summary>
    private void TrackHits(double delta)
    {
        _clock += delta;
        // 换场守卫用 BattleState 对象身份，不能用关卡 id：死亡回到起点和整关循环都会回到同一个关卡 id，
        // id 守卫挡不住，会把 _lastHp 里所有旧 id 当成击杀、刷一波死亡音。
        if (!ReferenceEquals(_lastBattle, Session.Battle))
        {
            _lastBattle = Session.Battle;
            ResetTransient();
            _lastElapsed = Session.Elapsed;
        }
        // 本帧实际模拟了多少秒。一帧可能跑 0 个也可能跑很多个固定步，必须按模拟时钟算，
        // 用真实帧间隔会把灼烧判据算错，见 IsBurnTick。
        double simSeconds = Math.Max(0, Session.Elapsed - _lastElapsed);
        _lastElapsed = Session.Elapsed;
        bool hitPlayed = false;
        foreach (var enemy in Session.Battle.Enemies)
        {
            if (!_lastHp.TryGetValue(enemy.Id, out double previous)) { _lastHp[enemy.Id] = enemy.Hp; continue; }
            _lastHp[enemy.Id] = enemy.Hp;
            double damage = previous - enemy.Hp;
            if (damage <= 0) continue;
            _flash[enemy.Id] = _clock + .12;
            bool burn = AddPopup(enemy, damage, simSeconds);
            if (burn) BurnTicks++;
            // 灼烧跳伤不出声：每步只掉一点点，逐帧播会变成机关枪。
            if (burn) continue;
            bool heavy = damage >= enemy.MaxHp * .12;
            // 每帧最多一次普通命中音，重击放行：范围技能同时打多个目标时也不会糊成一片。
            if (!heavy && hitPlayed) continue;
            hitPlayed = true;
            HitLanded?.Invoke(heavy);
        }
        bool announced = false;
        foreach (long id in _lastHp.Keys.Where(id => !Session.Battle.Enemies.Any(e => e.Id == id)).ToArray())
        {
            _lastHp.Remove(id); _flash.Remove(id);
            // 同一帧死多个只报一次，避免叠成一片。
            if (announced) continue;
            announced = true;
            EnemyDefeated?.Invoke();
        }
        foreach (var popup in _popups) popup.Life -= delta;
        _popups.RemoveAll(p => p.Life <= 0);
    }
    /// <summary>返回是否为灼烧跳伤，供调用方决定要不要播命中音。</summary>
    private bool AddPopup(EnemyState enemy, double damage, double simSeconds)
    {
        // 灼烧是每帧连续小额伤害，逐帧出字会刷屏，故同一目标的灼烧伤害合并成一个跳字。
        bool burn = IsBurnTick(enemy, damage, simSeconds);
        if (burn)
        {
            var merged = _popups.FirstOrDefault(p => p.Id == enemy.Id && p.Burn);
            if (merged is not null) { merged.Total += damage; merged.Life = merged.MaxLife; return burn; }
        }
        double ratio = damage / Math.Max(1, enemy.MaxHp);
        _popups.Add(new Popup
        {
            Id = enemy.Id, Burn = burn, X = (float)enemy.X, Total = damage,
            Life = burn ? .7 : .85, MaxLife = burn ? .7 : .85,
            Color = burn ? Flame : ratio >= .12 ? UiKit.Gold : UiKit.Text,
            Size = burn ? 18 : ratio >= .12 ? 30 : 22,
        });
        if (_popups.Count > 60) _popups.RemoveAt(0);
        return burn;
    }
    public override void _Draw()
    {
        if (Session is null) return;
        // ── 背景配色：整体压暗降饱和，把暖白色的主角从画面里"拽"出来 ──
        // 旧配色（天空 #253f50 / 山 #2f5060）和旧主角的青绿几乎同色相同明度，两边互相糊在一起，
        // 这是"画面不好看"的真正原因，不只是人不对。下面这一档刻意比实体暗一大截。
        DrawRect(new(0, 0, 1920, 440), new Color("#0f1c26"));
        DrawCircle(new(1560, 86), 38, new Color("#d9cfad"));
        // 远景使用分层整数坐标多边形，摄像机滚动不影响世界坐标和攻击距离。
        // 层数从 3 减到 2：少一层就少一条亮边，山只当轮廓看，不跟主角抢注意力。
        for (int layer = 0; layer < 2; layer++)
        {
            var color = new Color[] { new("#1b2c38"), new("#131f28") }[layer];
            float scroll = (float)(CameraX * (.025 + layer * .025) % 700);
            for (int i = -1; i < 5; i++)
            {
                float x = i * 700 - scroll;
                DrawColoredPolygon([new(x, 350), new(x + 70, 245 - layer * 8), new(x + 150, 245 - layer * 8), new(x + 300, 80 + layer * 45), new(x + 340, 80 + layer * 45), new(x + 560, 300), new(x + 720, 350)], color);
            }
        }
        // 树减到 8 棵（间距 180 → 360），只留剪影。
        for (int i = -1; i < 7; i++)
        {
            float x = i * 360 - (float)(CameraX * .32 % 360);
            DrawRect(new(x + 22, 202, 8, 167), new Color("#17282f"));
            for (int j = 0; j < 4; j++)
                DrawColoredPolygon([new(x - 35, 240 + j * 23), new(x + 26, 177 + j * 23), new(x + 92, 240 + j * 23)], new Color("#1c3038"));
        }
        DrawRect(new(0, 365, 1920, 75), new Color("#0a141a"));
        // 地面上沿原来用 #748772，是全场最亮的背景元素，直接把主角比下去；压成一条安静的暗地平线。
        DrawRect(new(0, 364, 1920, 5), new Color("#4a6663"));
        for (int i = -1; i < 45; i++)
        {
            float x = i * 52 - (float)(CameraX % 52);
            DrawRect(new(x, 379 + i % 3 * 9, 23, 4), new Color("#1b2f3a"));
        }
        DrawVignette();
        var font = GetThemeDefaultFont();
        foreach (var (cell, spawn) in Session.Battle.Spawns)
        {
            float x = X(cell * Session.Config.Setting("cell_width") + Session.Config.Rows("spawn_point")[0].Number("offset"));
            if (x < -100 || x > 2000) continue;
            DrawRect(new(x, 292, 5, 73), new Color("#8a7a52"));
            DrawRect(new(x + 5, 297, 35, 27), spawn.Passed ? new Color("#3d5a55") : new Color("#75684a"));
            DrawString(font, new(x - 20, 394), spawn.Passed ? "已越过" : $"刷怪点 {cell + 1}", HorizontalAlignment.Left, -1, 18, UiKit.Muted);
        }
        // 诛仙的"天光尽墨"要盖住角色、敌人与已有的地面效果，所以铺在这里；神剑本身稍后才画，因此是亮的。
        DrawEclipse();
        // 地面持续效果（剑阵/领域）画在角色与敌人之下，避免盖住血条。
        foreach (var effect in Session.Effects.Where(e => e.Kind == "ground" && !e.Hostile)) DrawEffect(effect, font);
        float bob = MovingNow ? (float)Math.Sin(MotionClock * 14) * 3 : 0;
        // 影分身（身外身）：先画本体身后那个半透明分身，再画本体，保证本体压在上面。
        // 位置取固定偏移而不是 FollowerSlot：FollowerSlot 是给召唤单位"左右交替、逐层向外"用的，
        // 分身只有一个、且必须恒定在本体正后方，用它会随位次左右跳。
        if (Session.MirrorRemaining > 0)
        {
            float cloneX = PlayerScreenX - 96;
            DrawEllipseShadow(cloneX, 366, 52);
            Sprite("player", cloneX, 368 + bob - PlayerAirHeight, 136, new Color(Void, .38f));
            // 分身手上有一式待发时，在它身上叠一圈剑气光。target / ground / sky_drop 三类没有发射点，
            // 光靠"晚 0.18 秒的第二下"不容易联想到是分身放的——这一圈光把因果关系点明，且与待发状态天然同步。
            if (Session.Effects.Any(e => e.Mirrored && e.Delay > 0))
            {
                float glow = 1 - (float)(Session.Effects.First(e => e.Mirrored && e.Delay > 0).Delay / GameSession.MirrorDelay);
                DrawArc(new(cloneX, 330), 34 + glow * 26, 0, Mathf.Tau, 24, new Color(Void, .55f * (1 - glow)), 4);
            }
        }
        DrawPuffs();
        DrawEllipseShadow(PlayerScreenX, 366, 65);
        if (Session.Battle.RespawnTimer <= 0)
        {
            // 挤压拉伸绕**底部中心**做：中心 x 与底边都不动，所以视觉位置和命中判定都不受影响
            // （锚点就是底部中心，见 assets/asset_spec.md）。squash > 0 = 拉高变瘦，< 0 = 压矮变宽。
            float squash = MovingNow ? (float)Math.Sin(MotionClock * 14) * .05f
                                     : (float)Math.Sin(MotionClock * 2.2) * .02f;
            squash += LandingSquash();
            float sy = 1 + squash, sx = 1 - squash * .7f;
            // 悬空只抬本体的底边，影子（上一行）留在地面线上——"抬角色、钉影子"才读成下落。
            Sprite("player", PlayerScreenX, 368 + bob - PlayerAirHeight, 136, null, sx, sy);
            // 受击白闪：叠一层**同轮廓的纯白图**。项目跑在 gl_compatibility（LDR）下，顶点色乘不过 1，
            // 深色像素提不亮，靠 modulate 做不出全白的土豆——所以让 SpriteGen 顺带生成 player_flash。
            double flashLeft = _playerFlash - _clock;
            if (flashLeft > 0)
                Sprite("player_flash", PlayerScreenX, 368 + bob - PlayerAirHeight, 136, new Color(1, 1, 1, (float)(flashLeft / .12)), sx * 1.06f, sy * 1.06f);
        }
        else DrawString(font, new(220, 290), "调息重生…", HorizontalAlignment.Left, -1, 26, UiKit.Gold);
        DrawPlayerAuras();
        DrawPhantom();
        int pi = 0;
        foreach (var pet in Session.State.EquippedPets)
        {
            float px = 220 + pi * 65, py = 226 + (float)Math.Sin(Session.Elapsed * 2 + pi) * 10;
            Sprite(Session.Config.Row("Pet", pet).Text("visual"), px, py, 70); pi++;
        }
        // 仅限制可见精灵数量，不删除怪物，不改变任何伤害和刷怪逻辑。
        var visible = Session.Battle.Enemies.Where(e => e.Hp > 0 && X(e.X) > -120 && X(e.X) < 2070).OrderBy(e => e.Kind == "boss" || e.Kind == "rift" ? 0 : 1).Take((int)Session.Config.Setting("enemy_visual_limit"));
        foreach (var enemy in visible)
        {
            float x = X(enemy.X), size = enemy.Kind switch { "boss" => 194, "rift" => 174, "elite" => 125, _ => 96 };
            // 飞行单位浮空：影子仍压在地面线上，所以"飞着"一眼可辨。血条、名字与状态图标都是按 y 算的，自动跟着走。
            float y = 368;
            if (Session.Config.Monsters[enemy.MonsterId].Layer == "air") y -= 92 + (float)Math.Sin(Session.Elapsed * 2 + enemy.Id) * 7;
            else if (enemy.Kind == "rift") y -= (float)Math.Sin(Session.Elapsed * 2) * 7;
            DrawEllipseShadow(x, 368, size * (Session.Config.Monsters[enemy.MonsterId].Layer == "air" ? .30f : .48f));
            // 受击瞬间放大 12% 并叠加高光，代替无法在 LDR 下实现的白色闪光。
            bool hit = _flash.TryGetValue(enemy.Id, out double until) && until > _clock;
            if (hit) size *= 1.12f;
            // 寒冷先于裂隙封印判定：被寒气侵住的敌人整只染成冰蓝，一眼可辨（减速冰环仍照常由 SlowUntil 画出）。
            var tint = enemy.ChillUntil > 0 ? new Color(.55f, .74f, 1f)
                : enemy.Kind == "rift" && !Session.RiftUnlocked ? new Color(.6f, .6f, .7f) : Colors.White;
            Sprite(Session.Config.Monsters[enemy.MonsterId].Visual, x, y, size, tint);
            if (hit) DrawCircle(new(x, y - size * .5f), size * .34f, new Color(1, 1, 1, .16f));
            DrawEnemyStatus(enemy, x, y, size);
            DrawRect(new(x - 42, y - size - 6, 84, 5), UiKit.Ink);
            DrawRect(new(x - 42, y - size - 6, 84 * (float)(enemy.Hp / enemy.MaxHp), 5), enemy.Kind == "boss" ? Hostile : UiKit.Jade);
            if (enemy.Kind is "boss" or "rift") DrawString(font, new(x - 65, y - size - 16), enemy.Kind == "rift" && !Session.RiftUnlocked ? "裂隙 · 封印中" : Session.Config.Monsters[enemy.MonsterId].Name, HorizontalAlignment.Left, -1, 20, UiKit.Gold);
        }
        // 召唤物不走通用标签：它们的站位由位次分配，标签要跟着单位走，见 DrawSummons。
        foreach (var effect in Session.Effects.Where(e => e.Kind is not ("ground" or "summon"))) DrawEffect(effect, font);
        DrawSummons(font);
        DrawPopups(font);
        // 战场左上的题词。原来是「剑问长生」（旧标题的余韵），跟着标题一起换；「一路」呼应主角一路向右。
        DrawString(font, new(32, 42), "青山不语   ·   一路长生", HorizontalAlignment.Left, -1, 23, new Color("#c0d2c9"));
        DrawString(font, new(32, 73), Session.Moving ? "前行中 · 尚未越过的刷怪点仍会补怪" : "交战中 · 清空攻击范围后继续前行", HorizontalAlignment.Left, -1, 18, UiKit.Muted);
    }

    /// <summary>按法术 ID 分流：每个技能用不同的形状、配色与节奏，使 15 个法术在画面上可辨认。</summary>
    private void DrawEffect(CombatEffect effect, Font font)
    {
        float x = X(effect.X);
        if (x < -1200 || x > 3200) return;
        // 还没到出场时刻的效果不画：影分身那一式要晚 0.18 秒才出现，先画出来会是一支僵在原地的剑。
        if (effect.Delay > 0) return;
        switch (effect.Kind)
        {
            case "projectile": DrawProjectile(effect, x); break;
            case "ground": DrawGround(effect, x); break;
            case "target": DrawTarget(effect, x); break;
        }
        // 技能名标签只由本次施法的第一支负责：多发法术每支都画会叠成一串糊字。
        // 影分身同步放出的那一份不再挂一次名字：同一式会叠出两个标签，糊成一片。
        if (effect.Index == 0 && !effect.Mirrored && Session.Config.Skills.TryGetValue(effect.Skill, out var skill) && effect.MaxLife > 0 && effect.Life > effect.MaxLife - .7)
        {
            var label = UiKit.Text; label.A = (float)Math.Clamp((effect.MaxLife - effect.Life) / .7, 0, 1) * .92f;
            DrawString(font, new(x - 130, 222), skill.Name, HorizontalAlignment.Center, 260, 18, label);
        }
    }
    private void DrawProjectile(CombatEffect effect, float x)
    {
        if (effect.Hostile) { DrawRect(new(x - 26, 289, 46, 4), Hostile); DrawRect(new(x - 3, 275, 4, 17), Hostile); return; }
        // 先按飞行形态分流，再回落到按法术 ID 的绘制：同类形态的新法术不需要再加分支。
        switch (effect.Trajectory)
        {
            case "hover_homing": DrawHoverBlade(effect, x); return;
            case "sky_drop": DrawSkyDropBlade(effect, x); return;
            case "arc_homing": DrawArcBlade(effect, x); return;
            case "line_shot": DrawPierceBlade(effect, x, false); return;
            case "line_pierce":
                // 同样是 line_pierce，两招的读法不同：寒潮是贴地的弧形剑气、天剑是横空斩出的巨剑，
                // 其余仍走原来的平射贯穿剑。形态只决定"怎么飞、怎么命中"，观感由技能 ID 分流。
                // （扎根原先是第三支 line_pierce，改成落在目标位置的聚怪力场后走 DrawGround。）
                if (effect.Skill == "skill_05") DrawGroundWave(effect, x);
                else if (effect.Skill == "skill_17") DrawHeavenSword(effect, x);
                else DrawPierceBlade(effect, x, true);
                return;
        }
        switch (effect.Skill)
        {
            default: // 剑灵弹丸等未指定形态的直线弹道
                DrawRect(new(x - 26, 289, 46, 4), UiKit.Jade); DrawRect(new(x - 3, 275, 4, 17), UiKit.Gold); break;
        }
    }

    /// <summary>画一支剑：剑身多边形 + 剑格横档。角度 0 为剑尖朝右、+90° 为朝下。</summary>
    private void DrawBlade(float x, float y, double angle, float length, Color blade, Color guard)
    {
        DrawColoredPolygon(BladePolygon(x, y, angle, length, Math.Max(3f, length * .17f)), blade);
        float gx = (float)Math.Cos(angle), gy = (float)Math.Sin(angle);
        float at = -length * .2f, arm = Math.Max(4f, length * .17f);
        DrawLine(new(x + gx * at - gy * arm, y + gy * at + gx * arm), new(x + gx * at + gy * arm, y + gy * at - gx * arm), guard, Math.Max(2f, length * .08f));
    }

    /// <summary>
    /// 御剑：肩侧浮现后平射。`line_shot` 命中即散，只拉一小段拖尾；`line_pierce` 是恒穿透形态，
    /// 保留贯穿全屏的剑光读法。两者共用肩侧排列与错时（VolleySlot + volley_interval）。
    /// </summary>
    private void DrawPierceBlade(CombatEffect effect, float x, bool pierce)
    {
        var shape = ShapeOf(effect);
        var (ox, oy) = VolleySlot(effect.Index, shape.Count, (float)shape.Spread);
        if (effect.Timer > 0)   // 浮现期：在各自肩位上逐渐显形，尚未射出
        {
            float fade = (float)Math.Clamp(1 - effect.Timer / Math.Max(.01, shape.Hold), .25f, 1f);
            var glow = Wind; glow.A = fade;
            DrawBlade(ox, oy + (float)Math.Sin(_clock * 6 + effect.Index) * 2, 0, 62, glow, new Color(UiKit.Gold, fade));
            return;
        }
        // 飞行期：y 固定在肩部高度。
        float y = oy + (float)Math.Sin(_clock * 6 + effect.Index) * 2;
        if (pierce) DrawRect(new(x - 900, y - 1, 900, 3), new Color(Wind, .10f));   // 贯穿剑光：恒穿透才有
        DrawLine(new(x - (pierce ? 120 : 54), y), new(x, y), new Color(Wind, pierce ? .45f : .3f), pierce ? 4 : 3);
        DrawBlade(x, y, 0, 68, Wind, UiKit.Gold);
    }

    /// <summary>
    /// 天剑：一柄巨剑**横空斩出**，贯穿一整排。刻意做成横向的大剑，而不是"自上而下的一戳"——
    /// 与斩鬼（自斜上方斩落**一只**）分开，也比御剑的肩侧小剑大一圈、慢一截，带一条长刀光。
    /// </summary>
    private void DrawHeavenSword(CombatEffect effect, float x)
    {
        float fade = effect.MaxLife > 0 ? (float)Math.Clamp(effect.Life / effect.MaxLife, 0, 1) : 1;
        const float y = 300;                        // 与敌人身体同高：横向扫过去才"斩到人"
        const float len = 200;                      // 御剑的剑身是 68，这里大一圈
        // 起手（hover_time）在身前蓄势：由小涨大；起飞后才是全尺寸的长刀光。
        float grow = effect.Timer > 0
            ? Math.Clamp(1 - (float)(effect.Timer / Math.Max(.01, ShapeOf(effect).Hold)), .3f, 1f)
            : 1f;
        DrawLine(new(x - 300 * grow, y), new(x - 90 * grow, y), new Color(Void, .30f * fade), 20);
        DrawLine(new(x - 330 * grow, y), new(x - 90 * grow, y), new Color("#f4efff", .45f * fade), 5);
        DrawBlade(x, y, 0, len * grow, new Color(Void, .35f + .5f * fade), UiKit.Gold);
    }

    /// <summary>头顶悬浮后追踪（当前无技能使用，形态保留在词汇表里供日后配置）。</summary>
    private void DrawHoverBlade(CombatEffect effect, float x)
    {
        var shape = ShapeOf(effect);
        var (hx, hy) = HoverSlot(effect.Index, shape.Count);
        float bob = (float)Math.Sin(_clock * 3 + effect.Index) * 3;
        if (effect.Timer > 0)   // 悬浮期：停在各自的剑阵位次上
        {
            DrawBlade(hx, hy + bob, .62, 62, Wind, UiKit.Gold);
            DrawRect(new(hx - 30, hy + 24, 22, 2), new Color(Wind, .3f));
            return;
        }
        float t = FlightProgress(effect, x, 330);
        float px = hx + (x - hx) * t, py = hy + (296 - hy) * t + bob * (1 - t);
        double angle = Math.Atan2(296 - hy, Math.Max(1, x - hx));
        DrawLine(new(px - (float)Math.Cos(angle) * 44, py - (float)Math.Sin(angle) * 44), new(px, py), new Color(Wind, .3f), 3);
        DrawBlade(px, py, angle, 68, Wind, UiKit.Gold);
    }

    /// <summary>
    /// 万剑：固定剑阵从敌人上空垂直落剑。落点 X 已由 Core 按间距算进 effect.X（表现层不再自己偏移，
    /// 否则会出现"看着没打中却掉血"），这里只负责出生高度、空中停留、下落与落点预警。
    /// </summary>
    private void DrawSkyDropBlade(CombatEffect effect, float x)
    {
        if (effect.Skill == "skill_18") { DrawMeteorBlade(effect, x); return; }   // 天陨有自己的一整套读法
        var shape = ShapeOf(effect);
        float radius = (float)(effect.AoeRadius > 0 ? effect.AoeRadius : 60);
        // 妖火与诛仙复用同一套下落编排，只换配色与剑的尺寸：
        // 前者是一柄火焰剑落地成火海（后续由 DrawGround 的 skill_02 分支接手），后者是四柄结成剑阵的诛仙剑。
        bool flame = effect.Skill == "skill_02", divine = effect.Skill == "skill_15";
        Color hue = flame ? Flame : divine ? Void : Thunder;
        // 出生高度带随机高低差；随机值由 Core 摇定一次，逐帧重摇会抖动。
        float top = SkyDropTop(effect.Index) + (float)(effect.Jitter * shape.SpawnJitter);
        // 停留与下落共用 Timer：Timer 大于 duration 时还没开始落，clamp 到 0 即为"悬停中"。
        float fall = (float)Math.Clamp(1 - effect.Timer / Math.Max(.05, shape.Duration), 0, 1);
        float y = Math.Clamp(top + (356 - top) * fall * fall, 6f, 394f);   // 平方：越接近地面越快
        DrawEllipseFloor(x, radius, new Color(hue, .08f + .14f * fall));
        DrawRect(new(x - 1, y, 2, Math.Max(0, 356 - y)), new Color(hue, .12f));
        if (fall > .85f) DrawEllipseFloor(x, radius * .7f, new Color(flame ? "#ffe0a8" : "#fffce8", .18f));
        // 悬停期轻微上下浮动，读起来是"浮现在空中停了一下"而不是卡住。
        float bob = effect.Timer > 0 ? (float)Math.Sin(_clock * 4 + effect.Index * 1.7) * 4 : 0;
        DrawBlade(x, y + bob, Math.PI / 2, divine ? 116 : 72, hue, UiKit.Gold);
    }

    /// <summary>
    /// 天陨：天上先浮出一个**黑洞**，剑锋自洞中慢慢探出、停一拍，再加速坠下，落地炸开一圈冲击环。
    /// 与诛仙共用 `sky_drop` 的时序（`hover_time` = 探出 + 停顿，`duration` = 下落），读法却完全不同：
    /// 诛仙是四道天光同时压下来，这里是"从黑洞里一柄柄射出去"。
    /// **生成带模式**下，黑洞开在天上那条宽带的**出生点**上，而剑要斜落到窄带里的落点——于是飞行过程中
    /// 位置在"出生点 → 落点"之间插值，朝向**沿飞行轨迹**（不另摇随机倾角：剑尖扫出画面外就是这么来的）。
    /// 判定始终是落点 `x`（Core 算进 effect.X），表现层只负责画得像"收束过去"。
    /// </summary>
    private void DrawMeteorBlade(CombatEffect effect, float x)
    {
        var shape = ShapeOf(effect);
        float radius = (float)(effect.AoeRadius > 0 ? effect.AoeRadius : 60);
        // 出生点：生成带模式给的是真实出生 X，其余情况与落点重合（原地垂直落下）。
        float sx = effect.SpawnX != 0 ? X(effect.SpawnX) : x;
        float top = SkyDropTop(effect.Index) + (float)(effect.Jitter * shape.SpawnJitter);
        float fall = (float)Math.Clamp(1 - effect.Timer / Math.Max(.05, shape.Duration), 0, 1);
        // 立方而不是平方：越接近地面越快，读作"被黑洞甩出去"，与万剑的匀速垂落区分开。
        float y = Math.Clamp(top + (356 - top) * fall * fall * fall, 6f, 394f);
        float bx = sx + (x - sx) * fall;             // 横向：一半时间走完一半路程会觉得"晚拐弯"，所以与纵向同步
        float emerge = (float)Math.Clamp(1 - effect.Timer / Math.Max(.05, shape.Hold), 0, 1);
        // 朝向 = 本支的飞行方向（出生点 → 落点）。落点与出生点重合时退回垂直，也就是万剑那种直落。
        double angle = Math.Abs(x - sx) < 1 ? Math.PI / 2 : Math.Atan2(356 - top, x - sx);
        float hole = 1 - fall;                       // 剑一下落，黑洞就随之散去
        DrawCircle(new(sx, top), 26, new Color("#0b0716", .92f * hole));
        DrawCircle(new(sx, top), 26, new Color(Void, .55f * hole));
        for (int i = 0; i < 3; i++)
        {
            float a = (float)(_clock * 2.6 + i * Mathf.Tau / 3);
            DrawArc(new(sx, top), 17 + i * 3, a, a + 2.1f, 10, new Color("#c9a8ff", .55f * hole), 2);
        }
        if (effect.Timer > 0)   // 探出期：剑从洞里长出来，还没射出去
        {
            float len = 20 + 92 * emerge;
            DrawBlade(sx, top + len * .5f, angle, len, new Color("#efe6ff", .5f + .5f * emerge), UiKit.Gold);
            return;
        }
        DrawLine(new(sx, top), new(bx, y), new Color(Void, .18f), 6);   // 出膛的拖尾，随剑一路收向落点
        DrawBlade(bx, y, angle, 96, new Color("#f4efff", .95f), UiKit.Gold);
        DrawEllipseFloor(x, radius, new Color(Void, .10f + .16f * fall));
        if (fall > .82f)        // 触地：一圈扩散的冲击环，把"炸开"这一下点出来
        {
            DrawEllipseFloor(x, radius * (.4f + .6f * fall), new Color("#ffe9c8", .20f * fall));
            DrawArc(new(x, 356), radius * (.5f + .5f * fall), 0, Mathf.Tau, 40, new Color("#fff6e0", .35f * fall), 3);
        }
    }

    /// <summary>
    /// 寒潮：一道**紧贴地面**的弧形刀光整体向前平移（不是肩侧平射，也不拉贯穿全屏的剑光）。
    /// 形状取新月：后缘自左下贴地起、扬起一条长曲线到尖端，前缘贴着尖端收回来，
    /// 中间填半透明的虚空剑气、前缘描亮，底下再拖一层尘土——读作"一道立起来的刀光在平推"，
    /// 而不是一个对称的鼓包。
    /// </summary>
    private void DrawGroundWave(CombatEffect effect, float x)
    {
        const float Rise = 190;   // 尖端离地高度
        // 新月由两条共享端点的二次贝塞尔围成：一端贴地、一端是尖端，两条边朝相反方向鼓，
        // 同参数处的间距就是新月在那里的厚度。leading = 前缘（描亮的那条）。
        Vector2[] Edge(float cx, float scale, bool leading)
        {
            // 整道刀光贴着 effect.X 摆：Core 用 X 判定命中，表现层不得再自己前移，
            // 否则会出现"看着没碰到却掉血"。只把体型收在 X 前后各一小段，读作"从角色身前推出去"。
            var a = new Vector2(cx - 44 * scale, 356);
            var b = new Vector2(cx + 56 * scale, 356 - Rise * scale);
            var c = leading ? new Vector2(cx + 30 * scale, 356 - Rise * .30f * scale)
                            : new Vector2(cx - 86 * scale, 356 - Rise * .58f * scale);
            var points = new Vector2[17];
            for (int i = 0; i < points.Length; i++)
            {
                float t = i / (float)(points.Length - 1), u = 1 - t;
                points[i] = new(u * u * a.X + 2 * u * t * c.X + t * t * b.X, u * u * a.Y + 2 * u * t * c.Y + t * t * b.Y);
            }
            return points;
        }
        void Sail(float cx, float scale, float alpha)
        {
            var back = Edge(cx, scale, false);
            var front = Edge(cx, scale, true);
            var shape = new Vector2[back.Length + front.Length];
            back.CopyTo(shape, 0);
            for (int i = 0; i < front.Length; i++) shape[back.Length + i] = front[front.Length - 1 - i];
            DrawColoredPolygon(shape, new Color(Void, .30f * alpha));
            DrawPolyline(front, new Color(Void, .75f * alpha), Math.Max(2f, 8 * scale));      // 外发光
            DrawPolyline(front, new Color("#f4efff", .95f * alpha), Math.Max(1f, 3 * scale)); // 亮芯
        }
        if (effect.Timer > 0)   // 出生：在角色身前由小涨大，走完前摇才整道推出去
        {
            float charge = (float)Math.Clamp(1 - effect.Timer / Math.Max(.01, ShapeOf(effect).Hold), 0f, 1f);
            // 出生点摆在角色身前（角色精灵到 398 为止），比发射点靠前半个身位；
            // 长到满尺寸时它自己的横向跨度已覆盖 x，所以起飞那一刻不会"跳"回去。
            float px = x + 46;
            Sail(px, .15f + .70f * charge, .25f + .6f * charge);
            DrawCircle(new(px - 26, 348), 4 + charge * 10, new Color(Void, .2f + .5f * charge));
            return;
        }
        for (int i = 3; i >= 1; i--) Sail(x - i * 52, .82f, .10f * (4 - i));   // 拖尾：整道刀光的残影
        Sail(x, 1f, 1f);
        DrawEllipseFloor(x - 24, 104, new Color("#e6e0d6", .16f));             // 贴地拖起的一层尘
    }

    /// <summary>
    /// 扎根：立在目标位置上的**聚怪力场**。位置就是 Core 算好的 effect.X（= 施放时选中那只的 X），
    /// 表现层不得再自己偏移。风眼是一道细龙卷，地面上是一圈**朝里转**的旋纹——它是把敌人卷进来、
    /// 而不是推出去，所以旋纹的走向必须向心，散场时越转越淡，不会突然消失。
    /// </summary>
    private void DrawIceField(CombatEffect effect, float x, float radius, float progress)
    {
        const float Bottom = 358;
        float fade = 1 - progress;
        // 地面霜痕：只在力场范围内，别抢走风眼的注意力。
        DrawEllipseFloor(x, radius * .68f, new Color(Frost, .18f * fade));
        // 向心旋纹：三条螺线各绕一圈半、半径由外向内收，读作"往里卷"——
        // 与寒潮向外推的新月正好相反，这两招因此不会看混。
        for (int arm = 0; arm < 3; arm++)
        {
            var spiral = new Vector2[28];
            for (int j = 0; j < spiral.Length; j++)
            {
                float t = j / (float)(spiral.Length - 1);
                float r = radius * .70f * (1 - t);
                float a = (float)(_clock * 2.4 + arm * Mathf.Tau / 3 + t * 5.6);
                spiral[j] = new(x + (float)Math.Cos(a) * r, Bottom - 5 + (float)Math.Sin(a) * r * .22f);
            }
            DrawPolyline(spiral, new Color(Frost, .34f * fade), 3);
            DrawPolyline(spiral, new Color("#e8f7ff", .20f * fade), 1);
        }
        // 风眼：下粗上细的螺旋筒壁，比原先推着走的龙卷矮一点、粗一点，读作"驻在这里卷"。
        float top = Bottom - radius * .88f;
        for (int i = 0; i < 8; i++)
        {
            float t = i / 7f;
            float y = Bottom - t * (Bottom - top);
            float width = 20 + 54 * (1 - t);
            float spin = (float)(_clock * 9 - i * .9);
            var wall = new Vector2[9];
            for (int j = 0; j < wall.Length; j++)
            {
                float phase = j / (float)(wall.Length - 1);
                wall[j] = new(x + (float)Math.Sin(spin + phase * Mathf.Tau * 1.5) * width * .5f, y - phase * 22);
            }
            DrawPolyline(wall, new Color(Frost, (.26f + .10f * (1 - t)) * fade), 5);
            DrawPolyline(wall, new Color("#e8f7ff", .40f * fade), 2);
        }
        // 被卷进来的冰点：沿同一条螺线由外向内走，越接近风眼越快——"被往里拽"全靠它读出来。
        for (int i = 0; i < 9; i++)
        {
            float t = (float)((_clock * .55 + i / 9.0) % 1.0);
            float r = radius * .70f * (1 - t);
            float a = (float)(_clock * 2.4 + i * 2.3 + t * 5.6);
            DrawRect(new(x + (float)Math.Cos(a) * r - 2, Bottom - 8 + (float)Math.Sin(a) * r * .22f, 4, 4),
                new Color("#e8f7ff", (.20f + .7f * t) * fade));
        }
        // 风眼中心一点冷光，让"聚到哪"一眼可见。
        DrawCircle(new(x, Bottom - 14), radius * .16f, new Color(Frost, .30f * fade));
    }

    /// <summary>
    /// 须芒：一道能量流光从角色前方划弧飞出。弧高取 Core 摇定的 effect.Arc（表现层不重摇，否则逐帧抖动），
    /// 允许为负——负值时从下方掠过（上方空间多、下方少，区间由配置给出）。
    /// 夹取：上到 250（弧顶 = 发射高度 300 − 弧高，再高会顶出战斗区）、下到地面线以上（356），不越界也不压下方界面。
    /// </summary>
    private void DrawArcBlade(CombatEffect effect, float x)
    {
        var shape = ShapeOf(effect);
        // 每支各自扇开（纵向 + 横向）：同打一个目标时逻辑 X 相同，不错开就会叠在同一条弧上、数不出几支。
        // 横向偏移只有 ±spread 上下，仍在敌人身上，因此画面与"打在谁身上"不矛盾。
        float lane = (effect.Index - (shape.Count - 1) / 2f) * (float)shape.Spread;
        // 这一段弧的起点：第一段是肩侧（逻辑起点再让 52 的身位），**重新索敌之后就是落点本身**——
        // 否则第二段还在拿发射点算进度，画面会跳一下。
        float from = X(effect.LegX) + (effect.Reacquired == 0 ? 52f : 0f) + lane * .5f;
        float origin = 300 + (effect.Index - (shape.Count - 1) / 2f) * 16;
        // 弧度取**对称**区间：重新索敌时 Core 会把弧度取反，若还按原来的 [-50, 250] 夹，
        // 反向那一段会被压平（比如 +140 取反成 -140，夹到 -50 就只剩一点点）。
        float arc = Math.Clamp((float)effect.Arc, -250f, 250f);
        if (effect.Timer > 0)   // 蓄势期：起点处一点逐渐变亮的光芒，还没射出去
        {
            float charge = (float)Math.Clamp(1 - effect.Timer / Math.Max(.01, shape.Hold), 0f, 1f);
            DrawCircle(new(from, origin), 3 + charge * 4, new Color(Void, .25f + .45f * charge));
            return;
        }
        float t = FlightProgress(effect, x + lane, from);
        float px = from + (x + lane - from) * t;
        float py = Math.Clamp(origin - arc * (float)Math.Sin(Math.PI * t), 6f, 350f);
        double angle = Math.Atan2(-arc * Math.PI * Math.Cos(Math.PI * t), Math.Max(60, x + lane - from));
        DrawArcLight(effect.Index, from, origin, px, py, angle, arc, t);
    }

    /// <summary>
    /// 画"剑芒"：不是实体剑，而是一道能量流光——沿轨迹拖尾取若干采样点，
    /// 先铺一层半透明外发光、再叠一条亮芯（照抄 DrawLightning 的双描边法，本文件没有 shader/bloom 可用）。
    /// </summary>
    private void DrawArcLight(int index, float from, float origin, float px, float py, double angle, float arc, float t)
    {
        const int Steps = 7;
        var spine = new Vector2[Steps + 1];
        for (int i = 0; i <= Steps; i++)
        {
            float back = Math.Max(0, t - i * .035f);   // 往回采样：拖尾贴着已经飞过的那段弧
            float bx = from + (px - from) * (t <= 0 ? 0 : back / t);
            spine[i] = new(bx, Math.Clamp(origin - arc * (float)Math.Sin(Math.PI * back), 6f, 350f));
        }
        DrawPolyline(spine, new Color(Void, .30f), 9);                    // 外发光
        DrawPolyline(spine, new Color("#e8dcff", .85f), 3);               // 亮芯
        DrawCircle(new(px, py), 7, new Color(Void, .55f));                // 头部光晕
        DrawCircle(new(px, py), 3, new Color("#fdfbff", .95f));
        // 顺着弧线的一点方向感：头部前方再补一小段更淡的流光
        DrawLine(new(px, py), new(px + (float)Math.Cos(angle) * 14, py + (float)Math.Sin(angle) * 14), new Color("#e8dcff", .35f), 2);
    }
    /// <summary>
    /// 天陨的落地爆炸：内核闪一下 + 一圈扩散的冲击环 + 一圈**放射状的剑气**（沿圆周甩出去又收细）。
    /// `progress` 由 Core 给的寿命算出 0→1，所以这是真正的"炸开"而不是坠落最后一帧闪一下。
    /// 落点 X 与半径都取自 Core（`effect.X` / `AoeRadius`），表现层不自己偏移。
    /// </summary>
    private void DrawBladeBurst(float x, float radius, float progress)
    {
        float fade = 1 - progress;
        // 内核：前 15% 之内由亮转暗，读作"炸点"而不是"一片光"。
        float core = (float)Math.Clamp(1 - progress / .15f, 0, 1);
        DrawCircle(new(x, 350), radius * (.18f + .30f * core), new Color("#fff8e2", .55f * core + .10f * fade));
        // 冲击环：扩散到满半径再随淡化收掉。
        float ring = radius * (.35f + .65f * progress);
        DrawArc(new(x, 356), ring, 0, Mathf.Tau, 40, new Color("#fff1cf", .5f * fade), 4);
        DrawArc(new(x, 356), ring * .72f, 0, Mathf.Tau, 40, new Color(Void, .35f * fade), 2);
        // 剑气：一圈短刃从炸点沿圆周甩出去，越远越细、越淡。
        for (int i = 0; i < 10; i++)
        {
            float a = i * Mathf.Tau / 10 + (float)(_clock * .4);
            float outer = radius * (.45f + .75f * progress);
            float inner = outer * .55f;
            var from = new Vector2(x + (float)Math.Cos(a) * inner, 352 + (float)Math.Sin(a) * inner * .3f);
            var to = new Vector2(x + (float)Math.Cos(a) * outer, 352 + (float)Math.Sin(a) * outer * .3f);
            DrawLine(from, to, new Color("#fdf3d8", .55f * fade), Math.Max(1f, 4f * fade));
        }
        // 地面上再压一层暖光，让"炸到哪一圈"一眼可见。
        DrawEllipseFloor(x, radius * (.5f + .5f * progress), new Color(Flame, .16f * fade));
    }

    private void DrawGround(CombatEffect effect, float x)
    {
        float radius = (float)(effect.AoeRadius > 0 ? effect.AoeRadius : 220);
        float progress = effect.MaxLife > 0 ? (float)(1 - effect.Life / effect.MaxLife) : 0;
        switch (effect.Skill)
        {
            case "skill_18": // 天陨落地：剑气爆炸的余韵（0→1 的进度驱动，见 GameSession.BurstLife）
                DrawBladeBurst(x, radius, progress);
                break;
            case "skill_12": // 扎根：落在目标位置的聚怪力场（向心旋纹 + 驻留的风眼）
                DrawIceField(effect, x, radius, progress);
                break;
            case "skill_02": // 妖火：火焰剑落地 → 一片小火海（落点半径 120，火苗因此更稀）
                DrawEllipseFloor(x, radius, new Color(Flame, .22f));
                for (int i = 0; i < 10; i++)
                {
                    float fx = x - radius + i * (radius * 2 / 9);
                    float h = 14 + (float)Math.Sin(_clock * 9 + i * 1.7) * 8;
                    DrawColoredPolygon([new(fx - 6, 358), new(fx, 358 - h), new(fx + 6, 358)], new Color(i % 3 == 0 ? new Color("#ffd98a") : Flame, .8f));
                }
                // 开头约 0.4 秒叠画一柄坠下的火焰剑：火海是这把剑落地才成的，不是凭空冒出来的。
                if (progress < .14f)
                {
                    float fall = progress / .14f;
                    float y = Math.Clamp(10 + (356 - 10) * fall * fall, 6f, 394f);
                    DrawRect(new(x - 1, y, 2, Math.Max(0, 356 - y)), new Color(Flame, .14f));
                    DrawBlade(x, y, Math.PI / 2, 72, Flame, UiKit.Gold);
                }
                break;
            default:
                DrawEllipseFloor(x, radius, new Color(UiKit.Jade, .18f));
                for (int j = 0; j < 7; j++) DrawRect(new(x - radius * .9f + j * (radius * .3f), 322, 3, 31), new Color(UiKit.Jade, .7f));
                break;
        }
        // 收束环：点数要够多，少于 16 个点会画成明显的多边形。
        DrawArc(new(x, 356), radius * (0.9f + progress * .1f), 0, Mathf.Tau, 40, new Color(Colors.White, .14f), 1);
    }
    private void DrawTarget(CombatEffect effect, float x)
    {
        float progress = effect.MaxLife > 0 ? (float)(1 - effect.Life / effect.MaxLife) : 1;
        // 敌方招式的落点标记画在玩家身上，所以跟着玩家屏幕位置走（序章里玩家会离开基准位）。
        if (effect.Hostile) { DrawArc(new(PlayerScreenX, 340), 38, 0, Mathf.Tau, 24, Hostile, 3); DrawLine(new(PlayerScreenX, 170), new(PlayerScreenX, 306), Hostile, 2); return; }
        switch (effect.Skill)
        {
            case "skill_07": // 落雷：天雷贯顶——主落雷之外再叠两道细弧，读起来比单体小技能重得多
                DrawLightning(x, 96, 348, Thunder);
                DrawLightning(x - 34, 150, 340, new Color(Thunder, .5f));
                DrawLightning(x + 34, 150, 340, new Color(Thunder, .5f));
                DrawArc(new(x, 350), 30 + progress * 52, 0, Mathf.Tau, 30, new Color(Thunder, .85f), 4);
                break;
            case "skill_10": // 斩鬼：巨剑自目标右上浮现，斜着**斩**过去——剑尖扫过目标，拖一道刀光
            {
                const float bladeLen = 170;                                 // 剑身长度：得够得着目标（柄距目标约 164）
                float wind = Math.Clamp(progress / .3f, 0f, 1f);            // 前摇：把剑亮出来
                float swing = Math.Clamp((progress - .3f) / .4f, 0f, 1f);   // 斩：0 → 1
                // 剑柄在目标的右上（"身后"侧），斩落时略微下沉；剑尖方向 = "柄→目标"再偏 ±0.6 弧度。
                // **这个 ±0.6 的扫动才是"斩"**：剑尖走一条穿过目标的弧。两个极端都试过了——
                // 固定角度朝上 ⇒"一把剑飘在敌人头上指着天"；恒定指着目标 ⇒ 变成平移捅过去（"戳"）。
                float hx = x + 70 - 18 * swing, hy = 140 + 26 * swing;
                double baseAngle = Math.Atan2(288 - hy, x - hx);
                double angle = baseAngle + .6 - 1.2 * swing;
                DrawRect(new(hx - 2, hy, 4, Math.Max(0, 350 - hy)), new Color(Void, .08f));
                DrawBlade(hx + (float)Math.Cos(angle) * bladeLen * .5f, hy + (float)Math.Sin(angle) * bladeLen * .5f,
                    angle, bladeLen, new Color(Void, .55f + .45f * wind), UiKit.Gold);
                // 剑身刀光：从剑柄到剑尖一条亮线，挥到哪亮到哪。
                DrawLine(new(hx, hy), new(hx + (float)Math.Cos(angle) * bladeLen, hy + (float)Math.Sin(angle) * bladeLen),
                    new Color("#f4efff", .8f), 4);
                if (swing > 0)
                {
                    // 刀光：剑尖这一路扫过的弧，中段最强——**"斩"的意味全在这条弧上**。
                    var edge = new Vector2[13];
                    for (int i = 0; i < edge.Length; i++)
                    {
                        float at = swing * i / (edge.Length - 1);
                        float tx = x + 70 - 18 * at, ty = 140 + 26 * at;
                        double ta = Math.Atan2(288 - ty, x - tx) + .6 - 1.2 * at;
                        edge[i] = new(tx + (float)Math.Cos(ta) * bladeLen, ty + (float)Math.Sin(ta) * bladeLen);
                    }
                    float glow = (float)Math.Sin(Math.PI * swing);
                    DrawPolyline(edge, new Color(Void, .45f * glow), 16);
                    DrawPolyline(edge, new Color("#f4efff", .55f * glow), 5);
                }
                if (swing >= 1)
                {
                    // 斩到底：那道斜杠是**击中效果**，要落在敌人身上，不是挂在天上——
                    // 以敌人（x, 约 288）为中心的一道短斜线，配一圈冲击环。方向与剑尖的扫向一致（左上→右下）。
                    float left = 1 - Math.Clamp((progress - .7f) / .3f, 0f, 1f);
                    var (sx, sy) = (x - 104, 218);
                    var (ex, ey) = (x + 96, 358);
                    DrawLine(new(sx, sy), new(ex, ey), new Color(Void, .5f * left), 16);
                    DrawLine(new(sx, sy), new(ex, ey), new Color("#f4efff", .85f * left), 5);
                    DrawArc(new(x, 340), 40 + (1 - left) * 60, 0, Mathf.Tau, 30, new Color(Void, .8f * left), 4);
                }
                // 利用状态（secondary = bonus_vs_state）：目标身上带着状态时，多画一圈"破绽"环，
                // 让"这一剑吃到了加成"看得见。判断放在表现层（直接读那个目标的状态字段），Core 不需要新增标记。
                if (Session.Battle.Enemies.FirstOrDefault(e => e.Id == effect.Target) is { } marked
                    && (marked.SlowUntil > 0 || marked.ChillUntil > 0 || marked.StunUntil > 0 || marked.DotUntil > 0 || marked.VulnerableUntil > 0))
                {
                    float pulse = 1 - Math.Clamp(Math.Abs(progress - .5f) * 2f, 0f, 1f);
                    DrawArc(new(x, 300), 46 + pulse * 16, 0, Mathf.Tau, 28, new Color(UiKit.Gold, .7f), 3);
                }
                break;
            }
            default:
                DrawArc(new(x, 340), 38, 0, Mathf.Tau, 24, UiKit.Jade, 3); DrawLine(new(x, 170), new(x, 306), UiKit.Jade, 2); break;
        }
    }
    /// <summary>
    /// 召唤物：三种剑灵各用一张素材，区别于此前全部复用同一张占位图。
    /// 站位取自位次而不是 effect.X——Core 把召唤物的 X 统一钉在玩家身后，
    /// 直接用会让所有召唤物精确重叠（见 FollowerSlot）。
    /// </summary>
    private void DrawSummons(Font font)
    {
        foreach (var effect in Session.Effects.Where(e => e.Kind == "summon"))
        {
            int slot = _companionSlots.GetValueOrDefault(effect);
            var (x, y) = FollowerSlot(slot);
            if (x < -160 || x > 2080) continue;
            // 各槽错开相位，否则多个单位同步浮动，看着像同一张图。
            y += (float)Math.Sin(_clock * 3 + slot * 1.3) * 7;
            // 当前没有在役的 summon 法术（剑侍也已归档），这几条映射服务于退役配置与自检造的伪实体；
            // 留着是为了「退役配置日后复活」时不必再补一次。
            string sprite = effect.Skill switch { "skill_05" => "summon_shadow", "skill_10" => "summon_taixu", "skill_15" => "summon_zhuxie", "skill_16" => "summon_taixu", _ => "pet_snow" };
            var tint = effect.Skill switch { "skill_05" => Wind, "skill_10" => Void, "skill_15" => UiKit.Gold, "skill_16" => Wind, _ => UiKit.Jade };
            DrawEllipseFloor(x, 56, new Color(tint, .16f));
            Sprite(sprite, x, y, 82);
            if (Session.Config.Skills.TryGetValue(effect.Skill, out var skill) && effect.MaxLife > 0 && effect.Life > effect.MaxLife - .7)
            {
                var label = UiKit.Text; label.A = (float)Math.Clamp((effect.MaxLife - effect.Life) / .7, 0, 1) * .92f;
                DrawString(font, new(x - 130, y - 138), skill.Name, HorizontalAlignment.Center, 260, 18, label);
            }
            if (effect.Skill == "skill_15" && effect.ExecuteThreshold > 0)
                DrawString(font, new(x - 60, y - 96), "斩杀", HorizontalAlignment.Center, 120, 16, UiKit.Gold);
        }
    }
    /// <summary>敌人身上的次级效果图标：减速冰环、眩晕电弧、灼烧火苗、易伤裂痕。</summary>
    private void DrawEnemyStatus(EnemyState enemy, float x, float y, float size)
    {
        if (enemy.SlowUntil > 0) DrawArc(new(x, 360), size * .44f, 0, Mathf.Tau, 26, new Color(Frost, .75f), 3);
        if (enemy.StunUntil > 0)
        {
            DrawLightning(x, y - size - 58, y - size - 6, Thunder);
            for (int i = 0; i < 3; i++)
            {
                float angle = (float)(_clock * 5 + i * Mathf.Tau / 3);
                DrawRect(new(x + (float)Math.Cos(angle) * 24 - 2, y - size - 30 + (float)Math.Sin(angle) * 7, 4, 4), Thunder);
            }
        }
        if (enemy.DotUntil > 0)
            for (int i = 0; i < 4; i++)
            {
                float fx = x - 18 + i * 12, h = 15 + (float)Math.Sin(_clock * 12 + i) * 8;
                DrawColoredPolygon([new(fx - 5, y - 4), new(fx, y - 4 - h), new(fx + 5, y - 4)], new Color(Flame, .8f));
            }
        if (enemy.VulnerableUntil > 0)
        {
            DrawLine(new(x - 16, y - size * .58f), new(x + 8, y - size * .34f), Hostile, 2);
            DrawLine(new(x + 8, y - size * .52f), new(x - 10, y - size * .2f), Hostile, 2);
        }
    }
    /// <summary>
    /// 诛仙的"天光尽墨"：整片战斗区压暗。取所有诛仙剑效果里「开头快速升起、结尾渐隐」的最大值，
    /// 于是四柄剑错时落下的整段时间里天色都是暗的，而不是每柄剑各暗一次、中间回亮。
    /// </summary>
    private void DrawEclipse()
    {
        double veil = 0;
        foreach (var effect in Session.Effects.Where(e => e.Skill == "skill_15" && e.MaxLife > 0))
        {
            double rise = Math.Clamp((1 - effect.Life / effect.MaxLife) / .25, 0, 1);
            double fall = Math.Clamp(effect.Life / .3, 0, 1);
            veil = Math.Max(veil, Math.Min(rise, fall));
        }
        if (veil > 0) DrawRect(new(0, 0, 1920, 440), new Color("#04060b", (float)(.84 * veil)));
    }

    /// <summary>
    /// 斩鬼的虚影：半身持剑的能量体浮在主角身上，先渐显、剑光落下时散去。
    /// 纯表现——它不改任何数值，也不产生 CombatEffect；只要场上还有斩鬼的效果就跟着画。
    /// </summary>
    private void DrawPhantom()
    {
        var strike = Session.Effects.FirstOrDefault(e => e.Skill == "skill_10" && e.MaxLife > 0);
        if (strike is null) return;
        double p = Math.Clamp(1 - strike.Life / strike.MaxLife, 0, 1);
        // 前 70% 渐显并稳住、后 30% 散去；两段之间取小，所以不会中途闪一下。
        float a = (float)(p < .7 ? Math.Clamp(p / .16, 0, 1) : Math.Clamp((1 - p) / .3, 0, 1));
        if (a <= 0) return;
        // 底边抬到 286：人物头顶在 232 上下，所以虚影浮在人**上方**、只压住一点点。
        float Cx = PlayerScreenX; const float Base = 286;
        float swing = (float)Math.Clamp((p - .45) / .45, 0, 1);   // 0 = 举剑蓄势，1 = 斩到底
        // 身形比初版矮一档（168 → 143）：原来是"整个人浮在头顶上方"，现在只比人物高出一头多一点。
        Vector2[] left = [new(Cx, Base - 143), new(Cx - 36, Base - 107), new(Cx - 44, Base)];
        Vector2[] right = [new(Cx, Base - 143), new(Cx + 36, Base - 107), new(Cx + 44, Base)];
        DrawColoredPolygon([left[0], left[1], left[2], right[2], right[1]], new Color(Void, .18f * a));
        // 只描"头 + 双肩"这条轮廓：能量体是浮着的，不该有一条实线的腰。
        DrawPolyline(left, new Color(Void, .85f * a), 4);
        DrawPolyline(right, new Color(Void, .85f * a), 4);
        DrawLine(new(Cx - 44, Base - 12), new(Cx + 44, Base - 12), new Color(Void, .40f * a), 3);
        DrawCircle(new(Cx, Base - 158), 16, new Color(Void, .40f * a));
        // 持剑的手落在右肩外：整把剑绕它从头顶举起到斩向前下方，与目标的巨剑同步。
        float Hx = Cx + 38; const float Hy = Base - 100;
        double angle = -1.5 + 2.35 * swing;
        float bx = Hx + (float)Math.Cos(angle) * 56, by = Hy + (float)Math.Sin(angle) * 56;
        DrawBlade(bx, by, angle, 112, new Color(Void, .9f * a), new Color(UiKit.Gold, a));
        // 剑身刀光：从剑柄到剑尖一条亮线，挥到哪亮到哪。
        DrawLine(new(Hx, Hy), new(Hx + (float)Math.Cos(angle) * 112, Hy + (float)Math.Sin(angle) * 112), new Color("#f4efff", .75f * a), 3);
        if (swing is > 0 and < 1)   // 挥砍残影：把刚扫过的那段画成一条弧，动作才读得出来
        {
            var trail = new Vector2[9];
            for (int i = 0; i < trail.Length; i++)
            {
                double at = -1.5 + 2.35 * swing * i / (trail.Length - 1);
                trail[i] = new(Hx + (float)Math.Cos(at) * 112, Hy + (float)Math.Sin(at) * 112);
            }
            DrawPolyline(trail, new Color(Void, .35f * a * (float)Math.Sin(Math.PI * swing)), 4);
        }
    }

    /// <summary>玩家增益光环：护盾金罡、归元回气、万剑吸血、疾风攻速、醉仙暴击，以及通用增益环。</summary>
    private void DrawPlayerAuras()
    {
        float t = (float)_clock;
        // 光环都绕玩家画。用 c 而不是写死 330——序章里玩家会离开屏幕基准位（见 PlayerScreenX）。
        // 注意别把**纵坐标**的 330 也换掉（吸血那一段的 py 就是 330）。
        float c = PlayerScreenX;
        // 攻速：脚下疾风环 + 向后掠过的速度线，读起来就是"出手变快"。
        if (Session.HasteRemaining > 0)
        {
            DrawArc(new(c, 358), 62, 0, Mathf.Tau, 26, new Color(Wind, .5f), 3);
            // 速度线整条落在角色左侧（精灵占 262..398），不压在人物身上。
            for (int i = 0; i < 6; i++)
            {
                float py = 250 + i * 22 + (float)Math.Sin(t * 6 + i) * 4;
                float span = 90 + i * 8;
                DrawLine(new(c - span - 40 - (float)((t * 120 + i * 24) % 60), py), new(c - span, py), new Color(Wind, .35f), 3);
            }
        }
        // 薯皮：一圈半透明的剑气护罩 + 环绕的飞剑。护盾还在时那些小剑会自行射向来犯之敌
        // （逻辑见 GameSession.TickSwordGuard），所以这里画六把只是"待发的家伙在转"，不代表出手数。
        if (Session.ShieldRemaining > 0)
        {
            DrawCircle(new(c, 306), 74, new Color(UiKit.Jade, .10f));                       // 半透明罩面
            DrawArc(new(c, 306), 74, 0, Mathf.Tau, 32, new Color(UiKit.Jade, .55f), 3);      // 罩沿
            DrawArc(new(c, 306), 68, 0, Mathf.Tau, 32, new Color(UiKit.Gold, .22f), 1);      // 内圈罡纹
            for (int i = 0; i < 6; i++)
            {
                float angle = t * 1.6f + i * Mathf.Tau / 6;
                float sx = c + (float)Math.Cos(angle) * 74, sy = 306 + (float)Math.Sin(angle) * 56;
                DrawBlade(sx, sy, angle + Mathf.Pi / 2, 26, new Color(UiKit.Jade, .9f), UiKit.Gold);
            }
        }
        // 暴击：身周金色星芒闪烁，随机相位让它一直在闪。
        if (Session.CritBonusRemaining > 0)
        {
            for (int i = 0; i < 5; i++)
            {
                float angle = t * 1.2f + i * 1.26f;
                float px = c + (float)Math.Cos(angle) * 74, py = 312 + (float)Math.Sin(angle) * 48;
                float size = 5 + (float)Math.Sin(t * 8 + i) * 3f;
                DrawLine(new(px - size, py), new(px + size, py), new Color(Thunder, .9f), 3);
                DrawLine(new(px, py - size), new(px, py + size), new Color(Thunder, .9f), 3);
            }
        }
        if (Session.ShieldRemaining > 0)
        {
            var hex = new Vector2[7];
            for (int i = 0; i <= 6; i++)
            {
                float angle = i % 6 / 6f * Mathf.Tau + t * .6f;
                hex[i] = new(c + (float)Math.Cos(angle) * 80, 300 + (float)Math.Sin(angle) * 92);
            }
            DrawPolyline(hex, new Color(UiKit.Gold, .75f), 3);
        }
        if (Session.RegenRemaining > 0)
        {
            DrawArc(new(c, 358), 70, 0, Mathf.Tau, 30, new Color(UiKit.Jade, .55f), 3);
            for (int i = 0; i < 5; i++)
            {
                float angle = t * 1.6f + i * 1.26f;
                DrawRect(new(c + (float)Math.Cos(angle) * 62 - 2, 358 - (float)((t * 70 + i * 32) % 120), 4, 4), new Color(UiKit.Jade, .85f));
            }
        }
        if (Session.LifestealRemaining > 0)
        {
            for (int i = 0; i < 8; i++)
            {
                float angle = t * 2 + i / 8f * Mathf.Tau;
                float px = c + (float)Math.Cos(angle) * 88, py = 330 + (float)Math.Sin(angle) * 56;   // py 的 330 是纵坐标，保留
                DrawLine(new(px, py), new(px + (float)Math.Cos(angle) * 18, py + (float)Math.Sin(angle) * 12), new Color(Blood, .85f), 3);
            }
        }
        if (Session.BuffRemaining > 0) DrawArc(new(c, 366), 54, 0, Mathf.Tau, 28, new Color(UiKit.Gold, .5f), 2);
    }
    private void DrawPopups(Font font)
    {
        foreach (var popup in _popups)
        {
            var enemy = Session.Battle.Enemies.FirstOrDefault(e => e.Id == popup.Id);
            float x = X(enemy?.X ?? popup.X);
            if (x < -80 || x > 2040) continue;
            float t = (float)(1 - popup.Life / popup.MaxLife);
            var color = popup.Color; color.A = 1 - t * t;
            DrawString(font, new(x - 60, 322 - t * 48), UiKit.Number(popup.Total), HorizontalAlignment.Center, 120, popup.Size, color);
        }
    }
    private void DrawLightning(float x, float top, float bottom, Color color)
    {
        int count = 7;
        var points = new Vector2[count];
        for (int i = 0; i < count; i++)
            points[i] = new(x + (float)Math.Sin(i * 2.7 + _clock * 30) * (i == count - 1 ? 0 : 15), top + i * (bottom - top) / (count - 1));
        DrawPolyline(points, color, 5);
        DrawPolyline(points, new Color("#fffce8"), 2);
    }
    /// <summary>
    /// 四边渐变暗角：把背景的四角再压一层，视线自然收到画面中段。
    /// 用**逐顶点色**的多边形（内侧顶点全透明）画，而不是叠一堆半透明矩形——后者色带很明显。
    /// 左侧带只铺到 x=200：主角站在 330、贴图半径 68，再宽就会吃到它的轮廓。
    /// **必须在实体之前调用**，否则会把角色和怪物一起压暗。
    /// </summary>
    private void DrawVignette()
    {
        // α 只到 .42：再重就把山和树一起吃掉，画面会变成一片空黑。
        var dark = new Color("#05090d", .42f);
        var clear = new Color("#05090d", 0f);
        DrawPolygon([new(0, 0), new(1920, 0), new(1920, 120), new(0, 120)], [dark, dark, clear, clear]);
        DrawPolygon([new(0, 440), new(1920, 440), new(1920, 310), new(0, 310)], [dark, dark, clear, clear]);
        DrawPolygon([new(0, 0), new(200, 0), new(200, 440), new(0, 440)], [dark, clear, clear, dark]);
        DrawPolygon([new(1920, 0), new(1720, 0), new(1720, 440), new(1920, 440)], [dark, clear, clear, dark]);
    }
    private void DrawEllipseFloor(float x, float radius, Color color)
    {
        var points = new Vector2[28];
        for (int i = 0; i < points.Length; i++)
        {
            float angle = i / (float)points.Length * Mathf.Tau;
            points[i] = new(x + (float)Math.Cos(angle) * radius, 356 + (float)Math.Sin(angle) * radius * .13f);
        }
        DrawColoredPolygon(points, color);
    }
    /// <summary>
    /// 画一张精灵。<paramref name="sx"/>/<paramref name="sy"/> 是挤压拉伸系数，**绕底部中心**缩放：
    /// 中心 x 与底边固定，所以调用方给的世界坐标与命中判定完全不受影响（默认 1 = 原尺寸）。
    /// </summary>
    private void Sprite(string id, float x, float bottom, float size, Color? color = null, float sx = 1, float sy = 1)
    {
        if (_textures.TryGetValue(id, out var texture))
            DrawTextureRect(texture, new(x - size * sx / 2, bottom - size * sy, size * sx, size * sy), false, color ?? Colors.White);
    }

    /// <summary>落地扬尘。画在主角**之前**，读作"从脚下扬起来的"，而不是糊在身上。</summary>
    private void DrawPuffs()
    {
        foreach (var puff in _puffs)
        {
            float fade = (float)(puff.Life / puff.MaxLife);
            DrawRect(new(puff.X - puff.Size / 2, puff.Y - puff.Size / 2, puff.Size, puff.Size), new Color("#cdb07e", .34f * fade));
        }
    }
    private void DrawEllipseShadow(float x, float y, float radius) => DrawRect(new(x - radius, y - 3, radius * 2, 7), new Color(0, 0, 0, .22f));

    // ── 落地（序章用）：下落终点由驱动层算，这里只负责"砸下来"的那一下反馈 ──

    /// <summary>打一个落地挤压的脉冲。<paramref name="y"/> 处才会产生形变，见 <see cref="LandingSquash"/>。</summary>
    public void PlayLandingSquash() => _landAt = _clock;

    /// <summary>
    /// 落地挤压：一个衰减振荡。负值 = 压矮变宽（正是落地的读法），随后回弹成一点拉伸再收敛。
    /// 与走路的挤压相加后仍然绕**底部中心**缩放，所以不动物理位置，也不影响命中判定。
    /// </summary>
    private float LandingSquash()
    {
        // 幅度刻意压到 -.28：第一版用 -.45，落地那一帧土豆被压掉近一半高度，读起来像被砸扁了。
        double t = _clock - _landAt;
        return t is >= 0 and < .4 ? (float)(-.28 * Math.Exp(-8 * t) * Math.Cos(16 * t)) : 0f;
    }

    /// <summary>
    /// 落地扬尘：绕一圈给**径向**初速，才读成"炸开一圈"，而不是走路时那两撮擦地尘。
    /// 重力与淡出沿用 <see cref="Puff"/> 的通用更新。
    /// </summary>
    public void SpawnLandingDust()
    {
        if (_puffs.Count > 60) return;
        float cx = PlayerScreenX;
        for (int i = 0; i < 14; i++)
        {
            double a = i / 14.0 * Math.Tau;
            _puffs.Add(new Puff
            {
                X = cx + (float)Math.Cos(a) * 18,
                Y = 362 + (float)Math.Sin(a) * 6,
                Vx = (float)Math.Cos(a) * 150,
                Vy = (float)Math.Sin(a) * 40 - 18,
                Size = 6 + i % 4,
                Life = .5, MaxLife = .5,
            });
        }
    }
}
