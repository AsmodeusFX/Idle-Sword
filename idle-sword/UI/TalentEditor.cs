using Godot;
using IdleSword.Core;
// `System.IO` 里也有个 FileAccess，这里要的是 Godot 那个（走 res:// 抽象层）。
using FileAccess = Godot.FileAccess;

namespace IdleSword.UI;

/// <summary>内存里的一份布局。字段与 `TalentLayout.csv` 的五列一一对应。</summary>
public sealed class TalentLayoutNode
{
    public string Id = "";
    public int Col;
    public int Row;
    /// <summary>前置节点 id，最多 <see cref="GameConfig.MaxTalentPrereqs"/> 个。空 = 它是根。</summary>
    public List<string> Prereqs = [];
    /// <summary>与 <see cref="Prereqs"/> 同下标：true 表示这条前置要求**满级**。</summary>
    public List<bool> PrereqMaxed = [];
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
/// 它**只拥有 `TalentLayout.csv`**——格子坐标与前置连线。节点的名字 / 等级 / 消耗 / 效果在
/// `Talent.csv` 里人工维护：那些数值不是画格子画出来的，让工具去猜只会帮倒忙。
/// 新建的节点会在内容表里补一行骨架（`effect=none`、免费），所以两表始终对得上，
/// 不会出现「摆了个节点、游戏就起不来了」这种半成品状态。
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
    /// <summary>编辑器画布左上角。网格原点落在这儿，所以 col/row 读数与屏幕位置直接对得上。</summary>
    private static readonly Vector2 EditorOrigin = new(30, 150);
    /// <summary>网格最大行列。列不设上限（星图本来就往右长），这里给的是**绘制**范围。</summary>
    private const int EditorCols = 20;

    private readonly List<TalentLayoutNode> _editorNodes = [];
    private Control _editorRoot = null!;
    private TalentEditorCanvas _editorCanvas = null!;
    private LineEdit _editorId = null!;
    private Label _editorStatus = null!, _editorInfo = null!;
    private Button _editorCreate = null!, _editorLink = null!, _editorLink2 = null!, _editorClear = null!, _editorToggle0 = null!, _editorToggle1 = null!, _editorDelete = null!;
    private TalentLayoutNode? _editorSelected;
    private (int Col, int Row)? _editorEmptyCell;
    /// <summary>正在等待点击的那个前置槽位；-1 表示没在连线。</summary>
    private int _editorAwaitingSlot = -1;
    private Vector2 _editorPan, _editorDragFrom;
    private bool _editorDragging, _editorMoved;

