using Godot;
using IdleSword.Features;

namespace IdleSword.UI;

/// <summary>
/// 修行星图的几何。抽成一处是因为**星图与下一轮的节点编辑器要共用这套换算**——
/// 两边各写一份的话，改了格子尺寸就会对不上，编辑器画出来的和玩家看到的不是一个东西。
/// </summary>
public static class TalentMap
{
    // 页签挪到左侧竖排之后，功能区从 1728×316 变成 1728×452：纵向多了 136px。
    // 5 行因此能分到 90px 一行，节点从 46px 放大到 **76px（+65%）**——这是这轮排版调整最直接的收益。
    public const float CellWidth = 176f, CellHeight = 90f, NodeSize = 76f;
    /// <summary>5 行 × 90 = 450，居中在 452 高的功能区里，所以纵向留 1。</summary>
    public const float OriginY = 1f;
    /// <summary>贴边时留的余量：节点正好贴住框沿会显得被裁掉了半个。</summary>
    public const float EdgePad = 26f;
    public static Vector2 CellCenter(int col, int row) => new((col + .5f) * CellWidth, OriginY + (row + .5f) * CellHeight);

    /// <summary>
    /// 把平移量夹回合理范围。**横向不再居中，而是左对齐**：所有节点都在根节点的右边，
    /// 居中只会让开局那唯一一个能点的根节点落在屏幕中间、玩家还得先把它拖回来。
    ///
    /// 规则：内容比框窄 → 只能停在最左（根贴左边距）；比框宽 → 在一段区间里随便拖，
    /// 但两端最多各贴住一边，**拖不出框**。纵向在"五行装得下"时直接锁死。
    /// 纯函数，不碰任何节点——自检可以脱离 Godot 直接验它。
    /// </summary>
    public static Vector2 ClampPan(Vector2 pan, Vector2 min, Vector2 max, Vector2 view)
    {
        // min/max 是节点**中心**的包围盒；夹取得按节点的实际外框（±半格）来算，
        // 否则贴边时会有半个节点被框裁掉。
        float half = NodeSize / 2;
        float lo = view.X - max.X - half - EdgePad, hi = half - min.X + EdgePad;   // hi = 最左贴左沿，lo = 最右贴右沿
        if (lo > hi) (lo, hi) = (hi, lo);          // 内容比框窄时两端会反过来，先摆正
        pan.X = Math.Clamp(pan.X, lo, hi);
        if (max.Y - min.Y <= view.Y) pan.Y = 0;
        else
        {
            float top = pan.Y + min.Y - half, bottom = pan.Y + max.Y + half;
            if (top > 0) pan.Y -= top;
            if (bottom < view.Y) pan.Y += view.Y - bottom;
        }
        return pan;
    }

    /// <summary>
    /// 让 <paramref name="min"/>～<paramref name="max"/> 这几个节点全都露出来所需的**最小**平移量。
    ///
    /// **已经在框内的那一侧一点都不动**——这正是要的口径：点一个就在眼前的天赋，画面必须纹丝不动；
    /// 只有新节点真的长到框外（多半是往右）才把画面推过去。两端各留一个 <see cref="EdgePad"/> 的边距。
    /// 纯函数，不碰任何节点。
    /// </summary>
    public static Vector2 RevealPan(Vector2 pan, Vector2 min, Vector2 max, Vector2 view)
    {
        float half = NodeSize / 2 + EdgePad;
        if (pan.X + min.X - half < 0) pan.X = half - min.X;                     // 左边露不出来 → 往右推
        else if (pan.X + max.X + half > view.X) pan.X = view.X - half - max.X;  // 右边露不出来 → 往左推
        if (pan.Y + min.Y - half < 0) pan.Y = half - min.Y;
        else if (pan.Y + max.Y + half > view.Y) pan.Y = view.Y - half - max.Y;
        return pan;
    }
}

