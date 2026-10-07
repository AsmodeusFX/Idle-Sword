using Godot;

namespace IdleSword.UI;

/// <summary>
/// GM 面板：调试专用的一小撮开关。与设置面板同一个套路——按需构建、置于最上层、背板拦截点击。
/// 它只调**会话态**（`GameSession` 上的几个调试开关）与发放资源，不碰存档格式，也不污染正式数值：
/// 波次数量、怪物倍率在 `wave.csv` / `monster.csv` 里都保持正常，测试时要多少只、多厚，在这儿临时调。
/// </summary>
public partial class Main
{
    private Control? _gmRoot;
    private Label _waveBonusValue = null!, _monsterHpValue = null!, _monsterAtkValue = null!;
    private Button _invincibleButton = null!;
    /// <summary>普攻形态的对照开关（近战 / 远程），见 `GameSession.MeleeBasic`。</summary>
    private Button _meleeButton = null!;
    /// <summary>技能预览的按钮也在 GM 面板里（主界面那条横带撤掉后搬过来的）。</summary>
    private Button? _previewToggle;
    private string InvincibleText => _game.PlayerInvincible ? "无敌：开" : "无敌：关";
    private string MeleeText => _game.MeleeBasic ? "普攻：近战" : "普攻：远程";
    // 倍率走一档一档的台阶而不是 ±1：测"怪太脆"通常要跳到 ×5 / ×10，逐 1 加太磨人。
    private static readonly double[] ScaleSteps = [.5, 1, 2, 3, 5, 8, 12, 20, 50];

    /// <summary>在当前值所在的台阶上挪一格（值被手改过就落到最近的档）。</summary>
    private static double StepScale(double current, int direction)
    {
        int at = Array.IndexOf(ScaleSteps, current);
        if (at < 0) at = Array.FindIndex(ScaleSteps, v => v >= current);
        if (at < 0) at = ScaleSteps.Length - 1;
        return ScaleSteps[Math.Clamp(at + direction, 0, ScaleSteps.Length - 1)];
    }

    private void ToggleGm()
    {
        if (_gmRoot is not null && _gmRoot.Visible) CloseGm();
        else OpenGm();
    }

    private void OpenGm()
    {
        if (_gmRoot is null) BuildGm();
        RefreshGm();
        _gmRoot!.Visible = true;
        PlaySfx("sfx_panel");
    }

    private void CloseGm()
    {
        if (_gmRoot is null || !_gmRoot.Visible) return;
        _gmRoot.Visible = false;
        PlaySfx("sfx_panel");
    }

    /// <summary>
    /// GM 面板的**两大区**。立这条规矩是为了解决一个具体的问题：命令越来越多之后，
    /// "新加的东西该放哪"没有判据，于是只能凭感觉挑个坐标塞进去，面板越长越乱。
    ///
    /// 判据只有一句——**点下去是"改这一局"还是"换个地方干活"**：
    /// - <see cref="GmCommands"/>：**直接改游戏当前内容**。发资源、开玩法开关、缩放怪物、切普攻形态、
    ///   调波次数量……都是"当场改掉这一局的状态"，改完还留在面板里接着调。
    /// - <see cref="GmTools"/>：**换个工具或换个视图**。点下去 GM 面板就让位（三个入口都先 `CloseGm()`），
    ///   活儿在别的地方干。它自己再分两类：**编辑器**（改配置源表：节点编辑器，以后还有关卡编辑器…）
    ///   与**特殊功能**（只看不改：伤害统计、技能预览）。
    ///
    /// **加新命令时按这个判据选边**，别按"顺手"塞。自检里有一条断言盯着
    /// ——面板里每个按钮都必须落在某一块大区底板之内（`GmAreaRectsForCheck`）。
    /// </summary>
    private const string GmCommands = "GM 指令", GmTools = "编辑器与特殊功能入口";

