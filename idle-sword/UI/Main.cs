using Godot;
using IdleSword.Core;
using IdleSword.Features;
using FileAccess = Godot.FileAccess;

namespace IdleSword.UI;

public partial class Main : Control
{
    private GameSession _game = null!;
    private SaveStore _store = null!;
    private Control _page = null!;
    private BattleView _battle = null!;
    private Label _wallet = null!, _hpText = null!, _stage = null!, _notice = null!;
    private ProgressBar _hp = null!;
    private Button _loop = null!;
    private OptionButton _levelSelect = null!;
    private Button _previewToggle = null!;
    private readonly List<string> _levelIds = [];
    private readonly List<Action> _bindings = [];
    private readonly List<Button> _tabs = [];
    private int _selectedTab, _intentTab = -1;
    private double _accumulator, _saveClock, _refreshClock;
    private bool _failed, _testMode;
    // 序章：只在**全新存档**时播一次。--prologue 可以强制播放（开发与截图用）。
    private bool _forcePrologue, _prologuePlayed;
    private PrologueState? _prologue;
    // 过场用的全屏遮罩，见 BuildShell 末尾。
    private ColorRect _fade = null!;
    // 截图路径（--capture）。提成字段是为了让序章也能逐阶段留图，见 CaptureFrame。
    private string? _capturePath;
    // 过场期间要收起来的 HUD。BuildShell 里逐个登记，由序章开关（见 SetHudVisible）。
    private readonly List<CanvasItem> _hudNodes = [];
    // 战斗区的常态位置。过场里 HUD 收起来之后画面会挤在上半屏，所以临时下移，见 StartPrologue。
    private Vector2 _battleHome, _battleHomeSize;
    private int _frames;
    private string _saveStatus = "本地存档";
    private string[] _args = [];