/// <summary>
/// 星图的连线层。**必须画在节点之下**（先加进容器），否则线会压在图标上。
/// 一条边归它的**下游**节点所有：两端都可见时才画，且下游点过就算"亮"。
/// 已满级的节点另外套一圈金框——只靠填充色分不出"满级"和"已点亮"。
/// </summary>
public partial class TalentLines : Control
{
    private readonly List<(Vector2 From, Vector2 To, bool Lit)> _edges = [];
    private readonly List<Vector2> _maxed = [];

    public void SetEdges(List<(Vector2 From, Vector2 To, bool Lit)> edges, List<Vector2> maxed)
    {
        _edges.Clear(); _edges.AddRange(edges);
        _maxed.Clear(); _maxed.AddRange(maxed);
        QueueRedraw();
    }

    public override void _Draw()
    {
        foreach (var (from, to, lit) in _edges)
            DrawLine(from, to, lit ? UiKit.Jade.Darkened(.35f) : UiKit.Line, 4);
        foreach (var center in _maxed)
        {
            float half = TalentMap.NodeSize / 2 + 6;
            DrawRect(new Rect2(center - new Vector2(half, half), new Vector2(half * 2, half * 2)), UiKit.Gold, false, 3);
        }
    }
}

/// <summary>
/// 修行星图：图标方块节点 + 三态配色 + 连线 + 悬停说明条 + 点击加点 + 拖拽平移。
///
/// **它是一块常驻 Control，不放进 `_page`。** `ShowPage(tab)` 每次都会清空 `_page` 的子节点
/// （而 `Act()` 每点一下就调它），`Refresh()` 又每 0.15 秒跑一次——放进去的话平移量、悬停状态、
/// 按钮身份会被反复摧毁。所以这里只在 `ShowPage` 里切可见性。
/// </summary>
public partial class Main
{
    private Control _talentRoot = null!, _talentHolder = null!, _talentDragLayer = null!;
    private TalentLines _talentLines = null!;
    private TalentTooltip _talentTip = null!;
    private readonly List<(string Id, Button Node)> _talentNodes = [];
    // `_talentPan` 是当前显示位置，`_talentPanTarget` 是目标位置，每帧朝目标平滑趋近（见 TickTalentPan）。
    // **拖拽时两者一起写** = 绕过平滑，手感才是瞬时跟手的。
    private Vector2 _talentPan, _talentPanTarget, _talentDragFrom;
    private bool _talentDragging;
    /// <summary>解锁可见过的节点 id。用来认出新冒出来的那些——**只有它们**才可能触发画面矫正。</summary>
    private readonly HashSet<string> _talentSeen = [];