    // ── 排版契约：小分区一律"左边小标题 + 右边控件"，一行一条 ──
    /// <summary>面板宽（居中放）。要更宽先确认控件区 460px 还够不够摆。</summary>
    private const float GmWidth = 660f;
    /// <summary>面板左上角。高度是**算出来的**（行排完才知道），所以这里只钉左上。</summary>
    private const float GmLeft = (1920f - GmWidth) / 2f, GmTop = 150f;
    private const float GmAreaHead = 46f;      // 区标题占的高度
    private const float GmRowHeight = 44f;     // 一行控件的高
    private const float GmRowPitch = 56f;      // 行距（44 + 12 的呼吸）
    private const float GmAreaPadBottom = 16f, GmAreaGap = 16f;
    private const float GmPadX = 40f;          // 面板内左右留白
    private const float GmLabelX = 40f;        // 小标题相对面板左沿
    private const float GmCtrlX = 160f;        // 控件区相对面板左沿

    /// <summary>两块大区的底板。自检用它断言"每个按钮都在某个区之内"。</summary>
    private readonly List<Panel> _gmAreas = [];

    /// <summary>自检用：两块大区的矩形（设计坐标）。</summary>
    public IEnumerable<Rect2> GmAreaRectsForCheck() => _gmAreas.Select(a => new Rect2(a.Position, a.Size));

    /// <summary>自检用：面板里的全部按钮（含间接子节点）。「关闭」不在任何区里，由断言自己排除。</summary>
    public IEnumerable<Button> GmButtonsForCheck() => Descendants(_gmRoot!).OfType<Button>();

