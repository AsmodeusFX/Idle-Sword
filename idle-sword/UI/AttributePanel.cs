using Godot;
using IdleSword.Core;
using IdleSword.Features;

namespace IdleSword.UI;

/// <summary>
/// **属性面板**（GM →「编辑器与特殊功能入口 → 特殊功能」）。
///
/// 它是"看角色属性"的那把尺子：改完天赋、换完武器、吃到 buff 之后，"我现在的攻防到底是多少"、
/// "这一下打出去比面板上写的多多少"，在这儿一眼看得见。形态与 `DamagePanel` 同一套
/// （全屏背板 + 居中面板 + 预建行 Label + 0.2 秒刷一次），因为两者是同一类东西。
///
/// **两块内容，口径不同，所以分开列**：
///
/// 1. **`fightattr` 全表**（**由 `Config.Rows("fightattr")` 现取**，加属性自动出现，不会漏）。
///    每行四列：`基础`（配置里的原值）/ `面板值`（各养成系统加成之后的最终值）/
///    `动态值`（**当前真正生效的量**）/ `受什么影响`（一句话说清它为什么是这个数）。
///    - 「动态值」不是"属性本身被改写成什么"，而是**当前真正生效的量**：`atk` 会乘上共享伤害倍率窗、
///      普攻间隔会除以攻速、暴击率会加上 buff 那一份。为什么这么定：buff 大多**不改属性**——
///      它乘的是结算伤害（见 `GameSession.BuffPower` 的说明），而"随时在变"的正是打出去的那个量。
///    - `format` 列**本工程至今没有别的消费者**，而且**没有加载期校验**（合法值只写在文档里），
///      所以这里对未知取值兜底成普通数字，不假设它一定合法。
///
/// 2. **当前生效的临时状态**：护盾 / 回血 / 吸血 / 攻速 / 暴击加成 / 影分身 / 伤害倍率窗 / 暴击缩冷却。
///    它们**全都不是 `fightattr` 属性**（是结算乘区与资源池），混进上面那张表会误导，
///    但不列出来又会漏掉"真正随时在变"的那部分——所以单开一块，带**剩余秒数**。
///    没生效的行显示成暗色 + "—"，**不隐藏**：位置稳定，扫一眼就知道现在有没有。
///
/// 数据源是 `_battle.Session`（**当前正在看的那一场**），与伤害统计同一口径——技能预览模式下也成立。
/// </summary>
public partial class Main
{
    private const double AttributeRefreshSeconds = .25;
    private const float AttrPanelX = 460, AttrPanelY = 100, AttrPanelW = 1000, AttrPanelH = 880;
    private const int AttrColCount = 5, BuffColCount = 3;

    private Control? _attrRoot;
    private Label _attrHint = null!;
    /// <summary>fightattr 那一张表的格子。行数按**表里的实际行数**建（加属性自动跟上）。</summary>
    private Label[] _attrCells = [];
    /// <summary>「当前生效的临时状态」那八行的格子。</summary>
    private readonly Label[] _buffCells = new Label[8 * BuffColCount];
    private double _attrClock;

    /// <summary>四列 + 一列说明的横向位置（相对面板左上角）。表头与数据行共用，才不会错位。</summary>
    private static readonly (string Title, float X, float W)[] AttributeColumns =
    [
        ("属性", 20, 190), ("基础", 210, 110), ("面板值", 330, 130), ("动态值", 470, 140), ("受什么影响", 620, 360),
    ];
    // 临时状态只有三列：**值** + **剩余**。没做"来源"那一列——只有护盾与影分身记住了来源法术，
    // 其余六条都没有，填一半的列比不填更容易误导。
    private static readonly (string Title, float X, float W)[] BuffColumns =
    [
        ("当前生效的临时状态", 20, 240), ("值", 270, 200), ("剩余", 490, 120),
    ];

    private void ToggleAttributes()
    {
        if (_attrRoot is not null && _attrRoot.Visible) CloseAttributes();
        else OpenAttributes();
    }

