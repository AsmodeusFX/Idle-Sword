using Godot;

namespace IdleSword.UI;

/// <summary>一行控件在什么时候该出现。行的 `Visible` **只由布局过程写**，它按这个判断。</summary>
public enum RowWhen
{
    /// <summary>选中了**主体**（节点编辑器里是节点）——只对主体有意义的那几行，默认走它。</summary>
    Acting,
    /// <summary>选中了主体**或**空格子：可读名那一行两种情形下都要在（新建时它是"起个名字"）。</summary>
    Any,
    /// <summary>只在"选中空格子、准备新建"时出现（「在此格新建」那个按钮）。</summary>
    Creating,
    /// <summary>与选中无关，常显（底栏说明）。</summary>
    Always,
    /// <summary>**有字才出现**：没有消息时整块收起来，别在顶上留一个空盘子。</summary>
    Filled,
}

/// <summary>
/// 供 <see cref="RowWhen"/> 判断的两个状态位。**显式入参而不是读使用方的字段**——
/// 这样 `RowVisible` 是纯函数，同一套排版机制能给多个编辑器用（各自有各自的"主体"是什么）。
/// </summary>
public readonly record struct SectionState(bool Acting, bool Creating);

/// <summary>
/// 右侧那一条**可收拢的分区面板**。从节点编辑器里抬出来的——两个编辑器共用同一套排版，
/// 免得第二份抄出分叉（这一套里踩过的坑不少，见下面各处的注释）。
///
/// 使用方式：
/// <code>
/// var sections = new SectionList(_root, x: 1202, top: 150, width: 696, "脚注那句话");
/// var s = sections.Add("基础信息", 120);
/// UiKit.Place(someInput, 8, 0, 300, 38); sections.Inner.AddChild(someInput);
/// SectionList.Row(s, someInput, dy: 0);
/// …每次刷新时：sections.Layout(new SectionState(Acting: true, Creating: false));
/// </code>
///
/// 契约（这几条都是踩出来的，别改）：
/// - **`Layout` 是唯一一处写行控件 y 与 `Visible` 的地方**。建的时候 y 一律给 0。
/// - 分区声明的高度只是**下限**，真正说了算的是"行里最靠下的那条底边"（控件的实际高度由 Godot 夹出来）。
/// - **没有可见行的分区不占高度**（否则一排空壳子看起来像界面没加载完）——
///   留白必须加在这条早退**之后**。
/// - 装不下时**横向不出滚动条**、纵向出；脚注钉在视口下方，不随内容滚走。
/// </summary>
public sealed class SectionList
{
    /// <summary>整块面板在屏幕上**允许占到的底边**。屏高 1080 留 16 的余量——脚注钉在这里。
    /// 分区那一段由滚动视口兜着，不受这条约束。</summary>
    public const float BottomLimit = 1080f - 16f;

    /// <summary>分区标题按钮**请求**的高。真实高由 Godot 说了算（字号行高 + 内边距 + 边框），
    /// 这个值只当"低于它就不给"的下限——布局一律读 `Header.Size.Y`，不读常量。</summary>
    private const float HeaderHeight = 30f;
    /// <summary>分区间距。</summary>
    private const float Gap = 5f;
    /// <summary>分区**内容**的上下留白（内容不顶着分区边框）。见类注释里那条"留白加在早退之后"。</summary>
    private const float Pad = 8f;
    /// <summary>分区底板的左右外扩：底板比行宽出这么多，看起来才像"把这一块框住了"。</summary>
    private const float PadX = 8f;
    /// <summary>滚动条的宽。视口要比分区内容宽出这一条——滚动条画在视口**里面**。</summary>
    private const float ScrollbarWidth = 14f;
    /// <summary>脚注的高：它占的是**一行**（`UiKit.Capped(..., 1)`）。</summary>
    private const float FooterHeight = 28f;

    /// <summary>
    /// 面板里的一个分区：可点的标题 + 一块底板 + 若干行控件（各带**相对分区顶**的偏移）+ 展开时的高度。
    /// 收起时高度不占，下面几块自动上提——这是面板"装得下"的关键（内容比一屏多）。
    /// </summary>
    public sealed class Section
    {
        public string Title = "";
        public Button Header = null!;
        public Panel Back = null!;
        public bool Collapsed;
        public float Height;
        public readonly List<(Control Control, float Dy, RowWhen When)> Rows = [];
    }