    public override void _Ready()
    {
        Theme = new Theme { DefaultFont = new SystemFont { FontNames = ["Microsoft YaHei", "Noto Sans CJK SC", "sans-serif"] }, DefaultFontSize = 22 };
        GetTree().AutoAcceptQuit = false;
        _args = OS.GetCmdlineUserArgs(); _testMode = _args.Contains("--smoke-test") || _args.Contains("--capture");
        _forcePrologue = _args.Contains("--prologue");
        // 截图路径必须**在这里**解析，不能等 RunUiSmoke——序章跑在冒烟之前，它也要逐阶段留图。
        int capAt = Array.IndexOf(_args, "--capture");
        if (capAt >= 0) _capturePath = capAt + 1 < _args.Length ? _args[capAt + 1] : "user://preview.png";
        try
        {
            var config = GameConfig.Load(file => FileAccess.GetFileAsString("res://Config/Tables/" + file));
            _store = new SaveStore(ProjectSettings.GlobalizePath(_testMode ? "user://qa/session.json" : "user://save_v1.json"));
            _settingsStore = new SettingsStore(ProjectSettings.GlobalizePath(_testMode ? "user://qa/settings.json" : "user://settings.json"));
            _settings = _settingsStore.Load();
            // 顺序要紧：ApplyAudio 先建好 Music/SFX 两条总线，LoadAudio 再把播放器挂上去。
            ApplyDisplay(); ApplyAudio(); LoadAudio();
            // 玩家拖动窗口改尺寸时要同步进设置，见 OnWindowSizeChanged。
            // 必须在 ApplyDisplay 之后接：启动那一次重设不该被当成玩家拖拽。
            GetWindow().SizeChanged += OnWindowSizeChanged;
            Sfx.Play = PlaySfx;
            // QA 总是使用新会话，不读取、不改写正常玩家进度。--prologue 同理：强制跑一次全新会话，
            // 并且会让 Save 直接返回（见 Save），免得开发时拿干净的 level-1 状态把真存档覆盖掉。
            var loaded = (_testMode || _forcePrologue) ? null : _store.Load(config);
            _game = new GameSession(config, loaded);
            _game.PersistRequested += Save;
            if (_store.Warning is not null) _saveStatus = _store.Warning;
            BuildShell(); ShowPage(0); Refresh(); StartBgm();
            // 序章只在"这台机器上从来没存过档"时播。注意不能用 `loaded is null` 单独判断——
            // QA、技能预览、冒烟用的隔离会话、以及重置进度**都会**传 null，那些场合必须跳过。
            if (_forcePrologue || (!_testMode && loaded is null)) StartPrologue();
            // 缺素材是静默损坏：Godot 吞掉 _Ready 异常，画面会空白但流程照常，必须显式提示。
            var problems = new[] { _battle.LoadError, _audioLoadError }.Where(p => p is not null).ToArray();
            if (problems.Length > 0) { foreach (var problem in problems) GD.PushError(problem!); _gameNotice = string.Join("  /  ", problems); }
            GD.Print($"Idle-Sword READY | levels={config.Levels.Count} skills={config.Skills.Count} audio={_audio.Count} sfx_voices={_sfxPlayers.Length}");
        }
        catch (Exception ex)
        {
            _failed = true; GD.PushError(ex.ToString());
            UiKit.PanelAt(this, 80, 120, 1760, 800);
            UiKit.Label(this, "工程加载失败", 120, 145, 1600, 65, 38, UiKit.Gold);
            UiKit.Wrapped(this, ex.Message, 120, 240, 1640, 500, 25);
            if (_testMode) GetTree().Quit(1);
        }
    }
    private void BuildShell()
    {
        var bg = new ColorRect { Color = UiKit.Ink, MouseFilter = MouseFilterEnum.Ignore }; UiKit.Place(bg, 0, 0, 1920, 1080); AddChild(bg);
        UiKit.Label(this, "土豆天尊", 32, 10, 270, 60, 35, UiKit.Gold);
        UiKit.Label(this, "IMMORTATO  /  修行初境", 265, 20, 360, 45, 17, UiKit.Muted);
        // 顶栏右侧依次为 GM / 设置 / 保存，钱包宽度收窄给设置按钮让位。
        _wallet = UiKit.Label(this, "", 825, 14, 620, 48, 23);
        var gm = UiKit.Button(this, "GM", 1462, 20, 120, 42, ToggleGm);
        gm.TooltipText = "调试专用：发放资源，以及临时加减每波的怪物数量（不影响正式配置）。";
        var settings = UiKit.Button(this, "设置", 1592, 20, 120, 42, ToggleSettings);
        settings.TooltipText = "画面、音频与进度重置。";
        var saveButton = UiKit.Button(this, "保存", 1730, 20, 150, 42, Save);
        _hp = new ProgressBar { ShowPercentage = false }; UiKit.Place(_hp, 34, 79, 465, 20);
        _hp.AddThemeStyleboxOverride("background", UiKit.Box(new Color("#263946"), 3)); _hp.AddThemeStyleboxOverride("fill", UiKit.Box(new Color("#89bda8"), 3)); AddChild(_hp);
        _hpText = UiKit.Label(this, "", 35, 104, 500, 32, 18, UiKit.Muted);
        _stage = UiKit.Label(this, "", 565, 76, 760, 54, 23, UiKit.Gold);
        _loop = UiKit.Button(this, "", 1570, 83, 310, 42, () => _game.ToggleLoop());
        _battle = new BattleView { Session = _game };
        _battle.HitLanded += heavy => PlaySfx(heavy ? "sfx_hit_heavy" : "sfx_hit");
        _battle.EnemyDefeated += () => PlaySfx("sfx_kill");
        UiKit.Place(_battle, 0, 143, 1920, 400); AddChild(_battle);
        _battleHome = _battle.Position; _battleHomeSize = _battle.Size;   // 过场里会临时挪位置与放大，见 StartPrologue
        var noticePanel = UiKit.PanelAt(this, 24, 552, 1872, 64);
        _notice = UiKit.Label(this, "", 46, 559, 1350, 48, 21, UiKit.Jade);
        _previewToggle = UiKit.Button(this, "技能预览", 1404, 563, 120, 42, TogglePreview);
        _previewToggle.TooltipText = "用独立会话逐个播放 15 个法术，不写存档；预览期间主线挂机暂停。";
        _levelSelect = new OptionButton(); UiKit.Place(_levelSelect, 1530, 563, 340, 42); AddChild(_levelSelect);
        _levelSelect.ItemSelected += index => { if (index < _levelIds.Count) { _game.SelectLevel(_levelIds[(int)index]); Refresh(); } };
        // **只有 4 个页签**：剑灵系统暂缓（用户决定先屏蔽入口，名字未定），PetPage 保留在代码里但不挂入口。
        // 页签等距铺满整行：4 个 × 360 宽，间隔 (1872 − 360) / 3 = 504。
        string[] names = ["01  修行", "02  境界 · 法术", "03  铸造", "04  参悟"];
        for (int i = 0; i < names.Length; i++) { int tab = i; _tabs.Add(UiKit.Button(this, names[i], 24 + i * 504, 632, 360, 56, () => ShowPage(tab))); }
        _page = new Control(); UiKit.Place(_page, 24, 704, 1872, 316); AddChild(_page);
        // 修行星图是**常驻控件**（不放进 _page，理由见 TalentMap.cs 的说明），盖在同一个矩形上。
        BuildTalentMap();
        // 节点编辑器：整屏覆盖的开发期工具，默认关着，从 GM 面板进。**不进 _hudNodes**——
        // 它本来就不是给玩家看的，序章收 HUD 时不必管它。
        BuildTalentEditor();
        var bottomLeft = UiKit.Label(this, "初版试炼  ·  在线自动战斗  ·  离线不产出", 32, 1040, 1050, 28, 17, UiKit.Muted);
        var bottomRight = UiKit.Label(this, "每关首杀灵核 ×1   /   长路无重置", 1460, 1040, 420, 28, 17, UiKit.Gold);
        // 过场要收起来的 HUD。**故意不含标题**——开场挂着游戏名是想要的。
        _hudNodes.AddRange([_wallet, _hp, _hpText, _stage, _loop, _previewToggle, _levelSelect, _notice, noticePanel, _page, _talentRoot,
                            gm, settings, saveButton, bottomLeft, bottomRight]);
        foreach (var tab in _tabs) _hudNodes.Add(tab);
        // 过场遮罩。**必须最后添加**：它要盖住包括顶栏与页签在内的整屏——这是一次换场，不是一块面板。
        // BattleView 自己扛不了：它只占 (0,143,1920,400) 且 ClipContents，画在里面会被裁掉。
        _fade = new ColorRect { Color = new Color(0, 0, 0, 0), MouseFilter = MouseFilterEnum.Ignore };
        UiKit.Place(_fade, 0, 0, 1920, 1080); AddChild(_fade);
    }
    public override void _Process(double delta)
    {
        if (_failed || _game is null) return;
        _accumulator += delta;
        double step = _game.Config.Setting("fixed_step");
        int iterations = 0;
        // 预览期间只推进预览会话：既让主线挂机暂停，也保证预览状态不回写存档。
        while (_accumulator >= step && iterations++ < 200)
        {
            _accumulator -= step;
            // 序章期间**不推进会话**：GameSession 的构造函数已经把第一关摆成原始状态（PlayerX=80、
            // 满血、无刷怪无敌人），只要不 Step 它就一直干净——所以过场里没有刷怪、没有战斗、也不写存档，
            // 是构造上没有，不是靠抑制。走路与镜头全由表现层驱动。
            if (_prologue is not null) TickPrologue(step);
            else if (_preview is not null) { TickPreview(step); _preview.Step(step); }
            else _game.Step(step);
            // 逐 Step 观测而不是逐帧：一次长帧会合并多个 Step，短冷却技能可能触发又走完而被漏掉。
            TrackSkillCasts(Active);
        }
        TrackFirstKills();
        TickSettings(delta);
        TickDamage(delta);
        _saveClock += delta; _refreshClock += delta;
        if (_preview is null && _saveClock >= _game.Config.Setting("save_interval")) { _saveClock = 0; Save(); }
        if (_refreshClock >= .15) { _refreshClock = 0; Refresh(); }
        // 过场期间不数帧：RunUiSmoke 在第 100 帧开跑，而它整段都假设"正常关卡正在运行"。
        // 序章跑的时候先冻住计数，交接之后从 0 重新数，冒烟因此仍拿到完整的 100 帧预热。
        if (_testMode && _prologue is null && ++_frames == 100) RunUiSmoke();
    }
    private async void RunUiSmoke()
    {
        try
        {
            // 截图路径在开头就解析：中途几段自检（分层、飞行单位）也要能各留一张图，
            // 而那些段落跑在下面那一大段截图流程之前。
            string? capturePath = _capturePath;   // 已在 _Ready 里解析（序章先于冒烟跑，也要用它）
            if (_battle.LoadError is not null) throw new Exception(_battle.LoadError);
            // 缺音频与缺图同理，是静默损坏，必须显式断言。
            if (_audioLoadError is not null) throw new Exception(_audioLoadError);
            if (_audio.Count == 0 || _sfxPlayers.Length != SfxVoices) throw new Exception("Audio voices were not created");
            // 序章只在"全新存档"或 `--prologue` 时播。这一条同时挡住两种错：该播没播、不该播却播了。
            // （普通 --smoke-test 走的是隔离会话，属于"不该播"。）
            if (_prologuePlayed != _forcePrologue) throw new Exception($"Prologue played={_prologuePlayed}, forced={_forcePrologue}");
            // 循环要真的生效：只设 LoopMode 而 loop_end 留在 0 是空区间，会"看着对、实际不循环"。
            if (_audio["bgm_battle"] is not AudioStreamWav bgm) throw new Exception("BGM is not an AudioStreamWav");
            if (bgm.LoopMode != AudioStreamWav.LoopModeEnum.Forward || bgm.LoopBegin != 0 || bgm.LoopEnd <= 0)
                throw new Exception($"BGM loop not applied: mode={bgm.LoopMode} begin={bgm.LoopBegin} end={bgm.LoopEnd}");
            // 发出真实控件信号，覆盖界面到系统的接线；QA 使用隔离会话。
            IEnumerable<Button> Buttons(Node node) => node.GetChildren().SelectMany(child => (child is Button b ? new[] { b } : Array.Empty<Button>()).Concat(Buttons(child)));
            void Press(string prefix) => Buttons(_page).First(b => b.Text.StartsWith(prefix)).EmitSignal(Button.SignalName.Pressed);
            // 外壳（顶栏/页签）上的按钮不在 _page 下，单独按整棵树查找。
            void Tap(string prefix) => Buttons(this).First(b => b.Text.StartsWith(prefix)).EmitSignal(Button.SignalName.Pressed);
            // 面板各自有"关闭"，而 `Tap` 取的是**全树第一个**前缀匹配——隐藏的面板仍留在树上，会抢在真正打开的那个前面。
            // 所以按面板关闭时得从它自己的根往下找。
            void TapIn(Node root, string prefix) => Buttons(root).First(b => b.Text.StartsWith(prefix)).EmitSignal(Button.SignalName.Pressed);
            // 星图的节点**没有文字**（只有图标），所以不能再用 Press(前缀) 找它——改按 Name 找，
            // 顺带证明节点真的是可点的 Button（这正是当初把它们做成真 Button 而不是自绘的原因）。
            ShowPage(0); Refresh();
            var talentRoot = Buttons(this).FirstOrDefault(b => b.Name == "talent_node_t_root")
                ?? throw new Exception("Talent node button is missing");
            talentRoot.EmitSignal(Button.SignalName.Pressed);
            if (_game.State.Talents.GetValueOrDefault("t_root") != 1) throw new Exception("Talent UI action failed");
            _intentTab = -1; ShowPage(3); Refresh(); Press("◇");
            var collect = _page.GetChildren().OfType<Button>().First(b => b.Text.StartsWith("移入收取"));
            collect.EmitSignal(Control.SignalName.MouseEntered);
            if (_game.State.Amount("intent_0") != 1) throw new Exception("Hover collection UI action failed");
            // 命中音：推进真实战斗，并逐帧放行让 BattleView._Process 里的血量差分真的跑到。
            // 一次跑完再取结果是不行的——那样敌人会在两帧之间生灭，表现层根本观察不到。
            for (int frame = 0; frame < 40; frame++)
            {
                for (int i = 0; i < 5; i++) _game.Step(_game.Config.Setting("fixed_step"));
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            // 开局不附带法术，这一段的输出全部来自普攻（每秒一柄、1× 攻击的飞剑）。
            // 这里只要求"伤害反馈发生过"，两个命中分支各自由下面专门的高血靶子断言覆盖。
            if (SfxCount("sfx_hit") + SfxCount("sfx_hit_heavy") + SfxCount("sfx_kill") == 0) throw new Exception("No combat feedback SFX fired");
            // 击杀音：死亡的敌人已在同一次 Step 里被移除，表现层只能靠"从列表消失"判定，这里专门验证那条路径。
            if (SfxCount("sfx_kill") == 0)
            {
                var victim = _game.Battle.Enemies.FirstOrDefault(e => e.Kind != "rift");
                if (victim is null) throw new Exception("No enemy available to verify the kill SFX");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);   // 先让表现层看到它还活着
                victim.Hp = 0;
                _game.Step(_game.Config.Setting("fixed_step"));
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (SfxCount("sfx_kill") == 0) throw new Exception("Kill SFX never fired");
            // 重击分支（伤害占比 ≥ 12%）：这一段的输出只有普攻，所以靶血必须让一发**未暴击**的普攻
            // 就够得上 12%——取 7 倍攻击时占比 14%。取 12 倍的话只有暴击（×1.5）才够，断言会变成
            // 摇暴击的抽签，偶发失败。同时 7 倍血也扛得住循环里的 5 发，不会先死变成击杀音。
            var slime = _game.Config.Monsters["slime"];
            double heavyHp = Math.Max(1, _game.Attack) * 7;
            var heavyTank = new EnemyState
            {
                Id = _game.Battle.NextEnemyId++, MonsterId = slime.Id, Kind = slime.Kind,
                X = _game.Battle.PlayerX + 200, Hp = heavyHp, MaxHp = heavyHp, Atk = 0, AttackTimer = 999,
            };
            _game.Battle.Enemies.Add(heavyTank);
            for (int frame = 0; frame < 20 && SfxCount("sfx_hit_heavy") == 0; frame++)
            {
                for (int i = 0; i < 5; i++) _game.Step(_game.Config.Setting("fixed_step"));
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (SfxCount("sfx_hit_heavy") == 0) throw new Exception("Heavy hit SFX never fired");
            // 靶子必须撤掉：它血量是攻击的六倍、要挨三下才死，留着会挡在下面那个普通命中靶子前面，
            // 而带渲染运行时每帧只推进 5 个固定步，普通命中断言的预算不够等它先倒下。
            _game.Battle.Enemies.Remove(heavyTank);
            // 普通命中分支要单独验证：伤害占比要低于 12%，这里把血量拉到 400 倍攻击。

            double tankHp = Math.Max(1, _game.Attack) * 400;
            _game.Battle.Enemies.Add(new()
            {
                Id = _game.Battle.NextEnemyId++, MonsterId = slime.Id, Kind = slime.Kind,
                X = _game.Battle.PlayerX + 200, Hp = tankHp, MaxHp = tankHp, Atk = 0, AttackTimer = 999,
            });
            for (int frame = 0; frame < 20 && SfxCount("sfx_hit") == 0; frame++)
            {
                for (int i = 0; i < 5; i++) _game.Step(_game.Config.Setting("fixed_step"));
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (SfxCount("sfx_hit") == 0) throw new Exception("Normal hit SFX never fired");
            // 灼烧跳伤必须不出命中音。跳伤按固定步长成块结算（每步 DotDps*fixed_step），
            // 而真实帧间隔小于固定步长：拿帧间隔当阈值会低于单步跳伤，判据永不成立，每步都播一次命中音。
            double step = _game.Config.Setting("fixed_step");
            var burnProbe = new EnemyState { DotUntil = 1, DotDps = 100 };
            if (!BattleView.IsBurnTick(burnProbe, 100 * step, step)) throw new Exception("Burn tick not classified as burn");
            if (BattleView.IsBurnTick(burnProbe, 100 * step, step * .2)) throw new Exception("Burn judged against the frame clock instead of the simulation clock");
            if (BattleView.IsBurnTick(burnProbe, 100 * 3, step)) throw new Exception("Direct hit misclassified as burn");
            // 端到端放在独立会话里：主线会话仍在挂机走路、可能死亡重生或补怪，
            // 任何一次额外命中都会污染"场上只有跳伤"这个前提。这里只留一个远处着火的靶子。
            // 刷怪点要标记为已越过而不是清空——清空会被 ActivateCell 在下一步重建并刷怪。
            var burnSession = new GameSession(_game.Config, seed: 1);
            for (int cell = 0; cell < burnSession.Level.Cells; cell++) burnSession.Battle.Spawns[cell] = new() { Passed = true };
            var burning = new EnemyState
            {
                Id = burnSession.Battle.NextEnemyId++, MonsterId = slime.Id, Kind = slime.Kind,
                X = burnSession.Battle.PlayerX + 5000, Hp = tankHp, MaxHp = tankHp, Atk = 0, AttackTimer = 999,
                DotUntil = 60, DotDps = Math.Max(1, _game.Attack * .2),
            };
            burnSession.Battle.Enemies.Add(burning);
            _battle.Session = burnSession;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);   // 先让表现层把这个敌人读成基准
            int hitsBeforeBurn = SfxCount("sfx_hit") + SfxCount("sfx_hit_heavy");
            int burnTicksBefore = _battle.BurnTicks;
            // 每帧跑满 30 步：headless 的真实帧间隔比固定步长大，帧数跑少了两种时钟差别不明显，
            // 用错时钟的写法会"碰巧"判对，这条断言就成了空断言。步数拉大后模拟时长必然远大于帧间隔。
            // 每步重新钉住靶距，免得角色一路走近后真的打中它。
            for (int frame = 0; frame < 6; frame++)
            {
                for (int i = 0; i < 30; i++) { burning.X = burnSession.Battle.PlayerX + 5000; burnSession.Step(step); }
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            // 先证明跳伤真的结算过，否则下面两条都是空断言。
            if (burning.Hp >= tankHp) throw new Exception("Burn never ticked, the SFX assertion would be vacuous");
            // 主判据看分类结果而不是听感：抑制机制用的是主线模拟钟，自检里那口钟几乎不走，
            // 误判出来的命中音会被整批吃掉、计数不变，"没出声"就成了能被掩盖的断言。
            if (_battle.BurnTicks - burnTicksBefore < 5) throw new Exception("Burn ticks were not classified as burn");
            if (SfxCount("sfx_hit") + SfxCount("sfx_hit_heavy") != hitsBeforeBurn) throw new Exception("Burn ticks played the hit SFX");
            _battle.Session = _game;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            // 普攻：每秒向最近合法目标平射一柄飞剑，间隔走配置。它不带来源法术（Skill 为空），
            // 因此不会被当成第 16 个法术混进任何按技能分流的地方。
            var basicSession = new GameSession(_game.Config, seed: 1);
            for (int cell = 0; cell < basicSession.Level.Cells; cell++) basicSession.Battle.Spawns[cell] = new() { Passed = true };
            basicSession.Battle.Enemies.Clear(); basicSession.State.Skills.Clear(); basicSession.Battle.Cooldowns.Clear();
            var basicTarget = new EnemyState
            {
                Id = basicSession.Battle.NextEnemyId++, MonsterId = slime.Id, Kind = slime.Kind,
                X = basicSession.Battle.PlayerX + 400, Hp = 1e6, MaxHp = 1e6, Atk = 0, AttackTimer = 999, StunUntil = 1e9,
            };
            basicSession.Battle.Enemies.Add(basicTarget);
            for (int i = 0; i < 30; i++) basicSession.Step(step);
            if (basicTarget.Hp >= 1e6) throw new Exception("Basic attack never landed");
            if (!basicSession.Battle.Cooldowns.ContainsKey(GameSession.BasicAttackKey)) throw new Exception("Basic attack never consumed its interval");
            // 神通：关掉普攻就绝不自动释放，只有显式放一次才看得到（技能预览正是靠 ForceRelease）。
            // 这一场绑到表现层上逐帧跑，让聚怪力场（寒冰龙卷）与火海真的走一遍绘制路径，而不是只留在 Core 里。
            var procSession = new GameSession(_game.Config, seed: 1) { BasicAttackEnabled = false };
            for (int cell = 0; cell < procSession.Level.Cells; cell++) procSession.Battle.Spawns[cell] = new() { Passed = true };
            procSession.Battle.Enemies.Clear(); procSession.State.Skills.Clear();
            var procTarget = new EnemyState
            {
                Id = procSession.Battle.NextEnemyId++, MonsterId = slime.Id, Kind = slime.Kind,
                X = procSession.Battle.PlayerX + 400, Hp = 1e6, MaxHp = 1e6, Atk = 0, AttackTimer = 999, StunUntil = 1e9,
            };
            procSession.Battle.Enemies.Add(procTarget);
            procSession.State.Skills["skill_07"] = 1;
            bool procAutoCast = false;
            for (int i = 0; i < 60; i++) { procSession.Step(step); if (procSession.Effects.Any(e => e.Skill == "skill_07")) procAutoCast = true; }
            if (procAutoCast) throw new Exception("A trigger skill auto-cast without a basic attack");
            if (!procSession.ForceRelease("skill_07")) throw new Exception("Force release failed");
            if (!procSession.Effects.Any(e => e.Skill == "skill_07")) throw new Exception("Force release produced no effect");
            procSession.Effects.Clear(); procSession.Battle.Cooldowns.Clear();
            _battle.Session = procSession;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            procSession.State.Skills.Clear(); procSession.State.Skills["skill_12"] = 1;
            procSession.ForceRelease("skill_12");
            bool tornadoSeen = false;
            for (int i = 0; i < 30; i++)
            {
                procSession.Step(step);
                if (procSession.Effects.Any(e => e.Skill == "skill_12")) tornadoSeen = true;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);   // 逐帧跑 _Draw，力场的绘制路径真的被执行
            }
            if (!tornadoSeen) throw new Exception("寒冰龙卷 never produced its gathering field");
            // 寒冷必须同时落到"表现用的状态源"与"实际减速"上：只染蓝不改速度就成了纯视觉欺骗。
            if (procTarget.ChillUntil <= 0 || procTarget.SlowUntil <= 0) throw new Exception("Chill did not mark the target");
            procSession.Effects.Clear(); procSession.Battle.Cooldowns.Clear();
            procSession.State.Skills.Clear(); procSession.State.Skills["skill_02"] = 1;
            procSession.ForceRelease("skill_02");
            bool seaSeen = false;
            for (int i = 0; i < 24; i++)
            {
                procSession.Step(step);
                if (procSession.Effects.Any(e => e.Kind == "ground" && e.Skill == "skill_02")) seaSeen = true;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (!seaSeen) throw new Exception("焚天剑诀 left no fire sea");
            if (procTarget.DotUntil <= 0) throw new Exception("The fire sea did not set the target alight");
            // 妖圣档三式终极：同样绑定到表现层逐帧跑，保证黑屏、虚影、贴地剑气这三条新绘制路径真的被执行过一遍。
            var ultimate = new GameSession(_game.Config, seed: 1) { BasicAttackEnabled = false };
            for (int cell = 0; cell < ultimate.Level.Cells; cell++) ultimate.Battle.Spawns[cell] = new() { Passed = true };
            ultimate.Battle.Enemies.Clear(); ultimate.State.Skills.Clear();
            // 定身靶：摆好就不再移动，位置变化只可能来自剑气击退。
            EnemyState AddTarget(double x, double hp, string monster = "slime")
            {
                var m = _game.Config.Monsters[monster];
                var e = new EnemyState
                {
                    Id = ultimate.Battle.NextEnemyId++, MonsterId = m.Id, Kind = m.Kind,
                    X = x, Hp = hp, MaxHp = hp, Atk = 0, AttackTimer = 999, StunUntil = 1e9,
                };
                ultimate.Battle.Enemies.Add(e); return e;
            }
            _battle.Session = ultimate;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            // ① 剑气流云壁：施放瞬间全场定身（不看射程），贴地剑气推出去把身前的敌人击退。
            var front = AddTarget(ultimate.Battle.PlayerX + 300, 1e8);
            var far = AddTarget(ultimate.Battle.PlayerX + 6000, 1e8);
            ultimate.State.Skills["skill_05"] = 1;
            ultimate.Battle.Cooldowns.Clear(); ultimate.Effects.Clear();
            front.StunUntil = 0; far.StunUntil = 0;               // 先解定身，否则"定住了"这条断言是空的
            double frontX = front.X;
            if (!ultimate.ForceRelease("skill_05")) throw new Exception("剑气流云壁 was not released");
            if (ultimate.Battle.Enemies.Any(e => e.StunUntil < 1)) throw new Exception("剑气流云壁 did not root the whole field");
            foreach (var e in ultimate.Battle.Enemies) e.StunUntil = 1e9;   // 定身看完就重新钉死，只看剑气自己的位移
            for (int i = 0; i < 60; i++) { ultimate.Step(step); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            if (front.X <= frontX) throw new Exception("剑气流云壁 did not knock the enemy back");
            if (far.Hp < 1e8) throw new Exception("剑气流云壁 reached an enemy that was never on its path");
            // ② 斩鬼神：全场血量最高的单位远在射程之外，也必须是它挨这一剑。
            ultimate.Battle.Enemies.Clear(); ultimate.Effects.Clear(); ultimate.Battle.Cooldowns.Clear();
            ultimate.State.Skills.Clear(); ultimate.State.Skills["skill_10"] = 1;
            var weakNear = AddTarget(ultimate.Battle.PlayerX + 300, 1e3);
            var strongFar = AddTarget(ultimate.Battle.PlayerX + 6000, 1e9);
            if (!ultimate.ForceRelease("skill_10")) throw new Exception("斩鬼神 was not released");
            if (weakNear.Hp != 1e3) throw new Exception("斩鬼神 struck something other than the healthiest enemy");
            for (int i = 0; i < 24; i++) { ultimate.Step(step); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            if (strongFar.Hp >= 1e9) throw new Exception("斩鬼神 never reached the healthiest enemy");
            if (weakNear.Hp != 1e3) throw new Exception("斩鬼神 hit something it was not aiming at");
            // ③ 诛仙剑阵：四柄剑依次落下，每一柄都打全体——身后的与极远的都不能漏。
            ultimate.Battle.Enemies.Clear(); ultimate.Effects.Clear(); ultimate.Battle.Cooldowns.Clear();
            ultimate.State.Skills.Clear(); ultimate.State.Skills["skill_15"] = 1;
            var ahead = AddTarget(ultimate.Battle.PlayerX + 300, 1e8);
            var behind = AddTarget(ultimate.Battle.PlayerX - 400, 1e8);
            var remote = AddTarget(ultimate.Battle.PlayerX + 6000, 1e8);
            if (!ultimate.ForceRelease("skill_15")) throw new Exception("诛仙剑阵 was not released");
            if (ultimate.Effects.Count(e => e.Trajectory == "sky_drop") != 4) throw new Exception("诛仙剑阵 did not raise four swords");
            for (int i = 0; i < 60; i++) { ultimate.Step(step); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            if (ahead.Hp >= 1e8 || behind.Hp >= 1e8 || remote.Hp >= 1e8)
                throw new Exception($"诛仙剑阵 did not strike the whole field: {ahead.Hp:0}/{behind.Hp:0}/{remote.Hp:0}");
            // ④ 天剑（横向贯穿）与苍穹剑陨（黑洞 + 剑雨落地爆炸）：让两条新的绘制分支真的被执行一遍，
            //    而不是只留在 Core 里。苍穹剑陨的探出 + 停顿 + 下坠共 1.3 秒，步数要给够。
            ultimate.Battle.Enemies.Clear(); ultimate.Effects.Clear(); ultimate.Battle.Cooldowns.Clear();
            ultimate.State.Skills.Clear(); ultimate.State.Skills["skill_17"] = 1;
            var beheaded = AddTarget(ultimate.Battle.PlayerX + 300, 1e8);
            if (!ultimate.ForceRelease("skill_17")) throw new Exception("天剑 was not released");
            for (int i = 0; i < 20; i++) { ultimate.Step(step); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            if (beheaded.Hp >= 1e8) throw new Exception("天剑 did not strike its target");
            ultimate.Battle.Enemies.Clear(); ultimate.Effects.Clear(); ultimate.Battle.Cooldowns.Clear();
            ultimate.State.Skills.Clear(); ultimate.State.Skills["skill_18"] = 1;
            // 各锁一敌：每支落在**自己锁定的目标**身上，敌人不足时循环重复。场上只放一只，三支就全砸在它身上
            // ——这也正是"打 BOSS 不丢伤害"那条口径的现场验证。
            var armored = AddTarget(ultimate.Battle.PlayerX + 500, 1e8);
            if (!ultimate.ForceRelease("skill_18")) throw new Exception("苍穹剑陨 was not released");
            if (ultimate.Effects.Count(e => e.Skill == "skill_18" && e.Trajectory == "sky_drop")
                != ultimate.Config.Skills["skill_18"].ProjectileCount)
                throw new Exception("苍穹剑陨 did not raise its configured volley");
            for (int i = 0; i < 60; i++) { ultimate.Step(step); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            if (armored.Hp >= 1e8) throw new Exception("苍穹剑陨 did not wound its target");
            // ⑤ 剑二十三（影分身）：先开分身窗口，再让本体放一式——分身应当同步复制一份，且这一段
            //    会带着分身逐帧跑过表现层，"身后那个半透明分身"的绘制路径因此真的被执行过。
            ultimate.Battle.Enemies.Clear(); ultimate.Effects.Clear(); ultimate.Battle.Cooldowns.Clear();
            var prey = AddTarget(ultimate.Battle.PlayerX + 400, 1e8);
            ultimate.State.Skills.Clear(); ultimate.State.Skills["skill_19"] = 1; ultimate.State.Skills["skill_01"] = 1;
            if (!ultimate.ForceRelease("skill_19")) throw new Exception("剑二十三 was not released");
            if (ultimate.MirrorRemaining <= 0) throw new Exception("剑二十三 did not open the clone window");
            ultimate.Battle.Cooldowns.Clear(); ultimate.Effects.Clear();
            if (!ultimate.ForceRelease("skill_01")) throw new Exception("御剑术 was not released");
            if (ultimate.Effects.Count(e => e.Skill == "skill_01") != 2 || !ultimate.Effects.Any(e => e.Mirrored))
                throw new Exception("影分身 did not mirror the cast");
            // 分身那一式必须**从分身上出发**（更小的 X）且**晚一拍**出现——两样都缺就看不出是分身放的。
            var cloneShot = ultimate.Effects.Single(e => e.Mirrored);
            var ownShot = ultimate.Effects.Single(e => e.Skill == "skill_01" && !e.Mirrored);
            if (cloneShot.Delay <= 0 || ownShot.Delay != 0) throw new Exception("影分身 did not stagger its cast");
            if (cloneShot.X >= ownShot.X) throw new Exception("影分身 cast from the wrong place");
            double cloneStart = cloneShot.X;
            for (int i = 0; i < 20; i++) { ultimate.Step(step); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            if (ultimate.MirrorRemaining <= 0) throw new Exception("影分身 expired far too early");
            if (cloneShot.X <= cloneStart) throw new Exception("影分身 never launched from the clone");
            if (prey.Hp >= 1e8) throw new Exception("影分身 dealt no damage");
            // 分层：地面定位的法术打不到飞行单位，不限层的照打。顺带绑着表现层跑几帧，
            // 让"飞行怪抬高、影子留在地面"这条绘制路径真的被执行过。
            ultimate.Battle.Enemies.Clear(); ultimate.Effects.Clear(); ultimate.Battle.Cooldowns.Clear();
            var onGround = AddTarget(ultimate.Battle.PlayerX + 200, 1e8);
            var inAir = AddTarget(ultimate.Battle.PlayerX + 260, 1e8, "bat");   // 就在地面靶旁边：没有层判定一定会被波及
            // 焚天剑诀定位是 ground（天降火海），且是触发类神通，不会自动释放，这里显式放一次。
            ultimate.State.Skills.Clear(); ultimate.State.Skills["skill_02"] = 1;
            if (!ultimate.ForceRelease("skill_02")) throw new Exception("焚天剑诀 was not released");
            for (int i = 0; i < 24; i++) { ultimate.Step(step); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            // 留一张图：地面怪踩着地、飞行怪浮空且影子留在地面，是这一眼要核对的东西。
            await Capture("-flying");
            if (onGround.Hp >= 1e8) throw new Exception("焚天剑诀 did not strike the ground target");
            if (inAir.Hp < 1e8) throw new Exception("a ground-only skill struck a flying monster");
            ultimate.Battle.Enemies.Remove(onGround);
            ultimate.State.Skills.Clear(); ultimate.State.Skills["skill_01"] = 1; ultimate.Battle.Cooldowns.Clear();
            for (int i = 0; i < 60; i++) { ultimate.Step(step); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            if (inAir.Hp >= 1e8) throw new Exception("a layer-agnostic skill never reached the flying monster");
            _battle.Session = _game;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            // 召唤/跟随单位的站位：Core 把召唤物的 X 统一钉在玩家身后，错开完全靠表现层分配位次。
            // 当前 15 个在役法术里已经没有 summon（剑侍已归档），这里造三份伪实体覆盖表现层的位次分配——它们不读配置，
            // id 只是用来让三个槽各画一张不同的素材。
            var fakes = new[] { "skill_05", "skill_10", "skill_15" }
                .Select(id => new CombatEffect { Kind = "summon", Skill = id, Life = 5, MaxLife = 5 }).ToArray();
            foreach (var fake in fakes) _game.Effects.Add(fake);
            for (int frame = 0; frame < 2; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var slots = fakes.Select(e => _battle.CompanionSlots.GetValueOrDefault(e, -1)).ToArray();
            if (slots.Any(s => s < 0) || slots.Distinct().Count() != 3) throw new Exception("Summons shared a slot: " + string.Join(",", slots));
            var summonXs = slots.Select(s => BattleView.FollowerSlot(s).X).ToArray();
            if (summonXs.Distinct().Count() != 3) throw new Exception("Summons overlapped on screen: " + string.Join(",", summonXs));
            if (summonXs.All(x => x > 330) || summonXs.All(x => x < 330)) throw new Exception("Summons all stacked on one side");
            foreach (var fake in fakes) _game.Effects.Remove(fake);
            // 自定飞行形态的站位与边界：多支剑必须错开、且不得越出战斗可用区（local y 0..400 之外会被裁掉，
            // 更上面是顶栏、更下面是养成面板）。落点 X 由 Core 冻结，这里只核对表现层的编排函数。
            var hover = Enumerable.Range(0, 5).Select(i => BattleView.HoverSlot(i, 5)).ToArray();
            if (hover.Select(p => p.X).Distinct().Count() != 5) throw new Exception("Hover blades overlap: " + string.Join(",", hover.Select(p => p.X)));
            if (hover.Any(p => p.Y < 10 || p.Y > 360)) throw new Exception("Hover blades out of bounds: " + string.Join(",", hover.Select(p => p.Y)));
            if (Enumerable.Range(0, 5).Select(BattleView.SkyDropTop).Distinct().Count() < 3) throw new Exception("Sky drop lanes overlap");
            // 平射剑阵（御剑术）：身前身后都要有、横向互不重叠、且落在肩部一带（不压顶栏、也不压下方界面）。
            var volley = Enumerable.Range(0, 5).Select(i => BattleView.VolleySlot(i, 5, 28)).ToArray();
            if (volley.Select(p => p.X).Distinct().Count() != 5) throw new Exception("Volley blades overlap: " + string.Join(",", volley.Select(p => p.X)));
            if (volley.Any(p => p.Y < 200 || p.Y > 280)) throw new Exception("Volley blades off the shoulder line: " + string.Join(",", volley.Select(p => p.Y)));
            if (volley.All(p => p.X > 330) || volley.All(p => p.X < 330)) throw new Exception("Volley blades all on one side of the caster");
            // 剑形多边形按角度旋转：0° 剑尖朝右、+90° 朝下。两种角度不能退化成一堆相同的点。
            var right = BattleView.BladePolygon(0, 0, 0, 60, 10);
            var down = BattleView.BladePolygon(0, 0, Math.PI / 2, 60, 10);
            if (right.Select(p => p.X).Max() <= 0 || down.Select(p => p.Y).Max() <= 0) throw new Exception("Blade tip does not follow its angle");
            if (right.Select(p => (p.X, p.Y)).Distinct().Count() < 4) throw new Exception("Blade polygon degenerated");
            // 镜头分离的**默认路径**必须与改动前逐像素等价：镜头跟随玩家时，玩家恒在屏幕 330，
            // 世界坐标按 `- PlayerX` 平移。这条把旧公式钉死，防止以后动 CameraOverride 时悄悄改了正常玩法。
            if (_battle.CameraOverride is not null) throw new Exception("Camera left overridden outside the prologue");
            if (_battle.PlayerScreenX != BattleView.PlayerAnchor) throw new Exception($"Player left off the anchor: {_battle.PlayerScreenX}");
            if (Math.Abs(_battle.ScreenX(_game.Battle.PlayerX) - 330f) > 1e-4) throw new Exception("Player no longer renders at screen 330");
            if (Math.Abs(_battle.ScreenX(_game.Battle.PlayerX + 500) - 830f) > 1e-4) throw new Exception("World→screen transform changed");
            // 首杀奖励音由 FirstKills 账本增加触发。自检跑不到 BOSS，这里直接改账本触发一次；
            // 测试模式不写存档（Save 会直接返回），不会污染进度。
            _game.State.FirstKills.Add(_game.Level.Id);
            TrackFirstKills();
            if (SfxCount("sfx_reward") == 0) throw new Exception("Reward SFX never fired");
            _game.State.FirstKills.Remove(_game.Level.Id);
            TrackFirstKills();
            // 释放音：预览模式逐个播放法术，覆盖全部 5 种 kind。
            // 其中 buff 类不产生 CombatEffect，正是"效果引用差集"会永久漏掉的那一类。
            TogglePreview();
            // summon 不在列：跟随召唤的剑侍已归档（机制留给以后的剑灵系统），当前 15 个在役法术里没有 summon，
            // 因此 sfx_cast_summon 没有活触发点（素材与映射保留，退役配置日后可能复活）。
            foreach (string kind in new[] { "projectile", "target", "ground", "buff" })
            {
                int index = PreviewSkillIds.FindIndex(id => _game.Config.Skills[id].Kind == kind);
                if (index < 0) throw new Exception("No skill of kind " + kind);
                SelectPreviewSkill(index);
                AdvancePreview(60);
                if (SfxCount("sfx_cast_" + kind) == 0) throw new Exception($"Cast SFX for kind '{kind}' never fired");
            }
            TogglePreview();
            // 实际实现在 CaptureFrame（类级），序章也要用它逐阶段留图。
            async Task Capture(string suffix) { if (!await CaptureFrame(suffix)) throw new Exception("Capture failed: " + suffix); }
            // 前 4 张对应 4 个页签；第 5 张是 PetPage——它的页签入口已屏蔽（剑灵系统暂缓），
            // 但页面本身与接线都留着，照旧截一张，等于给这个暂时不可达的页面留一份回归覆盖。
            for (int i = 0; i < 5; i++) { ShowPage(i); Refresh(); await Capture("-page" + i); }
            // 星图单独留两张：一张点亮前几层（看清三态、连线、满级金框），一张把悬停说明条调出来。
            // 看完就把这些等级撤掉——后面还有重置相关的断言，别留在状态里。
            var demo = new (string Id, int Level)[] { ("t_root", 5), ("t_hp", 2), ("t_atk", 5), ("t_auto", 1), ("t_atk_01", 1), ("t_hp_01", 1), ("t_atk_02", 1) };
            foreach (var (id, level) in demo) _game.State.Talents[id] = level;
            ShowPage(0); Refresh(); await Capture("-talent");
            TalentShowTipForCapture("t_hp"); await Capture("-talent-tip");
            // 节点编辑器：截一张，并**真的走一遍保存**——保存只整份重写布局表，写的是同一份数据，
            // 所以这是条无损的往返验证；"保存被取消/失败"会被下面这条断言抓住。
            ToggleTalentEditor(); await Capture("-talent-editor");
            TalentEditorSaveForCheck();
            if (!TalentEditorStatus.StartsWith("已保存")) throw new Exception("Talent editor save failed: " + TalentEditorStatus);
            ToggleTalentEditor();
            foreach (var (id, _) in demo) _game.State.Talents.Remove(id);
            ShowPage(0); Refresh();
            _intentTab = 0; ShowPage(3); Refresh(); await Capture("-intent");
            ShowPage(0); Refresh(); await Capture("");
            // GM 面板：发放后续步骤所需资源（既覆盖 GM→GameSession 接线，也避免界面直接改写钱包），
            // 并顺带按一下波次加成的 ＋ / −，把那条接线也走一遍。
            Tap("GM");
            if (_gmRoot is null || !_gmRoot.Visible) throw new Exception("GM panel did not open");
            await Capture("-gm");
            Tap("每种货币");
            if (_game.State.Amount("gold") < 10000 || _game.State.Amount("core") < 10000) throw new Exception("GM UI action failed");
            Tap("波次＋"); if (_game.WaveBonus != 1) throw new Exception("GM wave bonus did not go up");
            Tap("波次−"); if (_game.WaveBonus != 0) throw new Exception("GM wave bonus did not go down");
            // 怪物倍率：按一下要跳档，并且**立刻**作用到场上的怪（血量按比例缩放，百分比不变）。
            var scaled = _game.Battle.Enemies.FirstOrDefault();
            double beforeScale = scaled?.MaxHp ?? 0;
            Tap("血量＋");
            if (_game.MonsterHpScale != 2) throw new Exception($"GM monster HP scale did not step: {_game.MonsterHpScale}");
            if (scaled is not null && Math.Abs(scaled.MaxHp - beforeScale * 2) > 1e-6)
                throw new Exception("monster HP scale did not reach the enemies already on the field");
            Tap("血量−"); Tap("攻击＋");
            if (_game.MonsterHpScale != 1 || _game.MonsterAtkScale != 2) throw new Exception("GM monster scale did not restore");
            Tap("攻击−");
            // 主角无敌：点一下要真的不再掉血（死亡会重置冷却、让伤害统计断档）。
            Tap("无敌：关");
            if (!_game.PlayerInvincible) throw new Exception("GM invincibility did not turn on");
            _game.Battle.PlayerHp = 1;
            for (int i = 0; i < 40; i++) { _game.Step(.05); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            if (_game.Battle.PlayerHp != 1 || _game.Battle.RespawnTimer > 0) throw new Exception("GM invincibility did not hold");
            Tap("无敌：开");
            if (_game.PlayerInvincible) throw new Exception("GM invincibility did not turn off");
            _game.Battle.PlayerHp = _game.MaxHp;
            // 伤害统计：开面板 → 断言统计到了东西 → 重置 → 关面板。数据源是 `_battle.Session`（当前在看的那一场）。
            Tap("伤害统计");
            if (_damageRoot is null || !_damageRoot.Visible) throw new Exception("Damage panel did not open");
            if (_gmRoot.Visible) throw new Exception("GM panel should close when the damage panel opens");
            if (_battle.Session.DamageStats.Rows.Count == 0) throw new Exception("Damage stats recorded nothing");
            if (_battle.Session.DamageStats.Rows.Values.Sum(r => r.Hits) == 0) throw new Exception("Damage stats recorded no hits");
            await Capture("-damage");
            Tap("重置");
            if (_battle.Session.DamageStats.Rows.Count != 0) throw new Exception("Damage stats did not reset");
            TapIn(_damageRoot, "关闭");
            if (_damageRoot.Visible) throw new Exception("Damage panel did not close");
            Tap("GM");
            if (_gmRoot is null || !_gmRoot.Visible) throw new Exception("GM panel did not reopen");
            TapIn(_gmRoot, "关闭");
            if (_gmRoot.Visible) throw new Exception("GM panel did not close");
            // 取"属于已解锁境界且尚未习得"的技能，而不是写死某个 id：技能书序会随设计调整重排，
            // 写死 id 的话每次重排都会在这里断掉。要在点按钮**之前**取，否则取到的是下一个还没学的。
            ShowPage(1); Refresh();
            var learnable = _game.Config.Skills.Values.First(s => _game.State.Realms.Contains(s.Realm) && _game.State.Skills.GetValueOrDefault(s.Id) == 0);
            Press("习得");
            if (_game.State.Skills.GetValueOrDefault(learnable.Id) != 1) throw new Exception("Skill UI action failed");
            // 升级提示必须写清"每级强化了什么"：输出类写威力幅度、增益类写覆盖率（它的峰值强度不随等级变）。
            string tipAtk = UpgradeTip(_game.Config.Skills["skill_01"], 1);
            string tipBuff = UpgradeTip(_game.Config.Skills["skill_04"], 1);
            if (!tipAtk.Contains("每级") || !tipAtk.Contains("威力")) throw new Exception("输出类的升级提示没写清幅度：" + tipAtk);
            if (!tipBuff.Contains("每级") || !tipBuff.Contains("覆盖率")) throw new Exception("增益类的升级提示没写清幅度：" + tipBuff);
            ShowPage(2); Refresh(); Press("打造并装备"); Press("淬炼");
            if (_game.State.Weapon != "sword_wood" || _game.State.WeaponLevel != 1) throw new Exception("Forge UI action failed");
            ShowPage(4); Refresh(); Press("召唤");
            if (_game.State.EquippedPets.Count != 1) throw new Exception("Pet UI action failed");
            // 突破音走的是"带专属成功音的 Act"路径；突破要花灵钱，GM 之后才有，所以放在这里。
            ShowPage(1); Refresh(); Press("突破");
            if (SfxCount("sfx_breakthrough") == 0) throw new Exception("Breakthrough SFX never fired");
            if (capturePath is not null)
            {
                // 技能展示场景：学会全部法术并把敌人做成耐打靶，便于逐帧核对 15 个技能的表现差异。
                foreach (var id in _game.Config.Skills.Keys) _game.State.Skills[id] = 3;
                // 原地清空不经过换场守卫，不重置的话旧 id 会被当成击杀、刷一波死亡音。
                _game.Battle.Enemies.Clear(); _game.Battle.Spawns.Clear(); _battle.ResetTransient();
                _game.Battle.PlayerX = 24 * _game.Config.Setting("cell_width") + 600;
                _game.Step(.05);
                foreach (var e in _game.Battle.Enemies) { e.Hp = e.MaxHp = 1e9; e.Atk = 0; }
                ShowPage(1); Refresh(); await Capture("-boss");
                for (int frame = 0; frame < 3; frame++)
                {
                    for (int i = 0; i < 12; i++) _game.Step(.05);
                    await Capture("-skills" + frame);
                }
                // 技能预览：15 个法术各出一张对照图，逐个核对表现与数值。
                TogglePreview();
                for (int i = 0; i < _game.Config.Skills.Count; i++)
                {
                    SelectPreviewSkill(i); ShowPage(_selectedTab); Refresh(); AdvancePreview(40);
                    await Capture("-skill" + (i + 1).ToString("00"));
                }
                TogglePreview();
            }
            // 设置面板：打开、改音量、改分辨率、二次确认重置，覆盖界面到系统的接线。
            OpenSettings();
            if (!_settingsRoot!.Visible) throw new Exception("Settings panel did not open");
            await Capture("-settings");
            _musicSlider.Value = 35; _soundSlider.Value = 20;
            if (_settings.MusicVolume != 35 || _settings.SoundVolume != 20) throw new Exception("Volume slider wiring failed");
            if (Math.Abs(AudioServer.GetBusVolumeDb(AudioServer.GetBusIndex("Music")) - Mathf.LinearToDb(.35f)) > .01) throw new Exception("Music bus volume not applied");
            _settings.Width = 1600; _settings.Height = 900; PersistSettings();
            var reloaded = new SettingsStore(ProjectSettings.GlobalizePath("user://qa/settings.json")).Load();
            if (reloaded.Width != 1600 || reloaded.MusicVolume != 35) throw new Exception("Settings persistence failed");
            double goldBefore = _game.State.Amount("gold"); int skillsBefore = _game.State.Skills.Count;
            // 预览模式下重置：必须退回主线会话，否则画面读预览、存档写主线，两者对不上。
            TogglePreview();
            if (_preview is null) throw new Exception("Preview mode did not start");
            Tap("重置游戏进度");
            if (_resetArmed <= 0) throw new Exception("Reset did not require confirmation");
            // 第一次点击只进入待确认，进度必须原封不动。
            if (_game.State.Amount("gold") != goldBefore || _game.State.Skills.Count != skillsBefore) throw new Exception("Reset fired on the first click");
            Tap("确认重置");
            if (_game.State.Amount("gold") != _game.Config.Setting("starting_gold") || _game.State.Amount("core") != 0 || _game.State.UnlockedLevels.Count != 1) throw new Exception("Reset did not restore the initial state");
            // 重置后法术数为 0：开局不再白送御剑术，得自己花灵钱学（见 GameSession 构造器）。
            if (_game.State.FirstKills.Count != 0 || _game.State.Skills.Count != 0) throw new Exception("Reset left progress behind");
            if (_settingsRoot.Visible) throw new Exception("Settings panel should close after reset");
            if (_preview is not null || !ReferenceEquals(_battle.Session, _game)) throw new Exception("Reset must leave preview mode and rebind the battle view");
            // 真实窗口路径：--capture 是有显示的，这里验证分辨率确实落到窗口上。
            if (capturePath is not null)
            {
                async Task WaitFrames(int count)
                {
                    for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                // 改窗口尺寸/显示模式要经操作系统一圈，读回值会落后一两帧，所以轮询几帧再判定，
                // 否则断言会随帧率偶发失败。
                async Task ExpectSize(Vector2I size, string what)
                {
                    for (int i = 0; i < 12 && DisplayServer.WindowGetSize() != size; i++) await WaitFrames(1);
                    if (DisplayServer.WindowGetSize() != size)
                        throw new Exception($"{what}: window is {DisplayServer.WindowGetSize()}, expected {size}");
                }
                async Task ExpectMode(DisplayServer.WindowMode mode, string what)
                {
                    for (int i = 0; i < 12 && DisplayServer.WindowGetMode() != mode; i++) await WaitFrames(1);
                    if (DisplayServer.WindowGetMode() != mode)
                        throw new Exception($"{what}: window mode is {DisplayServer.WindowGetMode()}, expected {mode}");
                }
                // 1) 分辨率下拉走真实信号：选中一项后真实窗口必须跟着变。
                _settings.Fullscreen = false; ApplyDisplay(); await ExpectSize(new(1600, 900), "Initial display apply failed");
                OpenSettings();
                int pick = _resolutionOptions.FindIndex(r => r.Width == 1920 && r.Height == 1080);
                if (pick < 0) throw new Exception("Resolution preset missing");
                _resolutionBox.Select(pick);
                _resolutionBox.EmitSignal(OptionButton.SignalName.ItemSelected, pick);
                await ExpectSize(new(1920, 1080), "Resolution dropdown did not resize the window");
                // 2) 全屏勾选框走真实信号：显示模式与还原后的尺寸都要对。
                _fullscreenBox.ButtonPressed = true;
                await ExpectMode(DisplayServer.WindowMode.Fullscreen, "Fullscreen toggle did not apply");
                _fullscreenBox.ButtonPressed = false;
                await ExpectMode(DisplayServer.WindowMode.Windowed, "Leaving fullscreen did not restore windowed mode");
                await ExpectSize(new(1920, 1080), "Leaving fullscreen lost the chosen resolution");
                // 3) 先最大化再调分辨率是很常见的顺序：退出最大化时系统会异步还原旧尺寸，
                //    同帧设的尺寸会被盖掉，必须推迟一帧才套用。
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized);
                await ExpectMode(DisplayServer.WindowMode.Maximized, "Maximize failed");
                _settings.Width = 1280; _settings.Height = 720; ApplyDisplay();
                await ExpectSize(new(1280, 720), "Resolution did not apply from a maximized window");
                // 4) 拖拽改出来的尺寸要被设置采纳，开面板不能反过来把窗口弹回旧预设。
                DisplayServer.WindowSetSize(new Vector2I(1000, 640));
                await ExpectSize(new(1000, 640), "Hand resize did not reach the window");
                if (_settings.Width != 1000 || _settings.Height != 640)
                    throw new Exception($"Hand resize not captured: {_settings.Width}x{_settings.Height}");
                // 故意让配置说"全屏"而窗口停在窗口模式，此时开面板只该如实勾选。
                // ButtonPressed 的 setter 会发出 Toggled，用错就会在这里把窗口顶成全屏。
                _settings.Fullscreen = true;
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetSize(new Vector2I(1000, 640));
                CloseSettings(); OpenSettings(); await WaitFrames(6);
                if (DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed)
                    throw new Exception("Opening settings applied the display mode by itself");
                if (DisplayServer.WindowGetSize() != new Vector2I(1000, 640))
                    throw new Exception($"Opening settings resized the window to {DisplayServer.WindowGetSize()}");
                if (!_fullscreenBox.ButtonPressed) throw new Exception("Fullscreen checkbox does not reflect the saved value");
                if (!_resolutionBox.Disabled) throw new Exception("Resolution dropdown should be disabled while fullscreen");
                // 收尾：回到窗口模式与预设尺寸。
                _settings.Fullscreen = false; _settings.Width = 1280; _settings.Height = 720;
                ApplyDisplay(); CloseSettings(); await WaitFrames(2);
                GD.Print($"DISPLAY {DisplayServer.WindowGetSize()} mode={DisplayServer.WindowGetMode()} screen={DisplayServer.ScreenGetSize()}");
            }
            // 界面音：上面所有按钮都是通过 EmitSignal 触发的真实信号，点击音应已随之播放。
            if (SfxCount("sfx_click") == 0) throw new Exception("Click SFX never fired");
            if (SfxCount("sfx_panel") == 0) throw new Exception("Panel SFX never fired");
            GD.Print($"SFX PLAYS {_sfxPlays} throttled={_sfxThrottled} | click={SfxCount("sfx_click")} hit={SfxCount("sfx_hit")}/{SfxCount("sfx_hit_heavy")} "
                + $"kill={SfxCount("sfx_kill")} buy={SfxCount("sfx_buy")} deny={SfxCount("sfx_deny")} panel={SfxCount("sfx_panel")} "
                + $"reward={SfxCount("sfx_reward")} breakthrough={SfxCount("sfx_breakthrough")}");
            GD.Print("UI SMOKE PASS"); GetTree().Quit();
        }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
    private void Refresh()
    {
        var s = _game.State;
        // 战斗读数跟随当前会话：预览时显示预览会话，钱包始终是玩家真实钱包。
        var shown = Active;
        _wallet.Text = $"灵钱  {UiKit.Number(s.Amount("gold"))}       灵核  {s.Amount("core"):0}       攻击  {UiKit.Number(shown.Attack)}";
        _hp.MaxValue = shown.MaxHp; _hp.Value = shown.Battle.PlayerHp;
        _hpText.Text = $"气血  {UiKit.Number(shown.Battle.PlayerHp)} / {UiKit.Number(shown.MaxHp)}     {_saveStatus}";
        _previewToggle.Text = _preview is null ? "技能预览" : "退出预览";
        _stage.Text = _preview is null
            ? $"{_game.Level.Name}     第 {_game.Battle.Cell + 1:00} / {_game.Level.Cells} 格     在场 {_game.Battle.Enemies.Count} 敌"
            : $"技能预览    第 {_previewSkill + 1:00} / {PreviewSkillIds.Count} 个法术    {_game.Config.Skills[PreviewSkillId].Name}";
        _notice.Text = _gameNotice ?? _game.Message;
        if (_talentRoot.Visible) RefreshTalentMap();
        _loop.Text = s.LoopLevel ? "整关循环  /  点击自动推进" : "自动推进  /  点击循环本关";
        if (_levelIds.Count != s.UnlockedLevels.Count)
        {
            _levelSelect.Clear(); _levelIds.Clear();
            foreach (var level in _game.Config.Levels.Where(l => s.UnlockedLevels.Contains(l.Id))) { _levelIds.Add(level.Id); _levelSelect.AddItem(level.Name); }
        }
        _levelSelect.Select(_levelIds.IndexOf(_game.Level.Id));
        foreach (var update in _bindings) update();
    }
    /// <summary>
    /// 按后缀留一张截图（--capture 用）。路径取自 `_capturePath`（RunUiSmoke 在开头解析）。
    /// 提成类级方法是为了让序章也能逐阶段留图。**这里不抛异常**——序章是即发即忘地调它的，
    /// 抛出去会变成未观测任务；失败只记日志，要不要断言由调用方（冒烟）自己决定。
    /// </summary>
    private async Task<bool> CaptureFrame(string suffix)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_capturePath is null) return true;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string path = _capturePath.Replace(".png", suffix + ".png");
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        if (err != Error.Ok) { GD.PushError("Capture failed: " + err); return false; }
        GD.Print("CAPTURE " + path);
        return true;
    }
    private void Save()
    {
        // --prologue 也拒写：它跑的是全新会话，一旦落盘就会拿干净的 level-1 状态覆盖真存档。
        if (_failed || _testMode || _forcePrologue) return;
        try { _store.Save(_game.State); _saveStatus = "已保存 " + DateTime.Now.ToString("HH:mm:ss"); _gameNotice = null; }
        catch (Exception ex) { _saveStatus = "保存失败"; GD.PushError(ex.ToString()); _gameNotice = "保存失败，请检查磁盘空间与存档目录权限。"; }
    }
    private string? _gameNotice;
    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) { if (!_failed) Save(); GetTree().Quit(); }
    }
    /// <summary>
    /// 退出时释放。静态转发持有本实例的方法引用，若不清掉，托管对象会一直活着，
    /// 连带它持有的音频资源也不释放，Godot 退出时会报一堆泄漏。
    /// </summary>
    public override void _ExitTree()
    {
        Sfx.Play = null;
        // 播放器持有 Stream 引用，不放掉的话音频资源要等 .NET GC 才回收，
        // 而 Godot 的退出泄漏检查跑在 GC 之前，于是每次退出都刷一堆"资源仍在使用"的噪声。
        foreach (var player in _sfxPlayers) { player.Stop(); player.Stream = null; }
        if (_music is not null) { _music.Stop(); _music.Stream = null; }
        _audio.Clear();
        _sfxPlayers = [];
        _music = null;
    }
    private void ShowPage(int tab)
    {
        _selectedTab = tab; _bindings.Clear();
        foreach (var child in _page.GetChildren()) { _page.RemoveChild(child); child.QueueFree(); }
        for (int i = 0; i < _tabs.Count; i++)
        {
            // 预览期间页面固定为法术列表，禁用页签避免切走后状态与画面不一致。
            _tabs[i].Disabled = _preview is not null;
            _tabs[i].AddThemeColorOverride("font_color", i == tab && _preview is null ? UiKit.Gold : UiKit.Muted);
        }
        // 修行是常驻星图（不在 _page 里），这里只切可见性——重建的话平移量与按钮身份都会丢。
        _talentRoot.Visible = tab == 0 && _preview is null;
        if (_talentRoot.Visible) { Recenter(); RefreshTalentMap(); }
        if (_preview is not null) { PreviewPage(); return; }
        switch (tab) { case 0: break; case 1: SkillPage(); break; case 2: ForgePage(); break; case 3: IntentPage(); break; case 4: PetPage(); break; }
    }
    private void Act(Func<bool> action, string? successSfx = null)
    {
        bool ok = action();
        // 失败音覆盖的不止"资源不足"，也包括已满级/已拥有/槽位已满；要精确区分得让 Core 返回失败原因，属于过度设计。
        PlaySfx(ok ? successSfx ?? "sfx_buy" : "sfx_deny");
        ShowPage(_selectedTab); Refresh();
    }
}