    private void BuildTalentEditor()
    {
        _editorRoot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        UiKit.Place(_editorRoot, 0, 0, 1920, 1080);
        AddChild(_editorRoot);

        var backdrop = new ColorRect { Color = new Color(0.02f, 0.04f, 0.06f, .96f), MouseFilter = Control.MouseFilterEnum.Stop };
        UiKit.Place(backdrop, 0, 0, 1920, 1080); _editorRoot.AddChild(backdrop);

        UiKit.Label(_editorRoot, "修行星图 · 节点编辑器", 30, 20, 700, 46, 34, UiKit.Gold);
        UiKit.Label(_editorRoot, "点空格子新建，点节点选中；「设为前置 / 加第二前置」后再点另一个节点即可连线；"
            + "「前置N：需点亮 / 需满级」切换这条边的要求。保存只写布局表，内容请手工维护 Talent.csv。",
            30, 70, 1500, 30, 19, UiKit.Muted);
        UiKit.Button(_editorRoot, "保存", 1500, 24, 120, 44, EditorSave, true);
        UiKit.Button(_editorRoot, "重新读取", 1630, 24, 140, 44, EditorReload);
        UiKit.Button(_editorRoot, "关闭", 1780, 24, 120, 44, ToggleTalentEditor);

        // 画布要裁到自己的矩形：网格比画布大时，多出来的部分会画到画布外面、从右侧面板底下穿过去。
        // 用 `ClipChildren` 而不是 `ClipContents`——格子、连线、id 文字都是这个控件自己 `_Draw`
        // 画的，`ClipContents` 只裁子节点、管不着它们。
        _editorCanvas = new TalentEditorCanvas(this) { Position = EditorOrigin, ClipChildren = ClipChildrenMode.AndDraw };
        UiKit.Place(_editorCanvas, EditorOrigin.X, EditorOrigin.Y, 1150, 880);
        _editorRoot.AddChild(_editorCanvas);

        // ── 右侧面板 ──
        float px = 1210;
        _editorStatus = UiKit.Wrapped(_editorRoot, "", px, 150, 680, 110, 21, UiKit.Muted);
        _editorInfo = UiKit.Wrapped(_editorRoot, "", px, 266, 680, 100, 22, UiKit.Text);
        // 新建区：选中空格子时才出现。
        _editorId = new LineEdit { PlaceholderText = "节点 id" };
        _editorId.AddThemeFontSizeOverride("font_size", 21);
        UiKit.Place(_editorId, px, 378, 300, 46); _editorRoot.AddChild(_editorId);
        _editorCreate = UiKit.Button(_editorRoot, "在此格新建", px + 316, 378, 200, 46, EditorCreateNode);
        // 节点操作：选中已有节点时才出现。
        _editorLink = UiKit.Button(_editorRoot, "设为前置", px, 436, 196, 48, () => EditorBeginLink(0));
        _editorLink2 = UiKit.Button(_editorRoot, "加第二前置", px + 212, 436, 196, 48, () => EditorBeginLink(1));
        _editorClear = UiKit.Button(_editorRoot, "清除前置", px + 424, 436, 196, 48, EditorClearPrereq);
        _editorToggle0 = UiKit.Button(_editorRoot, "前置1：—", px, 496, 196, 48, () => EditorTogglePrereqMax(0));
        _editorToggle1 = UiKit.Button(_editorRoot, "前置2：—", px + 212, 496, 196, 48, () => EditorTogglePrereqMax(1));
        _editorDelete = UiKit.Button(_editorRoot, "删除节点", px + 424, 496, 196, 48, EditorDeleteNode);
        UiKit.Label(_editorRoot, "画布可拖拽平移 · 保存会整份重写 TalentLayout.csv，并给 Talent.csv 补上缺的骨架行",
            30, 1030, 1500, 30, 19, UiKit.Muted);
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
            var node = new TalentLayoutNode { Id = r.Text("id"), Col = r.Int("col"), Row = r.Int("row") };
            var ids = r.TextList("prereq"); var states = r.TextList("prereq_state");
            for (int i = 0; i < ids.Count; i++)
            {
                node.Prereqs.Add(ids[i]);
                node.PrereqMaxed.Add(states.Count > i && states[i] == "max");
            }
            _editorNodes.Add(node);
        }
        _editorSelected = null; _editorEmptyCell = null; _editorAwaitingSlot = -1;
        EditorRefreshUi();
    }

    private void EditorRefreshUi()
    {
        var sel = _editorSelected;
        _editorInfo.Text = sel is null
            ? (_editorEmptyCell is { } cell ? $"空格子 col {cell.Col} · row {cell.Row}" : "点一个格子：空格子新建，已有节点则选中。")
            : $"{sel.Id}\ncol {sel.Col} · row {sel.Row}\n前置：{(sel.Prereqs.Count == 0 ? "（根节点）" : string.Join(" / ", sel.Prereqs.Select((p, i) => p + (sel.PrereqMaxed[i] ? "（需满级）" : ""))))}";
        bool creating = sel is null && _editorEmptyCell is not null;
        _editorId.Visible = _editorCreate.Visible = creating;
        bool acting = sel is not null;
        _editorLink.Visible = _editorLink2.Visible = _editorClear.Visible = _editorToggle0.Visible = _editorToggle1.Visible = _editorDelete.Visible = acting;
        if (sel is not null)
        {
            // 一个节点最多两条前置：已经连满的那一槽把按钮禁掉，免得连出第三条再被校验拦下。
            _editorLink.Disabled = sel.Prereqs.Count > 0;
            _editorLink2.Disabled = sel.Prereqs.Count > 1;
            _editorToggle0.Text = "前置1：" + (sel.Prereqs.Count > 0 ? (sel.PrereqMaxed[0] ? "需满级" : "需点亮") : "—");
            _editorToggle1.Text = "前置2：" + (sel.Prereqs.Count > 1 ? (sel.PrereqMaxed[1] ? "需满级" : "需点亮") : "—");
            _editorToggle0.Disabled = sel.Prereqs.Count < 1;
            _editorToggle1.Disabled = sel.Prereqs.Count < 2;
        }
        _editorCanvas.QueueRedraw();
    }

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
        for (int c = 0; c <= EditorCols; c++)
            canvas.DrawLine(new(c * w + _editorPan.X, 0), new(c * w + _editorPan.X, rows * h), gridLine, 1);
        for (int r = 0; r <= rows; r++)
            canvas.DrawLine(new(0, r * h + _editorPan.Y), new(EditorCols * w, r * h + _editorPan.Y), gridLine, 1);

        foreach (var n in _editorNodes)
            foreach (var p in n.Prereqs)
            {
                var up = _editorNodes.FirstOrDefault(x => x.Id == p);
                if (up is not null) canvas.DrawLine(EditorCenter(up), EditorCenter(n), new Color(UiKit.Jade, .85f), 4);
            }

        foreach (var n in _editorNodes)
        {
            var topLeft = EditorCenter(n) - new Vector2(TalentMap.NodeSize / 2, TalentMap.NodeSize / 2);
            var rect = new Rect2(topLeft, new Vector2(TalentMap.NodeSize, TalentMap.NodeSize));
            canvas.DrawRect(rect, UiKit.Panel, true);
            // 根节点描金：一眼看出"整张图是从这里长出来的"。
            canvas.DrawRect(rect, n.Prereqs.Count == 0 ? UiKit.Gold : UiKit.Line, false, 2);
            if (ReferenceEquals(n, _editorSelected)) canvas.DrawRect(rect.Grow(4), UiKit.Gold, false, 3);
            // id 写在方块**下面**而不是里面：方块只有 46px，长 id 会被截断。
            canvas.DrawString(font, new(topLeft.X, topLeft.Y + TalentMap.NodeSize + 15), n.Id, HorizontalAlignment.Left, -1, 14, UiKit.Text);
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
        if (_editorAwaitingSlot >= 0)
        {
            if (hit is not null) EditorCompleteLink(hit);
            else _editorStatus.Text = "连线要落在另一个**节点**上。";
            return;
        }
        if (hit is null) { _editorSelected = null; _editorEmptyCell = (col, row); _editorId.Text = ""; }
        else { _editorSelected = hit; _editorEmptyCell = null; }
        EditorRefreshUi();
    }

    private void EditorCreateNode()
    {
        if (_editorEmptyCell is not { } cell) return;
        string id = _editorId.Text.Trim();
        if (id.Length == 0) { _editorStatus.Text = "id 不能为空。用 snake_case，与其它表一致（如 t_atk_3）。"; return; }
        if (_editorNodes.Any(n => n.Id == id)) { _editorStatus.Text = $"id「{id}」已经存在。**id 一旦创建就不要再改**——存档按 id 记等级，改名等于废档。"; return; }
        var node = new TalentLayoutNode { Id = id, Col = cell.Col, Row = cell.Row };
        _editorNodes.Add(node);
        _editorSelected = node; _editorEmptyCell = null; _editorId.Text = "";
        _editorStatus.Text = $"已新建「{id}」。**记得给它连一条前置**（除根节点外，每个节点都必须有）。";
        EditorRefreshUi();
    }

    private void EditorBeginLink(int slot)
    {
        if (_editorSelected is null) return;
        if (slot >= _editorSelected.Prereqs.Count) { _editorStatus.Text = $"先连第 {_editorSelected.Prereqs.Count + 1} 条。"; return; }
        _editorAwaitingSlot = slot;
        _editorStatus.Text = $"正在连前置 {slot + 1}：点它的上游节点。";
    }

    private void EditorCompleteLink(TalentLayoutNode upstream)
    {
        var sel = _editorSelected!;
        int slot = _editorAwaitingSlot; _editorAwaitingSlot = -1;
        if (ReferenceEquals(upstream, sel)) { _editorStatus.Text = "不能把自己设为前置。"; return; }
        if (sel.Prereqs.Contains(upstream.Id)) { _editorStatus.Text = "这个前置已经连过了。"; return; }
        // 成环当场拦住——等保存时再报就太晚了，那时图已经摆乱。
        if (EditorReaches(upstream.Id, sel.Id)) { _editorStatus.Text = $"会连成一个环：{upstream.Id} 已经（间接）依赖 {sel.Id}。"; return; }
        if (upstream.Col > sel.Col) { _editorStatus.Text = $"「{upstream.Id}」在更右边——星图只能向右延伸，前置必须在本节点左侧或同列。"; return; }
        if (slot < sel.Prereqs.Count) { sel.Prereqs[slot] = upstream.Id; sel.PrereqMaxed[slot] = false; }
        else { sel.Prereqs.Add(upstream.Id); sel.PrereqMaxed.Add(false); }
        _editorStatus.Text = $"已把「{upstream.Id}」设为「{sel.Id}」的前置 {sel.Prereqs.Count}。";
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

    private void EditorTogglePrereqMax(int slot)
    {
        if (_editorSelected is not { } sel || slot >= sel.Prereqs.Count) return;
        sel.PrereqMaxed[slot] = !sel.PrereqMaxed[slot];
        EditorRefreshUi();
    }

    private void EditorClearPrereq()
    {
        if (_editorSelected is not { } sel) return;
        sel.Prereqs.Clear(); sel.PrereqMaxed.Clear();
        _editorStatus.Text = "已清除前置。注意：除根节点外，没有前置的节点会让加载期拒绝。";
        EditorRefreshUi();
    }

    private void EditorDeleteNode()
    {
        if (_editorSelected is not { } sel) return;
        _editorNodes.Remove(sel);
        // 下游引用了它的边要一起摘掉，否则会留下指向不存在节点的悬空前置。
        foreach (var n in _editorNodes)
            for (int i = n.Prereqs.Count - 1; i >= 0; i--)
                if (n.Prereqs[i] == sel.Id) { n.Prereqs.RemoveAt(i); n.PrereqMaxed.RemoveAt(i); }
        _editorSelected = null;
        _editorStatus.Text = $"已删除「{sel.Id}」，下游引用它的前置也一并摘掉了。";
        EditorRefreshUi();
    }

    /// <summary>保存前自查。**与加载期同一套规则**——不合格就拒绝保存、不碰任何文件。</summary>
    private string EditorValidate()
    {
        int gridRows = (int)_game.Config.Setting("talent_grid_rows");
        if (_editorNodes.Count == 0) return "一个节点都没有。";
        var roots = _editorNodes.Where(n => n.Prereqs.Count == 0).ToList();
        if (roots.Count != 1) return $"必须恰好有一个根节点，现在有 {roots.Count} 个。";
        int rootRow = gridRows / 2;
        if (roots[0].Col != 0 || roots[0].Row != rootRow) return $"根节点必须落在最左一列的正中（col=0, row={rootRow}），现在在 col {roots[0].Col} row {roots[0].Row}。";
        foreach (var n in _editorNodes)
        {
            if (n.Row < 0 || n.Row >= gridRows) return $"「{n.Id}」的行号 {n.Row} 越界（网格是 {gridRows} 行）。";
            if (n.Col < 0) return $"「{n.Id}」的列号不能为负。";
            if (n.Prereqs.Count > GameConfig.MaxTalentPrereqs) return $"「{n.Id}」有 {n.Prereqs.Count} 条前置，最多 {GameConfig.MaxTalentPrereqs} 条。";
            foreach (var p in n.Prereqs)
            {
                var up = _editorNodes.FirstOrDefault(x => x.Id == p);
                if (up is null) return $"「{n.Id}」的前置「{p}」不存在。";
                if (up.Col > n.Col) return $"「{n.Id}」的前置「{p}」在更右边——星图只能向右延伸。";
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
        if (cycle is not null) return $"前置连成了一个环（{cycle}）。";
        return "";
    }

    private void EditorSave()
    {
        string problem = EditorValidate();
        if (problem.Length > 0) { _editorStatus.Text = "保存已取消：" + problem; return; }
        if (OS.HasFeature("template")) { _editorStatus.Text = "打包版只读，无法保存。请在源码目录里运行。"; return; }
        try
        {
            // 布局表**整份重写**，按 (行, 列) 排序，所以 diff 只反映真正的改动。
            var rows = _editorNodes.OrderBy(n => n.Row).ThenBy(n => n.Col).Select(n => new[]
            {
                n.Id, n.Col.ToString(), n.Row.ToString(), string.Join("|", n.Prereqs),
                // 整列全 active 时就留空——写满只会让表变吵，还会把真正要求满级的那几行淹没掉。
                n.PrereqMaxed.Any(m => m) ? string.Join("|", n.PrereqMaxed.Select(m => m ? "max" : "active")) : "",
            });
            WriteAtomic(ProjectSettings.GlobalizePath(LayoutPath), "id,col,row,prereq,prereq_state\n" + CsvTable.Write(rows));
            int added = AppendMissingContent(ProjectSettings.GlobalizePath(ContentPath));
            _editorStatus.Text = $"已保存：布局 {_editorNodes.Count} 个节点"
                + (added > 0 ? $"，并给 Talent.csv 补了 {added} 行骨架（记得去填名字/效果/数值）" : "") + "。";
        }
        catch (Exception ex) { _editorStatus.Text = "保存失败：" + ex.Message; }
    }

    /// <summary>
    /// 给 `Talent.csv` 补上还没有内容行的节点，补成一行骨架。
    /// **这一步是必须的**：两表 id 对不上会让游戏直接起不来，补一行合法骨架就能避免
    /// 「摆了个节点、还没填数值、结果整个工程加载失败」这种半成品状态。
    /// </summary>
    private int AppendMissingContent(string path)
    {
        string text = FileAccess.GetFileAsString(ContentPath);
        var rows = CsvTable.Parse("Talent.csv", text);
        var have = rows.Select(r => r.Text("id")).ToHashSet();
        var missing = _editorNodes.Select(n => n.Id).Where(id => !have.Contains(id)).OrderBy(id => id).ToList();
        if (missing.Count == 0) return 0;
        var all = text.TrimEnd('\n') + "\n" + CsvTable.Write(missing.Select(id => new[]
        {
            id, id, "1", "gold", "0", "none", "0", "utility",
        }));
        WriteAtomic(path, all);
        return missing.Count;
    }

    /// <summary>先写 `.tmp` 再原子替换，最后留一份 `.bak`——中途失败不会把源表写坏。</summary>
    private static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
