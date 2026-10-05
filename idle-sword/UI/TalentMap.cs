using Godot;
using IdleSword.Features;

namespace IdleSword.UI;

/// <summary>
/// 修行星图的几何。抽成一处是因为**星图与下一轮的节点编辑器要共用这套换算**——
/// 两边各写一份的话，改了格子尺寸就会对不上，编辑器画出来的和玩家看到的不是一个东西。
/// </summary>
public static class TalentMap
{
    public const float CellWidth = 150f, CellHeight = 60f, NodeSize = 46f;
    /// <summary>5 行 × 60 = 300，居中在 316 高的页带里，所以纵向留 8。</summary>
    public const float OriginY = 8f;
    /// <summary>平移时至少要让这么宽的节点区域留在视野里——否则一拖就把整棵树拖没了。</summary>
    public const float PanKeepX = 200f;

    public static Vector2 CellCenter(int col, int row) => new((col + .5f) * CellWidth, OriginY + (row + .5f) * CellHeight);

    /// <summary>
    /// 把平移量夹回合理范围：横向保证包围盒至少 <see cref="PanKeepX"/> 留在视野内；
    /// 纵向在"整棵树装得下"时直接锁死（装得下就不该能上下拖）。
    /// 纯函数，不碰任何节点——自检可以脱离 Godot 直接验它。
    /// </summary>
    public static Vector2 ClampPan(Vector2 pan, Vector2 min, Vector2 max, Vector2 view)
    {
        float baseX = (view.X - (max.X - min.X)) / 2 - min.X;
        float baseY = (view.Y - (max.Y - min.Y)) / 2 - min.Y;
        float left = baseX + pan.X + min.X, right = baseX + pan.X + max.X;
        if (left > view.X - PanKeepX) pan.X -= left - (view.X - PanKeepX);
        if (right < PanKeepX) pan.X += PanKeepX - right;
        if (max.Y - min.Y <= view.Y) pan.Y = 0;
        else
        {
            float top = baseY + pan.Y + min.Y, bottom = baseY + pan.Y + max.Y;
            if (top > 0) pan.Y -= top;
            if (bottom < view.Y) pan.Y += view.Y - bottom;
        }
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
    private Vector2 _talentPan, _talentDragFrom;
    private bool _talentDragging;

    private void BuildTalentMap()
    {
        _talentRoot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        UiKit.Place(_talentRoot, 24, 704, 1872, 316);
        AddChild(_talentRoot);

        // 拖拽层在**下面**：它只负责"按空白处 = 平移"。节点是真 Button、压在它上面，
        // 所以"按节点 = 加点 / 按空白 = 平移"不需要任何阈值判断。
        _talentDragLayer = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        UiKit.Place(_talentDragLayer, 0, 0, 1872, 316);
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

        var center = UiKit.Button(_talentRoot, "居中", 1720, 6, 130, 38, Recenter);
        center.AddThemeFontSizeOverride("font_size", 18);
        _talentRoot.Visible = false;
        _talentPan = Vector2.Zero;
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
        _talentLines.SetEdges(edges, maxed);
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
            _talentPan = TalentMap.ClampPan(_talentPan + motion.Position - _talentDragFrom, TalentNodeMin(), TalentNodeMax(), _talentRoot.Size);
            _talentDragFrom = motion.Position;
            UpdateTalentTransform();
        }
    }

    private void UpdateTalentTransform()
    {
        var (min, max) = (TalentNodeMin(), TalentNodeMax());
        var size = _talentRoot.Size;
        _talentHolder.Position = new Vector2((size.X - (max.X - min.X)) / 2 - min.X, (size.Y - (max.Y - min.Y)) / 2 - min.Y) + _talentPan;
        _talentHolder.Size = size;
        _talentLines.Size = size;
    }

    /// <summary>按节点的**实际包围盒**取中，而不是整张网格——网格比树大时，居中会居到一片空白上。</summary>
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

    private void Recenter()
    {
        _talentPan = Vector2.Zero;
        UpdateTalentTransform();
    }
}
