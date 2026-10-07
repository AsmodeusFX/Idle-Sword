using Godot;
using IdleSword.Core;

namespace IdleSword.UI;

/// <summary>关卡编辑器的左侧画布：倍率曲线（自绘）。不接收输入。</summary>
public partial class LevelCurveCanvas : Control
{
    private readonly Main _host;
    public LevelCurveCanvas(Main host) { _host = host; MouseFilter = MouseFilterEnum.Ignore; }

    public override void _Draw() => _host.DrawLevelCurve(this);
}

/// <summary>
/// 关卡编辑器（开发期工具，从 GM 面板 →「编辑器与特殊功能入口 → 编辑器」进）。
///
/// **角色与节点编辑器同一条**：编辑器不是为了手动配置整个游戏的数据，而是让人**检查、修正、指引方向**。
/// 所以这一屏的重心是"看得见"——**曲线视图**（跨 100 关看七个倍率的形状）、**模型建议值与偏差**、
/// **实际数值预览**（把 `monster 模板 × 关卡倍率 × 波次系数` 算出来）、**体检清单**。
///
/// 配表口径（这一屏要把它讲清楚）：
/// - `monster.csv` = **原型模板**，只负责"各原型之间的差异"；
/// - 强度由**关卡表的七个倍率**放大：`monster 基础值 × level 倍率 × 波次系数（只有普通怪吃）`。
///
/// ⚠️ `level.csv` 的倍率列**原来是 `tools/BalanceCurve` 生成的**。用户拍板改成"**编辑器为准、模型降级成建议**"，
/// 所以那个工具也改成默认不写盘了。这里的建议值来自 `Core/Data/LevelCurve.cs`，与工具**共用同一份模型**。
/// </summary>
public partial class Main
{
    // ── 外壳 ──
    private Control _levelRoot = null!;
    private OptionButton _editorLevelSelect = null!;
    private readonly List<string> _editorLevelIds = [];
    private Label _levelSummary = null!, _levelStatus = null!;
    private SectionList _levelPanel = null!;
    private LevelCurveCanvas _levelCurve = null!;
    // ── 基础信息 ──
    private Label _levelIdText = null!;
    private LineEdit _levelName = null!, _levelOrder = null!;
    // ── 强度：七列，各配输入框 + 建议值 + 采纳按钮 ──
    private readonly Dictionary<string, LineEdit> _levelScaleInputs = [];
    private readonly Dictionary<string, Label> _levelScaleHints = [];
    // ── 挑战 ──
    private LineEdit _levelCells = null!, _waveInterval = null!, _waveCount = null!, _waveCountMax = null!;
    private LineEdit _waveHpScale = null!, _waveAtkScale = null!, _waveEliteEvery = null!;
    /// <summary>阵容那一块：怪物 id → （勾选按钮, 权重输入框）。勾上 = 这一波会刷它。</summary>
    private readonly Dictionary<string, (Button Toggle, LineEdit Weight)> _waveUnitCells = [];
    /// <summary>「挑战」分区与本编辑器的面板内容层——阵容那 8 行要等配置读完之后才补建。</summary>
    private SectionList.Section _challengeSection = null!;
    private Control _levelInner = null!;
    /// <summary>每个怪物上一次用过的权重：取消勾选再勾回来时不必重新输一遍。</summary>
    private readonly Dictionary<string, double> _waveUnitDefault = [];
    private OptionButton _waveSelect = null!, _waveElite = null!, _levelBoss = null!, _levelRift = null!;
    private OptionButton _levelFirstReward = null!, _levelRepeatReward = null!;
    private Label _waveSharedHint = null!, _waveUnitsHint = null!, _levelCellsHint = null!;
    private readonly List<string> _waveIds = [], _bossIds = [], _riftIds = [], _rewardGroups = [], _monsterIds = [];
    // ── 只读区 ──
    private Label _levelPreview = null!, _levelAudit = null!;
    // ── 待写通道 ──
    /// <summary>关卡 id → 列 → 值。**只放被明确改过的格子**，其余列原样往返——
    /// 否则编辑器开着时 AI 在磁盘上改的行会被保存悄悄改回去（这条纪律是节点编辑器踩出来的）。</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _levelPending = [];
    /// <summary>波次 id → 列 → 值。口径同上。</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _wavePending = [];
    /// <summary>波次 id → 整份阵容。阵容是**整条替换**（增删行没有"格子"可言），所以它与上面两个不一个形状。</summary>
    private readonly Dictionary<string, List<(string Monster, double Weight)>> _waveUnitPending = [];
    /// <summary>新增的关卡行 / 波次行（**末尾追加**，不动已有行的 order）。</summary>
    private readonly List<Dictionary<string, string>> _levelAdded = [], _waveAdded = [];
    /// <summary>回填各控件时置位，挡住 `TextChanged` / `ItemSelected` 反写。</summary>
    private bool _levelFilling;
    private string _editorLevelSelected = "";
    /// <summary>左侧曲线的实际值采样（按 order）。选关 / 改数时重算。</summary>
    private readonly List<(int Order, Dictionary<string, double> Values)> _curvePoints = [];
    /// <summary>曲线看哪一族：0 血量 / 1 攻击 / 2 两个都要。**状态留着**——切关、改数之后不该跳回默认。</summary>
    private int _curveTab;
    /// <summary>逐条线的开关（列名 → 画不画）。没登记过的按"画"处理。</summary>
    private readonly Dictionary<string, bool> _curveOn = [];
    /// <summary>模型建议那组虚线整体开关。</summary>
    private bool _curveShowModel = true;
    /// <summary>标签页按钮与逐条开关按钮（建一次，按当前标签页显隐）。</summary>
    private readonly List<(Button Button, int Tab)> _curveTabButtons = [];
    private readonly List<(Button Button, string Column, bool IsModel)> _curveToggles = [];
    /// <summary>与工具共用的模型（算不出来就是 null，界面降级成"没有建议值"）。</summary>
    private LevelCurve.Model? _levelModel;

    /// <summary>七个倍率列的显示顺序与中文名。列名必须与 `level.csv` 表头一致。</summary>
    private static readonly (string Column, string Label)[] LevelScales =
        [("normal_hp", "小怪血量"), ("normal_atk", "小怪攻击"),
         ("elite_hp", "精英血量"), ("elite_atk", "精英攻击"),
         ("boss_hp", "BOSS 血量"), ("boss_atk", "BOSS 攻击"),
         ("rift_hp", "裂隙血量")];

    private const string LevelPath = "res://Config/Tables/level.csv";
    private const string WavePath = "res://Config/Tables/wave.csv";
    private const string WaveUnitPath = "res://Config/Tables/wave_unit.csv";

