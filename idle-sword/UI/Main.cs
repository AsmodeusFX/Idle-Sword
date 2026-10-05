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
    // 左排页签（顺序 = TabSystems 的下标）。
    private readonly List<Button> _tabs = [];
    // 每个页签角上的小锁，与 `_tabs` 一一对应。
    private readonly List<LockBadge> _tabBadges = [];
    // 未解锁时压在整个操作区上的遮罩，与解锁那一刻的炸开动画层。
    private LockMask _lockMask = null!;
    private LockFx _lockFx = null!;
    private BattleView _battle = null!;
    private Label _wallet = null!, _hpText = null!, _stage = null!, _notice = null!;
    private ProgressBar _hp = null!;
    private Button _loop = null!;
    private OptionButton _levelSelect = null!;
    private readonly List<string> _levelIds = [];
    private readonly List<Action> _bindings = [];
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
            // 冒烟 / 截图跑的是**后期盘面**：系统全开、「生根」已点（自动出手）、「剑气」已学（远程）。
            // 教学期那一段（全锁 + 近战 + 只能点）是另一套前提，由专门的断言覆盖；
            // 不这么做的话主线会话会卡在第一格——没买「生根」就不会自动出手，玩家不动，整条冒烟都推不动。
            if (_testMode)
            {
                _game.UnlockAllSystems();
                _game.State.Talents["t_auto"] = 1;
                _game.State.Talents["t_ranged"] = 1;
                // 开局不给钱是**玩法**的选择（第一点修为得靠杀怪换来），而冒烟点的是"后期盘面"：
                // 它要能把节点点起来验 UI 接线。给一笔钱而不是把节点直接写成高等级——
                // 后者绕过了 `BuyTalent` 那条真正的接线，等于把要验的东西验掉了。
                _game.State.Wallet["gold"] = 10000;
            }
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
        _hp = new ProgressBar { ShowPercentage = false }; UiKit.Place(_hp, 34, 72, 465, 18);
        _hp.AddThemeStyleboxOverride("background", UiKit.Box(new Color("#263946"), 3)); _hp.AddThemeStyleboxOverride("fill", UiKit.Box(new Color("#89bda8"), 3)); AddChild(_hp);
        _hpText = UiKit.Label(this, "", 35, 94, 500, 30, 18, UiKit.Muted);
        _stage = UiKit.Label(this, "", 545, 64, 720, 52, 23, UiKit.Gold);
        // 关卡选择与"自动推进 / 本关循环"并排放在顶栏这一行。
        // 中间那条横带（原来装关卡选择与技能预览的那条）**整条撤掉**了，具体见下面 _page 处的说明。
        _levelSelect = new OptionButton(); UiKit.Place(_levelSelect, 1276, 70, 336, 42); AddChild(_levelSelect);
        _levelSelect.ItemSelected += index => { if (index < _levelIds.Count) { _game.SelectLevel(_levelIds[(int)index]); Refresh(); } };
        _loop = UiKit.Button(this, "", 1626, 70, 258, 42, () => _game.ToggleLoop());
        _battle = new BattleView { Session = _game };
        _battle.HitLanded += heavy => PlaySfx(heavy ? "sfx_hit_heavy" : "sfx_hit");
        _battle.EnemyDefeated += () => PlaySfx("sfx_kill");
        UiKit.Place(_battle, 0, 128, 1920, 400); AddChild(_battle);
        _battleHome = _battle.Position; _battleHomeSize = _battle.Size;   // 过场里会临时挪位置与放大，见 StartPrologue
        // 提示行**不再有底板面板**：它原来占着一条 64px 高的横带，撤掉之后那段纵向空间归功能区。
        _notice = UiKit.Label(this, "", 40, 536, 1840, 34, 21, UiKit.Jade);
        // **页签改成左侧竖排**。原来横排占掉一整个 56px 高的横带，而画布是"左右宽、上下紧"——
        // 竖排之后横向让出 128px，纵向净赚 ~150px，功能区从 316 高变成 452 高（星图节点因此能放大 65%）。
        // 只留 4 个页签：剑灵系统暂缓（用户决定先屏蔽入口，名字未定），PetPage 保留在代码里但不挂入口。
        // 「境界」改叫「**法术**」：这一页装的是"学哪些剑诀"，境界突破只是它内部的阶梯。
        string[] names = ["修行", "法术", "铸造", "参悟"];
        for (int i = 0; i < names.Length; i++)
        {
            int tab = i;
            var button = UiKit.Button(this, names[i], 24, 580 + i * 118, 128, 104, () => ShowPage(tab));
            button.AddThemeFontSizeOverride("font_size", 25);
            _tabs.Add(button);
            // 角上的小锁：锁着的页签一眼看得见（置灰只说明"点不动"，说不出为什么）。
            var badge = new LockBadge();
            badge.Locked = true;
            UiKit.Place(badge, 96, 4, 26, 26);
            button.AddChild(badge);
            _tabBadges.Add(badge);
        }
        // 功能区：让出左侧竖排页签的宽度后剩下的全部空间。
        _page = new Control(); UiKit.Place(_page, 168, 580, 1728, 452); AddChild(_page);
        // 修行星图是**常驻控件**（不放进 _page，理由见 TalentMap.cs 的说明），盖在同一个矩形上。
        BuildTalentMap();
        // 未解锁时压在整个操作区上的遮罩（含修行星图那一片：它俩本来就共用一个矩形）。
        // **必须在 `_talentRoot` 之后创建**——星图自带一块底板 Panel，加在遮罩之后就会把遮罩整个盖住，
        // 表现为"锁着修行却照样看得到星图"。加在 `_page` 与 `_talentRoot` 之后才盖得住这两者。
        // `MouseFilter` 由 RefreshTabs 按状态切：锁着时 Stop（拦点击），开着时 Ignore（一点都不挡）。
        _lockMask = new LockMask(); UiKit.Place(_lockMask, 168, 580, 1728, 452); AddChild(_lockMask);
        // 解锁炸开的动画层：铺满整屏、永远不拦点击。加在这里，于是页签、操作区、星图都在它下面。
        _lockFx = new LockFx(); UiKit.Place(_lockFx, 0, 0, 1920, 1080); AddChild(_lockFx);
        // 节点编辑器：整屏覆盖的开发期工具，默认关着，从 GM 面板进。**不进 _hudNodes**——
        // 它本来就不是给玩家看的，序章收 HUD 时不必管它。
        BuildTalentEditor();
        var bottomLeft = UiKit.Label(this, "初版试炼  ·  在线自动战斗  ·  离线不产出", 180, 1044, 1050, 28, 17, UiKit.Muted);
        var bottomRight = UiKit.Label(this, "每关首杀灵核 ×1   /   长路无重置", 1480, 1044, 420, 28, 17, UiKit.Gold);
        // 过场要收起来的 HUD。**故意不含标题**——开场挂着游戏名是想要的。
        // **星图与锁那三层（`_talentRoot` / `_lockMask` / `_lockFx`）刻意不在这里**：它们把守的矩形是
        // 同一块，而且星图的可见性本来就跟着"修行锁没锁"走——交给 `RefreshHudLayers` 一处算，
        // 否则过场结束那一趟 `SetHudVisible(true)` 会**把锁着的星图一起点亮**（表现为"进到第 1 关，锁没了"）。
        _hudNodes.AddRange([_wallet, _hp, _hpText, _stage, _loop, _levelSelect, _notice, _page,
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
        // 阵亡/复活的表演走**渲染时钟**：模拟在读条期间是暂停的，用固定步推进的话动画会卡住。
        TrackDeath(delta);
        // 解锁那半秒的"先罩住、再碎开"也走渲染时钟。
        TickSystemReveal(delta);
        // 星图的平滑平移也得走渲染时钟（放 Refresh 里会一顿一顿的，那是 6.7Hz）。
        if (_talentRoot.Visible) TickTalentPan(delta);
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
            // 星图取景的口径：**新节点在框内就一点不动**，只有长到框外才把画面推过去。
            // 买根节点会让 t_hp/t_atk 冒出来，它们就在右边一点点、本来就在框内——所以画面必须纹丝不动。
            float talentPanBefore = TalentPanXForCheck;
            var talentRoot = Buttons(this).FirstOrDefault(b => b.Name == "talent_node_t_root")
                ?? throw new Exception("Talent node button is missing");
            talentRoot.EmitSignal(Button.SignalName.Pressed);
            if (_game.State.Talents.GetValueOrDefault("t_root") != 1) throw new Exception("Talent UI action failed");
            if (Math.Abs(TalentPanXForCheck - talentPanBefore) > .01) throw new Exception("Talent view moved for an on-screen node");
            // 反向：点亮 col 9 的节点，它的孩子（col 10）会冒出来——那已经在框外了，画面应该滑过去。
            float panBeforeReveal = TalentPanXForCheck;
            TalentDemo(("t_hp_14", 1));
            if (TalentPanXForCheck >= panBeforeReveal) throw new Exception("Talent view did not reveal an off-screen node");
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
            // 这个独立会话也要**后期盘面**：不点「生根」「剑气」的话它是教学期状态——
            // 近战射程 150、而且根本不会自动出手，靶子摆在 400 就永远打不到。
            basicSession.State.Talents["t_auto"] = 1; basicSession.State.Talents["t_ranged"] = 1;
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
            // 等"先罩住、再碎开"那半秒走完。它是个**渲染时钟**的倒计时，所以只能按帧轮询；
            // 上限给得很宽松——无窗口自检下帧跑得飞快，真正的约束是 `RevealHold` 那 0.45 秒。
            async Task AwaitReveal()
            {
                for (int i = 0; i < 20000 && _revealTab >= 0; i++)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (_revealTab >= 0) throw new Exception("揭示动画没有收尾");
            }
            // 出生动画：新刷出来的怪应当是从地里"弹"出来的（见 SpawnSquash，只在头 0.35 秒生效）。
            // **不能拿"场上刚出现的一批"来验**——那不是新刷的，是进关/读档后对场上现有怪的"补录"，
            // 故意不弹（见 `_skipSpawnAnim`，那是为了读档不整场一起弹一下）。
            // 要等的是 `FreshSpawns` 涨一只：那才是玩家走到刷新点时"怪突然出现"的那一种，
            // 也正是这一条反馈针对的情况。放在这里是因为**这一段游戏是在自然跑的**，刷新点会真的被走到；
            // 换到冒烟末尾（重置之后）就不行了——重开一局要走到下一个刷新点，等待窗口撑不住。
            // 等待按**模拟时钟**计时，不能按帧数：`_Process` 的固定步循环让模拟按真实时间推进，
            // 所以"走到下一个刷新点"需要多少**帧**完全取决于帧率——无窗口自检一秒能跑几百帧，
            // 600 帧可能连半秒模拟都不到，窗口化下同样的 600 帧却有十秒。按帧数写就是抽签。
            int spawnedBefore = _battle.FreshSpawns;
            double spawnDeadline = _game.Elapsed + 30;
            for (int i = 0; i < 20000 && _battle.FreshSpawns == spawnedBefore && _game.Elapsed < spawnDeadline; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_battle.FreshSpawns == spawnedBefore) throw new Exception("No fresh spawn to capture the birth animation");
            // 一帧一张紧着拍：那条曲线头两三帧就走完大半，"缩到很小"只有第一张看得到。
            for (int i = 0; i < 5; i++) { await Capture("-spawn" + i); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            // 前 4 张对应 4 个页签；第 5 张是 PetPage——它的页签入口已屏蔽（剑灵系统暂缓），
            // 但页面本身与接线都留着，照旧截一张，等于给这个暂时不可达的页面留一份回归覆盖。
            for (int i = 0; i < 5; i++) { ShowPage(i); Refresh(); await Capture("-page" + i); }
            // 系统解锁门：锁上「法术」切过去该是一张**说明页**（纯文字，没有真内容），解锁后恢复。
            // 页签本身始终可见——隐藏会让上面那圈按下标取的断言全乱，见 `Main.TabSystems` 的说明。
            _game.State.UnlockedSystems.Remove(Systems.Realm);
            _game.State.Talents["t_realm"] = 0;   // 解锁有**两个来源**，只清其中一个不算锁上（见 GameSession.Unlocked）
            ShowPage(1); Refresh();
            if (!_tabs[1].Disabled) throw new Exception("A locked system's tab should be disabled");
            // 锁的三件套：页签角上的小锁、操作区上的遮罩、遮罩拦点击。
            if (!_tabBadges[1].Locked) throw new Exception("A locked tab should carry the padlock badge");
            if (!_lockMask.Locked || _lockMask.MouseFilter != MouseFilterEnum.Stop)
                throw new Exception("A locked tab should mask the work area and swallow clicks");
            if (Buttons(_page).Any()) throw new Exception("A locked page should be copy only, not the real content");
            await Capture("-locked");
            _game.State.UnlockedSystems.Add(Systems.Realm);
            ShowPage(1); Refresh();
            if (_tabs[1].Disabled) throw new Exception("Unlocking should re-enable the tab");
            if (_tabBadges[1].Locked) throw new Exception("Unlocking should drop the padlock badge");
            // **解锁那一下是"先罩住、再碎开"**（用户要求）：这一拍遮罩还压着、还拦着点击，
            // 但底下的**真内容**已经铺好了；而且**强制切到了这一页**——否则解锁发生在别的页面上，
            // 玩家根本不会注意到多了个功能。
            if (!_lockMask.Locked || _lockMask.MouseFilter != MouseFilterEnum.Stop || _selectedTab != 1)
                throw new Exception("Unlocking should switch to the new tab and hold the mask over it");
            if (!Buttons(_page).Any()) throw new Exception("The real content should already be underneath");
            await Capture("-unlocked");   // 罩住 + 碎开的那几帧
            await AwaitReveal();
            if (_lockMask.Locked || _lockMask.MouseFilter != MouseFilterEnum.Ignore)
                throw new Exception("The reveal mask should drop by itself");
            ShowPage(0); Refresh();

            // 星图与锁那三层共用同一块矩形，**方向相反的两条**都真出过问题：
            // ① 过场收 HUD 时，遮罩与炸开层必须一起收——不然序章画面上会挂着一把锁
            //    （重置会重播序章，所以在"重置完锁还挂在画面里"那里露头）。
            // ② 过场结束**不能**把锁着的星图一起点亮——否则就是"进到第 1 关，锁没了、星图却露出来"。
            SetHudVisible(false);
            if (_lockMask.Visible || _lockFx.Visible || _talentRoot.Visible)
                throw new Exception("Cutscene must hide the star map and the lock layers");
            SetHudVisible(true);
            if (!_lockMask.Visible) throw new Exception("The lock mask should come back with the HUD");
            // 锁着修行 = 星图不该露面 + 遮罩压着；解锁之后两样一起翻过来。
            _game.State.UnlockedSystems.Remove(Systems.Cultivation);
            ShowPage(0); Refresh();
            if (_talentRoot.Visible) throw new Exception("A locked 修行 must not show the star map");
            if (!_lockMask.Locked) throw new Exception("A locked 修行 must mask the work area");
            await Capture("-star-locked");   // 锁着的星图那一片：该只有遮罩与锁，不该露出节点
            _game.State.UnlockedSystems.Add(Systems.Cultivation);
            ShowPage(0); Refresh();
            await AwaitReveal();
            if (!_talentRoot.Visible || _lockMask.Locked)
                throw new Exception("Unlocking 修行 should reveal the star map and drop the mask");
            // 这一张是给"**遮罩真的从画面上消失了**"留的：`Locked = false` 只是状态，
            // 少了 `QueueRedraw` 的话上一帧那块遮罩会**一直挂着**——状态断言抓不到，只有看图才看得见。
            await Capture("-star-unlocked");

            // 星图前三列的**形状**：清空天赋、只点亮根，应当冒出**三条支**（生存 / 掉落+1 / 输出）。
            // 这一段是给"树形重排"留的验收图——断言只能验拓扑，验不了"看起来是不是三支"。
            // 拍完把天赋原样放回去：后面还有依赖盘面的断言（自动攻击、剑气都挂在这上面）。
            var keptTalents = new Dictionary<string, int>(_game.State.Talents);
            _game.State.Talents.Clear();
            if (!_talentRoot.Visible) ShowPage(0);
            ResetTalentView();   // 内容变少了，平移量得跟着归零，否则拍到的是空白（顺带验那条修复）
            Refresh(); await Capture("-root-only");
            _game.State.Talents["t_root"] = 1;
            Refresh(); await Capture("-three-branches");
            _game.State.Talents["t_drop"] = 1;
            Refresh(); await Capture("-drop-line");
            _game.State.Talents.Clear();
            foreach (var (id, level) in keptTalents) _game.State.Talents[id] = level;
            ShowPage(0); Refresh();

            // 另三条门也各走一遍「锁着 → 说明页」，确认四个页签挂的是**各自的**系统而不是同一个。
            for (int tab = 2; tab <= 3; tab++)
            {
                _game.State.UnlockedSystems.Remove(TabSystems[tab]!);
                ShowPage(tab); Refresh();
                if (!_tabs[tab].Disabled) throw new Exception($"Tab {tab} should follow its own system");
                if (!_tabBadges[tab].Locked || !_lockMask.Locked) throw new Exception($"Tab {tab} did not get locked visuals");
                if (Buttons(_page).Any()) throw new Exception($"Locked tab {tab} should be copy only");
                _game.State.UnlockedSystems.Add(TabSystems[tab]!);
                ShowPage(tab); Refresh();
                await AwaitReveal();   // 解锁那一下会先罩住半秒（见 OnSystemUnlocked），等它碎开再看
                if (_tabs[tab].Disabled || _tabBadges[tab].Locked || _lockMask.Locked) throw new Exception($"Tab {tab} did not come back");
            }
            // GM 一键解锁：四个系统一次全开。**先真的全锁上**（连修行节点那份推导来源一起撤），
            // 否则"解锁成功"可能只是因为本来就开着。
            var unlockIds = _game.Config.Rows("Talent")
                .Where(r => Systems.ByEffect.ContainsKey(r.Text("effect"))).Select(r => r.Text("id")).ToArray();
            var kept = unlockIds.ToDictionary(id => id, id => _game.State.Talents.GetValueOrDefault(id));
            foreach (var id in unlockIds) _game.State.Talents[id] = 0;
            _game.State.UnlockedSystems.Clear();
            ShowPage(1); Refresh();
            if (!TabLocked(1) || !TabLocked(2)) throw new Exception("Systems should all be locked before the GM unlock");
            Tap("GM");   // 面板是懒建的，不先开出来就没有那个按钮可点
            if (_gmRoot is null || !_gmRoot.Visible) throw new Exception("GM panel did not open");
            Tap("一键解锁");
            if (!Systems.All.All(_game.Unlocked)) throw new Exception("GM unlock did not open every system");
            ShowPage(1); Refresh();
            if (TabLocked(1) || TabLocked(2)) throw new Exception("GM unlock did not re-enable the tabs");
            CloseGm();   // 用方法关而不是 `Tap("关闭")`：同时开着别的面板时全树第一个"关闭"未必是它
            foreach (var (id, level) in kept) _game.State.Talents[id] = level;   // 把修行树还原，别搅乱后面的断言
            ShowPage(0); Refresh();
            // 星图单独留两张：一张点亮前几层（看清三态、连线、满级金框），一张把悬停说明条调出来。
            // 看完就把这些等级撤掉——后面还有重置相关的断言，别留在状态里。
            var demo = new (string Id, int Level)[] { ("t_root", 5), ("t_hp", 2), ("t_atk", 5), ("t_auto", 1), ("t_atk_01", 1), ("t_hp_01", 1), ("t_atk_02", 1) };
            foreach (var (id, level) in demo) _game.State.Talents[id] = level;
            ShowPage(0); Refresh(); await Capture("-talent");
            TalentShowTipForCapture("t_hp"); await Capture("-talent-tip"); TalentHideTipForCapture();
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
            // 普攻形态：切近战 → **点画面要真的挥出一下** → 再切回远程。
            // 点击直接调 `_GuiInput`，与 Godot 真实路由走的是同一个入口（`MouseFilter = Stop` 那条链的另一半
            // 是"战斗区里没有别的可点东西"，那是布局事实，冒烟断言不了）。不伪造视口坐标：
            // 无窗口自检下视口不一定有尺寸，推事件反而更假。
            Tap("普攻：远程");
            if (!_game.MeleeBasic || !_meleeButton.Text.StartsWith("普攻：近战"))
                throw new Exception($"GM melee toggle did not take: {_meleeButton.Text}");
            // 用**独立会话**跑这一段，与上面几场同一个套路：主线会话的自动普攻每步都会出手，
            // 会把"抬手不触发""冷却没过不再出一手"两条断言全搅乱。这一场关掉自动，场上只有玩家点出来的那一手。
            // 全新会话 = 教学期盘面：没点「剑气」所以是近战，没点「生根」所以不会自动出手。
            var melee = new GameSession(_game.Config, seed: 1) { BasicAttackEnabled = false };
            for (int cell = 0; cell < melee.Level.Cells; cell++) melee.Battle.Spawns[cell] = new() { Passed = true };
            melee.Battle.Enemies.Clear(); melee.State.Skills.Clear();
            var slam = _game.Config.Monsters["slime"];
            var dummy = new EnemyState
            {
                Id = melee.Battle.NextEnemyId++, MonsterId = slam.Id, Kind = slam.Kind,
                // 摆在 100：近战射程（150）内、停步距离（120）内 —— 站定就能砍到，且不会继续往前挤。
                X = melee.Battle.PlayerX + 100, Hp = 1e6, MaxHp = 1e6,
                Atk = 0, AttackTimer = 999, StunUntil = 1e9,
            };
            melee.Battle.Enemies.Add(dummy);
            _battle.Session = melee;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            void Click(bool pressed) => _battle._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed });
            melee.Battle.Cooldowns[GameSession.BasicAttackKey] = 0;
            double swingHp = dummy.Hp;
            Click(true);
            // 挥砍是"出手当拍进 Effects、在同一次 Step 的 TickEffects 里结算"的，点完**要先推进一步**
            // 才看得到掉血——真机上也是这个口径：点下去到伤害落地最多差一个固定步（50ms）。
            if (!melee.Effects.Any(e => e.Trajectory == "melee_slash"))
                throw new Exception("Clicking the battle view did not produce a melee swing");
            melee.Step(.05);
            if (dummy.Hp >= swingHp) throw new Exception("The melee swing did not land on the target");
            if (melee.Battle.Cooldowns[GameSession.BasicAttackKey] <= 0) throw new Exception("The swing did not start the interval");
            // 冷却没过再点一下：**不该再出一手**——点击只是"自己扣扳机"，不会凭空多出输出，
            // 这也正是「激活自动攻击」的价值所在（省的是手，不是伤害）。
            double afterOne = dummy.Hp;
            Click(true);
            melee.Step(.05);
            if (dummy.Hp < afterOne) throw new Exception("A second click landed while the interval was still running");
            // 抬手不该触发（否则一次拖拽会多打一下）。冷却清零，保证"没掉血"不是因为还没转好。
            melee.Battle.Cooldowns[GameSession.BasicAttackKey] = 0;
            melee.Effects.Clear();
            Click(false);
            melee.Step(.05);
            if (dummy.Hp < afterOne) throw new Exception("Releasing the button swung a second time");
            if (capturePath is not null)
            {
                // 留一张挥剑的图：斩击弧 + 剑本身 + 贴身站位。
                // ① **不要**在截图前 Step——一步之后那条弧已经扫过大半，拍到的会是收势；
                // ② 把 GM 面板收起来再拍，否则它正好盖在战斗区中间（后面还要用它，所以拍完开回来）。
                CloseGm();
                // 教学期气泡：这一刻还没点亮「生根」，所以"点击鼠标攻击敌人"应当飘在画面中央靠上。
                await Capture("-hint");
                melee.Effects.Clear();
                melee.Battle.Cooldowns[GameSession.BasicAttackKey] = 0;
                Click(true);
                await Capture("-melee");
                OpenGm();
            }
            _battle.Session = _game;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Tap("普攻：近战");
            if (_game.MeleeBasic) throw new Exception("GM melee toggle did not turn back off");
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
            // 突破音走的是"带专属成功音的 Act"路径；突破要花灵石，GM 之后才有，所以放在这里。
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
            // 重置后法术数为 0：开局不再白送御剑术，得自己花灵石学（见 GameSession 构造器）。
            if (_game.State.FirstKills.Count != 0 || _game.State.Skills.Count != 0) throw new Exception("Reset left progress behind");
            if (_settingsRoot.Visible) throw new Exception("Settings panel should close after reset");
            if (_preview is not null || !ReferenceEquals(_battle.Session, _game)) throw new Exception("Reset must leave preview mode and rebind the battle view");
            // 阵亡与复活的四拍：强制死一次、逐拍留图。**放在最后**——它会整关重置，
            // 放前面会把上面那些依赖关卡状态的断言搅乱。帧数是按 60fps 估的，只看个大概齐。
            // 先让游戏**自然跑几帧**把怪刷出来。**不要用同步的一大串 `Step`**：那会把这一帧的
            // 真实 delta 撑大，下一帧的固定步循环就一口气走完整个 2 秒复活读条——倒地演出整个被跳过去。
            // （排查"尸体画不出来"就是栽在这里：读条一帧走完 → EnterLevel 换了 BattleState →
            //  换场守卫把刚落的尸体全清了。真机 60fps 下不会这样。）
            // 等待按**模拟时钟**而不是帧数：同样的帧数在无窗口自检下可能连半秒模拟都不到（帧率越高模拟越慢），
            // 窗口化下却是十秒——按帧数写就是抽签。下面的 `DeathFrames` 仍按帧数，那是估演出时长，另当别论。
            double wipeDeadline = _game.Elapsed + 30;
            for (int i = 0; i < 20000 && _game.Battle.Enemies.Count == 0 && _game.Elapsed < wipeDeadline; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_game.Battle.Enemies.Count == 0) throw new Exception("No enemies on the field to wipe for the death capture");
            _game.Battle.PlayerHp = 0;   // 下一帧的固定步就会判死，不必自己 Step
            async Task DeathFrames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            await DeathFrames(5); await Capture("-death-burst");   // ① 怪物爆开的那几帧
            await Capture("-death-corpses");                       // 尸体渐隐（与上一张隔一帧，看得到残影）
            await DeathFrames(17); await Capture("-death-fall");   // ① 向后倒 + 画面压暗
            await DeathFrames(18); await Capture("-death-cut");    // ② 全暗、镜头切回关卡起点
            await DeathFrames(26); await Capture("-death-drop");   // ③ 画面变亮 + 从天上落下
            await DeathFrames(40); await Capture("-death-land");   // ④ 落地压扁 + 扬尘
            await DeathFrames(50);                                 // 让读条走完，回到常态
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
        // 货币名一律从 `item.csv` 取：写死的话改名时这里会静默留在旧名上（界面上同时出现两个叫法）。
        _wallet.Text = $"{_game.CurrencyName("gold")}  {UiKit.Number(s.Amount("gold"))}       "
            + $"{_game.CurrencyName("core")}  {s.Amount("core"):0}       攻击  {UiKit.Number(shown.Attack)}";
        _hp.MaxValue = shown.MaxHp; _hp.Value = shown.Battle.PlayerHp;
        _hpText.Text = $"气血  {UiKit.Number(shown.Battle.PlayerHp)} / {UiKit.Number(shown.MaxHp)}     {_saveStatus}";
        _stage.Text = _preview is null
            ? $"{_game.Level.Name}     第 {_game.Battle.Cell + 1:00} / {_game.Level.Cells} 格     在场 {_game.Battle.Enemies.Count} 敌"
            : $"技能预览    第 {_previewSkill + 1:00} / {PreviewSkillIds.Count} 个法术    {_game.Config.Skills[PreviewSkillId].Name}";
        _notice.Text = _gameNotice ?? _game.Message;
        if (_talentRoot.Visible) RefreshTalentMap();
        RefreshTabs();   // 解锁常常发生在战斗里（首杀小怪），页签得当场亮起来，而不是等玩家切一次页
        // 就一个开关：**亮 = 自动推进，灭 = 本关循环**。文字只写当前状态，不再写"点一下会怎样"——
        // 那半句在两种状态下各占 8 个字，是它原来必须做那么宽的原因。
        _loop.Text = s.LoopLevel ? "本关循环" : "自动推进";
        _loop.AddThemeColorOverride("font_color", s.LoopLevel ? UiKit.Muted : UiKit.Gold);
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
    /// <summary>
    /// 页签 → 系统 id。第 5 个（剑灵）留空：它本来就没有入口，不该被门挡住。
    /// 每个系统的解锁途径见 <see cref="Systems.ByEffect"/> 与 `GameSession.Unlocked`。
    /// </summary>
    private static readonly string?[] TabSystems =
        [Systems.Cultivation, Systems.Realm, Systems.Forge, Systems.Intent, null];

    private bool TabLocked(int tab) => TabSystems[tab] is { } system && !_game.Unlocked(system);

    /// <summary>上一轮的锁定状态。用来发现"**刚刚**解锁了"——解锁那一刻要炸一下，见 <see cref="OnSystemUnlocked"/>。</summary>
    private readonly bool[] _tabWasLocked = new bool[5];

    /// <summary>
    /// 解锁"揭示"的进行状态：新功能强制切过去之后，先被遮罩罩住一拍，到点那一刻碎开、露出内容。
    /// `-1` = 没有正在进行的揭示。走**渲染时钟**（`_Process` 的 delta），与其它演出同一套路。
    /// </summary>
    private const double RevealHold = .45;
    private int _revealTab = -1;
    private double _revealClock;

    private void TickSystemReveal(double delta)
    {
        if (_revealTab < 0) return;
        _revealClock -= delta;
        if (_revealClock > 0) return;
        _revealTab = -1;
        _lockFx.Play(_lockMask.GetGlobalRect().GetCenter(), 96);
        RefreshTabs();   // 遮罩落下，新内容露出来
    }

    /// <summary>过场（序章）期间收起了 HUD。</summary>
    private bool _hudHidden;

    /// <summary>
    /// 星图与锁那三层的可见性**统一在这里算**（`_talentRoot` / `_lockMask` / `_lockFx` 共用同一块矩形）。
    ///
    /// 不能只靠 `SetHudVisible(true)` 一把全亮：修行锁着时星图本来就不该显示，
    /// 而过场结束那一趟会把它一起点亮——现象就是"进到第 1 关，锁没了、星图却露出来了"。
    /// </summary>
    private void RefreshHudLayers()
    {
        bool shown = !_hudHidden;
        _talentRoot.Visible = shown && _selectedTab == 0 && _preview is null && !TabLocked(0);
        _lockMask.Visible = shown;
        _lockFx.Visible = shown;
    }

    /// <summary>
    /// 页签的可用性与配色。**也挂在 <see cref="Refresh"/> 上**：解锁可能发生在别的页面上
    /// （在战斗中击杀第一只怪就解锁了修行），页签该当场亮起来，而不是等玩家切一次页。
    /// </summary>
    private void RefreshTabs()
    {
        int fresh = -1;   // 刚刚解锁的那个页签（出循环再处理，见下）
        for (int i = 0; i < _tabs.Count; i++)
        {
            // 预览期间页面固定为法术列表，禁用页签避免切走后状态与画面不一致。
            bool locked = TabLocked(i) || _preview is not null;
            _tabs[i].Disabled = locked;
            _tabs[i].AddThemeColorOverride("font_color",
                i == _selectedTab && !locked ? UiKit.Gold : UiKit.Muted);
            if (i < _tabBadges.Count) _tabBadges[i].Locked = locked;
            // **刚刚解锁**：先记下状态、出循环再处理——处理会**切页**，切页又会绕回来调本方法，
            // 在循环里当场触发会变成一段绕来绕去的重入。
            bool wasLocked = _tabWasLocked[i];
            _tabWasLocked[i] = TabLocked(i);
            if (wasLocked && !TabLocked(i)) fresh = i;
        }
        // 选中的页锁着时压上遮罩；解锁的瞬间它跟着碎掉（见 OnSystemUnlocked）。
        // 揭示期间**无条件**压着——那半秒是刻意的"先罩住、再碎开"。
        _lockMask.Locked = _revealTab >= 0 || (_preview is null && TabLocked(_selectedTab));
        _lockMask.MouseFilter = _lockMask.Locked ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        RefreshHudLayers();
        if (fresh >= 0) OnSystemUnlocked(fresh);
    }

    /// <summary>
    /// 某个系统**刚刚**解锁：页签角上的锁炸开，并且**强制切到那一页**、用遮罩把它先罩住一拍，
    /// 到点再碎开露出内容。
    ///
    /// 为什么要强制切页 + 先罩住（用户要求）：系统是逐个开的，解锁本身在别的页面上发生
    /// （在星图上点出「法术」、在战斗里杀掉第一只怪），不切过去的话玩家根本不会注意到多了个功能；
    /// 而直接切过去、内容瞬间铺满，又容易被当成"我刚才点到哪了"。先罩住半秒再碎开，
    /// 这一下就成了一次**有始有终的揭示**，与页签上那把锁碎掉是同一拍。
    /// </summary>
    private void OnSystemUnlocked(int tab)
    {
        if (tab < _tabBadges.Count) _lockFx.Play(_tabBadges[tab].GetGlobalRect().GetCenter(), 44);
        _selectedTab = tab;
        ShowPage(tab);            // 这一趟铺的是**真页面**；遮罩只是罩在它上面，不是"锁定页"
        _revealTab = tab;
        _revealClock = RevealHold;
        RefreshTabs();
    }

    private void ShowPage(int tab)
    {
        _selectedTab = tab; _bindings.Clear();
        foreach (var child in _page.GetChildren()) { _page.RemoveChild(child); child.QueueFree(); }
        // 修行是常驻星图（不在 _page 里），它**只切可见性**、不重建——重建的话平移量与按钮身份都会丢。
        // 可见性由 `RefreshTabs → RefreshHudLayers` 一处算（还要看锁没锁、过场收没收），这里不重复写。
        RefreshTabs();
        // **不重置平移**：切走再切回来应该还停在原处。"居中"按钮已按用户要求删掉，平移全交给拖拽。
        if (_talentRoot.Visible) RefreshTalentMap();
        if (_preview is not null) { PreviewPage(); return; }
        if (TabLocked(tab)) { LockedPage(_tabs[tab].Text); return; }
        switch (tab) { case 0: break; case 1: SkillPage(); break; case 2: ForgePage(); break; case 3: IntentPage(); break; case 4: PetPage(); break; }
    }

    /// <summary>
    /// 未解锁的系统页：**说清为什么锁着、怎么解开**，而不是甩一张空页给玩家。
    /// 页签本身保留可见但置灰——一来"还有东西没开"本身就是盼头，二来隐藏页签会把冒烟里
    /// 按下标取页签的断言全打乱（`PetPage` 就是靠这个留着一份回归覆盖的）。
    /// </summary>
    private void LockedPage(string title)
    {
        UiKit.Label(_page, $"{title} · 尚未开启", 60, 140, 1200, 60, 40, UiKit.Muted);
        string hint = title switch
        {
            "修行" => "击败前方之敌，修行自会显现。",
            _ => $"在修行星图上点亮「{title}」。",   // 其余四个都挂在「生根」底下，各自花一颗灵核
        };
        UiKit.Label(_page, hint, 60, 216, 1200, 40, 24, UiKit.Muted);
    }
    private void Act(Func<bool> action, string? successSfx = null)
    {
        bool ok = action();
        // 失败音覆盖的不止"资源不足"，也包括已满级/已拥有/槽位已满；要精确区分得让 Core 返回失败原因，属于过度设计。
        PlaySfx(ok ? successSfx ?? "sfx_buy" : "sfx_deny");
        ShowPage(_selectedTab); Refresh();
    }
}
