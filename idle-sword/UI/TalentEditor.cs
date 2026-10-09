using Godot;
using IdleSword.Core;
using IdleSword.Features;   // TalentText：效果文案（长句 + 短标签）都从那儿取，不在编辑器里另写一套
// `System.IO` 里也有个 FileAccess，这里要的是 Godot 那个（走 res:// 抽象层）。
using FileAccess = Godot.FileAccess;

namespace IdleSword.UI;

/// <summary>内存里的一份布局。字段与 `TalentLayout.csv` 的四列一一对应。</summary>
public sealed class TalentLayoutNode
{
    public string Id = "";
    public int Col;
    public int Row;
    /// <summary>前置节点 id，**条数不限**。空 = 它是根。语义是"任一点亮即可"。</summary>
    public List<string> Prereqs = [];
    /// <summary>
    /// **给人看的可读名**，随便改、可以空、可以重名——游戏一个字节都不读它。
    /// 它和 `id` 是两件事：`id` 给机器（存档与配置引用它，**永远不能变**），这里给人和 AI。
    /// 之所以要分开：策划只关心"这是个什么节点"，逼他起 id 的结果就是 `1313` / `sdfsdf` 这种名字，
    /// 而且改起来还绑着"旧存档会丢等级"的代价。分开之后改名零风险。
    /// </summary>
    public string Label = "";
}

/// <summary>编辑器的画布：网格、节点方块、id 文字、连线、选中高亮，全部自绘。</summary>
public partial class TalentEditorCanvas : Control
{
    private readonly Main _host;
    public TalentEditorCanvas(Main host) { _host = host; MouseFilter = MouseFilterEnum.Stop; }

    public override void _Draw() => _host.DrawTalentEditor(this);
    public override void _GuiInput(InputEvent @event) => _host.TalentEditorInput(@event);
}

/// <summary>
/// 修行星图的节点编辑器（开发期工具，从 GM 面板进）。
///
/// **这里的分工是「人交结构 + 意图，AI 出内容」**——不是"给策划填数值的界面"。
/// 人做两件事：摆格子与连线（`TalentLayout.csv`）、给每个节点写一句**功能意图**（`TalentPlan.csv`）。
/// 名字 / 等级 / 消耗 / 效果这些数值仍然在 `Talent.csv` 里，但由 AI 照意图产出 ——
/// 那些数值不是画格子画出来的，让工具去猜只会帮倒忙。
///
/// 所以编辑器拥有两张表：`TalentLayout.csv`（几何与拓扑）与 `TalentPlan.csv`（意图侧车）。
/// **侧车游戏不加载**（不在 `GameConfig.Files` 里），纯粹是交接单；缺文件按空处理，
/// 解析失败则**拒绝保存**——宁可不动，也不拿一份解析坏了的表去覆盖人已经写好的意图。
///
/// `Talent.csv` 编辑器**只补骨架行**（新建节点补 `effect=none`、免费），不改正文：
/// 两表 id 对不上会让游戏直接起不来，补一行合法骨架就能避免「摆了个节点、还没填数值、
/// 结果整个工程加载失败」这种半成品状态。
///
/// **只在从源码运行时能保存**：打包版的 `res://` 是只读的，这一点在点保存时立刻报出来，
/// 而不是等摆完半张图才发现。
///
/// 这套东西与 [TalentMap] 共用同一份几何——编辑器画出来的格子与玩家看到的星图必须逐格对得上。
/// </summary>
public partial class Main
{
    private const string LayoutPath = "res://Config/Tables/TalentLayout.csv";
    private const string ContentPath = "res://Config/Tables/Talent.csv";
    /// <summary>意图侧车。**不进 `GameConfig.Files`**——它是人给 AI 的交接单，不是配置表。</summary>
    private const string PlanPath = "res://Config/Tables/TalentPlan.csv";
    private const string PlanHeader = "id,intent";
    /// <summary>编辑器画布左上角。网格原点落在这儿，所以 col/row 读数与屏幕位置直接对得上。</summary>
    private static readonly Vector2 EditorOrigin = new(30, 150);
    /// <summary>
    /// 网格的**绘制**范围：左右各这么多列（列号在数据上不设限，往左可以是负的）。
    /// 给的是画多少格，不是允许摆到哪——摆到范围外只是看不见网格线，节点照常能放。
    /// </summary>
    private const int EditorCols = 20;

    private readonly List<TalentLayoutNode> _editorNodes = [];
    /// <summary>id → 一句话功能意图。**空串 = 待定**（人还没写，AI 不许猜）。只在保存时落到 `TalentPlan.csv`。</summary>
    private readonly Dictionary<string, string> _editorIntents = [];
    /// <summary>
    /// id → `Talent.csv` 的 `name`（**游戏正式名**）。编辑器拥有这一格：读出来预填、改了写回、不动就原样往返。
    /// **只有这一格**归编辑器，其余列（effect / cost / icon）仍由人和 AI 维护。
    /// </summary>
    private readonly Dictionary<string, string> _editorNames = [];
    /// <summary>
    /// id → 它的**整行内容**，来自用户点「复制信息」抄过来的源节点。
    /// 与 `_editorNames` 分开，是为了让"编辑器写哪些格子"精确可控：只写它被明确要求写的那几格，
    /// 其余列一律不碰——否则编辑器开着的时候，AI 在磁盘上改过的行会被它保存时**悄悄改回去**。
    /// 保存成功后清空（磁盘已经是它了）。
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, string>> _editorCopied = [];
    /// <summary>侧车解析失败时置位：拒绝保存。宁可不动，也不拿一份坏表覆盖人已写好的意图。</summary>
    private bool _editorPlanCorrupt;
    /// <summary>回填意图输入框时短暂置位，避免 `TextChanged` 反过来又写一遍（幂等，但省一次重绘）。</summary>
    private bool _editorFillingIntent;
    /// <summary>同上，给"可读名"那个输入框用。**两个框共用一个 `LineEdit`**（见 `_editorId`），角色不同。</summary>
    private bool _editorFillingName;
    /// <summary>同上，给"正式名"那个输入框用。</summary>
    private bool _editorFillingFormal;
    /// <summary>可读名输入框里现在填的是谁的 label（空串 = 正在新建）。变了才回填，免得打断正在打字的人。</summary>
    private string _editorIdShownFor = "";
    private Control _editorRoot = null!;
    private TalentEditorCanvas _editorCanvas = null!;
    private LineEdit _editorId = null!;
    private Label _editorStatus = null!, _editorInfo = null!;
    // 「意图」那栏的提示句**并进了标题**（面板塞不下第三行），所以没有独立的 hint 控件。
    private Label _editorIntentLabel = null!;
    private LineEdit _editorIntent = null!;
    private Button _editorCreate = null!, _editorLink = null!, _editorClear = null!, _editorMakeRoot = null!;
    private Button _editorCopy = null!, _editorMove = null!, _editorDelete = null!;
    private Label _editorNameHint = null!, _editorFormalHint = null!;
    private Label _editorEffectLabel = null!, _editorCurrencyLabel = null!, _editorCostLabel = null!;
    private Label _editorCostHint = null!, _editorLevelHint = null!;
    private Label _editorPerLevelLabel = null!, _editorMaxLevelLabel = null!, _editorFooter = null!;
    /// <summary>效果下拉里的 id 表（与条目**下标一一对应**）。条目文案取 `TalentText.ShortLabel`。</summary>
    private readonly List<string> _editorEffectIds = [];
    private OptionButton _editorEffect = null!;
    /// <summary>回填效果下拉时置位——理由同 <see cref="_editorFillingCurrency"/>。</summary>
    private bool _editorFillingEffect;
    private Label _editorIconLabel = null!;
    private OptionButton _editorIcon = null!;
    /// <summary>图标下拉的 id 表（与条目**下标一一对应**）。取值就是 `GameConfig` 的 `icon` 白名单。</summary>
    private readonly List<string> _editorIconIds = [];
    /// <summary>回填图标下拉时置位——理由同 <see cref="_editorFillingCurrency"/>。</summary>
    private bool _editorFillingIcon;

    // ── 右侧面板：排版机制在 `UI/SectionList.cs`（与关卡编辑器共用一份）──
    /// <summary>面板第一个分区标题的 y。</summary>
    private const float EditorPanelTop = 150f;
    /// <summary>右侧面板在**屏幕**坐标里的左沿与宽度。</summary>
    private const float EditorPanelX = 1202f, EditorPanelWidth = 696f;
    /// <summary>分区在面板内的左沿（`SectionList` 内部会再加一次它自己的 `PadX`）。</summary>
    private const float EditorPanelPadX = 8f;

    private SectionList _editorPanel = null!;
    /// <summary>当前选中的是节点 / 是空格子。行控件的可见性由排版过程按它们算。</summary>
    private bool _editorActing, _editorCreating;
    /// <summary>货币的 id 表（与下拉菜单的**下标一一对应**）——种类从 `item.csv` 现取，不写死。</summary>
    private readonly List<string> _editorCurrencyIds = [];
    private OptionButton _editorCurrency = null!;
    /// <summary>回填下拉时置位：`Selected` 的 setter **会发出** `ItemSelected`，不挡的话会自己触发一次"用户改了货币"。</summary>
    private bool _editorFillingCurrency;
    private LineEdit _editorFormalName = null!, _editorCost = null!;
    private LineEdit _editorPerLevel = null!, _editorMaxLevel = null!;
    /// <summary>回填"每级消耗"框时短暂置位，避免 `TextChanged` 把刚读出来的值又写一遍。</summary>
    private bool _editorFillingCost;
    /// <summary>同上，给"每级效果量"与"最大等级"两个框用。</summary>
    private bool _editorFillingLevels;
    private VBoxContainer _editorPrereqList = null!;
    private TalentLayoutNode? _editorSelected;
    /// <summary>已经点过一次「删除节点」的那一项：再点一次才真删。删一个 id 是不可逆的，值得多问一句。</summary>
    private string? _editorPendingDelete;
    private (int Col, int Row)? _editorEmptyCell;
    /// <summary>正在等待点击一个节点、把它加成前置。**条数不限**，所以只记"在不在连线"，不记槽位。</summary>
    private bool _editorAwaitingLink;
    /// <summary>正在等待点击一个**节点**作为「复制信息」的源。</summary>
    private bool _editorAwaitingCopy;
    /// <summary>正在等待点击一个**空格子**，把选中节点移过去。</summary>
    private bool _editorAwaitingMove;
    /// <summary>下一个自动 id 的序号。**只增不减**——理由见 <see cref="NewNodeId"/>。</summary>
    private int _editorNextId = 100001;
    private Vector2 _editorPan, _editorDragFrom;
    private bool _editorDragging, _editorMoved;