    private readonly List<Section> _sections = [];
    private readonly ScrollContainer _viewport;
    private readonly Label _footer;
    private readonly float _width;
    private SectionState _state;
    private float _innerHeight, _footerBottom;

    /// <summary>分区与行要挂在这一层上（滚动视口里的内容层，坐标以它左上角为原点）。</summary>
    public Control Inner { get; }

    /// <param name="host">挂在谁身上（编辑器那个全屏根 Control）。</param>
    /// <param name="x">面板在**屏幕**坐标里的左沿。</param>
    /// <param name="top">面板顶（第一个分区标题的 y）。</param>
    /// <param name="width">分区内容的宽。底板的宽 = 它，行的宽 = 它 − `2 × PadX`。</param>
    /// <param name="footerText">钉在面板底部的脚注（一行）。面板自己的说明写这儿，别挂进分区。</param>
    public SectionList(Control host, float x, float top, float width, string footerText)
    {
        _width = width;
        _viewport = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        // 视口比分区内容**宽出一条滚动条**：滚动条是画在视口里面的，不留出来的话它会把分区底板的右沿切掉。
        // 宽度用 `CustomMinimumSize` 钉死，不靠主题默认值——否则"切掉多少"就成了环境相关的量。
        UiKit.Place(_viewport, x, top, width + ScrollbarWidth, BottomLimit - FooterHeight - top - Gap);
        _viewport.GetVScrollBar().CustomMinimumSize = new Vector2(ScrollbarWidth, 0);
        host.AddChild(_viewport);
        // 内容层要 `Ignore`：裸 `Control` 的默认 `MouseFilter` 是 `Stop`，它铺满视口、也压住滚动条，
        // **按住滚动条拖是拖不动的**（滚轮不受影响——`MouseForcePassScrollEvents` 默认为真，
        // 滚轮本来就会从子控件冒泡到容器）。分区底板与标签本来就已经是 `Ignore`，
        // 所以这一改之后"点在没有控件的地方"就直接落到 `ScrollContainer`，它内建的处理生效。
        Inner = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        _viewport.AddChild(Inner);

        // 脚注建在**视口外面**：它是整块面板的说明，滚走就没人看得到。
        _footer = UiKit.WrappedCapped(host, footerText, x + PadX, 0, width - 2 * PadX, FooterHeight, 1, 17, UiKit.Muted);
    }

    /// <summary>建一个分区：一块底板 + 一个可点的标题。返回它，随后用 <see cref="Row"/> 把控件挂进去。</summary>
    public Section Add(string title, float height)
    {
        var back = UiKit.PanelAt(Inner, 0, 0, _width, height + HeaderHeight);
        var header = UiKit.Button(Inner, title, PadX, 0, _width - 2 * PadX, HeaderHeight, null!, pad: 0);
        // 标题按钮的**最小高度** = 字号行高 + 上下内边距 + 边框，而 `Control.set_size` 会把尺寸夹到不小于最小尺寸——
        // 默认那套（21 号字 + 8 内边距）量出来是 **45**，不是常量里的 30：六个分区光标题就吃掉 270px，
        // 面板被顶到屏幕外。所以要真矮，字号与内边距得一起压，光设 `CustomMinimumSize` 没用（那是**下限**）。
        header.AddThemeFontSizeOverride("font_size", 18);
        header.Alignment = HorizontalAlignment.Left;
        var section = new Section { Title = title, Header = header, Back = back, Height = height };
        header.Pressed += () => Toggle(section);
        _sections.Add(section);
        return section;
    }

    /// <summary>把一个控件挂成某个分区的**一行**。<paramref name="dy"/> 是它相对分区顶的偏移。</summary>
    public static void Row(Section section, Control control, float dy, RowWhen when = RowWhen.Acting) =>
        section.Rows.Add((control, dy, when));

    /// <summary>点分区标题：收起 / 展开，然后重排（下面的分区跟着上提或让位）。</summary>
    private void Toggle(Section section)
    {
        section.Collapsed = !section.Collapsed;
        Layout();
    }