    private static IEnumerable<Node> Descendants(Node node) =>
        node.GetChildren().SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private void BuildGm()
    {
        _gmRoot = new Control { Visible = false }; UiKit.Place(_gmRoot, 0, 0, 1920, 1080); AddChild(_gmRoot);
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, .62f) }; UiKit.Place(backdrop, 0, 0, 1920, 1080); _gmRoot.AddChild(backdrop);
        // 面板本体先按占位高建——它得画在**所有内容之下**，所以只能先加；高度等内容排完再补（见方法末尾）。
        var panel = UiKit.PanelAt(_gmRoot, GmLeft, GmTop, GmWidth, 100);
        UiKit.Label(_gmRoot, "GM · 调试", GmLeft + GmPadX, GmTop + 18, 300, 46, 30, UiKit.Gold);
        UiKit.Button(_gmRoot, "关闭", GmLeft + GmWidth - GmPadX - 100, GmTop + 20, 100, 40, CloseGm);

        // 排版游标：从上往下走，**加一行只需要插一句**，不必手算坐标。
        float y = GmTop + 92;
        // 开一块大区：底板先建（于是画在行**下面**），高度等行排完由 `EndArea` 补——
        // "行数改了忘了改区高"这类错在结构上就不存在了。
        Panel StartArea(string title)
        {
            var area = UiKit.PanelAt(_gmRoot, GmLeft + 20, y, GmWidth - 40, 100, UiKit.Ink);
            UiKit.Label(_gmRoot, title, GmLeft + GmPadX, y + 12, 460, 30, 20, UiKit.Jade);
            _gmAreas.Add(area);
            y += GmAreaHead;
            return area;
        }
        void EndArea(Panel area)
        {
            y += GmAreaPadBottom - 12;   // 最后一行已经带着 12 的呼吸了
            area.Size = new Vector2(area.Size.X, y - area.Position.Y);
            y += GmAreaGap;
        }
        // 一个**小分区**：左边画标题、返回这一行的 y；控件由调用方按 `GmCtrlX` 摆，摆完调 `NextRow()`。
        // 第二行起传空标题（如「怪物倍率」的第二条），保持左列对齐而不重复写字。
        float Row(string title)
        {
            if (title.Length > 0) UiKit.Label(_gmRoot, title, GmLeft + GmLabelX, y, 120, GmRowHeight, 19, UiKit.Muted);
            return y;
        }
        void NextRow() => y += GmRowPitch;

        // ── 大区一：GM 指令（直接改这一局） ──
        var commands = StartArea(GmCommands);
        float row, left = GmLeft + GmCtrlX;
        row = Row("资源");
        UiKit.Button(_gmRoot, "每种货币 +10000", left, row, 260, GmRowHeight, () => { _game.GrantAllCurrencies(); Refresh(); })
            .Tip("item.csv 中每种货币各 +10000，新增货币自动纳入。");
        NextRow();
        // 「解锁」单独一行：它是**开玩法开关**，不是发东西；而且这一行正是以后加解锁 / 开关类命令的落点
        // （现在只有一个按钮是刻意的，不是没排满）。
        row = Row("解锁");
        UiKit.Button(_gmRoot, "一键解锁", left, row, 140, GmRowHeight, () => { _game.UnlockAllSystems(); Refresh(); })
            .Tip("解锁全部系统（修行 / 境界 / 铸造 / 参悟 / 剑灵），与正常玩法走的是同一个解锁集合。");
        NextRow();
        // 怪物倍率：乘在关卡 / 波次倍率之后，**改完立刻作用于场上的怪**（不必等下一波）。
        // 按钮文字带上名字（"血量−" / "血量＋"），否则烟雾测试里 `Tap("＋")` 会撞上另外两行——
        // 它取的是全树第一个前缀匹配。
        row = Row("怪物倍率");
        UiKit.Button(_gmRoot, "血量−", left, row, 100, GmRowHeight, () => { _game.MonsterHpScale = StepScale(_game.MonsterHpScale, -1); RefreshGm(); })
            .Tip("怪物血量的额外倍率（1 = 不额外缩放）。乘在关卡与波次倍率之后。");
        _monsterHpValue = UiKit.Label(_gmRoot, "", left + 110, row, 240, GmRowHeight, 24);
        UiKit.Button(_gmRoot, "血量＋", left + 360, row, 100, GmRowHeight, () => { _game.MonsterHpScale = StepScale(_game.MonsterHpScale, 1); RefreshGm(); })
            .Tip("怪物血量的额外倍率（1 = 不额外缩放）。改完立刻作用于场上的怪，不会等下一波。");
        NextRow();
        row = Row("");
        UiKit.Button(_gmRoot, "攻击−", left, row, 100, GmRowHeight, () => { _game.MonsterAtkScale = StepScale(_game.MonsterAtkScale, -1); RefreshGm(); })
            .Tip("怪物攻击的额外倍率（1 = 不额外缩放）。");
        _monsterAtkValue = UiKit.Label(_gmRoot, "", left + 110, row, 240, GmRowHeight, 24);
        UiKit.Button(_gmRoot, "攻击＋", left + 360, row, 100, GmRowHeight, () => { _game.MonsterAtkScale = StepScale(_game.MonsterAtkScale, 1); RefreshGm(); })
            .Tip("怪物攻击的额外倍率（1 = 不额外缩放）。改完立刻作用于场上的怪。");
        NextRow();
        // 主角这一行放两个**玩家侧开关**：无敌与普攻形态。原来它们各占"标题一行 + 按钮一行"，
        // 竖向太浪费；而两者的性质一致（都是改主角自己），并排一行读起来也更顺。
        row = Row("主角");
        // 无敌：**不掉血、不死亡**。死亡会清增益 + 重置冷却，于是伤害统计里各技能的周期会断掉、
        // 每秒数字失真——想量准就得先站得住。
        _invincibleButton = UiKit.Button(_gmRoot, InvincibleText, left, row, 180, GmRowHeight, () =>
        {
            _game.PlayerInvincible = !_game.PlayerInvincible;
            _invincibleButton.Text = InvincibleText;
            RefreshGm();
        });
        _invincibleButton.Tip("不掉血、不死亡。死亡会清增益并重置冷却，让伤害统计断档——挂一局量数字前先打开它。");
        // 普攻形态：翻转修行树上的「剑气」解锁，用来对照近战与远程两种形态。
        // **它改的就是玩法状态本身**（不是另开一个会话开关）：所以两边行为一定一致，没有"调试态与真实态不同"的坑。
        _meleeButton = UiKit.Button(_gmRoot, MeleeText, left + 190, row, 180, GmRowHeight, () =>
        {
            _game.DebugToggleRangedBasic();
            RefreshGm();
        });
        _meleeButton.Tip("翻转「剑气」节点：近战 = 射程 150 / 停步 120 / 挥剑斩击；远程 = 射程 950 / 停步 640 / 平射飞剑。");
        NextRow();
        // 波次数量：只影响**之后**刷出的波次与前行的下一格，已经在场的怪不动。
        row = Row("波次数量");
        UiKit.Button(_gmRoot, "波次−", left, row, 100, GmRowHeight, () => { _game.WaveBonus--; RefreshGm(); })
            .Tip("每波额外少刷几只（下限 0）。");
        _waveBonusValue = UiKit.Label(_gmRoot, "", left + 110, row, 240, GmRowHeight, 24);
        UiKit.Button(_gmRoot, "波次＋", left + 360, row, 100, GmRowHeight, () => { _game.WaveBonus++; RefreshGm(); })
            .Tip("每波额外多刷几只，种类从普通怪里随机挑。上限 20。");
        NextRow();
        EndArea(commands);

        // ── 大区二：编辑器与特殊功能入口（换个地方干活） ──
        // 三个入口都**先关掉自己再开**：保证同时只有一个弹层（`TapIn(_gmRoot, "关闭")` 是全树取首个匹配）。
        var tools = StartArea(GmTools);
        row = Row("编辑器");
        // 修行星图的节点编辑器（开发期工具）。它**只写 TalentLayout.csv**，内容表仍手工维护；
        // 保存前按与加载期同一套规则自查，不合格就拒绝保存、不碰任何文件。
        UiKit.Button(_gmRoot, "节点编辑器", left, row, 180, GmRowHeight, () => { CloseGm(); ToggleTalentEditor(); })
            .Tip("编辑修行星图的格子与前置连线。只写布局表；新建节点会给内容表补一行骨架。");
        // 第二个编辑器（以后再来的按同一条规矩加在这一行/下面），落在「编辑器」这个小分区里。
        UiKit.Button(_gmRoot, "关卡编辑器", left + 190, row, 180, GmRowHeight, () => { CloseGm(); ToggleLevelEditor(); })
            .Tip("看关卡长度 / 强度倍率曲线 / 波次与首领，并显示模型建议值。");
        NextRow();
        row = Row("特殊功能");
        UiKit.Button(_gmRoot, "伤害统计", left, row, 145, GmRowHeight, () => { CloseGm(); OpenDamage(); })
            .Tip("按技能看累计伤害 / 每秒伤害，用来核对数值平衡。");
        // 技能预览**从主界面搬进 GM**：它原来占着中间那条横带的一个位置，而那条横带整条撤掉了。
        // 按钮文字跟着状态走，进这里也能退出来。
        _previewToggle = UiKit.Button(_gmRoot, "", left + 155, row, 145, GmRowHeight, () => { CloseGm(); TogglePreview(); });
        _previewToggle.Tip("用独立会话逐个播放 15 个法术，不写存档；预览期间主线挂机暂停。再点一次退出。");
        // 第三个入口：看角色属性——fightattr 全表（基础 / 养成后 / 当前生效）+ 当前临时状态。
        UiKit.Button(_gmRoot, "属性面板", left + 310, row, 145, GmRowHeight, () => { CloseGm(); OpenAttributes(); })
            .Tip("看每个属性的基础值、养成后的最终值，以及叠上临时状态后当前真正生效的量。");
        NextRow();
        EndArea(tools);

        // 分成两条定长标签而不是一条 `Wrapped`：CJK 长句在 `WordSmart` 下不一定断行，会直接溢出面板。
        UiKit.Label(_gmRoot, "倍率立刻作用于场上的怪；波次只影响之后刷出的波次。", GmLeft + GmPadX, y, 580, 26, 18, UiKit.Muted);
        y += 26;
        UiKit.Label(_gmRoot, "开关都不写配置、不落盘。", GmLeft + GmPadX, y, 580, 26, 18, UiKit.Muted);
        y += 26 + 24;

        // 面板高度按内容底边补上（先建后补是为了让底板画在内容之下）。
        panel.Size = new Vector2(GmWidth, y - GmTop);
        _gmPanelBottom = y;
    }

    /// <summary>面板底边（设计坐标）。自检断言它留在屏内——面板是浮动的、没有滚动条。</summary>
    private float _gmPanelBottom;

    /// <summary>自检用：面板底边。见 <see cref="_gmPanelBottom"/>。</summary>
    public float GmPanelBottomForCheck() => _gmPanelBottom;

    private void RefreshGm()
    {
        if (_gmRoot is null) return;
        _invincibleButton.Text = InvincibleText;
        _meleeButton.Text = MeleeText;
        if (_previewToggle is not null) _previewToggle.Text = _preview is null ? "技能预览" : "退出预览";
        _monsterHpValue.Text = $"×{_game.MonsterHpScale:0.##}";
        _monsterAtkValue.Text = $"×{_game.MonsterAtkScale:0.##}";
        _waveBonusValue.Text = $"+{_game.WaveBonus} 只";
    }
}