    private void OpenAttributes()
    {
        if (_attrRoot is null) BuildAttributes();
        RefreshAttributes();
        _attrClock = 0;
        _attrRoot!.Visible = true;
        PlaySfx("sfx_panel");
    }

    private void CloseAttributes()
    {
        if (_attrRoot is null || !_attrRoot.Visible) return;
        _attrRoot.Visible = false;
        PlaySfx("sfx_panel");
    }

    /// <summary>打开期间每 0.25 秒刷一次。挂在 <see cref="Main._Process"/> 里，与 <c>TickDamage</c> 并列。
    /// 为什么要刷：动态列与临时状态那一块**每一帧都在变**，开一次抄一份数字没有意义。</summary>
    private void TickAttributes(double delta)
    {
        if (_attrRoot?.Visible != true) return;
        _attrClock += delta;
        if (_attrClock < AttributeRefreshSeconds) return;
        _attrClock = 0;
        RefreshAttributes();
    }

    private void BuildAttributes()
    {
        _attrRoot = new Control { Visible = false }; UiKit.Place(_attrRoot, 0, 0, 1920, 1080); AddChild(_attrRoot);
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, .62f) }; UiKit.Place(backdrop, 0, 0, 1920, 1080); _attrRoot.AddChild(backdrop);
        UiKit.PanelAt(_attrRoot, AttrPanelX, AttrPanelY, AttrPanelW, AttrPanelH);
        UiKit.Label(_attrRoot, "属性面板", AttrPanelX + 20, AttrPanelY + 16, 300, 46, 30, UiKit.Gold);
        UiKit.Label(_attrRoot, "「动态值」= 当前真正生效的量（含临时状态）；数据源是当前正在看的那一场",
            AttrPanelX + 20, AttrPanelY + 58, 700, 26, 17, UiKit.Muted);
        UiKit.Button(_attrRoot, "关闭", AttrPanelX + 810, AttrPanelY + 20, 110, 40, CloseAttributes);

        // 表一：fightattr 全表。表头与数据行共用同一组列坐标。
        float y = AttrPanelY + 96;
        foreach (var (title, x, w) in AttributeColumns)
            UiKit.Label(_attrRoot, title, AttrPanelX + x, y, w, 30, 20, UiKit.Muted);
        y += 34;
        int rows = _game.Config.Rows("fightattr").Count;
        _attrCells = new Label[rows * AttrColCount];
        for (int i = 0; i < rows; i++)
            for (int c = 0; c < AttrColCount; c++)
            {
                var (_, x, w) = AttributeColumns[c];
                _attrCells[i * AttrColCount + c] = UiKit.Label(_attrRoot, "", AttrPanelX + x, y + i * 26, w, 26, 19,
                    c == 0 ? UiKit.Text : UiKit.Jade);
            }
        y += rows * 26 + 18;

        // 表二：当前生效的临时状态。
        foreach (var (title, x, w) in BuffColumns)
            UiKit.Label(_attrRoot, title, AttrPanelX + x, y, w, 30, 20, UiKit.Muted);
        y += 34;
        for (int i = 0; i < _buffCells.Length / BuffColCount; i++)
            for (int c = 0; c < BuffColCount; c++)
            {
                var (_, x, w) = BuffColumns[c];
                _buffCells[i * BuffColCount + c] = UiKit.Label(_attrRoot, "", AttrPanelX + x, y + i * 26, w, 26, 19,
                    c == 0 ? UiKit.Text : UiKit.Jade);
            }
        _attrHint = UiKit.Wrapped(_attrRoot, "", AttrPanelX + 20, y + _buffCells.Length / BuffColCount * 26 + 10,
            AttrPanelW - 40, 52, 18, UiKit.Muted);
    }

    /// <summary>只回填文字，**不重建控件**（与伤害面板同一条纪律）。</summary>
    private void RefreshAttributes()
    {
        if (_attrRoot is null) return;
        var rows = AttributeRows();
        for (int i = 0; i < _attrCells.Length / AttrColCount; i++)
        {
            int at = i * AttrColCount;
            if (i >= rows.Length)
            {
                for (int c = 0; c < AttrColCount; c++) _attrCells[at + c].Text = "";
                continue;
            }
            var row = rows[i];
            _attrCells[at].Text = row.Name;
            _attrCells[at + 1].Text = Fmt(row.Base, row.Format);
            _attrCells[at + 2].Text = Fmt(row.Panel, row.Format);
            // 动态值和面板值一样时写个"＝"，一眼看出这一项**没被临时状态影响**（大多数行都是这样）。
            _attrCells[at + 3].Text = Math.Abs(row.Current - row.Panel) < 1e-9
                ? "＝" : Fmt(row.Current, row.Format);
            _attrCells[at + 4].Text = row.Note;
        }
        var buffs = BuffRows();
        for (int i = 0; i < _buffCells.Length / BuffColCount; i++)
        {
            int at = i * BuffColCount;
            var buff = buffs[i];
            bool on = buff.Remaining > 0;
            _buffCells[at].Text = buff.Name;
            _buffCells[at + 1].Text = on ? buff.Display : "—";
            _buffCells[at + 2].Text = on ? $"{buff.Remaining:0.0}s" : "";
            for (int c = 0; c < BuffColCount; c++)
                _buffCells[at + c].AddThemeColorOverride("font_color",
                    on ? (c == 0 ? UiKit.Text : UiKit.Jade) : UiKit.Muted);
        }
        _attrHint.Text = "「基础」= fightattr.csv 的原值；「面板值」= 各养成系统加成之后；"
            + "「动态值」= 叠上当前临时状态后真正生效的量（写「＝」表示这一项不受临时状态影响）。"
            + "下面那一块是临时状态本身——它们都不是 fightattr 属性，所以单列。";
    }

    /// <summary>
    /// fightattr 全表的四列值。**纯计算、不碰控件**，所以自检可以直接断言它。
    ///
    /// 「面板值」与「动态值」的分工见类注释：前者是养成后的最终属性，后者是**当前真正生效的量**。
    /// </summary>
    internal (string Id, string Name, string Format, double Base, double Panel, double Current, string Note)[] AttributeRows()
    {
        var s = _battle.Session;
        var list = new List<(string, string, string, double, double, double, string)>();
        foreach (var r in s.Config.Rows("fightattr"))
        {
            string id = r.Text("id"), format = r.Text("format");
            double baseValue = r.Number("base_value");
            double panel = baseValue, current = baseValue;
            string note = "";
            switch (id)
            {
                case "hp":
                    panel = current = s.MaxHp;
                    note = TalentNote(s, "hp", "hp_flat");
                    break;
                case "atk":
                    // 攻击属性**不被临时状态改写**；被改的是结算伤害，所以动态列乘的是那个倍率窗。
                    panel = s.Attack;
                    current = panel * s.BuffPower;
                    note = Join(WeaponNote(s), TalentNote(s, "atk", "atk_flat"));
                    if (s.BuffPower > 1) note = Join(note, $"伤害倍率窗 ×{s.BuffPower:0.##}");
                    break;
                case "atk_percent":
                    panel = current = baseValue + s.TalentBonus("atk");
                    note = TalentNote(s, "atk", "");
                    break;
                case "hp_percent":
                    panel = current = baseValue + s.TalentBonus("hp");
                    note = TalentNote(s, "hp", "");
                    break;
                case "crit":
                    current = baseValue + s.CritBonus;
                    note = s.CritBonus > 0 ? $"临时加成 +{s.CritBonus:P0}" : "";
                    break;
                case "basic_interval":
                    current = baseValue / s.HasteFactor;
                    note = s.HasteFactor > 1 ? $"攻速 ×{s.HasteFactor:0.##}" : "";
                    break;
                case "basic_power":
                    current = baseValue * s.BuffPower;
                    note = s.BuffPower > 1 ? $"伤害倍率窗 ×{s.BuffPower:0.##}" : "";
                    break;
                // 这四个随普攻形态走：当前形态只用到一半，不说明的话看不出"我练的近战，普攻射程怎么显示 950"。
                case "basic_range":
                    note = s.MeleeBasic ? "当前形态：近战（未在用）" : "当前形态：远程（在用）";
                    break;
                case "melee_range":
                    note = s.MeleeBasic ? "当前形态：近战（在用）" : "当前形态：远程（未在用）";
                    break;
                case "stop_range":
                    note = s.MeleeBasic ? "未在用（近战用 melee_stop_range）" : "当前形态：远程（在用）";
                    break;
                case "melee_stop_range":
                    note = s.MeleeBasic ? "当前形态：近战（在用）" : "未在用（远程用 stop_range）";
                    break;
            }
            list.Add((id, r.Text("name"), format, baseValue, panel, current, note));
        }
        return [.. list];
    }

    /// <summary>当前生效的临时状态那八行。**它们都不是 fightattr 属性**——是结算乘区与资源池。</summary>
    internal (string Name, string Display, double Remaining)[] BuffRows()
    {
        var s = _battle.Session;
        return
        [
            ("伤害倍率窗", $"×{s.BuffPower:0.##}", s.BuffRemaining),
            ("护盾", $"{s.ShieldAmount:0.#}", s.ShieldRemaining),
            ("回血", $"{s.RegenRate:P1}/秒", s.RegenRemaining),
            ("吸血", $"{s.LifestealFactor:P0}", s.LifestealRemaining),
            ("攻速", $"×{s.HasteFactor:0.##}", s.HasteRemaining),
            ("暴击加成", $"+{s.CritBonus:P0}", s.CritBonusRemaining),
            ("影分身", $"{s.MirrorRatio:P0}", s.MirrorRemaining),
            ("暴击缩冷却", $"{s.CritReduceSeconds:0.##}s", s.CritBonusRemaining),
        ];
    }

    /// <summary>天赋给这一项贡献了多少（乘区与平值分开说）。两样都没有时返回空串。</summary>
    private static string TalentNote(GameSession s, string percentEffect, string flatEffect)
    {
        string percent = percentEffect.Length > 0 && s.TalentBonus(percentEffect) > 0
            ? $"天赋 +{s.TalentBonus(percentEffect):P0}" : "";
        string flat = flatEffect.Length > 0 && s.TalentBonus(flatEffect) > 0
            ? $"天赋平值 +{s.TalentBonus(flatEffect):0.#}" : "";
        return Join(percent, flat);
    }

    private static string WeaponNote(GameSession s) => s.WeaponAttack > 0 ? $"武器 +{s.WeaponAttack:0.#}" : "";

    private static string Join(string a, string b) =>
        a.Length == 0 ? b : b.Length == 0 ? a : a + "；" + b;

    /// <summary>
    /// 按 `format` 列渲染。**这一列全工程只有这里读**，而且加载期**没有校验**（合法取值只写在
    /// `docs/data/fields.md`）——所以未知取值一律兜底成普通数字，不假设它一定合法。
    /// </summary>
    private static string Fmt(double value, string format) => format switch
    {
        "integer" => value.ToString("0"),
        "percent" => value.ToString("P0"),
        _ => value.ToString("0.##"),
    };

    // ── 自检钩子 ──
    /// <summary>自检用：面板开着没有。</summary>
    internal bool AttributesOpenForCheck() => _attrRoot?.Visible == true;

    /// <summary>自检用：fightattr 那张表**建出来了几行控件**。
    /// ⚠️ 它测的是"建面板那一刻表里有多少行"，与数据行数不同源——所以能抓住
    /// "面板比配置先建好、一行都没建出来"那类错（这类错在本工程已经犯过两次）。</summary>
    internal int AttributeCellRowCountForCheck() => _attrCells.Length / AttrColCount;
}