    /// <summary>重排。**唯一一处写行控件 y 的地方**（建的时候一律给 0）——两处都写迟早会分叉。
    /// 同理，行控件的 `Visible` 也只在这里写。</summary>
    public void Layout(SectionState? state = null)
    {
        if (state is { } s) _state = s;
        // y 从 **0** 起：分区挂在滚动视口的内容层上，坐标相对面板左上角。
        float y = 0;
        foreach (var section in _sections)
        {
            // 标记跟着"看得见内容没有"走：块是空的时候画 ▾ 会让人以为里面藏着东西。
            bool empty = HeightOf(section) <= 0;
            section.Header.Text = (section.Collapsed || empty ? "▸ " : "▾ ") + section.Title;
            section.Header.Position = new Vector2(section.Header.Position.X, y);
            float height = section.Collapsed ? 0 : HeightOf(section);
            // 标题与行都按**标题按钮自己的**高度排，不用常量：按钮会被 Godot 夹到最小尺寸，
            // 拿常量累加会让标题压住自己的底板。
            float header = section.Header.Size.Y;
            section.Back.Position = new Vector2(section.Back.Position.X, y);
            section.Back.Size = new Vector2(section.Back.Size.X, height + header);
            foreach (var (control, dy, when) in section.Rows)
            {
                // 行整体下移 `Pad`：内容不顶着分区边框（留白含在上面的 `height` 里）。
                control.Position = new Vector2(control.Position.X, y + header + Pad + dy);
                control.Visible = !section.Collapsed && Visible(when, control);
            }
            y += header + height + Gap;
        }
        // 内容层的高度就是分区栈总高（末尾那个间距不计）——滚动条按它决定要不要出现、能滚多远。
        //
        // ⚠️ **必须写 `CustomMinimumSize`，只写 `Size` 是不算数的**：`ScrollContainer` 的滚动范围取的是
        // 直接子节点的**最小尺寸**（不是它的 `Size`）。`Inner` 是个裸 `Control`，最小尺寸恒为 (0,0)——
        // 于是可滚范围是零，**滚轮与拖动都完全没反应**（滚动条画得出来，因为它按别的判据显示）。
        // 这就是右侧面板"看得到下面的字、但滚不动"的根因。
        // 反证：节点编辑器「前置」那个列表能滚，因为它的内容层是 `VBoxContainer`——容器的最小尺寸由子项累加。
        _innerHeight = Math.Max(0, y - Gap);
        Inner.CustomMinimumSize = new Vector2(0, _innerHeight);
        Inner.Size = new Vector2(Inner.Size.X, _innerHeight);
        // 脚注钉在视口**下方**。
        float footerY = BottomLimit - FooterHeight;
        _footer.Position = new Vector2(_footer.Position.X, footerY);
        _footerBottom = footerY + _footer.Size.Y;
    }

    /// <summary>
    /// 分区**实际**占多高：声明高度只是**下限**，真正说了算的是"行里最靠下的那条底边"，再加**上下留白**。
    ///
    /// 为什么不能直接用声明值：行的控件高度是 Godot 说了算的（`Control.set_size` 会把尺寸夹到不小于最小尺寸，
    /// 按钮在 21 号字体下就是夹到 45），而且自动换行的说明文字会随文案长短长高——声明值一旦小于实际，
    /// 行就撑出底板之外，**下一个分区的底板再盖上来**，画面上是半行字。
    ///
    /// ⚠️ **留白加在"空块返回 0"那条早退之后**：先加留白再判空的话，空壳子也会占高度，
    /// "没有内容的块不占地方"（`Layout` 里那个 `empty` 判断）会一起坏掉。
    /// </summary>
    private float HeightOf(Section section)
    {
        float height = 0;
        foreach (var (control, dy, when) in section.Rows)
            if (Visible(when, control)) height = Math.Max(height, dy + control.Size.Y);
        return height <= 0 ? 0 : Math.Max(height, section.Height) + 2 * Pad;
    }

    /// <summary>一行在什么时候该出现。`Visible` 与"算不算进分区高度"都读它——**只有这一处判断**。</summary>
    private bool Visible(RowWhen when, Control control) => when switch
    {
        RowWhen.Always => true,
        RowWhen.Filled => control is Label { Text.Length: > 0 },
        RowWhen.Creating => _state.Creating,
        RowWhen.Any => _state.Acting || _state.Creating,
        _ => _state.Acting,
    };