    private void BuildTalentEditor()
    {
        _editorRoot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        UiKit.Place(_editorRoot, 0, 0, 1920, 1080);
        AddChild(_editorRoot);

        // **遮罩要完全不透明**：留着那 4% 的透明度，底下游戏的 HUD（底栏那行小字之类）会透上来，
        // 与编辑器自己的文字叠在一起，看着像排版坏了（实机反馈过）。这是个开发工作台，藏住游戏才对。
        var backdrop = new ColorRect { Color = new Color(0.02f, 0.04f, 0.06f), MouseFilter = Control.MouseFilterEnum.Stop };
        UiKit.Place(backdrop, 0, 0, 1920, 1080); _editorRoot.AddChild(backdrop);

        UiKit.Label(_editorRoot, "修行星图 · 节点编辑器", 30, 20, 700, 46, 34, UiKit.Gold);
        // 一行写不下就会顶着标签宽度溢出到右边去（Label 不裁文字），所以这句保持短。
        UiKit.Label(_editorRoot, "画布可拖拽平移；点空格子新建，点节点选中；「添加前置」后再点另一个节点即可连线。"
            + "选中节点可写一句功能意图，保存时一并落进 TalentPlan.csv。",
            30, 70, 1500, 30, 19, UiKit.Muted);
        UiKit.Button(_editorRoot, "保存", 1500, 24, 120, 44, EditorSave, true);
        UiKit.Button(_editorRoot, "重新读取", 1630, 24, 140, 44, EditorReload);
        UiKit.Button(_editorRoot, "关闭", 1780, 24, 120, 44, ToggleTalentEditor);

        // 画布要裁到自己的矩形：网格比画布大时，多出来的部分会画到画布外面、从右侧面板底下穿过去。
        // **两个裁剪开关都要开**，各自管一半：`ClipContents` 裁这个控件自己的 `_Draw` 输出
        // （格子、连线、id 与意图文字全是它自己画的），`ClipChildren` 管挂在它上面的子节点。
        // 只开后者是不够的——`--capture` 的截图里能看到 col 6 之后的节点一路画到右侧面板底下。
        _editorCanvas = new TalentEditorCanvas(this)
        {
            Position = EditorOrigin, ClipContents = true, ClipChildren = ClipChildrenMode.AndDraw,
        };
        UiKit.Place(_editorCanvas, EditorOrigin.X, EditorOrigin.Y, 1150, 880);
        // 给画布加一块**底板 + 描边**：不然看不出"哪块是可以拖拽/点选的操作区"（用户反馈）。
        // 底板垫在画布**之前**（先加的先画）。
        // ⚠️ 用 `Ink` 而不是默认的 `Panel`：节点方块的填充就是 `Panel`，同色时方块只剩描边认得出（实机反馈）。
        // 这样三层色阶拉开——背幕（更暗的自定义色）< 画布底 `Ink` < 节点 `Panel`。
        UiKit.PanelAt(_editorRoot, EditorOrigin.X - 10, EditorOrigin.Y - 10, 1170, 900, UiKit.Ink);
        _editorRoot.AddChild(_editorCanvas);

        // ── 右侧面板：六个**可收拢**的分区，装在一个滚动容器里 ──
        // 排版机制（收拢、留白、装不下就滚、钉底脚注）在 `UI/SectionList.cs`，与关卡编辑器共用一份。
        // 分区与行的坐标全部**相对面板左上角**（`EditorPanelPadX` = 屏幕上的 1210）。
        _editorPanel = new SectionList(_editorRoot, EditorPanelX, EditorPanelTop, EditorPanelWidth,
            "保存即生效：结构/复制/名字/效果/价目都由编辑器写盘，要让节点有用就写意图给 AI。");
        float px = EditorPanelPadX;
        Control inner = _editorPanel.Inner;
        // 下面那一大段沿用 `Section(...)` / `Row(...)` 的写法，只是转发到共用组件上。
        SectionList.Section Section(string title, float height) => _editorPanel.Add(title, height);
        void Row(SectionList.Section section, Control control, float dy, RowWhen when = RowWhen.Acting) =>
            SectionList.Row(section, control, dy, when);

        // ① 提示：状态消息**独占一块**，不再与下面的行挤在一起（文案本身也缩短了）。
        // 高度按**两行**声明：状态文案一律要能两行内说完（20 号字 × 680 宽 ≈ 每行 34 个字），
        // 超了就会撑出底板、被下面那一块盖住（实机反馈的"遮挡"，根因在这儿）。
        var promptSection = Section("提示", 56);
        // 19 号字（不是 20）：两行的**最小**高度 = 2×行高 + 3 ≈ 55，正好卡进 56 的声明值里。
        // 用 20 号字量出来是 59，声明值就得跟着改，而面板总高本来就紧。
        _editorStatus = UiKit.Capped(UiKit.Wrapped(inner, "", px, 0, 680, 56, 19, UiKit.Muted), 2);
        Row(promptSection, _editorStatus, 0, RowWhen.Filled);

        // ② 选中节点：可读名 / 正式名 / 图标 / id·位置 / 前置摘要。
        // 行距按"上一行的底 + 6"算：这里原先给的是 60 / 94，而可读名那行本身有 56 高、LineEdit 有 38，
        // 94 < 60+38 ⇒ **第 2、3 行压在一起**（截图上是叠字）。控件的高度不是声明值说了算的，
        // 所以行距得让开实际高度——自检里有一条专门量这个。
        var nodeSection = Section("选中节点", 188);
        _editorInfo = UiKit.Capped(UiKit.Wrapped(inner, "", px, 0, 680, 56, 19, UiKit.Text), 2);
        Row(nodeSection, _editorInfo, 0);
        // 这一格是**可读名**：选中节点时是它的 label，选中空格子时是"给新节点起的名字"，
        // 后者旁边才出现「在此格新建」。**id 不在这儿**——它由编辑器自动分配，界面上不暴露。
        _editorId = new LineEdit { PlaceholderText = "可读名（只给编辑器看）" };
        _editorId.AddThemeFontSizeOverride("font_size", 20);
        UiKit.Place(_editorId, px, 0, 300, 38); inner.AddChild(_editorId);
        _editorId.TextChanged += EditorNameChanged;
        Row(nodeSection, _editorId, 62, RowWhen.Any);
        _editorNameHint = UiKit.Label(inner, "只给编辑器看", px + 316, 0, 364, 38, 18, UiKit.Muted);
        Row(nodeSection, _editorNameHint, 62);
        _editorCreate = UiKit.Button(inner, "在此格新建", px + 316, 0, 200, 38, EditorCreateNode);
        Row(nodeSection, _editorCreate, 62, RowWhen.Creating);
        _editorFormalName = new LineEdit { PlaceholderText = "正式名（玩家看到的）" };
        _editorFormalName.AddThemeFontSizeOverride("font_size", 20);
        UiKit.Place(_editorFormalName, px, 0, 300, 38); inner.AddChild(_editorFormalName);
        _editorFormalName.TextChanged += EditorFormalNameChanged;
        Row(nodeSection, _editorFormalName, 106);
        _editorFormalHint = UiKit.Label(inner, "玩家在说明条上看到的就是它", px + 316, 0, 364, 38, 18, UiKit.Muted);
        Row(nodeSection, _editorFormalHint, 106);
        // 图标：**只在这四个里选**（`GameConfig` 的 `icon` 白名单就是这四个），条目带缩略图。
        // 做成下拉而不是"素材选择库"：Godot 的 `EditorResourcePicker` 是**编辑器专用**控件，运行时游戏用不了；
        // 而 `FileDialog` 会让用户挑到任意文件、写出白名单外的值——加载期直接拒整份配置（工程起不来）。
        _editorIconLabel = UiKit.Label(inner, "图标", px, 0, 120, 38, 19, UiKit.Muted);
        Row(nodeSection, _editorIconLabel, 150);
        _editorIcon = new OptionButton();
        _editorIcon.AddThemeFontSizeOverride("font_size", 19);
        // 节点图标本身是 60px 的图，**直接塞进下拉会把按钮撑到 72 高**（按钮最小高 = 图高 + 内边距）。
        // `icon_max_width` 这个主题常量既限制画出来的大小、也把它计入最小高的那部分限制住——
        // 不然光这一行就会顶掉小半个分区。它是 **theme constant**，不是 C# 属性（没有 `IconMaxWidth`）。
        _editorIcon.AddThemeConstantOverride("icon_max_width", 22);
        UiKit.Place(_editorIcon, px + 130, 0, 300, 38); inner.AddChild(_editorIcon);
        foreach (string iconId in IconChoiceIds())
        {
            _editorIconIds.Add(iconId);
            _editorIcon.AddItem(TalentText.IconLabel(iconId));
            // 图标取不到就**不带图**（`Texture` 找不到时返回 null，星图那边也是这么兜的）。
            _editorIcon.SetItemIcon(_editorIconIds.Count - 1, _battle.Texture("node_" + iconId));
        }
        _editorIcon.ItemSelected += index =>
        {
            if (_editorFillingIcon) return;
            if (index >= 0 && index < _editorIconIds.Count) EditorSetIcon(_editorIconIds[(int)index]);
        };
        Row(nodeSection, _editorIcon, 150);

        // ③ 内容：改这几格是最常做的微调，每次都去改 CSV 或找 AI 效率太低（用户原话）。
        // 写完与"复制信息"走同一条"待写"通道，只覆盖被明确要求写的格子。
        // 高度按实测声明（行距 46 = LineEdit 38 + 8）——声明值只是下限，但写成真实值才对得上自检。
        var contentSection = Section("内容", 176);
        _editorEffectLabel = UiKit.Label(inner, "效果", px, 0, 120, 38, 19, UiKit.Muted);
        Row(contentSection, _editorEffectLabel, 0);
        // **效果也用下拉**（同货币）：12 项已经占两行，以后效果只会更多。
        // 条目文案取 `TalentText.ShortLabel`——与玩家侧那句长文案**同一处维护**，不在编辑器里另写一套。
        _editorEffect = new OptionButton();
        _editorEffect.AddThemeFontSizeOverride("font_size", 19);
        UiKit.Place(_editorEffect, px + 126, 0, 554, 38); inner.AddChild(_editorEffect);
        foreach (string effectId in EffectChoiceIds())
        {
            _editorEffectIds.Add(effectId);
            _editorEffect.AddItem(TalentText.ShortLabel(effectId));
        }
        _editorEffect.ItemSelected += index =>
        {
            if (_editorFillingEffect) return;   // 同货币下拉：`Selected` 的 setter 会发信号，回填时要挡住
            if (index >= 0 && index < _editorEffectIds.Count) EditorSetEffect(_editorEffectIds[(int)index]);
        };
        Row(contentSection, _editorEffect, 0);
        // 两个数值框**各配一个标题**（之前只有占位文字，看不出是什么）。
        _editorPerLevelLabel = UiKit.Label(inner, "每级效果量", px, 0, 124, 38, 19, UiKit.Muted);
        Row(contentSection, _editorPerLevelLabel, 46);
        _editorPerLevel = new LineEdit();
        _editorPerLevel.AddThemeFontSizeOverride("font_size", 19);
        UiKit.Place(_editorPerLevel, px + 130, 0, 150, 38); inner.AddChild(_editorPerLevel);
        _editorPerLevel.TextChanged += EditorPerLevelChanged;
        Row(contentSection, _editorPerLevel, 46);
        _editorMaxLevelLabel = UiKit.Label(inner, "最大等级", px + 296, 0, 110, 38, 19, UiKit.Muted);
        Row(contentSection, _editorMaxLevelLabel, 46);
        _editorMaxLevel = new LineEdit();
        _editorMaxLevel.AddThemeFontSizeOverride("font_size", 19);
        UiKit.Place(_editorMaxLevel, px + 410, 0, 110, 38); inner.AddChild(_editorMaxLevel);
        _editorMaxLevel.TextChanged += EditorMaxLevelChanged;
        Row(contentSection, _editorMaxLevel, 46);
        _editorLevelHint = UiKit.Label(inner, "", px + 528, 0, 152, 38, 17, UiKit.Muted);
        Row(contentSection, _editorLevelHint, 46);
        // 货币按 `item.csv` 里 `kind=currency` **现取**，不写死 gold/core：加货币不用改这里，也不会漏。
        _editorCurrencyLabel = UiKit.Label(inner, "货币", px, 0, 120, 38, 19, UiKit.Muted);
        Row(contentSection, _editorCurrencyLabel, 92);
        _editorCurrency = new OptionButton();
        _editorCurrency.AddThemeFontSizeOverride("font_size", 19);
        UiKit.Place(_editorCurrency, px + 126, 0, 554, 38); inner.AddChild(_editorCurrency);
        foreach (var cur in _game.Config.Rows("item").Where(r => r.Text("kind") == "currency"))
        {
            _editorCurrencyIds.Add(cur.Text("id"));
            _editorCurrency.AddItem(cur.Text("name"));
        }
        _editorCurrency.ItemSelected += index =>
        {
            if (_editorFillingCurrency) return;
            if (index >= 0 && index < _editorCurrencyIds.Count) EditorSetCurrency(_editorCurrencyIds[(int)index]);
        };
        Row(contentSection, _editorCurrency, 92);
        // 每级消耗**也有标题**（原先只有占位文字，实机反馈看不懂那两个框是什么）。
        _editorCostLabel = UiKit.Label(inner, "货币消耗", px, 0, 124, 38, 19, UiKit.Muted);
        Row(contentSection, _editorCostLabel, 138);
        _editorCost = new LineEdit { PlaceholderText = "每级消耗，用 | 分隔" };
        _editorCost.AddThemeFontSizeOverride("font_size", 19);
        UiKit.Place(_editorCost, px + 130, 0, 340, 38); inner.AddChild(_editorCost);
        _editorCost.TextChanged += EditorCostChanged;
        Row(contentSection, _editorCost, 138);
        _editorCostHint = UiKit.Label(inner, "", px + 480, 0, 200, 38, 17, UiKit.Muted);
        Row(contentSection, _editorCostHint, 138);

        // ④ 前置列表：**条数不限**，所以是滚动容器而不是固定的几个槽位。
        var prereqSection = Section("前置（任一点亮即可）", 90);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        UiKit.Place(scroll, px, 0, 680, 90); inner.AddChild(scroll);
        _editorPrereqList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_editorPrereqList);
        Row(prereqSection, scroll, 0);

