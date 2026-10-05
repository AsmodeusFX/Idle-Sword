using Godot;

namespace IdleSword.UI;

/// <summary>
/// 节点悬停说明条。用**自绘面板**而不是 Godot 的 `TooltipText`——后者放不下多行、也没法按状态变色，
/// 而这里要同时说清「这是什么 / 现在几级 / 下一级给什么 / 要多少钱 / 为什么买不了」。
///
/// 定位由宿主（<see cref="Main"/>）负责：默认贴在节点上方，顶到边就翻到下方，再夹一下横向。
/// </summary>
public partial class TalentTooltip : Control
{
    private const float Width = 380f, Pad = 14f;

    private readonly Panel _panel;
    private readonly Label _title, _level, _effect, _cost, _why;
    private float _shown;

    public TalentTooltip()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        _panel = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        _panel.AddThemeStyleboxOverride("panel", UiKit.Box(UiKit.Ink, 6, UiKit.Gold));
        AddChild(_panel);
        _title = Row(0, 28, UiKit.Text);
        _level = Row(34, 22, UiKit.Muted);
        _effect = Row(62, 22, UiKit.Jade);
        _cost = Row(90, 22, UiKit.Gold);
        _why = Row(118, 20, UiKit.Muted);
        Visible = false;
        // 说明条浮在节点上方，必须整棵子树放行鼠标：不穿透的话它会把 MouseExited 从节点手里抢走，
        // 节点就会一直闪"悬停 / 离开"。
        UiKit.PassThrough(this);
    }

    private Label Row(float y, int size, Color color)
    {
        var label = new Label { MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        UiKit.Place(label, Pad, Pad + y, Width - Pad * 2, size + 8);
        AddChild(label);
        return label;
    }

    public float Height => _shown;

    /// <summary>显示内容。`why` 为空表示现在买得起（或已满级），那一行就不占位置。</summary>
    public void Show(string title, string level, string effect, string cost, string why, Vector2 anchor, Vector2 bounds)
    {
        _title.Text = title;
        _level.Text = level;
        _effect.Text = effect;
        _cost.Text = cost;
        _why.Text = why;
        _why.Visible = why.Length > 0;

        _shown = Pad * 2 + (_why.Visible ? 118 + 28 : 90 + 30);
        _panel.Position = Vector2.Zero;
        _panel.Size = new(Width, _shown);
        Size = new(Width, _shown);

        // 默认贴节点上方；上方放不下就翻到下方——说明条被顶出可视区等于没有。
        float x = Math.Clamp(anchor.X - Width / 2, 0, Math.Max(0, bounds.X - Width));
        float y = anchor.Y - _shown - 10;
        if (y < 0) y = anchor.Y + TalentMap.NodeSize + 10;
        Position = new(x, y);
        Visible = true;
    }

    /// <summary>
    /// 收起来。**不能叫 `Hide()`**——那会盖住 Godot `CanvasItem.Hide()`，引擎侧调用谁就说不准了
    /// （打包脚本把警告当错误，这条也是它先报出来的）。
    /// </summary>
    public void Dismiss() => Visible = false;
}
