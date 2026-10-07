using Godot;

namespace IdleSword.UI;

/// <summary>共享视觉规范：靛青底、暖金强调、青玉状态色；全部坐标使用 1920×1080 设计空间。</summary>
public static class UiKit
{
    public static readonly Color Ink = new("#101f2d"), Panel = new("#172b3b"), Line = new("#2c4655"), Gold = new("#d9bc82"), Text = new("#e3e5d6"), Muted = new("#96acae"), Jade = new("#8cc4b1");
    /// <summary>
    /// 面板/按钮的底。`pad` 是**上下内边距**：它直接决定按钮的最小高度
    /// （`Button` 的最小高 = 字号行高 + 上下内边距），而 `Control.set_size` 会把尺寸夹到不小于最小尺寸——
    /// 想要一个**比默认更矮**的按钮（分区标题那种），只能把内边距压下去，设 `CustomMinimumSize` 是没用的。
    /// </summary>
    public static StyleBoxFlat Box(Color color, int radius = 8, Color? border = null, int pad = 8)
    {
        return new StyleBoxFlat { BgColor = color, CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius, BorderColor = border ?? Line,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = pad, ContentMarginBottom = pad };
    }
    public static void Place(Control node, float x, float y, float w, float h) { node.Position = new(x, y); node.Size = new(w, h); }
    /// <summary>
    /// 给自动换行的说明文字**封顶行数**，多出来的走省略号。
    ///
    /// 为什么必须封：`Label` 装不下就自己长高，而 `Control` **只会长、不会缩**——
    /// 只要有一条长文案经过，这一格就永久变高，下面几块被整体顶出去，而且**再也回不来**。
    /// 截图上是"某次操作之后布局就歪了"，很难倒查。封顶 + 省略号，好过布局随上一条消息的长短乱跳。
    /// </summary>
    public static Label Capped(Label label, int lines)
    {
        label.MaxLinesVisible = lines;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return label;
    }

    /// <summary>
    /// 一行说明：**自动换行 + 封顶行数**，一次性建好。
    ///
    /// ⚠️ 用它，不要写 `Capped(Wrapped(…))`——`Wrapped` 是**先定尺寸再返回**的，那时还没封顶，
    /// 长文案的两行最小高已经把 `Size.Y` 顶到 49；而 `Control` **只长不缩**，封顶之后它不会自己回去
    /// （结果就是这一行比声明的矮不了，压住下一行——自检的重叠断言抓到过两次）。
    /// 所以顺序必须是：**先把换行与封顶都设上，再 `Place`**。
    /// </summary>
    public static Label WrappedCapped(Control parent, string text, float x, float y, float w, float h,
        int lines, int size = 22, Color? color = null)
    {
        var label = new Label
        {
            Text = text, MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MaxLinesVisible = lines, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? Text);
        Place(label, x, y, w, h); parent.AddChild(label);
        return label;
    }
    public static Label Label(Control parent, string text, float x, float y, float w, float h, int size = 22, Color? color = null)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", size); label.AddThemeColorOverride("font_color", color ?? Text);
        Place(label, x, y, w, h); parent.AddChild(label); return label;
    }
    /// <summary>
    /// 自动换行标签。换行必须开在**第一次定尺寸之前**：Label 未开换行时最小宽度等于整行文字宽度，
    /// 而 `Control.set_size` 会把尺寸**夹到不小于最小尺寸**——于是 `Size.X` 被顶成整行宽度、
    /// 换行永远不生效，长句直接画出容器外。
    ///
    /// **不是照抄 `Label()` 再补一句 `AutowrapMode = …`**：那样第一次 `Place` 已经夹过了，
    /// 之后即使重设尺寸，Godot 也要等最小尺寸重算完才认（它内部按当前宽度排版来算最小尺寸），
    /// 中途这一段夹出来的宽就留下了。实测：底栏那句 75 个字被夹成 1249 宽（面板只有 680），
    /// 画面上是"半句话"。所以这里直接建 Label，换行与尺寸一次到位。
    /// </summary>
    public static Label Wrapped(Control parent, string text, float x, float y, float w, float h, int size = 22, Color? color = null)
    {
        var label = new Label
        {
            Text = text, MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        label.AddThemeFontSizeOverride("font_size", size); label.AddThemeColorOverride("font_color", color ?? Text);
        Place(label, x, y, w, h); parent.AddChild(label);
        return label;
    }
    public static Button Button(Control parent, string text, float x, float y, float w, float h, Action action, bool accent = false, int pad = 8)
    {
        var button = new Button { Text = text, MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.All };
        button.AddThemeStyleboxOverride("normal", Box(accent ? new Color("#395c59") : Panel, 6, accent ? Jade : Line, pad));
        button.AddThemeStyleboxOverride("hover", Box(new Color("#365061"), 6, Gold, pad));
        button.AddThemeStyleboxOverride("pressed", Box(new Color("#446966"), 6, Gold, pad));
        button.AddThemeStyleboxOverride("focus", Box(new Color(0, 0, 0, 0), 6, Gold, pad));
        button.AddThemeColorOverride("font_color", accent ? Gold : Text); button.AddThemeFontSizeOverride("font_size", 21);
        // 点击音在 action 之前：失败的按钮会连播"点击 + 否决"，符合先点再判的听感。
        Place(button, x, y, w, h); parent.AddChild(button); button.Pressed += () => { Sfx.Click(); action(); }; return button;
    }
    /// <summary>
    /// 一块底板。`color` 默认是 `Panel`——**绝大多数调用点不用传**，传的只有编辑器画布那一处：
    /// 它底下垫的就是节点方块的填充色（同样是 `Panel`），两者同色时节点只能靠描边才分得出来。
    /// 不给 `Panel` 常量本身换值：它还当着按钮 normal 底与星图节点，一改就是全工程一起变。
    /// </summary>
    public static Panel PanelAt(Control parent, float x, float y, float w, float h, Color? color = null)
    {
        var panel = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", Box(color ?? Panel)); Place(panel, x, y, w, h); parent.AddChild(panel); return panel;
    }
    /// <summary>
    /// 把一整棵子树的鼠标事件放行。悬停说明条**必须**调它——说明条浮在节点上方，
    /// 不穿透的话它会把 `MouseExited` 从节点手里抢走，节点就会一直闪"悬停 / 离开"。
    /// </summary>
    public static void PassThrough(Control root)
    {
        root.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in root.GetChildren())
            if (child is Control control) PassThrough(control);
    }
    public static string Number(double n) => n >= 1e9 ? (n / 1e9).ToString("0.##") + "B" : n >= 1e6 ? (n / 1e6).ToString("0.##") + "M" : n >= 1e4 ? (n / 1e3).ToString("0.#") + "K" : Math.Floor(n).ToString("0");
}