        // ⑤ 操作：**六个按钮排两行各三个**——挤一行每个只剩 113px，"复制信息"这类四字标题会顶出边框。
        // 按钮做成**紧凑**的（18 号字 + 内边距 2）：默认那套（21 号字 + 内边距 8）最小高是 45，
        // 六个按钮要 93px，而这里是全屏高度最挤的一段。压到 32 之后两个按钮 70px 就够。
        var actSection = Section("节点操作", 71);
        Button Act(string text, float ax, Action action)
        {
            var b = UiKit.Button(inner, text, ax, 0, 220, 32, action, pad: 2);
            b.AddThemeFontSizeOverride("font_size", 18);
            return b;
        }
        _editorLink = Act("添加前置", px, EditorBeginLink);
        _editorClear = Act("清空前置", px + 230, EditorClearPrereq);
        _editorMakeRoot = Act("设为Root节点", px + 460, EditorMakeRoot);
        _editorCopy = Act("复制信息", px, EditorBeginCopy);
        _editorMove = Act("移到某格", px + 230, EditorBeginMove);
        _editorDelete = Act("删除节点", px + 460, EditorDeleteNode);
        Row(actSection, _editorLink, 0); Row(actSection, _editorClear, 0); Row(actSection, _editorMakeRoot, 0);
        Row(actSection, _editorCopy, 38); Row(actSection, _editorMove, 38); Row(actSection, _editorDelete, 38);