    // ── 自检钩子：四条布局断言共用，两个编辑器都吃同一套 ──

    /// <summary>自检用：有几个分区。</summary>
    public int Count => _sections.Count;

    /// <summary>自检用：第 index 个分区**标题**当前的 y（验收拢之后下面那块上提了）。</summary>
    public float SectionY(int index) => _sections[index].Header.Position.Y;

    /// <summary>自检用：滚动内容层（分区栈）的总高与视口高度。</summary>
    public (float Content, float Viewport) ScrollSize => (_innerHeight, _viewport.Size.Y);

    /// <summary>自检用：脚注的底边（屏幕坐标）。它钉在视口下方，必须留在屏内。</summary>
    public float FooterBottom => _footerBottom;

    /// <summary>自检用：纵向滚动条。**"可滚范围 &gt; 视口"** 就是"这一屏真的能滚"的判据——
    /// 从前面板看得到下面的字却滚不动，根因正是内容层的**最小尺寸**恒为 0，于是可滚范围是零。</summary>
    public VScrollBar ScrollBar => _viewport.GetVScrollBar();

    /// <summary>自检用：内容层的最小尺寸。`ScrollContainer` 就是**按它**（而不是按 `Size`）算可滚范围的。</summary>
    public Vector2 InnerMinimumSize => Inner.GetCombinedMinimumSize();

    /// <summary>自检用：内容层会不会把滚动条按住不放（`Stop` 就会——拖不动滚动条）。</summary>
    public bool InnerLetsInputThrough => Inner.MouseFilter == Control.MouseFilterEnum.Ignore;

    /// <summary>自检用：每个分区**真正需要**的高度（= 各行里最靠下的那条底边，**不含留白**）与**声明的**高度。
    /// 声明值只是下限（`HeightOf` 会兜住），但**两者必须一致**：声明值偏小说明数字已经没人维护了，
    /// 面板总高（现在是滚动内容高）就成了算命。两边都**不含留白**，是同类比同类。</summary>
    public IEnumerable<(string Title, float Need, float Declared)> Fit()
    {
        foreach (var s in _sections)
        {
            var visible = s.Rows.Where(r => Visible(r.When, r.Control)).ToList();
            yield return (s.Title, visible.Count == 0 ? 0 : visible.Max(r => r.Dy + r.Control.Size.Y), s.Height);
        }
    }

    /// <summary>自检用：分区里**行与行压在一起**的地方（空 = 没有）。
    /// 同一个 dy 上的行是并排的（一行里放"标签 + 输入框"），不算压；不同 dy 的行如果在竖向上交叠、
    /// 横向也占同一段，就是真的叠字——根因是行距比控件的**实际**高度小（控件的高度由 Godot 夹出来，
    /// 不是建的时候写的那个数）。</summary>
    public IEnumerable<string> RowOverlaps()
    {
        foreach (var s in _sections.Where(s => !s.Collapsed))
        {
            // 行矩形：y 用行自己的 dy（**不是**控件当前位置——位置是相对分区顶的，比不了），x/宽用控件的。
            var rows = s.Rows.Where(r => Visible(r.When, r.Control))
                .Select(r => new Rect2(r.Control.Position.X, r.Dy, r.Control.Size.X, r.Control.Size.Y))
                .OrderBy(r => r.Position.Y).ToList();
            for (int i = 1; i < rows.Count; i++)
            {
                var above = rows[i - 1];
                var below = rows[i];
                if (below.Position.Y < above.End.Y - .5f
                    && below.Position.X < above.End.X - .5f && above.Position.X < below.End.X - .5f)
                    yield return $"{s.Title}：x {above.Position.X:F0}~{above.End.X:F0} / y {above.Position.Y:F0}~{above.End.Y:F0}"
                        + $" 压住了 x {below.Position.X:F0}~{below.End.X:F0} / y {below.Position.Y:F0}~{below.End.Y:F0}"
                        + "（同一行里横向撞了，或者行距比控件实际高度小）";
            }
        }
    }

    /// <summary>自检 / 截图用：收起 / 展开第 index 个分区（等价于点它的标题）。</summary>
    public void ToggleForCheck(int index)
    {
        _sections[index].Collapsed = !_sections[index].Collapsed;
        Layout();
    }
}