    private void BuildTalentMap()
    {
        // `ClipContents` 是这儿的要害：拖到框外的节点与连线必须被裁掉。
        // 不加的话它们会一路飘到左边的页签上面去——玩家反馈的"拖拽会到屏幕外面、和页签重叠"就是这个。
        _talentRoot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        UiKit.Place(_talentRoot, 168, 580, 1728, 452);
        AddChild(_talentRoot);

        // 可操作区的底框：让玩家一眼看出"能操作的范围有多大"，也让裁切看起来是有意的而不是画错了。
        var frame = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        frame.AddThemeStyleboxOverride("panel", UiKit.Box(UiKit.Ink, 10, UiKit.Line));
        UiKit.Place(frame, 0, 0, 1728, 452);
        _talentRoot.AddChild(frame);

        // 拖拽层在**下面**：它只负责"按空白处 = 平移"。节点是真 Button、压在它上面，
        // 所以"按节点 = 加点 / 按空白 = 平移"不需要任何阈值判断。
        _talentDragLayer = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        UiKit.Place(_talentDragLayer, 0, 0, 1728, 452);
        _talentDragLayer.GuiInput += OnTalentDrag;
        _talentRoot.AddChild(_talentDragLayer);

        // 星图容器自己不吃鼠标，但里面的 Button 照常命中。
        _talentHolder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _talentRoot.AddChild(_talentHolder);

        _talentLines = new TalentLines { MouseFilter = Control.MouseFilterEnum.Ignore };
        _talentHolder.AddChild(_talentLines);

        foreach (var row in _game.Config.Rows("TalentLayout"))
        {
            string id = row.Text("id");
            var node = new Button
            {
                // 节点**没有文字**（只有图标），所以冒烟与自检得靠 Name 找它。
                Name = "talent_node_" + id,
                CustomMinimumSize = new(TalentMap.NodeSize, TalentMap.NodeSize),
                Size = new(TalentMap.NodeSize, TalentMap.NodeSize),
                ExpandIcon = true,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                Icon = _battle.Texture("node_" + _game.Config.Row("Talent", id).Text("icon")),
            };
            node.Position = TalentMap.CellCenter(row.Int("col"), row.Int("row")) - new Vector2(TalentMap.NodeSize / 2, TalentMap.NodeSize / 2);
            node.MouseEntered += () => ShowTalentTip(id, node);
            node.MouseExited += () => _talentTip.Dismiss();
            node.Pressed += () => PressTalentNode(id, node);
            _talentHolder.AddChild(node);
            _talentNodes.Add((id, node));
        }

        // 说明条挂在 Main 上（不是容器里），这样它能浮出页带、不被裁掉。
        _talentTip = new TalentTooltip();
        AddChild(_talentTip);

        _talentRoot.Visible = false;
        // 初始位置**左对齐**：根节点贴着左边距。所有节点都在它右边，居中毫无意义，
        // 而且居中会让"开局只有一个根节点能点"时那个节点落在屏幕正中，玩家还得先把它拖回来。
        // 推到最左允许的位置：根节点的左沿离框边正好一个 EdgePad。
        _talentPan = _talentPanTarget = new Vector2(TalentMap.NodeSize / 2 - TalentNodeMin().X + TalentMap.EdgePad, 0);
    }

    /// <summary>按当前存档重建节点的三态、连线与满级金框。每帧由 Refresh 调（它自己已经限流到 0.15s）。</summary>
    private void RefreshTalentMap()
    {
        var edges = new List<(Vector2 From, Vector2 To, bool Lit)>();
        var maxed = new List<Vector2>();
        foreach (var (id, node) in _talentNodes)
        {
            bool visible = _game.TalentVisible(id);
            node.Visible = visible;
            if (!visible) continue;

            var content = _game.Config.Row("Talent", id);
            int level = _game.TalentLevel(id), max = content.Int("max_level");
            bool buyable = _game.CanBuyTalent(id, out _);
            bool done = level >= max;
            // 三态靠**填充 + 描边 + 透明度**三样一起区分，单靠颜色在深底上分不干净。
            var (fill, border) = done ? (UiKit.Gold.Darkened(.62f), UiKit.Gold)
                : buyable ? (UiKit.Panel, UiKit.Jade)
                : (UiKit.Panel, UiKit.Line);
            node.AddThemeStyleboxOverride("normal", UiKit.Box(fill, 6, border));
            node.AddThemeStyleboxOverride("hover", UiKit.Box(fill.Lightened(.12f), 6, UiKit.Gold));
            node.AddThemeStyleboxOverride("pressed", UiKit.Box(fill.Darkened(.15f), 6, UiKit.Gold));
            node.AddThemeStyleboxOverride("focus", UiKit.Box(new Color(0, 0, 0, 0), 6, UiKit.Gold));
            node.Modulate = done || buyable ? Colors.White : new Color(1, 1, 1, .45f);

            var layout = _game.Config.Row("TalentLayout", id);
            var here = TalentMap.CellCenter(layout.Int("col"), layout.Int("row"));
            if (done) maxed.Add(here);
            foreach (var (pid, _) in _game.TalentPrereqs(id))
            {
                if (!_game.TalentVisible(pid)) continue;
                var up = _game.Config.Row("TalentLayout", pid);
                edges.Add((TalentMap.CellCenter(up.Int("col"), up.Int("row")), here, level > 0));
            }
        }
        // 只有**新冒出来**的节点才可能触发矫正：已经看得见的节点不该让画面动
        // （用户口径：在操作区域内就不要刷新节点树位置）。
        var fresh = new List<string>();
        foreach (var (id, _) in _talentNodes)
            if (_game.TalentVisible(id) && _talentSeen.Add(id)) fresh.Add(id);
        if (fresh.Count > 0) RevealTalentNodes(fresh);

        _talentLines.SetEdges(edges, maxed);
        UpdateTalentTransform();
    }

