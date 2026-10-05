using Godot;

namespace IdleSword.UI;

/// <summary>共享视觉规范：靛青底、暖金强调、青玉状态色；全部坐标使用 1920×1080 设计空间。</summary>
public static class UiKit
{
    public static readonly Color Ink = new("#101f2d"), Panel = new("#172b3b"), Line = new("#2c4655"), Gold = new("#d9bc82"), Text = new("#e3e5d6"), Muted = new("#96acae"), Jade = new("#8cc4b1");
    public static StyleBoxFlat Box(Color color, int radius = 8, Color? border = null)
    {
        return new StyleBoxFlat { BgColor = color, CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius, BorderColor = border ?? Line,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 8, ContentMarginBottom = 8 };
    }
    public static void Place(Control node, float x, float y, float w, float h) { node.Position = new(x, y); node.Size = new(w, h); }
    public static Label Label(Control parent, string text, float x, float y, float w, float h, int size = 22, Color? color = null)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", size); label.AddThemeColorOverride("font_color", color ?? Text);
        Place(label, x, y, w, h); parent.AddChild(label); return label;
    }
    /// <summary>
    /// 自动换行标签。必须先开启换行再定尺寸：Label 未开换行时最小宽度等于整行文字宽度，
    /// 此时设置 Size 会被夹回整行宽度，导致换行不按给定宽度生效（长句会溢出容器）。
    /// </summary>
    public static Label Wrapped(Control parent, string text, float x, float y, float w, float h, int size = 22, Color? color = null)
    {
        var label = Label(parent, text, x, y, w, h, size, color);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        Place(label, x, y, w, h);
        return label;
    }
    public static Button Button(Control parent, string text, float x, float y, float w, float h, Action action, bool accent = false)
    {
        var button = new Button { Text = text, MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.All };
        button.AddThemeStyleboxOverride("normal", Box(accent ? new Color("#395c59") : Panel, 6, accent ? Jade : Line));
        button.AddThemeStyleboxOverride("hover", Box(new Color("#365061"), 6, Gold));
        button.AddThemeStyleboxOverride("pressed", Box(new Color("#446966"), 6, Gold));
        button.AddThemeStyleboxOverride("focus", Box(new Color(0, 0, 0, 0), 6, Gold));
        button.AddThemeColorOverride("font_color", accent ? Gold : Text); button.AddThemeFontSizeOverride("font_size", 21);
        // 点击音在 action 之前：失败的按钮会连播"点击 + 否决"，符合先点再判的听感。
        Place(button, x, y, w, h); parent.AddChild(button); button.Pressed += () => { Sfx.Click(); action(); }; return button;
    }
    public static Panel PanelAt(Control parent, float x, float y, float w, float h)
    {
        var panel = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", Box(Panel)); Place(panel, x, y, w, h); parent.AddChild(panel); return panel;
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