        // ⑥ 交接给 AI：人机分工里"人"的那一半。
        // 语义容易被误解成"这个节点的说明"——**它是待办**，所以标题里就把它说死。
        // 长度控制在**一行内**：超了会顶着标签宽度溢出到面板外面（截图上量到过）。
        // 也别写 `**`：这是普通 Label，不解析 markdown，星号会原样显示出来。
        var aiSection = Section("交接给 AI", 66);
        _editorIntentLabel = UiKit.Label(inner,
            "功能意图（要 AI 做的改动 · 做完就清空；空 = 没有待办，AI 不动它）", px, 0, 680, 22, 18, UiKit.Muted);
        Row(aiSection, _editorIntentLabel, 0);
        _editorIntent = new LineEdit { PlaceholderText = "例：攻击 +2 ／ 解锁铸造系统 ／ 章节门 · 破土" };
        _editorIntent.AddThemeFontSizeOverride("font_size", 20);
        UiKit.Place(_editorIntent, px, 0, 680, 40); inner.AddChild(_editorIntent);
        Row(aiSection, _editorIntent, 26);
        // 底栏那句脚注由 `SectionList` 建并钉在面板底部（见它的构造参数）——它不进分区、也不进视口：
        // 挂进分区会让那块在没选中节点时也撑开、底下留一大段空白；挂进视口则会随内容滚走。
        LayoutEditorPanel();
        _editorIntent.TextChanged += EditorIntentChanged;
        EditorReload();
    }

    public void ToggleTalentEditor()
    {
        bool open = !_editorRoot.Visible;
        _editorRoot.Visible = open;
        if (open) { EditorReload(); Sfx.Click(); }
    }

    /// <summary>重新从磁盘读一份布局进内存。**只刷新编辑器自己那份副本**——游戏本体不热重载。</summary>
    private void EditorReload()
    {
        _editorNodes.Clear();
        foreach (var r in CsvTable.Parse("TalentLayout.csv", FileAccess.GetFileAsString(LayoutPath)))
        {
            var node = new TalentLayoutNode
            {
                Id = r.Text("id"), Col = r.Int("col"), Row = r.Int("row"), Label = r.Text("label").Trim(),
            };
            node.Prereqs.AddRange(r.TextList("prereq"));
            _editorNodes.Add(node);
        }
        EditorReloadIntents();
        EditorReloadNames();
        // 自动 id 的起点：取现有最大的 +1。**只增不减**——理由见 `NewNodeId`（复用会继承已删节点的信息）。
        _editorNextId = 100001;
        foreach (string id in _editorNodes.Select(n => n.Id).Concat(_editorNames.Keys))
            if (id.StartsWith("t_") && int.TryParse(id[2..], out int n) && n >= _editorNextId)
                _editorNextId = n + 1;
        _editorSelected = null; _editorEmptyCell = null;
        _editorAwaitingLink = _editorAwaitingCopy = _editorAwaitingMove = false;
        _editorIdShownFor = "";   // 强制重新回填可读名输入框
        EditorRefreshUi();
    }

    /// <summary>
    /// 读意图侧车。**缺文件不算错**（第一次跑、或侧车被删掉），按"全部待定"处理；
    /// 但**解析失败要置位、并在保存时拒绝写盘**——那时表已经坏了，再整份重写一遍
    /// 就等于把人写过的意图全冲干净，而那是最不容易被发现的一种损失。
    /// </summary>
    private void EditorReloadIntents()
    {
        _editorIntents.Clear();
        _editorPlanCorrupt = false;
        if (!FileAccess.FileExists(PlanPath)) return;
        try
        {
            foreach (var r in CsvTable.Parse("TalentPlan.csv", FileAccess.GetFileAsString(PlanPath)))
                _editorIntents[r.Text("id")] = r.Text("intent");
        }
        catch (Exception ex)
        {
            _editorPlanCorrupt = true;
            _editorStatus.Text = "TalentPlan.csv 读不出来（" + ex.Message + "）：这一屏只能看，保存会被拒绝。";
        }
    }

    /// <summary>
    /// 节点在**编辑器界面上**显示的名字：**填了 `label` 就用它，没填就显示 `id`**。
    ///
    /// **刻意不去读 `Talent.csv` 的 `name`**（那是给玩家看的正式名字）：那样反倒要人多维护一份，
    /// 而且两个名字会分叉。这里要的只是"策划自己能认出来的一串字"。
    /// </summary>
    private string NodeLabel(string id) =>
        _editorNodes.FirstOrDefault(n => n.Id == id)?.Label.Trim() is { Length: > 0 } own ? own : id;

    /// <summary>
    /// 报错 / 状态文案里的节点指称：**可读名 + 括号里的 id**。
    /// 只给可读名的话，用户回 `TalentLayout.csv` 里对不上号（文件里写的是 id）。
    /// </summary>
    private string Ref(TalentLayoutNode n) => n.Label.Trim().Length > 0 ? $"{n.Label.Trim()}（{n.Id}）" : n.Id;

    /// <summary>
    /// 读 `Talent.csv` 的 `name` 一列进内存（正式名），并**清掉上一轮的"复制"痕迹**。
    /// 与 `EditorReloadIntents` 同一时机刷新——「重新读取」就该把一切回到磁盘上的样子。
    /// </summary>
    private void EditorReloadNames()
    {
        _editorNames.Clear();
        _editorCopied.Clear();
        foreach (var r in CsvTable.Parse("Talent.csv", FileAccess.GetFileAsString(ContentPath)))
            _editorNames[r.Text("id")] = r.Text("name");
    }

    /// <summary>
    /// 编辑器改了一格内容（货币 / 每级消耗）→ 记进**"待写"那一份**里。
    /// 与「复制信息」共用同一套机制（`_editorCopied`）：它只覆盖**编辑器被明确要求写的那些格子**，
    /// 其余列一个字不碰——否则编辑器开着的时候，AI 在磁盘上改过的行会被它保存时**悄悄改回去**。
    /// </summary>
    private void EditorSetContentCell(string id, string column, string value)
    {
        if (!_editorCopied.TryGetValue(id, out var cells)) _editorCopied[id] = cells = [];
        cells[column] = value;
    }

    /// <summary>改这个节点的图标（玩家星图上那块图形）。走同一条"待写"通道，只覆盖这一格。</summary>
    private void EditorSetIcon(string iconId)
    {
        if (_editorSelected is not { } sel) return;
        EditorSetContentCell(sel.Id, "icon", iconId);
        _editorStatus.Text = $"「{NodeLabel(sel.Id)}」的图标改成「{TalentText.IconLabel(iconId)}」。"
            + "画布上不画图标，星图上才是这个形状。";
        EditorRefreshUi();
    }

    /// <summary>改这个节点花哪种货币。灵核节点在画布上带**菱形标记**，所以一改图上立刻看得出来。</summary>
    private void EditorSetCurrency(string currencyId)
    {
        if (_editorSelected is not { } sel) return;
        EditorSetContentCell(sel.Id, "cost_currency", currencyId);
        _editorStatus.Text = $"「{NodeLabel(sel.Id)}」的货币改成「{_game.Config.Row("item", currencyId).Text("name")}」。"
            + "画布上带菱形标记的就是灵核节点。";
        EditorRefreshUi();
    }

    /// <summary>
    /// 把每级消耗的列表**补齐或截断**，让它与最大等级对上（项数不等是加载期会拒整份配置的硬规则）。
    /// 补齐按**最后一档向上递延**：`4|8` 要 5 级 → `4|8|16|32|64`。
    /// 补出来的只是起点，改完还能逐档微调——总比让工具留一段空白强。
    /// </summary>
    private void AdjustCostForLevels(string id, int maxLevel)
    {
        var current = PendingContentRow(id).GetValueOrDefault("cost", "")
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (current.Count == 0) current.Add("0");
        while (current.Count < maxLevel)
        {
            double last = double.TryParse(current[^1], out double v) ? v : 0;
            current.Add((last * 2).ToString("0.##"));
        }
        if (current.Count > maxLevel) current.RemoveRange(maxLevel, current.Count - maxLevel);
        EditorSetContentCell(id, "cost", string.Join("|", current));
    }

    /// <summary>
    /// 改效果。**开关 / 解锁类只能是 1 级、且每级效果量必须大于 0**——这两条都是加载期会拒整份配置的硬规则，
    /// 所以选到这类效果时**当场自动压一下并说明**，而不是留到保存（甚至下次启动）才炸。
    /// </summary>
    private void EditorSetEffect(string effectId)
    {
        if (_editorSelected is not { } sel) return;
        EditorSetContentCell(sel.Id, "effect", effectId);
        // 这两句自动修正的说明**必须短**：状态那一格只装得下两行（分区高度是照着两行声明的），
        // 超了就会撑出底板——底下那一块再盖上来，画面上是半句话。所以理由留在代码里，提示只报事实。
        string note = "";
        if (GameConfig.SwitchEffects.Contains(effectId))
        {
            var row = PendingContentRow(sel.Id);
            if (row.GetValueOrDefault("max_level", "1") != "1")
            {
                EditorSetContentCell(sel.Id, "max_level", "1");
                AdjustCostForLevels(sel.Id, 1);
                note += "　开关类锁 1 级，等级与价目已收成 1 档。";
            }
            if (!double.TryParse(row.GetValueOrDefault("effect_per_level", "0"), out double per) || per <= 0)
            {
                EditorSetContentCell(sel.Id, "effect_per_level", "1");
                note += "　每级量要 > 0 才生效，已设成 1。";
            }
        }
        _editorStatus.Text = $"「{NodeLabel(sel.Id)}」的效果 → {TalentText.ShortLabel(effectId)}。{note}";
        _editorIdShownFor = "";   // 强制回填各格：最大等级 / 每级量 / 价目都可能刚被自动改过
        EditorRefreshUi();
    }

    /// <summary>改最大等级。价目列表要**跟着补齐或截断**，否则项数对不上、加载期会拒整份配置。</summary>
    private void EditorMaxLevelChanged(string text)
    {
        if (_editorFillingLevels || _editorSelected is not { } sel) return;
        if (!int.TryParse(text.Trim(), out int max) || max < 1)
        {
            _editorLevelHint.Text = "要 ≥1 的整数";
            return;
        }
        if (GameConfig.SwitchEffects.Contains(PendingContentRow(sel.Id).GetValueOrDefault("effect", "none")) && max != 1)
        {
            _editorLevelHint.Text = "开关 / 解锁类只能 1 级";
            return;
        }
        EditorSetContentCell(sel.Id, "max_level", max.ToString());
        AdjustCostForLevels(sel.Id, max);
        // **只回填价目框，不动用户正在打的那个**（`_editorFillingCost` 抑制它的 TextChanged）——
        // 顺手把它一起回填的话，光标会跟着跳。
        _editorFillingCost = true;
        _editorCost.Text = PendingContentRow(sel.Id).GetValueOrDefault("cost", "");
        _editorFillingCost = false;
        _editorCostHint.Text = $"共 {max} 级";
        _editorLevelHint.Text = $"共 {max} 级 · 价目已对齐";
        _editorCanvas.QueueRedraw();
    }

    /// <summary>
    /// **按百分比理解的效果**：这两个在 `Talent.csv` 里存的是**分数**（`0.2` = +20%），
    /// 因为它们并进 `1 + …` 乘区（见 `GameSession.MaxHp` / `Attack`）。
    /// 而固定值那三个（`atk_flat` / `hp_flat` / `drop_flat`）是乘区外直接相加的绝对量。
    ///
    /// 编辑器对这两支**按百分数收、按百分数显示**：用户填 20 就是 +20%。
    /// 从前是原样读写分数，于是"填 20"被当成 **20 倍**（+2000%）——差两个数量级，
    /// 而加载期只要求 ≥0，不会替你判断，属于**静默放大**那类错。
    /// 展示侧本来就是 `:P0`（见 `TalentText`），这里只是把编辑侧对齐到同一个口径。
    /// </summary>
    private static readonly string[] PercentEffects = ["atk", "hp"];

    private static bool IsPercentEffect(string effectId) => PercentEffects.Contains(effectId);

    /// <summary>把存盘的分数换算成输入框里的百分数（0.2 → 20）。不是数值字符串时原样返回。</summary>
    private static string PerLevelToBox(string effectId, string stored)
        => IsPercentEffect(effectId) && double.TryParse(stored, out double v) ? (v * 100).ToString("0.####") : stored;

    /// <summary>改每级效果量。开关类必须大于 0——填 0 是"买了不生效且不报错"的典型静默失效。</summary>
    private void EditorPerLevelChanged(string text)
    {
        if (_editorFillingLevels || _editorSelected is not { } sel) return;
        string effect = PendingContentRow(sel.Id).GetValueOrDefault("effect", "none");
        bool percent = IsPercentEffect(effect);
        if (!double.TryParse(text.Trim(), out double per) || per < 0)
        {
            _editorLevelHint.Text = "要 ≥0 的数";
            return;
        }
        if (GameConfig.SwitchEffects.Contains(effect) && per <= 0)
        {
            _editorLevelHint.Text = "开关类的每级效果量要大于 0";
            return;
        }
        // 百分比效果：输入的是**百分数**，落盘要换成分数。用 `0.####` 而不是 `0.##`——
        // 百分数下 0.1% 这类小值换成 0.001，两位小数会被截成 0。
        double stored = percent ? per / 100 : per;
        EditorSetContentCell(sel.Id, "effect_per_level", stored.ToString(percent ? "0.####" : "0.##"));
        _editorLevelHint.Text = percent ? $"每级 +{per:0.##}%" : $"每级 +{per:0.##}";
        _editorCanvas.QueueRedraw();
    }

    /// <summary>
    /// 改每级消耗。**项数必须等于 `max_level`**（加载期就是这么校验的）——写坏了整份配置都加载不了，
    /// 所以不合法时**不写内存**，只在右边提示里说清要写几项。
    /// </summary>
    private void EditorCostChanged(string text)
    {
        if (_editorFillingCost || _editorSelected is not { } sel) return;
        int max = int.TryParse(PendingContentRow(sel.Id).GetValueOrDefault("max_level", "1"), out int m) && m > 0 ? m : 1;
        var parts = text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != max || parts.Any(p => !double.TryParse(p, out double v) || v < 0))
        {
            _editorCostHint.Text = $"要写 {max} 项（每级一项）、都是非负数";
            return;
        }
        EditorSetContentCell(sel.Id, "cost", string.Join("|", parts));
        _editorCostHint.Text = $"共 {max} 级 · 已记下";
    }

    /// <summary>正式名改动 → **只写内存**，落盘交给「保存」（与可读名、意图同一套写法）。</summary>
    private void EditorFormalNameChanged(string text)
    {
        if (_editorFillingFormal || _editorSelected is not { } sel) return;
        _editorNames[sel.Id] = text.Trim();
    }

    /// <summary>可读名框改动 → **只写内存**，落盘交给「保存」（与意图框同一套写法）。</summary>
    private void EditorNameChanged(string text)
    {
        if (_editorFillingName || _editorSelected is not { } sel) return;
        sel.Label = text.Trim();
        _editorCanvas.QueueRedraw();
    }

    /// <summary>意图框改动 → **只写内存**。落盘交给「保存」，否则每敲一个字都在写盘。</summary>
    private void EditorIntentChanged(string text)
    {
        if (_editorFillingIntent || _editorSelected is not { } sel) return;
        _editorIntents[sel.Id] = text.Trim();
        MarkIntentLabel(text);
        _editorCanvas.QueueRedraw();
    }

    /// <summary>空意图用金色顶出来：这一屏本身就是"还有哪些没交代清楚"的清单。</summary>
    private void MarkIntentLabel(string intent) =>
        _editorIntentLabel.AddThemeColorOverride("font_color", intent.Trim().Length == 0 ? UiKit.Gold : UiKit.Muted);

    private void EditorRefreshUi()
    {
        var sel = _editorSelected;
        // 换了选中项就撤掉"待确认"——否则那个已经变成「确认删除」的按钮会跟着走到别的节点上去。
        if (_editorPendingDelete is not null && _editorPendingDelete != sel?.Id) _editorPendingDelete = null;
        // **两行写完**（那一格只声明了两行，多的会被 `Capped` 截掉）：可读名与 id·位置并到一行，
        // 第二行留给前置摘要。三行的版本会把这一格顶到 90 高，整块面板跟着往下坠。
        _editorInfo.Text = sel is null
            ? (_editorEmptyCell is { } cell ? $"空格子 col {cell.Col} · row {cell.Row}" : "点一个格子：空格子新建，已有节点则选中。")
            : $"{NodeLabel(sel.Id)}｜{sel.Id} · col {sel.Col} · row {sel.Row}\n前置：{DescribePrereqsForEditor(sel)}";
        // 行的**位置与可见性一律交给 `LayoutEditorPanel()`**（它同时负责收拢与上提），
        // 这里只更新"当前是什么状态"与各控件的**内容**——两处都写 `Visible` 迟早互相覆盖。
        _editorActing = sel is not null;
        _editorCreating = sel is null && _editorEmptyCell is not null;
        LayoutEditorPanel();
        // 换了选中项才回填两个名字框 —— 每帧回填会**打断正在打字的人**。
        string idFor = sel?.Id ?? "";
        if (_editorIdShownFor != idFor)
        {
            _editorIdShownFor = idFor;
            _editorFillingName = true;
            _editorId.Text = sel?.Label ?? "";
            _editorFillingName = false;
            // 正式名：新建的节点还没有内容行，表里给的是骨架（`name = id`），所以回退到 id。
            _editorFillingFormal = true;
            _editorFormalName.Text = sel is null ? "" : _editorNames.GetValueOrDefault(sel.Id, sel.Id);
            _editorFillingFormal = false;
            // **只有"每级消耗"这个框**要跟着选中项回填（它装的是用户输入，每次刷新都覆盖会打断打字）。
            // ⚠️ `sel` 在这里**可能是 null**：删除节点后 `_editorSelected` 就没了，而 `idFor` 跟着变、
            // 于是这一段照样会进来——直接写 `sel.Id` 会空引用（改这行时踩过）。
            var pending = sel is null ? new Dictionary<string, string>() : PendingContentRow(sel.Id);
            _editorFillingCost = true;
            _editorCost.Text = pending.GetValueOrDefault("cost", "");
            _editorFillingCost = false;
            _editorFillingLevels = true;
            // 百分比效果的两支按百分数显示（`0.2` 读成 `20`）——与写入口径对称，见 `PercentEffects`。
            _editorPerLevel.Text = PerLevelToBox(pending.GetValueOrDefault("effect", "none"),
                pending.GetValueOrDefault("effect_per_level", ""));
            _editorMaxLevel.Text = pending.GetValueOrDefault("max_level", "");
            _editorFillingLevels = false;
        }
        if (sel is not null)
        {
            // ⚠️ 这一段**每次刷新都要跑**，不能塞进"选中项变了"那个分支里：
            // 它们是**当前状态的读数**，不是用户正在输入的框。点完效果 / 货币之后如果这里不跟手，
            // 这一栏就在显示过期状态、骗人（实机反馈过一次："选了灵核，货币状态没跟着变"）。
            var pending = PendingContentRow(sel.Id);
            _editorCostHint.Text = $"共 {pending.GetValueOrDefault("max_level", "1")} 级";
            // 百分比效果**常显单位**：不然"框里那个 20 到底是 20% 还是 20 倍"只能靠猜。
            // 这一格只有 152px 宽（约 9 个字），所以说不了长句——只说口径。
            // 其余效果这里清空（它是校验消息的落点，不能留上一条选择留下的字）。
            _editorLevelHint.Text = IsPercentEffect(pending.GetValueOrDefault("effect", "none")) ? "（百分数）" : "";
            // 效果与货币都改由**下拉的选中项**表达状态（不再是 `●` 标记）。回填时要挡住信号——
            // `Selected` 的 setter 会发出 `ItemSelected`，不挡的话会被当成"用户选了"。
            _editorFillingEffect = true;
            int effectIndex = _editorEffectIds.IndexOf(pending.GetValueOrDefault("effect", "none"));
            _editorEffect.Selected = effectIndex >= 0 ? effectIndex : 0;
            _editorFillingEffect = false;
            // 货币改由**下拉的选中项**表达状态（不再是 `●` 标记）。回填时要挡住信号——
            // `Selected` 的 setter 会发出 `ItemSelected`，不挡的话会被当成"用户改了货币"。
            _editorFillingCurrency = true;
            int currencyIndex = _editorCurrencyIds.IndexOf(pending.GetValueOrDefault("cost_currency", ""));
            _editorCurrency.Selected = currencyIndex >= 0 ? currencyIndex : 0;
            _editorFillingCurrency = false;
            // 图标同理：它是"当前状态的读数"，回填时挡住信号（`Selected` 的 setter 会发 `ItemSelected`）。
            _editorFillingIcon = true;
            int iconIndex = _editorIconIds.IndexOf(pending.GetValueOrDefault("icon", "utility"));
            _editorIcon.Selected = iconIndex >= 0 ? iconIndex : 0;
            _editorFillingIcon = false;
        }
        if (sel is not null)
        {
            // 回填时抑制 `TextChanged`：写回去的是同一个值，幂等，但会白跑一次重绘。
            _editorFillingIntent = true;
            _editorIntent.Text = _editorIntents.GetValueOrDefault(sel.Id, "");
            _editorFillingIntent = false;
            MarkIntentLabel(_editorIntent.Text);
            _editorDelete.Text = _editorPendingDelete is not null ? "确认删除" : "删除节点";
            _editorClear.Disabled = sel.Prereqs.Count == 0;
            _editorCopy.Disabled = _editorNodes.Count < 2;   // 树上只有一个节点时没有源可抄
            // 「设为Root节点」是个**状态按钮**：它已经就是根的时候不该还喊"点我来设为根"。
            // 灰掉的原因是"**没有前置可摘**"（也就是已经做完了），不是"做不到"——
            // 光灰着不改字，看到的人只会以为功能不可用（2026-10-06 用户实机就是这么误会的）。
            _editorMakeRoot.Disabled = sel.Prereqs.Count == 0;
            _editorMakeRoot.Text = sel.Prereqs.Count > 0 ? "设为Root节点" : "已是Root节点";
            RefreshPrereqList(sel);
        }
        _editorCanvas.QueueRedraw();
    }

    /// <summary>
    /// 重建前置列表。每行一个按钮（`✕ id`），点它就把**那一条**摘掉——条数不限，
    /// 所以必须能逐条删；「清空前置」只作为整块重来的快捷方式。
    /// 行的样式复用 `UiKit.Button`，但**不要**用 `UiKit.Place` 定位：容器会接管子节点的位置与宽度。
    /// </summary>
    private void RefreshPrereqList(TalentLayoutNode sel)
    {
        foreach (Node child in _editorPrereqList.GetChildren())
        {
            _editorPrereqList.RemoveChild(child);
            child.QueueFree();
        }
        if (sel.Prereqs.Count == 0)
        {
            var hint = new Label { Text = "（无前置）", VerticalAlignment = VerticalAlignment.Center };
            hint.AddThemeFontSizeOverride("font_size", 19);
            hint.AddThemeColorOverride("font_color", UiKit.Muted);
            hint.CustomMinimumSize = new Vector2(0, 40);
            _editorPrereqList.AddChild(hint);
            return;
        }
        foreach (string id in sel.Prereqs)
        {
            string prey = id;   // 闭包捕获：循环变量直接进 lambda 会捕获到最后一次的值
            // 显示可读名，但**摘除时用的仍然是 id**（写盘、引用一律按 id）。
            var row = UiKit.Button(_editorPrereqList, "✕   " + NodeLabel(prey), 0, 0, 0, 40, () => EditorRemovePrereq(prey));
            row.Alignment = HorizontalAlignment.Left;   // 列表要左对齐；`UiKit.Button` 默认居中，读起来像一排按钮
            row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.CustomMinimumSize = new Vector2(0, 40);
        }
    }

    /// <summary>
    /// 「前置」这一栏的**一句话摘要**（具体 id 在下面的列表里）。
    /// **不把"还没连线"说成"根节点"**：全图只允许一个根、且固定在 `(0, 2)`，
    /// 而新建出来的节点只是还没接上——说成根节点会让人以为它是合法的，保存时才被校验打回来。
    /// </summary>
    private string DescribePrereqsForEditor(TalentLayoutNode sel)
    {
        if (sel.Prereqs.Count > 0) return $"{sel.Prereqs.Count} 条（任一点亮即可）";
        return IsRoot(sel) ? "（根节点 · 全图起点）" : "⚠ 未连前置（除根节点外，每个节点都必须有）";
    }

    /// <summary>
    /// 这个节点是不是根。**判据是"全图只有它一个没有前置"，不看位置**——
    /// 根落在哪一行由设计决定（左上、左下都行），从前钉死 `(0, 2)` 会让"在起点前面插几个节点"很难做。
    /// </summary>
    private bool IsRoot(TalentLayoutNode n) =>
        n.Prereqs.Count == 0 && _editorNodes.Count(x => x.Prereqs.Count == 0) == 1;

    /// <summary>最近一次保存的说明文字。自检用它断言"没被取消、也没失败"。</summary>
    public string TalentEditorStatus => _editorStatus.Text;

    /// <summary>自检 / 截图用：走一遍真实的保存路径（只写布局表）。</summary>
    public void TalentEditorSaveForCheck() => EditorSave();

    /// <summary>画布上的输入：按下记起点，拖动超过阈值就算平移，松手时没拖动才算点格子。</summary>
    public void TalentEditorInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            _editorDragging = button.Pressed;
            _editorDragFrom = button.Position; _editorMoved = false;
            if (!button.Pressed && !_editorMoved)
            {
                var cell = EditorCellAt(button.Position);
                EditorClickCell(cell.Col, cell.Row);
            }
            return;
        }
        if (@event is InputEventMouseMotion motion && _editorDragging)
        {
            var delta = motion.Position - _editorDragFrom;
            if (!_editorMoved && delta.Length() < 6) return;   // 6px 阈值：手抖不该被当成平移
            _editorMoved = true;
            _editorPan += delta; _editorDragFrom = motion.Position;
            _editorCanvas.QueueRedraw();
        }
    }

    /// <summary>画布局部坐标 → 格号。允许落在没有节点的空格子上（那是"新建"的入口）。</summary>
    private (int Col, int Row) EditorCellAt(Vector2 local)
    {
        var p = local - _editorPan - new Vector2(0, TalentMap.OriginY - 8);
        return ((int)Math.Floor(p.X / TalentMap.CellWidth), (int)Math.Floor(p.Y / TalentMap.CellHeight));
    }

    private Vector2 EditorCenter(TalentLayoutNode n) =>
        _editorPan + new Vector2((n.Col + .5f) * TalentMap.CellWidth, (n.Row + .5f) * TalentMap.CellHeight);

    /// <summary>
    /// 画整张图。用的是**与玩家星图同一份几何**（<see cref="TalentMap"/>）——编辑器摆的格子
    /// 与玩家看到的必须逐格对得上，否则工具就失去意义了。
    /// </summary>
    public void DrawTalentEditor(Control canvas)
    {
        int rows = (int)_game.Config.Setting("talent_grid_rows");
        float w = TalentMap.CellWidth, h = TalentMap.CellHeight;
        var font = GetThemeDefaultFont();
        var gridLine = new Color(UiKit.Line, .40f);
        // **列可以是负的**（根往左长），所以网格左右都要铺。只画 `0..EditorCols` 的话，
        // 负列那一侧看着像块空白——虽然能点、能放节点，但你不知道该往哪儿放。
        for (int c = -EditorCols; c <= EditorCols; c++)
            canvas.DrawLine(new(c * w + _editorPan.X, 0), new(c * w + _editorPan.X, rows * h), gridLine, 1);
        for (int r = 0; r <= rows; r++)
            canvas.DrawLine(new(-EditorCols * w, r * h + _editorPan.Y), new(EditorCols * w, r * h + _editorPan.Y), gridLine, 1);

        foreach (var n in _editorNodes)
            foreach (var p in n.Prereqs)
            {
                var up = _editorNodes.FirstOrDefault(x => x.Id == p);
                if (up is not null) TalentMap.DrawArrow(canvas, EditorCenter(up), EditorCenter(n), new Color(UiKit.Jade, .85f), 4);
            }

        // 连线状态下：**能当前置的描金框、其余压暗**。高亮与"点下去会不会被接受"同源
        // （两处都走 `CanBePrereq`），否则金框里点一下反而被拒，比不高亮更让人糊涂。
        // 集合在这里算一次，别丢进绘制循环里反复算——`CanBePrereq` 里带着一次查环遍历。
        var sel = _editorSelected;
        HashSet<string>? candidates = _editorAwaitingLink && sel is not null
            ? _editorNodes.Where(n => CanBePrereq(n, sel)).Select(n => n.Id).ToHashSet()
            : null;
        // 「复制信息」/「移到某格」模式下**标的是"目标"**，不是候选：
        // 复制模式的"合法源"是**除自己以外的全部**，全亮出来等于没亮（所以不点金框）；
        // 真正要看清的是"我正在把哪一个节点搬走 / 复制到哪一个"。
        // 用**青玉色**与「添加前置」那套金色区分开：金色 = 可作前置，青玉 = 正在搬 / 正在复制。
        bool marking = (_editorAwaitingCopy || _editorAwaitingMove) && sel is not null;

        // 方块与文字**分两趟画**：id 与意图摘要落在方块下沿之外，行距 90 而方块 76，
        // 所以它们正好压进**下一行**的方块范围里——同一个循环里画的话，下一行的方块会把它盖掉。
        foreach (var n in _editorNodes)
        {
            var rect = new Rect2(EditorCenter(n) - new Vector2(TalentMap.NodeSize / 2, TalentMap.NodeSize / 2),
                new Vector2(TalentMap.NodeSize, TalentMap.NodeSize));
            bool dimmed = candidates is not null && !candidates.Contains(n.Id);
            // 压暗用**实心**的暗色，不用半透明：填充一透，底下的连线就穿过节点显出来（实机反馈）。
            // `Dim()` 保留给描边与文字——它们本来就是线，透一点没关系。
            canvas.DrawRect(rect, dimmed ? UiKit.Ink : UiKit.Panel, true);
            if (candidates?.Contains(n.Id) == true)
            {
                canvas.DrawRect(rect.Grow(5), new Color(UiKit.Gold, .25f), false, 2);
                canvas.DrawRect(rect, UiKit.Gold, false, 3);
            }
            else
            {
                // 根节点描金：一眼看出"整张图是从这里长出来的"。
                Color border = n.Prereqs.Count == 0 ? UiKit.Gold : UiKit.Line;
                canvas.DrawRect(rect, dimmed ? Dim(border) : border, false, 2);
            }
            // **还没配内容的节点**（`effect=none` 的占位，含"连行都还没有"的那种）在格子里标一个「空」——
            // 一眼扫出"哪些还没配"，不用逐个点开看。这类节点在游戏里**买不了**（`CanBuyTalent` 见 none 直接拒），
            // 挂在起点上还会把整棵树锁死，所以值得显眼。
            if (PendingContentRow(n.Id).GetValueOrDefault("effect", "none") == "none")
                canvas.DrawString(font, new(rect.Position.X, rect.Position.Y + TalentMap.NodeSize / 2 + 7), "空",
                    HorizontalAlignment.Center, TalentMap.NodeSize, 20, UiKit.Muted);
            // 灵核节点：四角角标（与玩家星图同一个 `DrawCoreCorners`，形状必须一致）。
            // 用**待写的那一行**判断，所以刚在面板上把货币改成灵核，画布上立刻就有角标。
            if (PendingContentRow(n.Id).GetValueOrDefault("cost_currency", "") == "core")
                TalentMap.DrawCoreMark(canvas, EditorCenter(n));
            if (ReferenceEquals(n, _editorSelected)) canvas.DrawRect(rect.Grow(4), UiKit.Gold, false, 3);
            if (marking)
            {
                if (ReferenceEquals(n, sel))
                {
                    canvas.DrawRect(rect.Grow(7), new Color(UiKit.Jade, .28f), false, 2);
                    canvas.DrawRect(rect, UiKit.Jade, false, 4);
                }
                // 其余节点给一层很淡的边：意思是"这一屏现在是可以点的"，但不去抢目标的注意力。
                else canvas.DrawRect(rect, new Color(UiKit.Jade, .35f), false, 2);
            }
        }

        foreach (var n in _editorNodes)
        {
            bool dimmed = candidates is not null && !candidates.Contains(n.Id);
            var topLeft = EditorCenter(n) - new Vector2(TalentMap.NodeSize / 2, TalentMap.NodeSize / 2);
            // id 写在方块**下面**而不是里面：方块只有 76px，长 id 会被截断。
            canvas.DrawString(font, new(topLeft.X, topLeft.Y + TalentMap.NodeSize + 15), NodeLabel(n.Id),
                HorizontalAlignment.Left, -1, 14, dimmed ? Dim(UiKit.Text) : UiKit.Text);
            // 再下一行是意图摘要，**只在写过意图时才画**——于是空意图的节点自己"光秃秃"的，
            // 整张图扫一眼就知道还有哪些没交代。截断到 12 字：画布有 ClipContents，长句会糊成一片。
            string intent = _editorIntents.GetValueOrDefault(n.Id, "").Trim();
            if (intent.Length > 0)
                canvas.DrawString(font, new(topLeft.X, topLeft.Y + TalentMap.NodeSize + 31), Truncate(intent, 12),
                    HorizontalAlignment.Left, -1, 12, dimmed ? Dim(UiKit.Jade) : UiKit.Jade);
            // 正在搬 / 正在复制时，在目标下面写一句"要干什么"——光有一圈颜色，第一次用的人还是不知道。
            if (marking && ReferenceEquals(n, sel))
                canvas.DrawString(font, new(topLeft.X, topLeft.Y + TalentMap.NodeSize + 47),
                    _editorAwaitingCopy ? "复制到这里" : "把它搬走",
                    HorizontalAlignment.Left, -1, 13, UiKit.Jade);
        }

        if (_editorEmptyCell is { } cell)
        {
            var topLeft = new Vector2((cell.Col + .5f) * w - TalentMap.NodeSize / 2, (cell.Row + .5f) * h - TalentMap.NodeSize / 2) + _editorPan;
            var rect = new Rect2(topLeft, new Vector2(TalentMap.NodeSize, TalentMap.NodeSize));
            canvas.DrawRect(rect, new Color(UiKit.Jade, .18f), true);
            canvas.DrawRect(rect, UiKit.Jade, false, 2);
        }
    }

    /// <summary>画布点击：空格子 → 记下待新建；已有节点 → 选中；正在连线 → 完成连线。</summary>
    public void EditorClickCell(int col, int row)
    {
        var hit = _editorNodes.FirstOrDefault(n => n.Col == col && n.Row == row);
        if (_editorAwaitingLink)
        {
            if (hit is not null) EditorCompleteLink(hit);
            else _editorStatus.Text = "连线要落在另一个**节点**上。";
            return;
        }
        if (_editorAwaitingCopy)
        {
            if (hit is not null) EditorCompleteCopy(hit);
            else _editorStatus.Text = "复制源要落在一个**节点**上。";
            return;
        }
        if (_editorAwaitingMove)
        {
            if (hit is null) EditorCompleteMove(col, row);
            else _editorStatus.Text = $"({col}, {row}) 上已经有节点了（{NodeLabel(hit.Id)}）——挑一个**空格子**。";
            return;
        }
        if (hit is null) { _editorSelected = null; _editorEmptyCell = (col, row); _editorId.Text = ""; }
        else { _editorSelected = hit; _editorEmptyCell = null; }
        EditorRefreshUi();
    }

    /// <summary>
    /// 新节点的 id：**内置、自动分配**，`t_100001` 起取最小未用序号。
    ///
    /// 界面上**不暴露 id、也不让人输**——策划只管可读名（`label`）。从前要求手输 id 的结果，
    /// 就是 `1313` / `sdfsdf` / `124124124` 这种名字；而且一旦想改，还绑着"旧存档会丢等级"的代价。
    /// **已有的 id 一律不动**（存档引用它们，改了才是风险）。
    /// </summary>
    private string NewNodeId()
    {
        // **只增不减、绝不复用刚删掉的号**。
        //
        // 从前的写法是"取最小未用"，于是"删掉一个 → 在同一格再建一个"会**立刻拿回同一个号**——
        // 而内存里属于那个旧 id 的 `_editorNames` / `_editorIntents` / `_editorCopied` 还留着，
        // 新节点一撞上就把**已删节点**的正式名、意图、内容全读了出来（用户实机撞到的就是这个）。
        // 又因为内容表的行要到保存时才消失，复用还会让那一行**被当成它自己的**留下来。
        while (true)
        {
            string id = "t_" + _editorNextId++;
            if (!_editorNodes.Any(n => n.Id == id) && !_editorNames.ContainsKey(id)) return id;
        }
    }

    private void EditorCreateNode()
    {
        if (_editorEmptyCell is not { } cell) return;
        var node = new TalentLayoutNode
        {
            Id = NewNodeId(), Col = cell.Col, Row = cell.Row, Label = _editorId.Text.Trim(),
        };
        _editorNodes.Add(node);
        _editorSelected = node; _editorEmptyCell = null; _editorId.Text = "";
        _editorStatus.Text = $"「{NodeLabel(node.Id)}」已建在 (col {cell.Col}, row {cell.Row})——它还没有前置。"
            + "点「添加前置」，再点画布上的上游节点把它接上。";
        EditorRefreshUi();
    }

    /// <summary>
    /// 退出所有"等你在画布上点一下"的模式。**三种模式必须互斥**：同时武装两个的话，
    /// `EditorClickCell` 只走先判的那一个，另一个既不生效、又退不掉。
    /// </summary>
    private void ClearModes() => _editorAwaitingLink = _editorAwaitingCopy = _editorAwaitingMove = false;

    /// <summary>
    /// 重排右侧面板。实现在 `SectionList.Layout`（与关卡编辑器共用）——
    /// 这里只把**本编辑器的状态**喂进去：`Always` 的行（提示 / 脚注）与是否选中节点无关，
    /// 其余行跟"选中了节点 / 选中了空格子"走。`EditorRefreshUi` 只负责更新这两个状态与各控件的**内容**。
    /// </summary>
    private void LayoutEditorPanel() => _editorPanel.Layout(new SectionState(_editorActing, _editorCreating));

    /// <summary>效果下拉的条目：数值类 + 开关类；**解锁类从 `Systems.ByEffect` 现取**（与加载期校验同一处）。</summary>
    private static string[] EffectChoiceIds() =>
        ["none", "atk", "hp", "atk_flat", "hp_flat", "drop_flat",
            "auto_basic", "ranged_basic", .. Systems.ByEffect.Keys];

    /// <summary>
    /// 图标下拉的条目。**必须与 `GameConfig` 里 `icon` 的白名单同源**——加载期是按那张表校验的，
    /// 这里多一个就会写出"下次启动工程起不来"的配置。界面上按 `node_<icon>` 去 `visuals.json` 取图。
    /// （要加图标得三处一起动：`GameConfig` 白名单 + `tools/SpriteGen` + `visuals.json`，
    /// 这也是"做成下拉而不是素材选择库"的原因：下拉天然写不出白名单外的值。）
    /// </summary>
    private static string[] IconChoiceIds() => ["attack", "defense", "utility", "special"];

    /// <summary>进入连线状态：接下来点到的那个节点会被**追加**成一条前置（条数不限，所以没有"第几槽"）。</summary>
    private void EditorBeginLink()
    {
        if (_editorSelected is not { } sel) return;
        // **再点一次同一个按钮 = 取消**：进错了模式总得有退路，不然只能硬着头皮点完。
        if (_editorAwaitingLink) { ClearModes(); _editorStatus.Text = "已取消。"; EditorRefreshUi(); return; }
        ClearModes();
        _editorAwaitingLink = true;
        int count = _editorNodes.Count(n => CanBePrereq(n, sel));
        _editorStatus.Text = count > 0
            ? $"正在添加前置：点金框里的节点（共 {count} 个）。"
            : "没有可接的了——其它节点要么**已经是它的前置**，要么接上会**成环**。";
        // **必须重绘**：金框与压暗都是 `_Draw` 里按连线状态现算的，不重绘一个都不会出现。
        EditorRefreshUi();
    }

    /// <summary>
    /// 候选能不能当 <paramref name="sel"/> 的前置？**这是"行不行"的唯一判据**——
    /// 画布上的金框与 <see cref="EditorCompleteLink"/> 的接受条件都走它。两边一旦分叉，
    /// 金框里点一下反而被拒，那比不高亮更让人糊涂。
    /// </summary>
    private bool CanBePrereq(TalentLayoutNode candidate, TalentLayoutNode sel) =>
        !ReferenceEquals(candidate, sel)            // 不能是自己
        && !sel.Prereqs.Contains(candidate.Id)      // 不能已经连过
        && !EditorReaches(candidate.Id, sel.Id);    // 不能连成环（**唯一**的结构约束，与列号无关）

    private void EditorCompleteLink(TalentLayoutNode upstream)
    {
        var sel = _editorSelected!;
        _editorAwaitingLink = false;
        if (!CanBePrereq(upstream, sel))
        {
            // 「行不行」只有一个判据，但「为什么不行」得逐条说清楚——所以这里按原来的顺序复测一遍。
            _editorStatus.Text = ReferenceEquals(upstream, sel) ? "不能把自己设为前置。"
                : sel.Prereqs.Contains(upstream.Id) ? "这个前置已经连过了。"
                // 成环当场拦住——等保存时再报就太晚了，那时图已经摆乱。
                // **列号不再约束前置**，所以"在更右边"不是拒绝理由（那条限制已按用户要求去掉）。
                : $"会连成一个环：{upstream.Id} 已经（间接）依赖 {sel.Id}。";
            EditorRefreshUi();   // 连线状态已经退出，金框要当场退回（否则会一直挂在屏幕上）
            return;
        }
        sel.Prereqs.Add(upstream.Id);   // **只追加**：条数不限，重复与成环都由 `CanBePrereq` 拦在前面
        _editorStatus.Text = $"已把「{upstream.Id}」加为「{sel.Id}」的前置（现在共 {sel.Prereqs.Count} 条 · 任一点亮即可）。";
        EditorRefreshUi();
    }

    /// <summary>
    /// 保存后额外要说的一句"**树还点不开**"。眼下只有一种情形：**起点是 `effect=none` 的占位节点**。
    ///
    /// 占位节点在引擎里**买不了**（`CanBuyTalent` 见 `none` 直接拒），而它后面的节点全都要求
    /// "任一条前置点亮"——于是整张星图只剩起点一个节点，还点不动，**游戏直接卡死**。
    /// 这一点在编辑器里完全看不出来（布局合法、id 对得上、保存成功），只能靠这里说一句。
    /// </summary>
    private string RootTrap()
    {
        var root = _editorNodes.FirstOrDefault(n => IsRoot(n));
        if (root is null) return "";
        var content = CsvTable.Parse("Talent.csv", FileAccess.GetFileAsString(ContentPath))
            .FirstOrDefault(r => r.Text("id") == root.Id);
        return content?.Text("effect") != "none" ? ""
            : $"　⚠ 起点「{root.Id}」还是占位节点（effect=none）——**它买不了，整棵树在游戏里都点不开**，记得配效果。";
    }

    /// <summary>压暗：连线状态下把"不能选"的节点退到背景里去（保留色相、只降不透明度）。</summary>
    private static Color Dim(Color color) => new(color, .28f);

    /// <summary>进入"选复制源"状态：接下来点到的那个节点，它的内容会被抄到当前选中节点上。</summary>
    private void EditorBeginCopy()
    {
        if (_editorSelected is not { } sel) return;
        if (_editorAwaitingCopy) { ClearModes(); _editorStatus.Text = "已取消。"; EditorRefreshUi(); return; }
        ClearModes();
        _editorAwaitingCopy = true;
        // 把"现在在干什么、能点什么、怎么移动画面"一次说完——这条模式的反馈光靠画布上那圈颜色不够。
        _editorStatus.Text = $"复制到「{NodeLabel(sel.Id)}」：点一个节点当复制源（其它 {_editorNodes.Count - 1} 个都行）。";
        EditorRefreshUi();
    }

    /// <summary>
    /// 把 <paramref name="source"/> 的**内容行**抄到当前选中节点上。
    ///
    /// **抄**：`Talent.csv` 的整行。**不抄**：`id`（行自己的）、`col/row`（新节点在自己格子上）、
    /// `prereq`（自己接）。`label` 与**意图**只在目标为空时才带——用户可能已经先起了名字、写了意图，
    /// 那是他的劳动，**不能**被一次"复制"抹掉。
    /// </summary>
    private void EditorCompleteCopy(TalentLayoutNode source)
    {
        var sel = _editorSelected!;
        _editorAwaitingCopy = false;
        if (ReferenceEquals(source, sel))
        { _editorStatus.Text = "复制源不能是它自己。"; EditorRefreshUi(); return; }
        string text = FileAccess.GetFileAsString(ContentPath);
        string[] header = text.TrimStart('﻿').TrimEnd('\n').Split('\n')[0].Split(',');
        var row = CsvTable.Parse("Talent.csv", text).FirstOrDefault(r => r.Text("id") == source.Id);
        if (row is null)
        { _editorStatus.Text = $"「{NodeLabel(source.Id)}」还没有内容行，没有可抄的东西。"; EditorRefreshUi(); return; }

        // **正式名避重**：抄过来会让两个节点叫同一个名字，而玩家悬停看到两个「培土」会分不清。
        string baseName = row.Text("name");
        var taken = _editorNames.Where(kv => kv.Key != sel.Id).Select(kv => kv.Value).ToHashSet();
        string unique = baseName;
        for (int n = 2; taken.Contains(unique); n++) unique = $"{baseName} {n}";

        var copied = header.ToDictionary(h => h, h => h == "id" ? sel.Id : h == "name" ? unique : row.Text(h));
        _editorCopied[sel.Id] = copied;
        _editorNames[sel.Id] = unique;

        // **意图不抄**——它是"要 AI 做的改动"（**待办**），不是这个节点的说明。
        // 复制要的是那个节点的**现成信息**；把它的待办也搬过来，只会让「某某分支起点」这类描述
        // 到处扩散、信息乱掉，还会让 AI 照着一个**不属于这个节点**的意图去改它（实机踩过：
        // 内容抄的是 B、意图留的是 A，AI 会把 B 改回 A）。
        // 想改复制出来的节点？**再给它写一句新意图**——那才是这一栏的意义。
        //
        // 可读名只在目标为空时带：它只是显示名，留着用户自己起的没有正确性风险。
        bool labelCarried = sel.Label.Trim().Length == 0 && source.Label.Trim().Length > 0;
        if (labelCarried) sel.Label = source.Label;

        _editorIdShownFor = "";   // 强制回填两个输入框（可读名与正式名都可能变了）
        // 复制**不动**意图，所以目标原来若留着一句，它现在很可能已经跟内容对不上了。
        // 而按新语义"非空 = AI 会去动它"——**不提醒的话，AI 会照那句过期的意图把内容改回去**。
        string staleIntent = _editorIntents.GetValueOrDefault(sel.Id, "").Trim();
        // **文案压到两行内**：状态区就那么大，三四行会顶掉下面的行（实机反馈"遮挡很严重"）。
        // 细节（"前置没抄""意图不同步"）都印在各自那一栏上了，这里只报**这次干了什么**。
        _editorStatus.Text = $"已复制「{NodeLabel(source.Id)}」的信息到「{NodeLabel(sel.Id)}」"
            + (unique != baseName ? $"（正式名重了 → {unique}）" : "")
            + (staleIntent.Length > 0 ? $"　⚠ 旧意图「{staleIntent}」可能已过期，记得改掉或清空" : "");
        EditorRefreshUi();
    }

    /// <summary>进入"选落点"状态：接下来点到的那个空格子，当前选中节点会搬过去。</summary>
    private void EditorBeginMove()
    {
        if (_editorSelected is not { } sel) return;
        if (_editorAwaitingMove) { ClearModes(); _editorStatus.Text = "已取消。"; EditorRefreshUi(); return; }
        ClearModes();
        _editorAwaitingMove = true;
        _editorStatus.Text = $"搬走「{NodeLabel(sel.Id)}」：点一个空格子作为落点。";
        EditorRefreshUi();
    }

    /// <summary>
    /// 把选中节点搬到 (col, row)。**只改位置**：`id`、内容行、前置引用一个字不动——
    /// 所以存档里那个节点的等级**完整保留**（这正是它相对"复制 + 删旧的"的价值）。
    ///
    /// **列号不再约束前置**，所以搬动的唯一条件是"落点是个空格子"（那个判断在 `EditorClickCell` 里做）——
    /// 从前这里要查"新列号 ≥ 所有前置、≤ 所有下游"，那是"只能向右延伸"的连带，那条限制已按用户要求去掉。
    /// </summary>
    private void EditorCompleteMove(int col, int row)
    {
        var sel = _editorSelected!;
        _editorAwaitingMove = false;
        sel.Col = col; sel.Row = row;
        _editorSelected = sel; _editorEmptyCell = null;
        _editorStatus.Text = $"「{NodeLabel(sel.Id)}」已搬到 (col {col}, row {row})——"
            + "**id 与内容没动**，存档里它的等级不受影响。";
        EditorRefreshUi();
    }

    /// <summary>从 <paramref name="from"/> 顺着前置能不能走到 <paramref name="to"/>（用来当场拦成环）。</summary>
    private bool EditorReaches(string from, string to)
    {
        var seen = new HashSet<string>(); var stack = new Stack<string>(); stack.Push(from);
        while (stack.Count > 0)
        {
            string id = stack.Pop();
            if (id == to) return true;
            if (!seen.Add(id)) continue;
            var node = _editorNodes.FirstOrDefault(n => n.Id == id);
            if (node is null) continue;
            foreach (var p in node.Prereqs) stack.Push(p);
        }
        return false;
    }

    /// <summary>从列表里摘掉**一条**前置（列表每行那个 `✕`）。</summary>
    private void EditorRemovePrereq(string id)
    {
        if (_editorSelected is not { } sel) return;
        sel.Prereqs.Remove(id);
        _editorStatus.Text = $"已移除前置「{id}」（还剩 {sel.Prereqs.Count} 条）。";
        EditorRefreshUi();
    }

    private void EditorClearPrereq()
    {
        if (_editorSelected is not { } sel) return;
        sel.Prereqs.Clear();
        _editorStatus.Text = "已清空前置。注意：除根节点外，没有前置的节点会让加载期拒绝。";
        EditorRefreshUi();
    }

    /// <summary>
    /// 把选中节点变成根：**只摘掉它的前置**，然后提醒你旧根现在也得有前置。
    ///
    /// 刻意**不自动**把旧根接到它下面：那一步要过"只能向右"和"不成环"两道判据，
    /// 替你猜位置很容易猜错（新根若在旧根右边就无解）。而且"有两个节点都没前置"是个**一眼能看懂**的
    /// 中间态——选中旧根点「添加前置」，再点一下新根就接上了，比让工具去推断稳。
    /// </summary>
    private void EditorMakeRoot()
    {
        if (_editorSelected is not { } sel) return;
        var oldRoot = _editorNodes.Where(n => n.Prereqs.Count == 0 && !ReferenceEquals(n, sel)).ToList();
        sel.Prereqs.Clear();
        // 后续指引：旧根要认新根当前置——**列号不再有限制**（那条"只能向右延伸"已按用户要求去掉），
        // 而 `sel` 的前置刚刚被清空，所以"旧根 → 新根"这条边**不可能成环**，照着做一定接得上。
        string tail = oldRoot.Count != 1
            ? $"{oldRoot.Count} 个节点都没有前置了，只能留一个。"
            : $"原来那个根「{oldRoot[0].Id}」现在也没有前置了：选中它、点「添加前置」，把「{sel.Id}」接成它的前置。";
        _editorStatus.Text = $"已清空「{sel.Id}」的前置——它现在是根。" + tail;
        EditorRefreshUi();
    }

    private void EditorDeleteNode()
    {
        if (_editorSelected is not { } sel) return;
        // **两步确认**。删一个 id 是不可逆的：存档按 id 记等级，加载时认出未知 id 会拒绝整份存档，
        // 而这些 id 往往是花了好几轮才调出来的。第一次点只是把按钮换成「确认删除」。
        if (_editorPendingDelete != sel.Id)
        {
            _editorPendingDelete = sel.Id;
            _editorStatus.Text = $"再点一次「确认删除」才会删掉「{sel.Id}」。**删 id 等于废档**——"
                + "存档按 id 记等级，保存后旧存档会被拒绝；它的内容行也会在保存时一并移除。"
                + "（还没保存的话，点「重新读取」就能整个撤回。）";
            EditorRefreshUi();
            return;
        }
        _editorPendingDelete = null;
        _editorNodes.Remove(sel);
        // 内存里属于它的那几笔也一并清掉（正式名 / 意图 / 复制内容）。id 已经不会复用了，
        // 这是第二道保险——留着一份指向"已经不存在的节点"的条目，迟早会在别处被读出来。
        _editorNames.Remove(sel.Id);
        _editorIntents.Remove(sel.Id);
        _editorCopied.Remove(sel.Id);
        // 下游引用了它的边要一起摘掉，否则会留下指向不存在节点的悬空前置。
        foreach (var n in _editorNodes)
            for (int i = n.Prereqs.Count - 1; i >= 0; i--)
                if (n.Prereqs[i] == sel.Id) n.Prereqs.RemoveAt(i);
        _editorSelected = null;
        _editorStatus.Text = $"已删除「{sel.Id}」，下游引用它的前置也一并摘掉了。它的内容行会在保存时一并移除。";
        EditorRefreshUi();
    }

    /// <summary>保存前自查。**与加载期同一套规则**——不合格就拒绝保存、不碰任何文件。</summary>
    private string EditorValidate()
    {
        int gridRows = (int)_game.Config.Setting("talent_grid_rows");
        if (_editorNodes.Count == 0) return "一个节点都没有。";
        // **根 = 唯一那个没有前置的节点**，位置不限。放开 `(0, 2)` 之后就不能再靠位置判断谁是根了，
        // 所以报错必须**点名**：只说"有两个没前置的"，你还得自己回图上找是哪两个。
        var roots = _editorNodes.Where(n => n.Prereqs.Count == 0).ToList();
        if (roots.Count == 0)
            return "找不到根节点：必须恰好有一个节点不带前置，它就是整张图的起点。";
        if (roots.Count > 1)
            return $"有 {roots.Count} 个节点都没有前置：{string.Join(" / ", roots.Select(Ref))}"
                + "——只能有一个（它就是根）。给其余的连一条前置，或选中要当起点的那一个点「设为Root节点」。";
        foreach (var n in _editorNodes)
        {
            // 正式名被清空要**当场**拦住：加载期本来就有 `name 不能为空` 的校验，
            // 放它过去就是"在编辑器里保存成功 → 下次启动工程起不来"。
            if (_editorNames.TryGetValue(n.Id, out string? formalName) && formalName.Trim().Length == 0)
                return $"{Ref(n)} 的**正式名**不能为空——玩家会在悬停说明条上看到它。";
            if (n.Row < 0 || n.Row >= gridRows) return $"{Ref(n)} 的行号 {n.Row} 越界（网格是 {gridRows} 行）。";
            // 图标现在也归编辑器写了，所以它也要跟着这条纪律走：**加载期会拒的值不许写出去**。
            // 下拉天然只产出白名单值，这条是给"以后有人往里塞别的东西"留的保险。
            string icon = _editorCopied.TryGetValue(n.Id, out var iconCells) && iconCells.TryGetValue("icon", out string? v)
                ? v : _game.Config.Row("Talent", n.Id).Text("icon");
            if (icon.Length > 0 && !IconChoiceIds().Contains(icon))
                return $"{Ref(n)} 的图标「{icon}」不在白名单里（只能选 {string.Join(" / ", IconChoiceIds())}）——写出去下次启动就加载不了。";
            foreach (var p in n.Prereqs)
            {
                var up = _editorNodes.FirstOrDefault(x => x.Id == p);
                if (up is null) return $"{Ref(n)} 的前置「{p}」不存在。";
            }
        }
        var cells = _editorNodes.Select(n => (n.Col, n.Row)).ToList();
        if (cells.Distinct().Count() != cells.Count) return "有两个节点摆在同一个格子上。";
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        string? cycle = null;
        void Visit(string id)
        {
            if (visited.Contains(id) || cycle is not null) return;
            if (!visiting.Add(id)) { cycle = id; return; }
            foreach (var p in _editorNodes.First(x => x.Id == id).Prereqs) Visit(p);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var n in _editorNodes) Visit(n.Id);
        if (cycle is not null) return $"前置连成了一个环（{Ref(_editorNodes.First(n => n.Id == cycle))}）。";
        return "";
    }

    /// <summary>
    /// 自检用：按 id 选中一个节点（等价于点它一下），用来验证内容面板与意图框的接线。
    /// 顺带**退出连线状态**——不然截图会带着上一次的金框。
    /// </summary>
    public bool TalentEditorSelectForCheck(string id)
    {
        var node = _editorNodes.FirstOrDefault(n => n.Id == id);
        if (node is null) return false;
        _editorSelected = node; _editorEmptyCell = null;
        _editorAwaitingLink = _editorAwaitingCopy = _editorAwaitingMove = false;
        EditorRefreshUi();
        return true;
    }

    /// <summary>自检 / 截图用：进入连线状态（等价于点「添加前置」）。</summary>
    public void TalentEditorBeginLinkForCheck() => EditorBeginLink();

    /// <summary>自检 / 截图用：进入"选复制源"状态（等价于点「复制信息」）。</summary>
    public void TalentEditorBeginCopyForCheck() => EditorBeginCopy();

    /// <summary>自检 / 截图用：进入"选落点"状态（等价于点「移到某格」）。</summary>
    public void TalentEditorBeginMoveForCheck() => EditorBeginMove();

    /// <summary>
    /// 某个 id 在 `Talent.csv` 里**即将写成**的那一行（按列名取值）。
    ///
    /// **必须从"对齐后的结果"里读**，不能从磁盘上的表里读：新建的节点还没有内容行，
    /// 而复制 / 改正式名的效果正是体现在"即将写下去的那一行"上——读磁盘就会让这些操作
    /// 在界面上**毫无反应**（实机反馈就是这么来的）。
    /// </summary>
    private IReadOnlyDictionary<string, string> PendingContentRow(string id)
    {
        string text = TalentEditorAlignedContentForCheck();
        var row = CsvTable.Parse("Talent.csv", text).FirstOrDefault(r => r.Text("id") == id);
        return row is null
            ? new Dictionary<string, string>()
            : text.TrimStart('﻿').TrimEnd('\n').Split('\n')[0].Split(',').ToDictionary(h => h, row.Text);
    }

    /// <summary>自检用：同 <see cref="PendingContentRow"/>（带 `ForCheck` 是为了让人知道这是自检在用的口径）。</summary>
    public IReadOnlyDictionary<string, string> TalentEditorAlignedRowForCheck(string id) => PendingContentRow(id);

    /// <summary>自检用：按 id 走一遍「点两次删除节点」（两步确认各算一次调用）。</summary>
    public bool TalentEditorDeleteForCheck(string id)
    {
        if (!TalentEditorSelectForCheck(id)) return false;
        EditorDeleteNode();   // 第一次：把按钮换成「确认删除」
        EditorDeleteNode();   // 第二次：真删
        return true;
    }

    /// <summary>自检用：丢弃内存里的改动（等价于点「重新读取」）。**不落盘**，所以可以放心在自检里乱删。</summary>
    public void TalentEditorReloadForCheck() => EditorReload();

    /// <summary>自检用：摘掉一条前置（等价于点列表里那一行的 `✕`）。</summary>
    public void TalentEditorRemovePrereqForCheck(string id) => EditorRemovePrereq(id);

    /// <summary>自检 / 截图用：在下拉里选一种货币（等价于用户在下拉里选）。</summary>
    public void TalentEditorSetCurrencyForCheck(string currencyId) => EditorSetCurrency(currencyId);

    /// <summary>自检 / 截图用：选一个效果（等价于在下拉里选）。</summary>
    public void TalentEditorSetEffectForCheck(string effectId) => EditorSetEffect(effectId);

    /// <summary>整块面板在屏幕上**允许占到的底边**（`SectionList` 那套的常量）。</summary>
    public const float EditorPanelBottomLimit = SectionList.BottomLimit;

    // 下面四条自检钩子转发到 `SectionList`——排版机制搬走了，断言的名字与口径**保持不变**，
    // 所以 `Main.cs` 的冒烟一行都不用改（这是这次重构的安全网）。

    /// <summary>自检用：右侧面板有几个分区。</summary>
    public int TalentEditorSectionCountForCheck => _editorPanel.Count;

    /// <summary>自检用：第 index 个分区**标题**当前的 y（验收拢之后下面那块上提了）。</summary>
    public float TalentEditorSectionYForCheck(int index) => _editorPanel.SectionY(index);

    /// <summary>自检用：滚动内容层（分区栈）的总高与视口高度。</summary>
    public (float Content, float Viewport) TalentEditorPanelScrollForCheck => _editorPanel.ScrollSize;

    /// <summary>自检用：面板**脚注**的底边（屏幕坐标）。它钉在视口下方，必须留在屏内。</summary>
    public float TalentEditorFooterBottomForCheck => _editorPanel.FooterBottom;

    /// <summary>自检用：每个分区**真正需要**的高度与**声明的**高度（两边都不含留白）。</summary>
    public IEnumerable<(string Title, float Need, float Declared)> TalentEditorSectionFitForCheck() => _editorPanel.Fit();

    /// <summary>自检用：分区里**行与行压在一起**的地方（空 = 没有）。</summary>
    public IEnumerable<string> TalentEditorRowOverlapsForCheck() => _editorPanel.RowOverlaps();

    /// <summary>自检 / 截图用：收起 / 展开第 index 个分区（等价于点它的标题）。</summary>
    public void TalentEditorToggleSectionForCheck(int index) => _editorPanel.ToggleForCheck(index);

    /// <summary>自检用：往「最大等级」框里打字（同样要显式发信号，见 `TalentEditorSetCostForCheck`）。</summary>
    public void TalentEditorSetMaxLevelForCheck(string text)
    {
        _editorFillingLevels = false;
        _editorMaxLevel.Text = text;
        _editorMaxLevel.EmitSignal(LineEdit.SignalName.TextChanged, text);
    }

    /// <summary>
    /// 自检用：货币那一排里**带 `●` 的是哪一个**（等价于"看面板上标的是哪种货币"）。
    /// 它测的是"状态读数会不会过期"——点完货币按钮、**不换选中项**也得当场跟过去。
    /// </summary>
    public string TalentEditorMarkedCurrencyForCheck()
    {
        int index = (int)_editorCurrency.Selected;
        return index >= 0 && index < _editorCurrencyIds.Count ? _editorCurrencyIds[index] : "";
    }

    /// <summary>
    /// 自检用：往「每级消耗」框里打字。与意图框同样的坑——Godot 给 `Text` 赋值**不发出** `TextChanged`，
    /// 所以显式发一次信号（那测的正是最容易漏的 `TextChanged +=` 那一行）。
    /// </summary>
    public void TalentEditorSetCostForCheck(string text)
    {
        _editorFillingCost = false;
        _editorCost.Text = text;
        _editorCost.EmitSignal(LineEdit.SignalName.TextChanged, text);
    }

    /// <summary>自检用：往「每级效果量」框里打字（同样要显式发信号，理由见 `TalentEditorSetCostForCheck`）。</summary>
    public void TalentEditorSetPerLevelForCheck(string text)
    {
        _editorFillingLevels = false;
        _editorPerLevel.Text = text;
        _editorPerLevel.EmitSignal(LineEdit.SignalName.TextChanged, text);
    }

    /// <summary>自检用：**框里现在显示的是什么**。百分比效果按百分数显示，所以它同时验了回填那一侧的换算。</summary>
    public string TalentEditorPerLevelForCheck() => _editorPerLevel.Text;

    /// <summary>自检用：图标下拉里当前选中的是哪一个（等价于"看面板上标的图标"）。</summary>
    public string TalentEditorIconForCheck()
    {
        int index = (int)_editorIcon.Selected;
        return index >= 0 && index < _editorIconIds.Count ? _editorIconIds[index] : "";
    }

    /// <summary>自检 / 截图用：选一个图标（等价于在下拉里选）。</summary>
    public void TalentEditorSetIconForCheck(string iconId) => EditorSetIcon(iconId);

    /// <summary>
    /// 自检 / 截图用：当前连线状态下**会被描成金框**的候选 id。
    /// 与绘制走同一个 <see cref="CanBePrereq"/>，所以断言它就是在断言画面上看到的金框。
    /// </summary>
    public IReadOnlyList<string> TalentEditorCandidatesForCheck() =>
        _editorAwaitingLink && _editorSelected is { } sel
            ? _editorNodes.Where(n => CanBePrereq(n, sel)).Select(n => n.Id).ToList()
            : [];

    /// <summary>自检用：某个节点的列号（「候选只能在左边或同列」这条断言要用它）。</summary>
    public int TalentEditorColumnForCheck(string id) =>
        _editorNodes.FirstOrDefault(n => n.Id == id)?.Col ?? int.MinValue;

    /// <summary>
    /// 自检用：**算一遍"如果现在保存，内容表会变成什么样"**（不落盘）。
    /// 有了它，"删节点要连带删掉内容表的孤儿行"这条就能在自检里验——
    /// 真去保存会把库里的表改脏，而这条路径恰恰只在"删完再保存"时才走到。
    /// </summary>
    public string TalentEditorAlignedContentForCheck() => BuildAlignedContent(out _, out _);

    /// <summary>
    /// 自检用：整棵树现在的 `(id, 列, 前置条数)`。
    /// smoke 靠它**现取**探针节点，而不是钉死某个 id——修行树是会被反复重搭的，
    /// 钉死 id 的断言会在删节点时变成假警报（2026-10-06 就是这么炸过一次）。
    /// </summary>
    public IReadOnlyList<(string Id, int Col, int Row, int PrereqCount)> TalentEditorNodesForCheck() =>
        _editorNodes.Select(n => (n.Id, n.Col, n.Row, n.Prereqs.Count)).ToList();

    /// <summary>自检用：这个节点在编辑器里**显示的名字**（可读名；没填就是 id）。</summary>
    public string TalentEditorLabelForCheck(string id) => NodeLabel(id);

    /// <summary>
    /// 自检 / 截图用：在一个空格子上新建节点（等价于点格子 → 填可读名 → 点「在此格新建」）。
    /// 返回自动分配到的 id。**不落盘**，用完「重新读取」丢掉即可。
    /// </summary>
    public string? TalentEditorCreateForCheck(int col, int row, string label)
    {
        EditorClickCell(col, row);
        _editorFillingName = false;
        _editorId.Text = label;
        EditorCreateNode();
        return _editorSelected is { } n && n.Col == col && n.Row == row ? n.Id : null;
    }

    /// <summary>自检用：读内存里的意图（面板上看到的就是它）。</summary>
    public string TalentEditorIntentForCheck(string id) => _editorIntents.GetValueOrDefault(id, "");

    /// <summary>
    /// 自检用：往意图框里写字（等价于真人敲键盘），验证 `TextChanged → 内存` 这条接线。
    /// 这是唯一一条冒烟发信号覆盖不到的路径——它由玩家的键盘驱动，不经过任何按钮回调。
    /// </summary>
    public void TalentEditorTypeIntentForCheck(string text)
    {
        _editorFillingIntent = false;   // 确保走的是"人在输入"那条分支
        // 两条路都试过、都不通，记在这儿免得下次再踩：
        // ① 给 `Text` 赋值 —— Godot **不发出** `TextChanged`（冒烟当场抓出来的）；
        // ② `InsertTextAtCaret` —— 无窗口冒烟里控件没有真实焦点，同样发不出来。
        // 于是显式发一次信号：控件状态用赋值摆好，信号手动发。**这测的正是最容易漏的那一行
        // `TextChanged +=`**（漏了的话界面上能打字、内存里什么都没有，且不报任何错）。
        _editorIntent.Text = text;
        _editorIntent.EmitSignal(LineEdit.SignalName.TextChanged, text);
    }

    private void EditorSave()
    {
        string problem = EditorValidate();
        if (problem.Length > 0) { _editorStatus.Text = "保存已取消：" + problem; return; }
        if (_editorPlanCorrupt) { _editorStatus.Text = "保存已取消：TalentPlan.csv 解析失败，不覆盖（怕冲掉已写的意图）。"; return; }
        // 打包版只读这条守卫必须**早于任何写操作**——侧车也一样。
        if (OS.HasFeature("template")) { _editorStatus.Text = "打包版只读，无法保存。请在源码目录里运行。"; return; }
        try
        {
            // 布局表**整份重写**，按 (行, 列) 排序，所以 diff 只反映真正的改动。
            var rows = _editorNodes.OrderBy(n => n.Row).ThenBy(n => n.Col).Select(n => new[]
            {
                n.Id, n.Col.ToString(), n.Row.ToString(), string.Join("|", n.Prereqs), n.Label,
            });
            string content = BuildAlignedContent(out int added, out int removed);
            // 三张表走同一次事务：两边 id 一旦对不上，加载期会**直接拒绝整份配置**，
            // 所以不允许留下"布局写成功了、内容没跟上"这种半成品。
            EditorFiles.WriteAllOrNothing(
                (ProjectSettings.GlobalizePath(PlanPath), BuildPlanText()),
                (ProjectSettings.GlobalizePath(LayoutPath), "id,col,row,prereq,label\n" + CsvTable.Write(rows)),
                (ProjectSettings.GlobalizePath(ContentPath), content));
            // 磁盘已经是它了：清掉"复制 / 改写"的痕迹（那是"还没写下去"的待办，不是长期状态）。
            // 清之前先数一下——**内容改动要在保存提示里报出来**，不然用户在面板上改了价目、
            // 看到"已保存"也拿不准到底写没写进去（实机反馈过）。
            int editedContent = _editorCopied.Count;
            _editorCopied.Clear();
            int pending = _editorNodes.Count(n => _editorIntents.GetValueOrDefault(n.Id, "").Trim().Length == 0);
            _editorStatus.Text = $"已保存：布局 {_editorNodes.Count} 个节点，意图 {_editorNodes.Count - pending} 条已写 / {pending} 条待定"
                + (added > 0 ? $"，并给 Talent.csv 补了 {added} 行骨架（记得去填名字/效果/数值）" : "")
                + (removed > 0 ? $"，并删掉了 {removed} 行已经不在树上的内容（存档按 id 记等级，那些 id 的进度就此作废）" : "")
                + (editedContent > 0 ? $"，改写了 {editedContent} 个节点的内容（名字 / 效果 / 属性 / 价目 / 货币）" : "")
                + "。" + RootTrap();
        }
        catch (Exception ex) { _editorStatus.Text = "保存失败：" + ex.Message; }
    }

    /// <summary>
    /// 侧车整份重写：**以布局表的节点为准**、按 (行, 列) 同序排列，没写意图的自动补空行。
    /// 于是侧车与布局天然不会漂移，不必指望谁记得手动同步——`tests` 里那条 id 集合断言是最后一道兜底。
    /// </summary>
    private string BuildPlanText() => PlanHeader + "\n" + CsvTable.Write(
        _editorNodes.OrderBy(n => n.Row).ThenBy(n => n.Col)
            .Select(n => new[] { n.Id, _editorIntents.GetValueOrDefault(n.Id, "") }));

    /// <summary>
    /// 把 `Talent.csv` **对齐到当前布局**：补上缺的骨架行、**删掉已经没有位置的孤儿行**。
    ///
    /// 两个方向缺一不可——两表 id 只要有一侧对不上，加载期就拒绝整份配置、工程直接起不来。
    /// 这里原先只做了「补」：于是在编辑器里删掉一个节点，布局对不上了，一保存工程就再也打不开，
    /// 而且报错出现在**下一次启动**，看着跟刚才那次删除毫无关系（2026-10-06 就是这么炸的）。
    ///
    /// 既有行的正文一个字都不动（正文归人和 AI），编辑器只决定**整行留还是整行删**。
    /// 表头从文件里现读、按**列名**取值，所以将来加列也不会把值塞错地方。
    /// </summary>
    private string BuildAlignedContent(out int added, out int removed)
    {
        string text = FileAccess.GetFileAsString(ContentPath);
        var rows = CsvTable.Parse("Talent.csv", text);
        string[] header = text.TrimStart('﻿').TrimEnd('\n').Split('\n')[0].Split(',');
        var keep = _editorNodes.Select(n => n.Id).ToHashSet();
        var kept = rows.Where(r => keep.Contains(r.Text("id"))).ToList();
        removed = rows.Count - kept.Count;
        var have = kept.Select(r => r.Text("id")).ToHashSet();
        var missing = _editorNodes.Select(n => n.Id).Where(id => !have.Contains(id)).OrderBy(id => id).ToList();
        added = missing.Count;
        return string.Join(",", header) + "\n"
            + CsvTable.Write(kept.Select(r => header.Select(h => CellValue(r, h)))
                .Concat(missing.Select(id => SkeletonRow(header, id))));
    }

    /// <summary>
    /// 既有行里**这一格**该写什么。默认就是行里原样的值——**编辑器只覆盖它被明确要求写的那两处**：
    /// ① `name`（用户在面板上改过正式名）；② 整行（用户点「复制信息」抄过来的）。
    ///
    /// 其余列一律不碰：否则编辑器开着的时候，AI 在磁盘上改过的行会被它保存时**悄悄改回去**。
    /// </summary>
    private string CellValue(CsvRow r, string column)
    {
        string id = r.Text("id");
        if (column == "id") return id;   // id 永远用行自己的——复制**不抄 id**
        if (_editorCopied.TryGetValue(id, out var copied) && copied.TryGetValue(column, out string? v)) return v;
        if (column == "name" && _editorNames.TryGetValue(id, out string? name)) return name;
        return r.Text(column);
    }

    /// <summary>
    /// 新节点的骨架行：按**列名**填、不按下标——将来表加一列也不会把值塞错位置。
    ///
    /// **若这个节点是"复制"来的，就用复制过来的值覆盖骨架**——这一点当初漏了：
    /// 补骨架与写既有行走的是两条路，只改后者的话，复制到**新建节点**上会**一点效果都没有**
    /// （内容全被骨架顶掉），而复制到已有节点上却是好的，非常难查。
    /// </summary>
    private string[] SkeletonRow(string[] header, string id)
    {
        var values = new Dictionary<string, string>
        {
            ["id"] = id, ["name"] = id, ["max_level"] = "1", ["cost_currency"] = "gold",
            ["cost"] = "0", ["effect"] = "none", ["effect_per_level"] = "0", ["icon"] = "utility",
        };
        if (_editorCopied.TryGetValue(id, out var copied))
            foreach (string h in header)
                if (copied.TryGetValue(h, out string? v)) values[h] = v;
        return header.Select(h => values.GetValueOrDefault(h, "")).ToArray();
    }

    /// <summary>画布上的单行摘要：超出就截断。格宽固定，长句会糊到隔壁格子上。</summary>
    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