    /// <summary>
    /// 把新解锁的节点带进视野。位移取"刚好够看见"的最小量——**本来就在框内的话一点不动**。
    /// 同时冒出来好几个时按**并集**处理，取把它们全部露出来所需的最小位移。
    /// </summary>
    private void RevealTalentNodes(List<string> fresh)
    {
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var id in fresh)
        {
            var layout = _game.Config.Row("TalentLayout", id);
            var c = TalentMap.CellCenter(layout.Int("col"), layout.Int("row"));
            min = new(Math.Min(min.X, c.X), Math.Min(min.Y, c.Y));
            max = new(Math.Max(max.X, c.X), Math.Max(max.Y, c.Y));
        }
        // 先算"露出来"所需的最小位移，再用夹取兜底——新节点入框后可见范围变大，夹取区间也跟着变了。
        _talentPanTarget = TalentMap.ClampPan(
            TalentMap.RevealPan(_talentPanTarget, min, max, _talentRoot.Size),
            TalentNodeMin(), TalentNodeMax(), _talentRoot.Size);
    }

    /// <summary>
    /// 每帧把平移滑向目标。**必须放在 `_Process` 里**——`Refresh` 只有 6.7Hz，
    /// 在那儿插值会滑得一顿一顿的，比瞬间跳过去还难看。帧率无关的指数趋近，约 0.25 秒到位。
    /// </summary>
    private void TickTalentPan(double delta)
    {
        if (_talentPan.IsEqualApprox(_talentPanTarget)) return;
        _talentPan = _talentPan.Lerp(_talentPanTarget, 1 - Mathf.Exp(-14f * (float)delta));
        // 收尾：指数趋近永远差一点点，不抹平的话每帧都会白跑一次 UpdateTalentTransform。
        if (_talentPan.DistanceTo(_talentPanTarget) < .5f) _talentPan = _talentPanTarget;
        UpdateTalentTransform();
    }

    /// <summary>当前平移目标。公开只读，供冒烟断言"点已在画面内的节点时画面不许动"。</summary>
    public float TalentPanXForCheck => _talentPanTarget.X;

    /// <summary>
    /// 换了一局（**重置进度** / 新建会话）：星图的平移量与"见过"的集合都要归零。
    ///
    /// **不归零的后果是一张空图**：重置后只剩根节点可见，而平移量还停在上一次那棵长树的右边；
    /// 而"点已在画面内的节点时画面不许动"那条口径让星图**不会**因为内容变少自己收回来
    /// （`RevealPan` 只在**新节点冒出来**时才推画面，`_talentSeen` 又记着所有老 id）。
    /// 于是玩家对着一张空白星图，连那个唯一能点的根都找不到。
    /// </summary>
    public void ResetTalentView()
    {
        _talentSeen.Clear();
        _talentPan = _talentPanTarget = new Vector2(TalentMap.NodeSize / 2 - TalentNodeMin().X + TalentMap.EdgePad, 0);
        RefreshTalentMap();
        UpdateTalentTransform();
    }

    private void PressTalentNode(string id, Button node)
    {
        // 不走 Act()——它会 ShowPage(_selectedTab)，而那会把 _page 整个重建一遍，这里并不需要。
        bool ok = _game.BuyTalent(id);
        Sfx.Play?.Invoke(ok ? "sfx_buy" : "sfx_deny");
        RefreshTalentMap();
        _notice.Text = _game.Message;
        if (node.IsHovered()) ShowTalentTip(id, node); else _talentTip.Dismiss();
    }

    private void ShowTalentTip(string id, Button node)
    {
        var r = _game.Config.Row("Talent", id);
        int level = _game.TalentLevel(id), max = r.Int("max_level");
        bool maxed = level >= max;
        bool canBuy = _game.CanBuyTalent(id, out string why);
        string currency = r.Text("cost_currency");
        string effect = maxed
            ? $"已达最高等级 · {TalentText.DescribeEffect(r)}"
            : $"下一级：{TalentText.DescribeEffect(r)}";
        string cost = maxed
            ? ""
            : $"{_game.Config.Row("item", currency).Text("name")} × {_game.TalentCost(id, level + 1):0.#}    持有 {_game.State.Amount(currency):0.#}";
        var center = _talentRoot.Position + _talentHolder.Position + node.Position + new Vector2(TalentMap.NodeSize / 2, 0);
        _talentTip.Show(r.Text("name"), $"等级 {level} / {max}", effect, cost, canBuy || maxed ? "" : why, center, new Vector2(1920, 1080));
    }

    /// <summary>截图用：把某条节点的说明条强制调出来（`--capture` 里悬停是模拟不出来的）。</summary>
    public void TalentShowTipForCapture(string id)
    {
        var node = _talentNodes.FirstOrDefault(n => n.Id == id).Node;
        if (node is not null) ShowTalentTip(id, node);
    }

    /// <summary>截图用：收起说明条。不收起的话它会一直飘在后面几张截图上。</summary>
    public void TalentHideTipForCapture() => _talentTip.Dismiss();

    /// <summary>截图用：把某几个节点伪装成已点亮，好看清三态与连线（自检不写存档，随便改）。</summary>
    public void TalentDemo(params (string Id, int Level)[] levels)
    {
        foreach (var (id, level) in levels) _game.State.Talents[id] = level;
        RefreshTalentMap();
    }

    private void OnTalentDrag(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            _talentDragging = button.Pressed; _talentDragFrom = button.Position; return;
        }
        if (@event is InputEventMouseMotion motion && _talentDragging)
        {
            // 拖拽**绕过平滑**：当前与目标一起写，手感才是瞬时跟手的。
            _talentPan = _talentPanTarget = TalentMap.ClampPan(_talentPan + motion.Position - _talentDragFrom, TalentNodeMin(), TalentNodeMax(), _talentRoot.Size);
            _talentDragFrom = motion.Position;
            UpdateTalentTransform();
        }
    }

    private void UpdateTalentTransform()
    {
        var size = _talentRoot.Size;
        // 基准就是 (0,0)：节点坐标本身就是相对框的，根节点因此天然落在左侧。
        _talentHolder.Position = _talentPan;
        _talentHolder.Size = size;
        _talentLines.Size = size;
    }

    private Vector2 TalentNodeMin() => TalentBounds().Min;
    private Vector2 TalentNodeMax() => TalentBounds().Max;

    /// <summary>
    /// 取**当前可见**节点的包围盒，而不是整张表。50 个节点铺开有十几列、比页带宽一倍多，
    /// 按整张表取中的话，开局那个唯一能点的根节点会被推到屏幕左边外面去。
    /// 按可见范围取中则天然跟着"修行推进到的边界"走。
    /// </summary>
    private (Vector2 Min, Vector2 Max) TalentBounds()
    {
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        bool any = false;
        foreach (var (id, _) in _talentNodes)
        {
            if (!_game.TalentVisible(id)) continue;
            var row = _game.Config.Row("TalentLayout", id);
            var c = TalentMap.CellCenter(row.Int("col"), row.Int("row"));
            min = new(Math.Min(min.X, c.X), Math.Min(min.Y, c.Y));
            max = new(Math.Max(max.X, c.X), Math.Max(max.Y, c.Y));
            any = true;
        }
        return any ? (min, max) : (Vector2.Zero, Vector2.Zero);
    }

}