    public void ToggleLevelEditor()
    {
        bool open = !_levelRoot.Visible;
        _levelRoot.Visible = open;
        if (open) { LevelEditorReload(); LevelRefreshUi(); Sfx.Click(); }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  外壳
    // ══════════════════════════════════════════════════════════════════════
    private void BuildLevelEditor()
    {
        _levelRoot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        UiKit.Place(_levelRoot, 0, 0, 1920, 1080);
        AddChild(_levelRoot);

        // 背板**完全不透明**（同节点编辑器）：底下游戏的 HUD 透上来会与这里的字叠在一起，看着像排版坏了。
        var backdrop = new ColorRect { Color = new Color(0.02f, 0.04f, 0.06f), MouseFilter = Control.MouseFilterEnum.Stop };
        UiKit.Place(backdrop, 0, 0, 1920, 1080); _levelRoot.AddChild(backdrop);

        UiKit.Label(_levelRoot, "关卡编辑器", 30, 20, 700, 46, 34, UiKit.Gold);
        UiKit.Label(_levelRoot, "选一关，改它的长度 / 强度倍率 / 波次与首领；保存即生效。"
            + "左侧是跨关卡的倍率曲线（实线 = 当前配置，虚线 = 模型建议）——两条分开就说明这一关被手工调离过。",
            30, 70, 1500, 30, 19, UiKit.Muted);
        // ⚠️ 按钮文案**不能以既有被测前缀开头**：冒烟取按钮用的是全树第一个 `StartsWith`，而隐藏面板仍留在树上。
        UiKit.Button(_levelRoot, "试打这一关", 1350, 24, 140, 44, LevelTryPlay);
        UiKit.Button(_levelRoot, "保存", 1500, 24, 120, 44, LevelSave, true);
        UiKit.Button(_levelRoot, "重新读取", 1630, 24, 140, 44, LevelEditorReload);
        UiKit.Button(_levelRoot, "关闭", 1780, 24, 120, 44, ToggleLevelEditor);

        // 关卡下拉：从 config 现取（**不写死 100 关**）。
        UiKit.Label(_levelRoot, "关卡", 30, 106, 76, 42, 19, UiKit.Muted);
        _editorLevelSelect = new OptionButton();
        _editorLevelSelect.AddThemeFontSizeOverride("font_size", 19);
        UiKit.Place(_editorLevelSelect, 110, 106, 320, 42); _levelRoot.AddChild(_editorLevelSelect);
        _editorLevelSelect.ItemSelected += index =>
        {
            if (_levelFilling || index < 0 || index >= _editorLevelIds.Count) return;
            _editorLevelSelected = _editorLevelIds[(int)index];
            LevelRefreshUi();
        };
        _levelSummary = UiKit.Label(_levelRoot, "", 450, 106, 760, 42, 19, UiKit.Text);

        // 左侧曲线（自绘）+ 两条控制带：上面选看哪一族，下面选画哪几条线。
        // 曲线一次全画七条时"不知道谁是谁"，所以这里把**图例做成了开关**——按钮上直接写这条线是什么。
        UiKit.PanelAt(_levelRoot, 20, 150, 1170, 900, UiKit.Ink);
        BuildCurveControls();
        _levelCurve = new LevelCurveCanvas(this) { ClipContents = true };
        UiKit.Place(_levelCurve, 30, 252, 1150, 788); _levelRoot.AddChild(_levelCurve);

        // 右侧分区面板。脚注写清"保存即生效"。
        _levelPanel = new SectionList(_levelRoot, 1202, 160, 696,
            "保存即生效：这一屏改的是 level.csv / wave.csv / wave_unit.csv 三张表，走同一次事务写盘。");
        BuildLevelSections();
        // ⚠️ 状态给 `Acting: true`：关卡编辑器**没有"选中态"这个维度**——只要选了关卡，
        // 这一屏的每一行都该在。传 false 会让所有行（默认 `RowWhen.Acting`）集体隐藏，
        // 面板只剩标题（实机截图抓到过一次）。
        _levelPanel.Layout(new SectionState(true, false));
        RefreshCurveControls();
        LevelEditorReload();
        LevelRefreshUi();
    }

    /// <summary>
    /// 曲线上面那两条控制带：**标签页**（先选看哪一族）+ **逐条开关**（按钮上写这条线是什么）。
    /// 开关按钮**一次建全**（血量四条 + 攻击三条 + 模型建议），切换标签页时只改显隐——
    /// 动态增删控件比一次建好更绕，而且位置会跳。
    /// </summary>
    private void BuildCurveControls()
    {
        // 按钮一律用 18 号字 + 内边距 2（紧凑）：默认那套（21 号 + 8）在 130 宽里放不下
        // 「☑ 裂隙血量」这几个字（实机截图里被截成了"小怪皿"）。
        (string Label, int Tab)[] tabs = [("血量倍率", 0), ("攻击倍率", 1), ("两个都要", 2)];
        for (int i = 0; i < tabs.Length; i++)
        {
            int tab = tabs[i].Tab;
            var button = Compact(_levelRoot, tabs[i].Label, 30 + i * 150, 166, 140, 36);
            button.Pressed += () => { _curveTab = tab; RefreshCurveControls(); };
            _curveTabButtons.Add((button, tab));
        }
        // 「模型建议」放在标签页那一行：它管的是**所有虚线**，不属于任何一族。
        var model = Compact(_levelRoot, "模型建议", 1000, 166, 140, 36);
        model.Pressed += () => { _curveShowModel = !_curveShowModel; RefreshCurveControls(); };
        _curveToggles.Add((model, "", true));

        (string Label, string Column)[] series =
        [
            ("小怪血量", "normal_hp"), ("精英血量", "elite_hp"), ("BOSS血量", "boss_hp"), ("裂隙血量", "rift_hp"),
            ("小怪攻击", "normal_atk"), ("精英攻击", "elite_atk"), ("BOSS攻击", "boss_atk"),
        ];
        for (int i = 0; i < series.Length; i++)
        {
            string column = series[i].Column;
            var button = Compact(_levelRoot, series[i].Label, 30 + i * 138, 210, 130, 34);
            button.Pressed += () =>
            {
                _curveOn[column] = !_curveOn.GetValueOrDefault(column, true);
                RefreshCurveControls();
            };
            _curveToggles.Add((button, column, false));
        }
    }

    /// <summary>控制带上的紧凑按钮：18 号字 + 内边距 2，够放下「☑ 裂隙血量」。</summary>
    private static Button Compact(Control parent, string text, float x, float y, float w, float h)
    {
        var button = UiKit.Button(parent, text, x, y, w, h, () => { }, pad: 2);
        button.AddThemeFontSizeOverride("font_size", 18);
        return button;
    }

    /// <summary>刷新控制带的外观：标签页只有当前那个是金的；开关按开/关换符号与颜色；
    /// 并且**只显示当前标签页那一族的开关**（切到攻击族就只剩三条 + 模型建议）。</summary>
    private void RefreshCurveControls()
    {
        foreach (var (button, tab) in _curveTabButtons)
        {
            bool on = tab == _curveTab;
            button.AddThemeColorOverride("font_color", on ? UiKit.Gold : UiKit.Muted);
            button.Text = (on ? "▸ " : "  ") + button.Text.TrimStart('▸', ' ');
        }
        var active = CurveColumnsForCheck().ToHashSet();
        foreach (var (button, column, isModel) in _curveToggles)
        {
            bool on = isModel ? _curveShowModel : _curveOn.GetValueOrDefault(column, true);
            button.Text = (on ? "☑ " : "☐ ") + CurveToggleLabel(column, isModel);
            button.AddThemeColorOverride("font_color", on ? UiKit.Jade : UiKit.Muted);
            button.Visible = isModel || active.Contains(column);
        }
        _levelCurve.QueueRedraw();
    }

    private static string CurveToggleLabel(string column, bool isModel) => isModel ? "模型建议" : column switch
    {
        "normal_hp" => "小怪血量", "elite_hp" => "精英血量", "boss_hp" => "BOSS血量", "rift_hp" => "裂隙血量",
        "normal_atk" => "小怪攻击", "elite_atk" => "精英攻击", _ => "BOSS攻击",
    };

    /// <summary>
    /// 建右侧那七个分区。控件的位置由 `SectionList` 每次重排时写（这里建的时候 y 一律 0）。
    ///
    /// ⚠️ 下面四个小助手**建完就挂成行**（`Lab` / `Note` / `Input` / `Drop`）——不要绕过它们直接 `UiKit.Label`：
    /// 没挂进 `Row` 的控件**永远是可见的**、而且停在建时的 y=0 上，会在面板顶上糊成一团（实机截图抓到过）。
    /// </summary>
    private void BuildLevelSections()
    {
        Control inner = _levelPanel.Inner;
        _levelInner = inner;
        const float px = 8;
        SectionList.Section Section(string title, float height) => _levelPanel.Add(title, height);
        void Row(SectionList.Section s, Control c, float dy, RowWhen when = RowWhen.Acting) => SectionList.Row(s, c, dy, when);
        Label Lab(SectionList.Section s, float x, float dy, float w, string text, Color? color = null)
        {
            var label = UiKit.Label(inner, text, px + x, 0, w, 38, 19, color ?? UiKit.Muted);
            Row(s, label, dy);
            return label;
        }
        /// 一行说明（可换行、封顶两行）
        Label Note(SectionList.Section s, float dy, string text, float h = 40)
        {
            var wrapped = UiKit.WrappedCapped(inner, text, px, 0, 680, h, 2, 17, UiKit.Muted);
            Row(s, wrapped, dy);
            return wrapped;
        }
        // 提示一律用**自动换行 + 封顶一行**：普通 `Label` 不换行，`Size.X` 会被顶到整行文字宽
        // （实测一句 35 字的提示撑到 1032 宽，横向撞上了隔壁按钮）。换行 + 省略号既保住了宽度，
        // 也让"文字太长"这件事在截图上看得出（而不是悄悄糊到别的控件上）。
        Label Hint(SectionList.Section s, float x, float dy, float w, string text = "")
        {
            var label = UiKit.WrappedCapped(inner, text, px + x, 0, w, 38, 1, 17, UiKit.Muted);
            Row(s, label, dy);
            return label;
        }
        LineEdit Input(SectionList.Section s, float x, float dy, float w, Action<string> onEdit)
        {
            var edit = new LineEdit();
            edit.AddThemeFontSizeOverride("font_size", 19);
            UiKit.Place(edit, px + x, 0, w, 38); inner.AddChild(edit);
            edit.TextChanged += text => { if (!_levelFilling) onEdit(text.Trim()); };
            Row(s, edit, dy);
            return edit;
        }
        OptionButton Drop(SectionList.Section s, float x, float dy, float w, List<string> ids, Action<string> onPick)
        {
            var list = new OptionButton();
            list.AddThemeFontSizeOverride("font_size", 19);
            UiKit.Place(list, px + x, 0, w, 38); inner.AddChild(list);
            foreach (string id in ids) list.AddItem(id);
            list.ItemSelected += index => { if (!_levelFilling && index >= 0 && index < ids.Count) onPick(ids[(int)index]); };
            Row(s, list, dy);
            return list;
        }

        // ① 提示：状态消息独占一块（有字才出现）。
        var prompt = Section("提示", 56);
        _levelStatus = UiKit.Capped(UiKit.Wrapped(inner, "", px, 0, 680, 56, 19, UiKit.Muted), 2);
        Row(prompt, _levelStatus, 0, RowWhen.Filled);

        // ② 基础信息
        var basic = Section("基础信息", 190);
        Lab(basic, 0, 0, 120, "关卡 id");
        _levelIdText = Lab(basic, 130, 0, 540, "", UiKit.Text);
        Lab(basic, 0, 46, 120, "名称");
        _levelName = Input(basic, 130, 46, 300, text => LevelCell("name", text));
        Lab(basic, 0, 92, 120, "顺序");
        _levelOrder = Input(basic, 130, 92, 100, text => LevelCell("order", text));
        Hint(basic, 240, 92, 430, "改顺序会连带波次规模（越往后每波刷得越多），不只是排个队");
        // 模型建议值按**已保存**的配置算（模型读的是磁盘上的表）；改了格数 / 首领之后「重新读取」会重算。
        Note(basic, 138, "模型建议值按「已保存的」配置算——改了格数或首领之后，点「重新读取」重算一遍。");

        // ③ 强度：七列，每列"输入 + 模型建议 + 采纳"。这一区是"检查 / 指引"的主战场。
        var power = Section("强度倍率（是 ×N，不是百分数）", 402);
        Note(power, 0, "这七列本身就是倍率：×1 = 不放大、×12.5 = 放大到 12.5 倍。最终数值 = monster.csv 的基础值"
            + " × 这里的倍率 × 波次系数（只有普通怪吃）。前 3 关是手抠的教学段，"
            + "它们的 BOSS 血量本来就不跟模型走。");
        for (int i = 0; i < LevelScales.Length; i++)
        {
            var (column, label) = LevelScales[i];
            float dy = 84 + i * 46;   // 前 56 留给上面那句说明（它是两行，49 高——行距必须让开它）
            Lab(power, 0, dy, 120, label);
            _levelScaleInputs[column] = Input(power, 130, dy, 110, text => LevelScaleChanged(column, text));
            _levelScaleHints[column] = Hint(power, 248, dy, 250);
            // 紧凑按钮（18 号字 + 内边距 2）：默认那套的最小高是 45，会把行距顶穿（与 GM 面板同一处理）。
            var adopt = UiKit.Button(inner, "采纳", px + 500, 0, 70, 38, () => LevelAdopt(column), pad: 2);
            adopt.AddThemeFontSizeOverride("font_size", 18);
            adopt.TooltipText = "把模型算出来的这一列填进待写值（模型口径见 balance_ttk.md）。";
            Row(power, adopt, dy);
        }

        // ④ 挑战：长度 / 波次 / 首领 / 奖励
        var challenge = Section("挑战", 656);
        Lab(challenge, 0, 0, 120, "格数");
        _levelCells = Input(challenge, 130, 0, 100, text => LevelCell("cells", text));
        _levelCellsHint = Hint(challenge, 240, 0, 430);
        Lab(challenge, 0, 46, 120, "波次");
        _waveSelect = Drop(challenge, 130, 46, 180, _waveIds, id => { LevelCell("wave_id", id); LevelRefreshUi(); });
        // 「复制成专用」：把当前这条共享波次抄一份新的（波次行 + 阵容），把本关指过去。
        // 这是"只想改这一关"的唯一正确做法——直接在共享模板上改会连带同 wave 的十几关。
        var copyWave = UiKit.Button(inner, "复制成专用", px + 320, 0, 120, 38, LevelCopyWavePrivate);
        copyWave.TooltipText = "把当前波次抄一份新的、只给这一关用；原共享模板一个字不动。";
        Row(challenge, copyWave, 46);
        // "这条被 N 关共用"独占一行（它是要紧的警告，不该被省略号吃掉）。
        _waveSharedHint = Hint(challenge, 0, 92, 680);
        Lab(challenge, 0, 138, 120, "两波间隔");
        _waveInterval = Input(challenge, 130, 138, 90, text => WaveCell("interval", text));
        Lab(challenge, 240, 138, 110, "每波只数");
        _waveCount = Input(challenge, 350, 138, 90, text => WaveCell("count", text));
        Lab(challenge, 460, 138, 70, "封顶");
        _waveCountMax = Input(challenge, 530, 138, 90, text => WaveCell("count_max", text));
        Lab(challenge, 0, 184, 120, "波次系数");
        _waveHpScale = Input(challenge, 130, 184, 90, text => WaveCell("hp_scale", text));
        Lab(challenge, 240, 184, 110, "攻击系数");
        _waveAtkScale = Input(challenge, 350, 184, 90, text => WaveCell("atk_scale", text));
        Hint(challenge, 460, 184, 210, "两者 ≤ 5，只乘普通怪");
        Lab(challenge, 0, 230, 120, "精英每 N 波");
        _waveEliteEvery = Input(challenge, 130, 230, 90, text => WaveCell("elite_every", text));
        _waveElite = Drop(challenge, 240, 230, 160, _monsterIds, id => WaveCell("elite_id", id));
        Hint(challenge, 410, 230, 260, "填 99 = 整关不出精英");
        // 阵容：**一行一个怪物、勾选即入队**（原来要手输怪物 id，对人不友好——用户实机反馈）。
        // ⚠️ 那 8 行**不能在这里建**：怪物列表要等 `LevelEditorReload` 读了 `monster.csv` 才有内容，
        // 此刻 `_monsterIds` 还是空的（与下拉那次同一个坑——建出来的行会一个都没有）。
        // 所以这里只放标题，行由 `BuildRosterRows()` 在读完配置之后补上。
        Lab(challenge, 0, 276, 120, "阵容");
        _challengeSection = challenge;
        _waveUnitsHint = Note(challenge, 452, "", 49);
        Lab(challenge, 0, 512, 120, "首领");
        _levelBoss = Drop(challenge, 130, 512, 220, _bossIds, id => LevelCell("boss_id", id));
        Lab(challenge, 370, 512, 70, "裂隙");
        _levelRift = Drop(challenge, 440, 512, 220, _riftIds, id => LevelCell("rift_id", id));
        Lab(challenge, 0, 558, 120, "首杀奖励");
        _levelFirstReward = Drop(challenge, 130, 558, 220, _rewardGroups, id => LevelCell("first_reward", id));
        Lab(challenge, 370, 558, 90, "重复奖励");
        _levelRepeatReward = Drop(challenge, 460, 558, 200, _rewardGroups, id => LevelCell("repeat_reward", id));
        Note(challenge, 604, "勾上的才进这一波；权重是比例不是只数——整波只数由「每波只数」与关卡系数定，再按比例摊给各模板。");

        // ⑤ 实际数值（只读）：把"模板 × 关卡倍率 × 波次系数"直接算出来摆在界面上。
        var preview = Section("实际数值", 150);
        _levelPreview = UiKit.Capped(UiKit.Wrapped(inner, "", px, 0, 680, 130, 19, UiKit.Text), 6);
        Row(preview, _levelPreview, 0, RowWhen.Always);

        // ⑥ 体检（只读）
        var audit = Section("体检", 150);
        _levelAudit = UiKit.Capped(UiKit.Wrapped(inner, "", px, 0, 680, 130, 18, UiKit.Muted), 8);
        Row(audit, _levelAudit, 0, RowWhen.Always);

        // ⑦ 新增关卡（末尾追加）
        var add = Section("新增关卡", 122);
        Note(add, 0, "以当前这一关为模板复制一份追加到末尾：自动分配 id 与 order、复用两个奖励组、"
            + "七列填模型建议值。不动任何已有行的 order。", 44);
        var append = UiKit.Button(inner, "追加一关", px, 0, 140, 38, LevelAppend);
        append.TooltipText = "追加后 tests 里那批「正好 100 关」的断言会红，要一起更新（体检区会列出来）。";
        Row(add, append, 50);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  读：磁盘 + 待写 合并出"即将写下去的那一份"
    // ══════════════════════════════════════════════════════════════════════
    private List<CsvRow> LevelDisk() => CsvTable.Parse("level.csv", Godot.FileAccess.GetFileAsString(LevelPath));
    private List<CsvRow> WaveDisk() => CsvTable.Parse("wave.csv", Godot.FileAccess.GetFileAsString(WavePath));
    private List<CsvRow> WaveUnitDisk() => CsvTable.Parse("wave_unit.csv", Godot.FileAccess.GetFileAsString(WaveUnitPath));

    /// <summary>某一关某列的**当前值**（待写优先，否则磁盘原样）。</summary>
    private string LevelValue(string id, string column)
    {
        if (_levelPending.TryGetValue(id, out var cells) && cells.TryGetValue(column, out string? v)) return v;
        if (_levelAdded.FirstOrDefault(r => r["id"] == id) is { } added) return added.GetValueOrDefault(column, "");
        return LevelDisk().FirstOrDefault(r => r.Text("id") == id)?.Text(column) ?? "";
    }

    /// <summary>某条波次某列的当前值。口径同上。</summary>
    private string WaveValue(string id, string column)
    {
        if (_wavePending.TryGetValue(id, out var cells) && cells.TryGetValue(column, out string? v)) return v;
        if (_waveAdded.FirstOrDefault(r => r["id"] == id) is { } added) return added.GetValueOrDefault(column, "");
        return WaveDisk().FirstOrDefault(r => r.Text("id") == id)?.Text(column) ?? "";
    }

    /// <summary>某条波次的阵容（待写优先，否则磁盘原样）。</summary>
    private List<(string Monster, double Weight)> WaveUnitsOf(string id)
    {
        if (_waveUnitPending.TryGetValue(id, out var units)) return units;
        return WaveUnitDisk().Where(r => r.Text("wave_id") == id)
            .Select(r => (r.Text("monster_id"), r.Number("weight"))).ToList();
    }

    private double LevelNumber(string id, string column) =>
        double.TryParse(LevelValue(id, column), out double v) ? v : 0;
    private double WaveNumber(string id, string column) =>
        double.TryParse(WaveValue(id, column), out double v) ? v : 0;

    // ── 写：只记被明确改过的格子 ──
    private void LevelCell(string column, string value)
    {
        if (_editorLevelSelected.Length == 0) return;
        if (!_levelPending.TryGetValue(_editorLevelSelected, out var cells)) _levelPending[_editorLevelSelected] = cells = [];
        cells[column] = value;
        LevelRefreshUi();
    }

    private void WaveCell(string column, string value)
    {
        string wave = LevelValue(_editorLevelSelected, "wave_id");
        if (wave.Length == 0) return;
        if (!_wavePending.TryGetValue(wave, out var cells)) _wavePending[wave] = cells = [];
        cells[column] = value;
        LevelRefreshUi();
    }

    /// <summary>改一格强度倍率。**这里不做百分数换算**——这七列本身就是倍率（`×12.5` 就是放大 12.5 倍），
    /// 与 `Talent.csv` 的 `atk`/`hp`（那两支是并进 `1 + …` 乘区的分数）**不是一回事**，别照搬那边的口径。</summary>
    private void LevelScaleChanged(string column, string text)
    {
        if (!double.TryParse(text, out double v) || v <= 0)
        {
            _levelScaleHints[column].Text = "要 > 0 的数";
            return;
        }
        LevelCell(column, v.ToString("0.####"));
    }

    /// <summary>点「采纳」：把模型建议值填进待写。模型算不出来（配置有问题）时只提示，不写。</summary>
    private void LevelAdopt(string column)
    {
        if (_levelModel is null) { _levelStatus.Text = "模型当前算不出建议值（配置有问题），先修体检里那几条。"; return; }
        int order = (int)LevelNumber(_editorLevelSelected, "order");
        if (order < 1 || order > _levelModel.Count) { _levelStatus.Text = $"顺序 {order} 不在模型范围内。"; return; }
        LevelCell(column, _levelModel.Suggested(order)[column].ToString("0.####"));
        _levelStatus.Text = $"「{column}」已采纳模型建议值。";
    }

    /// <summary>勾 / 取消一个怪物。勾上时给个默认权重（沿用原来那条的权重，或 1）。</summary>
    private void LevelWaveUnitToggled(string monster)
    {
        string wave = LevelValue(_editorLevelSelected, "wave_id");
        if (wave.Length == 0) return;
        var units = WaveUnitsOf(wave).ToList();
        int at = units.FindIndex(u => u.Monster == monster);
        if (at >= 0) units.RemoveAt(at);
        else units.Add((monster, _waveUnitDefault.GetValueOrDefault(monster, 1)));
        if (units.Count == 0) { _waveUnitsHint.Text = "阵容不能为空（这一波会刷不出怪）——至少勾一个。"; return; }
        _waveUnitPending[wave] = units;
        LevelRefreshUi();
    }

    /// <summary>改某个怪物的权重。没勾上的改动直接忽略（那一行本来就是灰的）。</summary>
    private void LevelWaveUnitWeight(string monster, string text)
    {
        string wave = LevelValue(_editorLevelSelected, "wave_id");
        if (wave.Length == 0) return;
        var units = WaveUnitsOf(wave).ToList();
        int at = units.FindIndex(u => u.Monster == monster);
        if (at < 0) return;
        if (!double.TryParse(text, out double weight) || weight <= 0)
        {
            _waveUnitsHint.Text = $"{MonsterName(monster)} 的权重要是 > 0 的数。";
            return;
        }
        units[at] = (monster, weight);
        _waveUnitDefault[monster] = weight;   // 记住它，取消勾选再勾回来时还用这个值
        _waveUnitPending[wave] = units;
        LevelRefreshUi();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  刷新
    // ══════════════════════════════════════════════════════════════════════
    private void LevelEditorReload()
    {
        _levelPending.Clear(); _wavePending.Clear(); _waveUnitPending.Clear();
        _levelAdded.Clear(); _waveAdded.Clear();
        // 下拉的条目**全部从 config 现取**：加了怪物 / 奖励组 / 波次不用回来改这里，也不会漏。
        var cfg = _game.Config;
        Fill(_editorLevelIds, cfg.Levels.OrderBy(l => l.Order).Select(l => l.Id));
        Fill(_waveIds, cfg.Waves.Keys.OrderBy(k => k));
        Fill(_bossIds, cfg.Monsters.Values.Where(m => m.Kind == "boss").Select(m => m.Id));
        Fill(_riftIds, cfg.Monsters.Values.Where(m => m.Kind == "rift").Select(m => m.Id));
        Fill(_monsterIds, cfg.Monsters.Values.Where(m => m.Kind is "normal" or "elite").Select(m => m.Id));
        Fill(_rewardGroups, cfg.Rows("drop").Select(r => r.Text("group_id")).Distinct().OrderBy(g => g));
        // 阵容那 8 行**到这一步才能建**（怪物列表刚刚才有内容）。只建一次——它是固定的几行。
        if (_waveUnitCells.Count == 0) BuildRosterRows();
        _levelModel = LevelCurve.TryCompute(file => Godot.FileAccess.GetFileAsString("res://Config/Tables/" + file));
        // 默认落在**当前正在打的那一关**（大多数时候"我要改的就是它"）。
        if (_editorLevelSelected.Length == 0 || !_editorLevelIds.Contains(_editorLevelSelected))
            _editorLevelSelected = _editorLevelIds.FirstOrDefault(l => cfg.Levels.First(x => x.Id == l).Order == _game.Level.Order)
                ?? _editorLevelIds.FirstOrDefault() ?? "";
        // ⚠️ **每个下拉都要在这里重填**：它们是在 `BuildLevelSections` 里建的，那时上面的 id 列表还是空的
        // （配置要等这一步才读）。只填电平那一个，其余的会一直是空框（实机截图抓到过）。
        FillDrop(_editorLevelSelect, _editorLevelIds, id => cfg.Rows("level").First(r => r.Text("id") == id).Text("name"));
        FillDrop(_waveSelect, _waveIds);
        FillDrop(_waveElite, _monsterIds);
        FillDrop(_levelBoss, _bossIds);
        FillDrop(_levelRift, _riftIds);
        FillDrop(_levelFirstReward, _rewardGroups);
        FillDrop(_levelRepeatReward, _rewardGroups);
        LevelRefreshUi();
    }

    /// <summary>
    /// 建阵容那 8 行（普通怪 + 精英各一行）：一行 = 勾选按钮（这个怪物进不进这一波）+ 权重输入框。
    /// **两列排布**（8 个怪物占 4 行）——一列排下来太高，而这一区本来就已经很长了。
    /// 只建一次；勾选态与权重由 `LevelRefreshUi` 每次刷新。
    /// </summary>
    private void BuildRosterRows()
    {
        for (int i = 0; i < _monsterIds.Count; i++)
        {
            string monster = _monsterIds[i];
            float x = i % 2 == 0 ? 0 : 340;
            float dy = 276 + i / 2 * 40;
            var toggle = Compact(_levelInner, MonsterName(monster), 8 + x + 120, 0, 150, 34);
            toggle.Pressed += () => LevelWaveUnitToggled(monster);
            SectionList.Row(_challengeSection, toggle, dy);
            var box = new LineEdit();
            box.AddThemeFontSizeOverride("font_size", 19);
            UiKit.Place(box, 8 + x + 280, 0, 60, 38);
            _levelInner.AddChild(box);
            box.TextChanged += text => { if (!_levelFilling) LevelWaveUnitWeight(monster, text.Trim()); };
            SectionList.Row(_challengeSection, box, dy);
            _waveUnitCells[monster] = (toggle, box);
        }
    }

    /// <summary>重填一个下拉：条目**从 config 现取**（加了怪物 / 奖励组不用回来改这里，也不会漏）。
    /// `label` 给了就用它当显示文案（关卡下拉显示中文名，其余显示 id）。</summary>
    private static void FillDrop(OptionButton list, List<string> ids, Func<string, string>? label = null)
    {
        list.Clear();
        foreach (string id in ids) list.AddItem(label?.Invoke(id) ?? id);
    }

    private static void Fill(List<string> target, IEnumerable<string> items)
    {
        target.Clear();
        target.AddRange(items);
    }

    private void LevelRefreshUi()
    {
        if (_editorLevelSelected.Length == 0) return;
        _levelFilling = true;
        var cfg = _game.Config;
        int order = (int)LevelNumber(_editorLevelSelected, "order");
        string wave = LevelValue(_editorLevelSelected, "wave_id");

        _levelIdText.Text = _editorLevelSelected + "（id 不可改——改它等于废档：存档按 id 记进度）";
        _levelName.Text = LevelValue(_editorLevelSelected, "name");
        _levelOrder.Text = LevelValue(_editorLevelSelected, "order");
        _levelCells.Text = LevelValue(_editorLevelSelected, "cells");
        _editorLevelSelect.Selected = Math.Max(0, _editorLevelIds.IndexOf(_editorLevelSelected));
        _levelSummary.Text = $"第 {order} 关 · 波次 {wave} · {(int)LevelNumber(_editorLevelSelected, "cells")} 格";

        for (int i = 0; i < LevelScales.Length; i++)
        {
            string column = LevelScales[i].Column;
            double actual = LevelNumber(_editorLevelSelected, column);
            _levelScaleInputs[column].Text = actual.ToString("0.####");
            string hint = "（无建议）";
            if (_levelModel is not null && order >= 1 && order <= _levelModel.Count)
            {
                double want = _levelModel.Suggested(order)[column];
                double delta = actual - want;
                // 判"一致"用的是**显示精度**（4 位小数）：采纳写进去的就是 4 位，
                // 拿全精度比会让刚采纳完还显示"差 -0.0%"，看着像 bug。
                hint = Math.Abs(delta) < 5e-5
                    ? $"模型 ×{want:0.####} ✓"
                    : $"模型 ×{want:0.####}（差 {delta / want:+0.0%;-0.0%}）";
            }
            _levelScaleHints[column].Text = hint;
        }

        // 波次那一组：先按共享模板填，再标出"这条被 N 关用"。
        if (wave.Length > 0)
        {
            _waveInterval.Text = WaveValue(wave, "interval");
            _waveCount.Text = WaveValue(wave, "count");
            _waveCountMax.Text = WaveValue(wave, "count_max");
            _waveHpScale.Text = WaveValue(wave, "hp_scale");
            _waveAtkScale.Text = WaveValue(wave, "atk_scale");
            _waveEliteEvery.Text = WaveValue(wave, "elite_every");
            _waveSelect.Selected = Math.Max(0, _waveIds.IndexOf(wave));
            _waveElite.Selected = Math.Max(0, _monsterIds.IndexOf(WaveValue(wave, "elite_id")));
            var units = WaveUnitsOf(wave);
            // 阵容那一块：勾选态 + 权重逐行同步；没勾上的那一行置灰（位置留着，勾回来不用重排）。
            foreach (var (monster, (toggle, weightBox)) in _waveUnitCells)
            {
                var unit = units.FirstOrDefault(u => u.Monster == monster);
                bool on = units.Any(u => u.Monster == monster);
                toggle.Text = (on ? "☑ " : "☐ ") + MonsterName(monster);
                toggle.AddThemeColorOverride("font_color", on ? UiKit.Jade : UiKit.Muted);
                weightBox.Text = unit.Monster == monster ? unit.Weight.ToString("0.##") : _waveUnitDefault.GetValueOrDefault(monster, 1).ToString("0.##");
                weightBox.Editable = on;
            }
            int shared = cfg.Levels.Count(l => LevelValue(l.Id, "wave_id") == wave);
            _waveSharedHint.Text = shared > 1
                ? $"⚠ 这条被 {shared} 关共用，改它会一起变。要只改这一关，点右边「复制成专用」"
                : "这一关独占这条波次";
            _waveUnitsHint.Text = DescribeUnits(units, WaveNumber(wave, "count"), WaveNumber(wave, "count_max"));
        }
        _levelBoss.Selected = Math.Max(0, _bossIds.IndexOf(LevelValue(_editorLevelSelected, "boss_id")));
        _levelRift.Selected = Math.Max(0, _riftIds.IndexOf(LevelValue(_editorLevelSelected, "rift_id")));
        _levelFirstReward.Selected = Math.Max(0, _rewardGroups.IndexOf(LevelValue(_editorLevelSelected, "first_reward")));
        _levelRepeatReward.Selected = Math.Max(0, _rewardGroups.IndexOf(LevelValue(_editorLevelSelected, "repeat_reward")));

        int cells = (int)LevelNumber(_editorLevelSelected, "cells");
        _levelCellsHint.Text = $"关卡长度 = {cells} 格 × {cfg.Setting("cell_width"):F0} 世界单位。"
            + "改小会让停在后半段的旧存档被判位置非法而归档。";
        _levelFilling = false;

        _levelPreview.Text = DescribeActualValues();
        _levelAudit.Text = LevelAudit();
        _levelPanel.Layout(new SectionState(true, false));   // 见 BuildLevelEditor 里那条说明：这一屏没有"选中态"
        _levelCanvas_Refresh();
    }

    /// <summary>重算曲线采样点（**含待写改动**，所以改一格当场就能看见曲线动），然后重画。</summary>
    private void _levelCanvas_Refresh()
    {
        _curvePoints.Clear();
        foreach (string id in _editorLevelIds)
            _curvePoints.Add(((int)LevelNumber(id, "order"),
                LevelScales.ToDictionary(s => s.Column, s => LevelNumber(id, s.Column))));
        _curvePoints.Sort((a, b) => a.Order.CompareTo(b.Order));
        _levelCurve.QueueRedraw();
    }

    /// <summary>阵容这一行的解读：配比 + 整波只数的上下界。**配置里只有比例，没有绝对只数**，
    /// 所以这里把"比例 → 这一波实际会刷几只"当场算出来（这部分最容易被误读）。</summary>
    private string DescribeUnits(List<(string Monster, double Weight)> units, double count, double countMax)
    {
        double sum = units.Sum(u => u.Weight);
        string mix = string.Join(" + ", units.Select(u => $"{MonsterName(u.Monster)}×{u.Weight:0.##}"));
        return $"比例 {mix}（合计权重 {sum:0.##}）。整波只数 = min({countMax:0}, {count:0} × 关卡系数)，"
            + "再按比例摊给各模板——所以配比与这一波刷 3 只还是 13 只无关。";
    }

    private string MonsterName(string id) => _game.Config.Monsters.TryGetValue(id, out var m) ? m.Name : id;

    /// <summary>实际数值预览：**把倍率链算到底**（`monster 模板 × 关卡倍率 × 波次系数`）。
    /// 这是"模板定差异、关卡表放大强度"那条口径在界面上的落点。</summary>
    private string DescribeActualValues()
    {
        var cfg = _game.Config;
        string wave = LevelValue(_editorLevelSelected, "wave_id");
        double waveHp = wave.Length > 0 ? WaveNumber(wave, "hp_scale") : 1;
        double waveAtk = wave.Length > 0 ? WaveNumber(wave, "atk_scale") : 1;
        var units = wave.Length > 0 ? WaveUnitsOf(wave) : [];
        string normalId = units.FirstOrDefault().Monster ?? cfg.Monsters.Values.First(m => m.Kind == "normal").Id;
        var normal = cfg.Monsters[normalId];
        var elite = cfg.Monsters.Values.First(m => m.Kind == "elite");
        var boss = cfg.Monsters[LevelValue(_editorLevelSelected, "boss_id")];
        var rift = cfg.Monsters[LevelValue(_editorLevelSelected, "rift_id")];
        double nh = LevelNumber(_editorLevelSelected, "normal_hp") * waveHp;
        double na = LevelNumber(_editorLevelSelected, "normal_atk") * waveAtk;
        return $"模板 → 本关实际（普通怪吃波次系数 ×{waveHp:0.##} / ×{waveAtk:0.##}）：\n"
            + $"普通怪 {normal.Name}：{normal.Hp:0.##} 血 ×{nh:0.##} = {normal.Hp * nh:0.##}，"
            + $"攻 {normal.Atk:0.##} ×{na:0.##} = {normal.Atk * na:0.##}\n"
            + $"精英 {elite.Name}：{elite.Hp:0.##} ×{LevelNumber(_editorLevelSelected, "elite_hp"):0.##} = "
            + $"{elite.Hp * LevelNumber(_editorLevelSelected, "elite_hp"):0.##} 血，攻 {elite.Atk * LevelNumber(_editorLevelSelected, "elite_atk"):0.##}\n"
            + $"首领 {boss.Name}：{boss.Hp:0.##} ×{LevelNumber(_editorLevelSelected, "boss_hp"):0.##} = "
            + $"{boss.Hp * LevelNumber(_editorLevelSelected, "boss_hp"):0.##} 血\n"
            + $"裂隙 {rift.Name}：{rift.Hp:0.##} ×{LevelNumber(_editorLevelSelected, "rift_hp"):0.##} = "
            + $"{rift.Hp * LevelNumber(_editorLevelSelected, "rift_hp"):0.##} 血";
    }

    /// <summary>
    /// 体检：把**加载期会拒的规则**与几处"看不出来的坑"当场列出来。
    /// 与 `EditorValidate` 的关系是"体检报告 vs 保存闸门"——两边都读同一批判据（见 `LevelProblems`）。
    /// </summary>
    private string LevelAudit()
    {
        var problems = LevelProblems();
        var lines = problems.Count == 0 ? ["✓ 当前这一关没有发现问题。"] : problems.Select(p => "• " + p);
        // 那批"写死在测试与工具里"的假设，是加关卡之后唯一会让人莫名其妙变红的东西，提前说清楚。
        lines = lines.Append("— 工程假设（新增关卡后会红，要一起更新）：")
            .Append($"• tests 断言关卡数正好 100（当前 {_game.Config.Levels.Count}）")
            .Append("• tests 用 order 1/20/50/100 取关；前 50 / 后 50 关的 wave 集合也被逐条钉着")
            .Append("• tools/BalanceCurve 的曲线按 order 1..100 定义（Lerp 除 99）");
        return string.Join("\n", lines);
    }

    /// <summary>加载期会拒的规则（只查**被改过或新增的**那一关，避免把别人的老问题算到这一关头上）。
    /// 保存前 `LevelValidate` 也读它——体检与闸门共用同一批判据。</summary>
    private List<string> LevelProblems()
    {
        var cfg = _game.Config;
        var problems = new List<string>();
        string wave = LevelValue(_editorLevelSelected, "wave_id");
        double cells = LevelNumber(_editorLevelSelected, "cells");
        if (cells < 2) problems.Add($"格数 {cells:0} 小于 2（加载期会拒：至少两格才有「最后一格」）");
        foreach (var (column, label) in LevelScales)
            if (LevelNumber(_editorLevelSelected, column) <= 0) problems.Add($"{label}（{column}）要 > 0");
        if (!cfg.Waves.ContainsKey(wave)) problems.Add($"波次 `{wave}` 不存在");
        else
        {
            var w = cfg.Waves[wave];
            if (WaveNumber(wave, "count_max") < WaveNumber(wave, "count"))
                problems.Add("封顶只数小于每波只数（关卡越高刷得越少，是配错）");
            if (WaveNumber(wave, "hp_scale") > 5 || WaveNumber(wave, "atk_scale") > 5)
                problems.Add("波次系数不得超过 5");
            if (WaveUnitsOf(wave).Count == 0) problems.Add($"波次 `{wave}` 没有任何阵容（那一波刷不出怪）");
        }
        if (!cfg.Monsters.TryGetValue(LevelValue(_editorLevelSelected, "boss_id"), out var boss) || boss.Kind != "boss")
            problems.Add("首领必须选一个 kind=boss 的怪物");
        if (!cfg.Monsters.TryGetValue(LevelValue(_editorLevelSelected, "rift_id"), out var rift) || rift.Kind != "rift")
            problems.Add("裂隙必须选一个 kind=rift 的怪物");
        foreach (string column in new[] { "first_reward", "repeat_reward" })
        {
            string group = LevelValue(_editorLevelSelected, column);
            var rows = cfg.Rows("drop").Where(r => r.Text("group_id") == group).ToList();
            if (rows.Count == 0) problems.Add($"奖励组 `{group}` 在 drop.csv 里没有行");
            else if (column == "first_reward" && Math.Abs(rows.Where(r => r.Text("item_id") == "core").Sum(r => r.Number("amount")) - 1) > 1e-9)
                problems.Add("首杀奖励组必须恰好含 1 个灵核");
            else if (column == "repeat_reward" && rows.Any(r => r.Text("item_id") == "core"))
                problems.Add("重复奖励组不得含灵核");
        }
        return problems;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  曲线视图
    // ══════════════════════════════════════════════════════════════════════
    /// <summary>血量族的四条线（裂隙攻击恒为 0，所以它只在这一族里）。</summary>
    private static readonly string[] HpCurveColumns = ["normal_hp", "elite_hp", "boss_hp", "rift_hp"];
    /// <summary>攻击族的条数**比血量少一条**——裂隙没有攻击线。</summary>
    private static readonly string[] AtkCurveColumns = ["normal_atk", "elite_atk", "boss_atk"];

    /// <summary>这一帧该画哪几条线（**纯函数**，自检直接断言它）。</summary>
    internal string[] CurveColumnsForCheck() => _curveTab switch
    {
        1 => AtkCurveColumns,
        2 => [.. HpCurveColumns, .. AtkCurveColumns],
        _ => HpCurveColumns,
    };

    /// <summary>候选里**真正会画**的那几条（标签页 ∩ 逐条开关）。</summary>
    private string[] VisibleCurveColumns(string[] columns) => columns.Where(c => _curveOn.GetValueOrDefault(c, true)).ToArray();

    /// <summary>自检用：当前这一帧实际会画哪几条线。</summary>
    internal string[] CurveVisibleForCheck() => VisibleCurveColumns(CurveColumnsForCheck());

    /// <summary>自检 / 截图用：切标签页（等价于点那三个按钮之一）。</summary>
    internal void CurveTabForCheck(int tab) { _curveTab = tab; RefreshCurveControls(); }

    /// <summary>自检 / 截图用：点一条线的开关。</summary>
    internal void CurveToggleForCheck(string column) { _curveOn[column] = !_curveOn.GetValueOrDefault(column, true); RefreshCurveControls(); }

    /// <summary>自检 / 截图用：点「模型建议」那个开关。</summary>
    internal void CurveModelToggleForCheck() { _curveShowModel = !_curveShowModel; RefreshCurveControls(); }

    /// <summary>自检用：模型虚线现在画不画。</summary>
    internal bool CurveModelVisibleForCheck() => _curveShowModel;

    /// <summary>
    /// 画倍率曲线。标签页决定画哪一族（血量 / 攻击 / 两个都要，后者上下分格）；
    /// 逐条开关决定这一族里画哪几条。**血量用对数轴**——它跨三个数量级（1.0 → ×2212），
    /// 线性轴下前 50 关会全贴在底部，等于看不见。实线 = 当前配置，虚线 = 模型建议（可整体关掉）。
    /// </summary>
    internal void DrawLevelCurve(Control canvas)
    {
        Vector2 size = canvas.Size;
        if (_curvePoints.Count < 2 || size.X < 100) return;
        if (_curveTab == 2)
        {
            float mid = size.Y * 0.5f;
            DrawPanel(canvas, new Rect2(60, 20, size.X - 100, mid - 60), HpCurveColumns, log: true);
            DrawPanel(canvas, new Rect2(60, mid + 20, size.X - 100, mid - 60), AtkCurveColumns, log: false);
        }
        else if (_curveTab == 1) DrawPanel(canvas, new Rect2(60, 20, size.X - 100, size.Y - 60), AtkCurveColumns, log: false);
        else DrawPanel(canvas, new Rect2(60, 20, size.X - 100, size.Y - 60), HpCurveColumns, log: true);
        // 当前关的竖线：一眼看出"我在曲线的哪一段"。
        int order = (int)LevelNumber(_editorLevelSelected, "order");
        float x = 60 + (order - 1) * (size.X - 100) / Math.Max(1, _curvePoints.Count - 1);
        canvas.DrawLine(new Vector2(x, 12), new Vector2(x, size.Y - 12), UiKit.Gold, 1.5f);
    }

    /// <summary>画一格。<paramref name="columns"/> 是这一格的候选线；**关掉的不画、也不参与纵轴定标**
    /// （否则一条离群的线会把其它几条全压扁）。</summary>
    private void DrawPanel(CanvasItem canvas, Rect2 box, string[] columns, bool log)
    {
        var shown = VisibleCurveColumns(columns);
        double scale = shown.Length == 0 ? 1
            : log ? Math.Log10(Math.Max(10, _curvePoints.Max(p => shown.Max(c => p.Values[c]))))
                  : Math.Max(3, _curvePoints.Max(p => shown.Max(c => p.Values[c])));
        Func<double, double> norm = log ? v => Math.Log10(Math.Max(1e-6, v)) / scale : v => v / scale;
        Func<double, string> axis = log ? t => $"{Math.Pow(10, t):0.#}" : t => $"{t * scale:0.##}";
        DrawFrame(canvas, box, log ? "血量倍率（对数轴）" : "攻击倍率（线性轴）", axis, _curvePoints.Count);
        if (shown.Length == 0)
        {
            canvas.DrawString(ThemeDB.FallbackFont, box.Position + new Vector2(box.Size.X * .35f, box.Size.Y * .5f),
                "（这一族的线全关了）", HorizontalAlignment.Left, -1, 18, UiKit.Muted);
            return;
        }
        foreach (string column in shown)
        {
            DrawCurve(canvas, box, column, norm, ColumnColor(column), dashed: false);
            if (_levelModel is not null && _curveShowModel)
                DrawCurve(canvas, box, column, norm, ColumnColor(column), dashed: true);
        }
    }

    private static Color ColumnColor(string column) => column.StartsWith("normal") ? UiKit.Jade
        : column.StartsWith("boss") ? UiKit.Gold : UiKit.Muted;

    private static void DrawFrame(CanvasItem canvas, Rect2 box, string title, Func<double, string> axisLabel, int count)
    {
        canvas.DrawRect(box, new Color(0, 0, 0, 0), false, 1);
        canvas.DrawString(ThemeDB.FallbackFont, box.Position + new Vector2(0, -8), title, HorizontalAlignment.Left, -1, 18, UiKit.Muted);
        for (int i = 0; i <= 4; i++)
        {
            float t = i / 4f;
            float y = box.Position.Y + box.Size.Y * (1 - t);
            canvas.DrawLine(new Vector2(box.Position.X, y), new Vector2(box.End.X, y), new Color(UiKit.Line, .35f), 1);
            canvas.DrawString(ThemeDB.FallbackFont, new Vector2(box.Position.X - 52, y + 6), axisLabel(t),
                HorizontalAlignment.Left, -1, 15, UiKit.Muted);
        }
        canvas.DrawString(ThemeDB.FallbackFont, new Vector2(box.Position.X - 40, box.End.Y + 22), "第 1 关",
            HorizontalAlignment.Left, -1, 15, UiKit.Muted);
        canvas.DrawString(ThemeDB.FallbackFont, new Vector2(box.End.X - 80, box.End.Y + 22), count + " 关",
            HorizontalAlignment.Left, -1, 15, UiKit.Muted);
    }

    /// <summary>画一条曲线。<paramref name="dashed"/> = 模型建议（走 `_levelModel` 的采样，与 `_curvePoints` 同 x 轴）。
    /// 虚线用"每帧只画一半"的朴素做法——这条曲线不需要平滑，看得出是虚的就够。</summary>
    private void DrawCurve(CanvasItem canvas, Rect2 box, string column, Func<double, double> norm, Color color, bool dashed)
    {
        Vector2? prev = null;
        for (int i = 0; i < _curvePoints.Count; i++)
        {
            double value;
            if (dashed)
            {
                if (_levelModel is null || _curvePoints[i].Order < 1 || _curvePoints[i].Order > _levelModel.Count) continue;
                value = _levelModel.Suggested(_curvePoints[i].Order)[column];
            }
            else value = _curvePoints[i].Values[column];
            float x = box.Position.X + box.Size.X * i / Math.Max(1, _curvePoints.Count - 1);
            float y = box.End.Y - box.Size.Y * (float)Math.Clamp(norm(value), 0, 1);
            var point = new Vector2(x, y);
            if (prev is { } p && (!dashed || i % 2 == 0)) canvas.DrawLine(p, point, color, dashed ? 1 : 2);
            prev = point;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  试打 / 新增 / 保存
    // ══════════════════════════════════════════════════════════════════════
    private void LevelTryPlay()
    {
        if (_editorLevelSelected.Length == 0) return;
        if (!_game.State.UnlockedLevels.Contains(_editorLevelSelected))
        {
            _game.State.UnlockedLevels.Add(_editorLevelSelected);   // 开发便利：与 GM 的「一键解锁」同一性质
            _levelStatus.Text = $"这一关原本锁着，已就地解锁（会随自动保存落盘）。";
        }
        _game.SelectLevel(_editorLevelSelected);
        _levelRoot.Visible = false;
        Refresh();
    }

    /// <summary>
    /// **copy-on-write**：把当前这条共享波次抄成一条只给本关用的。
    /// 原共享模板（`wave.csv` 那一行 + 它的 `wave_unit` 行）**一个字不动**——所以同 wave 的其它关卡不受影响。
    /// 这正是 `wave_10` / `wave_11` 两条教学波当初的来历。
    /// </summary>
    private void LevelCopyWavePrivate()
    {
        string from = LevelValue(_editorLevelSelected, "wave_id");
        if (from.Length == 0) return;
        int order = (int)LevelNumber(_editorLevelSelected, "order");
        string id = $"{from}_l{order}";
        for (int n = 2; _game.Config.Waves.ContainsKey(id) || _waveAdded.Any(r => r["id"] == id); n++)
            id = $"{from}_l{order}_{n}";
        var row = new Dictionary<string, string>();
        foreach (string column in new[] { "interval", "elite_every", "elite_id", "hp_scale", "atk_scale", "count", "count_max" })
            row[column] = WaveValue(from, column);
        row["id"] = id;
        _waveAdded.Add(row);
        _waveUnitPending[id] = [.. WaveUnitsOf(from)];   // 阵容一并抄过去
        LevelCell("wave_id", id);
        _levelStatus.Text = $"已把波次 {from} 复制成 {id}，只给第 {order} 关用。原模板未改动。";
        LevelRefreshUi();
    }

    /// <summary>
    /// 末尾追加一关：以当前关为模板，自动分配 id 与 order、复用两个奖励组、七列填模型建议值。
    /// **不动任何已有行的 order**——插入到中间要重排后面所有关的 order，而 order 是 `WaveScale` 的输入，
    /// 等于整条难度曲线重算，且已存档玩家不会自动解锁新关。那是另一件事，这一轮明确不做。
    /// </summary>
    private void LevelAppend()
    {
        var cfg = _game.Config;
        int next = 1;
        while (cfg.Levels.Any(l => l.Id == $"level_{next:000}") || _levelAdded.Any(r => r["id"] == $"level_{next:000}")) next++;
        string id = $"level_{next:000}";
        int order = cfg.Levels.Max(l => l.Order) + 1;
        if (_levelAdded.Count > 0) order = Math.Max(order, _levelAdded.Max(r => int.Parse(r["order"])) + 1);
        var row = new Dictionary<string, string>
        {
            ["id"] = id, ["name"] = $"{LevelValue(_editorLevelSelected, "name")}·续", ["order"] = order.ToString(),
            ["cells"] = LevelValue(_editorLevelSelected, "cells"), ["wave_id"] = LevelValue(_editorLevelSelected, "wave_id"),
            ["boss_id"] = LevelValue(_editorLevelSelected, "boss_id"), ["rift_id"] = LevelValue(_editorLevelSelected, "rift_id"),
            // 奖励组**直接复用**：校验只要求"组存在 + 首杀组恰好 1 灵核 + 重复组无灵核"，所以新关不必碰 drop.csv。
            ["first_reward"] = LevelValue(_editorLevelSelected, "first_reward"),
            ["repeat_reward"] = LevelValue(_editorLevelSelected, "repeat_reward"),
        };
        foreach (var (column, _) in LevelScales)
            row[column] = _levelModel is not null && order <= _levelModel.Count
                ? _levelModel.Suggested(order)[column].ToString("0.####")
                : LevelValue(_editorLevelSelected, column);
        _levelAdded.Add(row);
        _levelStatus.Text = $"已追加 {id}（第 {order} 关，模板取自 {_editorLevelSelected}）。"
            + "保存后会多一关；`tests` 里那批「正好 100 关」的断言要一起更新。";
        LevelRefreshUi();
    }

    private void LevelSave()
    {
        string problem = LevelValidate();
        if (problem.Length > 0) { _levelStatus.Text = "保存已取消：" + problem; return; }
        if (OS.HasFeature("template")) { _levelStatus.Text = "打包版只读，无法保存。请在源码目录里运行。"; return; }
        try
        {
            // 三张表走**同一次事务**：任一张写坏（比如 wave_unit 引用了不存在的怪物）加载期就拒整份配置，
            // 不允许留下"关卡写了、波次没跟上"这种半成品。
            EditorFiles.WriteAllOrNothing(
                (ProjectSettings.GlobalizePath(LevelPath), BuildLevelText()),
                (ProjectSettings.GlobalizePath(WavePath), BuildWaveText()),
                (ProjectSettings.GlobalizePath(WaveUnitPath), BuildWaveUnitText()));
            int touched = _levelPending.Count, waves = _wavePending.Count;
            int added = _levelAdded.Count;
            _levelPending.Clear(); _wavePending.Clear(); _waveUnitPending.Clear(); _levelAdded.Clear(); _waveAdded.Clear();
            _levelStatus.Text = $"已保存：改了 {touched} 关 / {waves} 条波次"
                + (added > 0 ? $"，并追加了 {added} 关" : "") + "。重启工程后下拉里就能看到新关卡。";
            LevelEditorReload();
        }
        catch (Exception ex) { _levelStatus.Text = "保存失败：" + ex.Message; }
    }

    /// <summary>保存前自查：**与加载期同一套规则**（`LevelProblems`），另外把"新增行"也过一遍。
    /// 不合格就拒绝保存、不碰任何文件——写坏了就是"下次启动工程起不来"。</summary>
    private string LevelValidate()
    {
        var problems = LevelProblems();
        foreach (var row in _levelAdded)
            if (!_game.Config.Waves.ContainsKey(row["wave_id"]))
                problems.Add($"新增的 {row["id"]} 引用了不存在的波次 `{row["wave_id"]}`");
        foreach (var (wave, units) in _waveUnitPending)
            foreach (var (monster, _) in units)
                if (!_game.Config.Monsters.ContainsKey(monster))
                    problems.Add($"波次 `{wave}` 引用了不存在的怪物 `{monster}`");
        // order 必须互不相同（加载期会拒）。
        var orders = _game.Config.Levels.Select(l => LevelValue(l.Id, "order"))
            .Concat(_levelAdded.Select(r => r["order"])).ToList();
        if (orders.Count != orders.Distinct().Count()) problems.Add("有两条关卡的顺序(order)相同（加载期会拒）");
        return problems.Count == 0 ? "" : string.Join("；", problems);
    }

    /// <summary>把三张表按**磁盘原样 + 待写覆盖**拼出来。没被改过的格子原样往返，
    /// 所以编辑器开着的时候 AI 在磁盘上改的行不会被悄悄改回去。</summary>
    private string BuildLevelText()
    {
        var header = HeaderOf("level.csv");
        var lines = new List<string> { string.Join(",", header) };
        foreach (var row in LevelDisk())
            lines.Add(string.Join(",", header.Select(c => Csv(row, c, LevelValue(row.Text("id"), c)))));
        foreach (var added in _levelAdded)
            lines.Add(string.Join(",", header.Select(c => Csv(added, c, added.GetValueOrDefault(c, "")))));
        return string.Join("\n", lines) + "\n";
    }

    private string BuildWaveText()
    {
        var header = HeaderOf("wave.csv");
        var lines = new List<string> { string.Join(",", header) };
        foreach (var row in WaveDisk())
            lines.Add(string.Join(",", header.Select(c => Csv(row, c, WaveValue(row.Text("id"), c)))));
        foreach (var added in _waveAdded)
            lines.Add(string.Join(",", header.Select(c => Csv(added, c, added.GetValueOrDefault(c, "")))));
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>阵容表：**按波次整条替换**（增删行没有"格子"可言），其它波次的行原样留着。</summary>
    private string BuildWaveUnitText()
    {
        var lines = new List<string> { "id,wave_id,monster_id,weight" };
        foreach (var row in WaveUnitDisk())
        {
            string wave = row.Text("wave_id");
            if (!_waveUnitPending.ContainsKey(wave)) lines.Add(string.Join(",", row.Text("id"), wave, row.Text("monster_id"), row.Text("weight")));
        }
        foreach (var (wave, units) in _waveUnitPending)
            foreach (var (monster, weight) in units)
                lines.Add($"{wave}_{monster},{wave},{monster},{weight:0.##}");
        return string.Join("\n", lines) + "\n";
    }

    private string[] HeaderOf(string file) =>
        Godot.FileAccess.GetFileAsString("res://Config/Tables/" + file).TrimStart('﻿')
            .Split('\n')[0].TrimEnd('\r').Split(',');

    /// <summary>一个单元格的写法：`id` 永远用行自己那一列（**不抄别行的 id**），其余按传入的值。</summary>
    private static string Csv(CsvRow row, string column, string value) => column == "id" ? row.Text("id") : value;
    private static string Csv(Dictionary<string, string> row, string column, string value) => column == "id" ? row["id"] : value;

    // ══════════════════════════════════════════════════════════════════════
    //  自检钩子 + 冒烟那一段
    //  （纪律与节点编辑器一致：探针**从配置现取**、破坏性动作演练完就丢弃、真保存前先双 Reload）
    // ══════════════════════════════════════════════════════════════════════
    internal string LevelEditorSelectedForCheck() => _editorLevelSelected;
    internal void LevelEditorSelectForCheck(string id) { _editorLevelSelected = id; LevelRefreshUi(); }
    internal void LevelEditorCellForCheck(string column, string value) => LevelCell(column, value);
    internal void LevelEditorAdoptForCheck(string column) => LevelAdopt(column);
    internal void LevelEditorCopyWaveForCheck() => LevelCopyWavePrivate();
    internal void LevelEditorAppendForCheck() => LevelAppend();
    internal void LevelEditorReloadForCheck() => LevelEditorReload();
    internal string LevelEditorStatusForCheck() => _levelStatus.Text;
    internal string LevelEditorScaleTextForCheck(string column) => _levelScaleInputs[column].Text;
    internal string LevelEditorScaleHintForCheck(string column) => _levelScaleHints[column].Text;
    internal IReadOnlyList<string> LevelEditorIdsForCheck() => _editorLevelIds;
    internal List<string> LevelEditorProblemsForCheck() => LevelProblems();
    internal int LevelEditorCurveCountForCheck() => _curvePoints.Count;
    internal double LevelEditorCurveValueForCheck(string levelId, string column) =>
        _curvePoints.First(p => p.Order == (int)LevelNumber(levelId, "order")).Values[column];

    /// <summary>把三张表按"即将写下去"的样子拼出来（**不落盘**）——断言用它看改动到底写进了哪一格。</summary>
    internal string LevelEditorAlignedForCheck(string file) => file switch
    {
        "level" => BuildLevelText(),
        "wave" => BuildWaveText(),
        _ => BuildWaveUnitText(),
    };

    /// <summary>
    /// 关卡编辑器那一段自检。与节点编辑器同一条纪律：**真保存之前先把这一轮的改动全部丢弃**
    /// （双 `Reload`），否则会往真实的 `level.csv` 里追加行。
    /// </summary>
    private async Task LevelEditorSmoke(Func<string, Task> capture)
    {
        ToggleLevelEditor();
        if (!_levelRoot.Visible) throw new Exception("level editor did not open");
        if (LevelEditorIdsForCheck().Count == 0) throw new Exception("level editor 没有关卡可选");
        await capture("-level-editor");

        // ① 选关 + 读值：下拉选中了谁，面板上就应当是那一关。
        //    ⚠️ 期望值**从 config 现取**，不钉死数字——改曲线之后这里不该跟着红。
        string probe = LevelEditorIdsForCheck().First(id => id == "level_007");
        LevelEditorSelectForCheck(probe);
        if (LevelEditorSelectedForCheck() != probe) throw new Exception("选关没生效：" + LevelEditorSelectedForCheck());
        string expectedHp = _game.Config.Levels.First(l => l.Id == probe).HpScale.ToString("0.####");
        if (LevelEditorScaleTextForCheck("normal_hp") != expectedHp)
            throw new Exception($"面板上的倍率不是这一关的值：{LevelEditorScaleTextForCheck("normal_hp")} vs {expectedHp}");
        // 曲线采样：关数必须与关卡数一致，且当前关那一列与配置对得上。
        if (LevelEditorCurveCountForCheck() != _game.Config.Levels.Count)
            throw new Exception($"曲线采样数 {LevelEditorCurveCountForCheck()} 与关卡数 {_game.Config.Levels.Count} 不一致");
        if (Math.Abs(LevelEditorCurveValueForCheck(probe, "normal_hp") - double.Parse(expectedHp)) > 1e-4)
            throw new Exception("曲线上的值与配置对不上");
        // 体检：这一关是现成配置，不该报问题。
        if (LevelEditorProblemsForCheck().Count > 0)
            throw new Exception("现成关卡的体检不该有问题：" + string.Join("；", LevelEditorProblemsForCheck()));

        // ㉔ 曲线视图：标签页决定看哪一族、逐条开关决定画哪几条线。这两件事都是纯函数，
        //    所以直接断言"这一帧会画哪几条"——绘制本身靠截图核对。
        CurveTabForCheck(0);
        if (string.Join(",", CurveVisibleForCheck()) != "normal_hp,elite_hp,boss_hp,rift_hp")
            throw new Exception("血量标签页默认应当画四条血量线：" + string.Join(",", CurveVisibleForCheck()));
        CurveToggleForCheck("elite_hp");
        if (CurveVisibleForCheck().Contains("elite_hp"))
            throw new Exception("关掉的线不该还在画：" + string.Join(",", CurveVisibleForCheck()));
        CurveTabForCheck(1);
        if (string.Join(",", CurveVisibleForCheck()) != "normal_atk,elite_atk,boss_atk")
            throw new Exception("攻击标签页应当只剩三条攻击线（裂隙没有攻击）：" + string.Join(",", CurveVisibleForCheck()));
        CurveTabForCheck(2);
        if (CurveVisibleForCheck().Length != 6)
            throw new Exception("两个都要应当把两族都列上（去掉关掉的那条）：" + string.Join(",", CurveVisibleForCheck()));
        CurveTabForCheck(0);
        CurveToggleForCheck("elite_hp");   // 还原
        if (CurveVisibleForCheck().Length != 4) throw new Exception("还原之后血量四条应当都在");
        CurveModelToggleForCheck();
        if (CurveModelVisibleForCheck()) throw new Exception("模型建议开关没生效");
        CurveModelToggleForCheck();

        // ㉕ 阵容多选：勾上就进这一波、取消就出去，**不用手输怪物 id**。
        string rosterWave = LevelValue(probe, "wave_id");
        string beforeUnits = LevelEditorAlignedForCheck("wave_unit");
        string extra = _monsterIds.First(m => !WaveUnitsOfForCheck(rosterWave).Any(u => u.Monster == m));
        LevelEditorUnitToggleForCheck(extra);
        string afterUnits = LevelEditorAlignedForCheck("wave_unit");
        if (NonEmptyLines(afterUnits) != NonEmptyLines(beforeUnits) + 1)
            throw new Exception($"勾一个怪物应当让 wave_unit 多一行（{NonEmptyLines(beforeUnits)} → {NonEmptyLines(afterUnits)}）");
        if (!afterUnits.Contains($",{extra},")) throw new Exception("勾上的怪物没进待写：" + extra);
        LevelEditorUnitToggleForCheck(extra);
        if (NonEmptyLines(LevelEditorAlignedForCheck("wave_unit")) != NonEmptyLines(beforeUnits))
            throw new Exception("取消勾选之后那一行没撤掉");

        // ② 改一格 cells → 只该动这一格。
        LevelEditorCellForCheck("cells", "20");
        string[] row7 = LevelEditorAlignedForCheck("level")
            .Split('\n').First(l => l.StartsWith(probe + ",")).Split(',');
        if (row7[3] != "20") throw new Exception("改格数没写进待写的那一行：" + row7[3]);
        if (row7[4] != expectedHp) throw new Exception("改一格把别的格子也动了：" + row7[4]);

        // ③ 采纳模型建议：写进去的必须正是模型算出来的那个数。
        LevelEditorAdoptForCheck("normal_hp");
        double want = _levelModel!.Suggested((int)LevelNumber(probe, "order"))["normal_hp"];
        string adopted = LevelEditorAlignedForCheck("level")
            .Split('\n').First(l => l.StartsWith(probe + ",")).Split(',')[4];
        // 写盘统一保留 4 位小数（`0.####`），所以比的是**四舍五入到 4 位之后**的模型值。
        if (Math.Abs(double.Parse(adopted) - Math.Round(want, 4)) > 1e-9)
            throw new Exception($"采纳建议写进去的不是模型值：{adopted} vs {want:F6}");
        if (!LevelEditorScaleHintForCheck("normal_hp").Contains('✓'))
            throw new Exception("采纳之后建议值那栏应当显示一致（✓）：" + LevelEditorScaleHintForCheck("normal_hp"));

        // ④ copy-on-write：换一条被多关共用的波次，复制成专用。
        //    **原共享模板必须一字未变**——这条是"只想改这一关"的全部价值所在。
        string shared = _game.Config.Waves.Keys.First(w => _game.Config.Levels.Count(l => LevelWaveOf(l.Id) == w) > 3);
        LevelEditorCellForCheck("wave_id", shared);
        int waveRowsBefore = NonEmptyLines(LevelEditorAlignedForCheck("wave"));
        string oldSharedRow = LevelEditorAlignedForCheck("wave").Split('\n').First(l => l.StartsWith(shared + ","));
        LevelEditorCopyWaveForCheck();
        if (!LevelEditorStatusForCheck().Contains("复制成"))
            throw new Exception("复制成专用没给出反馈：" + LevelEditorStatusForCheck());
        string afterWave = LevelEditorAlignedForCheck("wave");
        string privateWave = $"{shared}_l{(int)LevelNumber(probe, "order")}";
        if (!afterWave.Contains(privateWave + ",")) throw new Exception("新波次行没进待写：" + afterWave.Split('\n').Last());
        if (NonEmptyLines(afterWave) != waveRowsBefore + 1)
            throw new Exception($"复制成专用应当只加一行（{waveRowsBefore} → {NonEmptyLines(afterWave)}）");
        if (LevelEditorAlignedForCheck("wave_unit").Split('\n').Count(l => l.Contains(privateWave + ",")) == 0)
            throw new Exception("新波次的阵容没跟着抄过去");
        //    再改这条专用波次：只该动新那一行，原共享那行不动。
        LevelEditorCellForCheck("wave_id", privateWave);
        LevelWaveCellForCheck("count", "5");
        var newRow = LevelEditorAlignedForCheck("wave").Split('\n').First(l => l.StartsWith(privateWave + ","));
        if (!newRow.Contains(",5,")) throw new Exception("改专用波次的每波只数没写进去：" + newRow);
        var oldRow = LevelEditorAlignedForCheck("wave").Split('\n').First(l => l.StartsWith(shared + ","));
        if (oldRow != oldSharedRow)
            throw new Exception("改专用波次把原共享模板也改了：" + oldRow);

        // ⑤ 新增关卡：id / order 自动分配，追加行进待写。
        LevelEditorReloadForCheck();                  // 先把上面那些演练丢掉
        LevelEditorSelectForCheck(probe);
        int levelsBefore = _game.Config.Levels.Count;
        LevelEditorAppendForCheck();
        string appended = LevelEditorAlignedForCheck("level").Split('\n')
            .Last(l => l.StartsWith($"level_{levelsBefore + 1:000},"));
        if (!appended.Contains($",{levelsBefore + 1},"))
            throw new Exception("新增关卡的 order 不对：" + appended);
        if (!LevelEditorStatusForCheck().Contains("已追加")) throw new Exception("新增关卡没给出反馈");

        // ⑥ **丢弃**：这一轮的改动一律不落盘（真保存会往 level.csv 追加一行、并改掉第 7 关）。
        LevelEditorReloadForCheck();
        LevelEditorReloadForCheck();
        if (LevelEditorAlignedForCheck("level").Split('\n').Count(l => l.Length > 0) != levelsBefore + 1)
            throw new Exception("丢弃之后待写的关卡行没清干净——真保存会把演练写进配置");
        // 面板布局四条（`SectionList` 自带那条口径）。
        var (content, viewportH) = _levelPanel.ScrollSize;
        if (viewportH <= 0) throw new Exception("关卡编辑器的面板视口高度不为正");
        // ㉓ 右侧面板必须**真的能滚**。内容溢出时，滚动条的可滚范围要大于视口——
        //    这条正是"看得到下面的字却滚不动"那个 bug 的判据：根因是内容层的最小尺寸恒为 0，
        //    `ScrollContainer` 于是认为可滚范围是零（滚轮与拖拽都失效）。写成断言，别再回来。
        if (content > viewportH + 1)
        {
            var bar = _levelPanel.ScrollBar;
            if (bar.MaxValue <= bar.Page)
                throw new Exception($"关卡编辑器面板装不下却不给滚：可滚范围 {bar.MaxValue:F0} ≤ 视口 {bar.Page:F0}"
                    + "（内容层的最小尺寸没设，`ScrollContainer` 算不出范围）");
            if (_levelPanel.InnerMinimumSize.Y < content - 1)
                throw new Exception($"内容层的最小尺寸 {_levelPanel.InnerMinimumSize.Y:F0} 小于实际内容 {content:F0}"
                    + "——`ScrollContainer` 按最小尺寸算范围，尾部会滚不到");
        }
        if (!_levelPanel.InnerLetsInputThrough)
            throw new Exception("内容层不是 Ignore：它会按住滚动条，拖不动");
        if (content + .5f < _levelPanel.SectionY(_levelPanel.Count - 1))
            throw new Exception("关卡编辑器的滚动内容层比最后一个分区还矮，尾部会被裁");
        foreach (var (title, need, declared) in _levelPanel.Fit())
            if (need > declared + .5f)
                throw new Exception($"关卡编辑器「{title}」声明的 {declared:F0}px 装不下它的行（需要 {need:F0}px）");
        foreach (string overlap in _levelPanel.RowOverlaps())
            throw new Exception("关卡编辑器分区里有行压在一起：" + overlap);
        if (_levelPanel.FooterBottom > SectionList.BottomLimit)
            throw new Exception($"关卡编辑器脚注出了屏：{_levelPanel.FooterBottom:F0}");
        // ⑦ 面板滚两屏再拍：试打区 / 阵容 / 体检那几块都在视口下方，只看第一屏是看不到的。
        //    顺手也是**滚动真的能用**的实证：偏移没动就说明还是滚不动。
        var scroll = _levelPanel.ScrollBar;
        double top = scroll.Value;
        scroll.Value = scroll.MaxValue;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (scroll.Value <= top + 1) throw new Exception("面板滚不动：把滚动条拉到底，偏移没变");
        await capture("-level-editor-bottom");
        // 停在"挑战"那一块上：阵容的多选就在那儿（把那一块的开头对到视口顶）。
        scroll.Value = Math.Min(scroll.MaxValue, _levelPanel.SectionY(3) - 20);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await capture("-level-editor-roster");
        ToggleLevelEditor();
    }

    private static int NonEmptyLines(string text) => text.Split('\n').Count(l => l.Trim().Length > 0);

    /// <summary>自检用：某条波次现在的阵容（含待写）。</summary>
    internal List<(string Monster, double Weight)> WaveUnitsOfForCheck(string wave) => WaveUnitsOf(wave);

    /// <summary>自检 / 截图用：勾 / 取消一个怪物（等价于点那一行的按钮）。</summary>
    internal void LevelEditorUnitToggleForCheck(string monster) => LevelWaveUnitToggled(monster);

    /// <summary>自检 / 截图用：改某个怪物的权重（等价于在那一行里打字）。</summary>
    internal void LevelEditorUnitWeightForCheck(string monster, string text) => LevelWaveUnitWeight(monster, text);

    private string LevelWaveOf(string levelId) => LevelValue(levelId, "wave_id");
    internal void LevelWaveCellForCheck(string column, string value) => WaveCell(column, value);
}
