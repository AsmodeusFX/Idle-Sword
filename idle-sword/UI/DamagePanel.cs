using Godot;
using IdleSword.Core;

namespace IdleSword.UI;

/// <summary>
/// 伤害统计面板：按技能列出**有效伤害 / 占比 / 溢出 / 每秒 / 命中**，用来在游戏里核对数值平衡。
///
/// 口径见 `DamageTally`：有效 = 实际打掉的血、溢出 = 名义伤害里超出剩余血量的部分；
/// **每秒 = 累计 ÷ 统计时长**（一个统一分母，所以各技能可以直接横比）。
/// 只列有伤害的来源——buff 与纯功能类技能自然不出现，正是"只看伤害"。
///
/// 数据源取 `_battle.Session`（当前正在看的那一场）而不是 `_game`：技能预览模式下 `_battle`
/// 绑的是预览会话，于是在对照台里选中某个技能就能当场读出它自己的每秒伤害。
/// </summary>
public partial class Main
{
    // 表最多列这么多行（15 法术 + 普攻 + 宠物技能 + 余量，够用）。数据超过了就截断——这是调试工具，不做分页。
    private const int DamageRowCount = 24;
    private const double DamageRefreshSeconds = .2;
    // 行高 26：24 行正好把面板撑到 880 高，塞得进 1080 的设计空间。
    private const float DamageRowHeight = 26;
    private const float DamagePanelX = 460, DamagePanelY = 100, DamagePanelW = 1000, DamagePanelH = 880;

    private Control? _damageRoot;
    private Label _damageSeconds = null!, _damageHint = null!;
    private readonly Label[] _damageCells = new Label[DamageRowCount * 6];
    private readonly Label[] _damageTotalCells = new Label[6];
    private double _damageClock;

    // 六列的横向位置与宽度（相对面板左上角）。表头、数据行、合计行共用，才不会错位。
    private static readonly (string Title, float X, float W)[] DamageColumns =
    [
        ("技能", 20, 240), ("有效伤害", 270, 170), ("占比", 450, 90),
        ("溢出", 550, 160), ("每秒", 720, 150), ("命中", 880, 80),
    ];

    private void ToggleDamage()
    {
        if (_damageRoot is not null && _damageRoot.Visible) CloseDamage();
        else OpenDamage();
    }

    private void OpenDamage()
    {
        if (_damageRoot is null) BuildDamage();
        RefreshDamage();
        _damageClock = 0;
        _damageRoot!.Visible = true;
        PlaySfx("sfx_panel");
    }

    private void CloseDamage()
    {
        if (_damageRoot is null || !_damageRoot.Visible) return;
        _damageRoot.Visible = false;
        PlaySfx("sfx_panel");
    }

    /// <summary>打开期间每 0.2 秒刷一次。挂在 <see cref="Main._Process"/> 里，与 <c>TickSettings</c> 并列。</summary>
    private void TickDamage(double delta)
    {
        if (_damageRoot?.Visible != true) return;
        _damageClock += delta;
        if (_damageClock < DamageRefreshSeconds) return;
        _damageClock = 0;
        RefreshDamage();
    }

    private void BuildDamage()
    {
        _damageRoot = new Control { Visible = false }; UiKit.Place(_damageRoot, 0, 0, 1920, 1080); AddChild(_damageRoot);
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, .62f) }; UiKit.Place(backdrop, 0, 0, 1920, 1080); _damageRoot.AddChild(backdrop);
        UiKit.PanelAt(_damageRoot, DamagePanelX, DamagePanelY, DamagePanelW, DamagePanelH);
        UiKit.Label(_damageRoot, "伤害统计", DamagePanelX + 20, DamagePanelY + 16, 300, 46, 30, UiKit.Gold);
        _damageSeconds = UiKit.Label(_damageRoot, "", DamagePanelX + 320, DamagePanelY + 22, 340, 40, 21, UiKit.Muted);
        UiKit.Button(_damageRoot, "重置", DamagePanelX + 680, DamagePanelY + 20, 110, 40, () => { _battle.Session.ResetDamageStats(); RefreshDamage(); });
        UiKit.Button(_damageRoot, "关闭", DamagePanelX + 810, DamagePanelY + 20, 110, 40, CloseDamage);

        // 表头：与数据行共用同一组列坐标。
        foreach (var (title, x, w) in DamageColumns)
            UiKit.Label(_damageRoot, title, DamagePanelX + x, DamagePanelY + 78, w, 30, 20, UiKit.Muted);
        // 数据行：**预建固定行数的 Label，刷新时回填文字**——`Pages.cs` 的多行页面也是这个做法。
        for (int i = 0; i < DamageRowCount; i++)
        {
            float y = DamagePanelY + 112 + i * 28;
            for (int c = 0; c < DamageColumns.Length; c++)
            {
                var (_, x, w) = DamageColumns[c];
                _damageCells[i * 6 + c] = UiKit.Label(_damageRoot, "", DamagePanelX + x, y, w, 28, 19,
                    c == 0 ? UiKit.Text : UiKit.Jade);
            }
        }
        float totalY = DamagePanelY + 112 + DamageRowCount * 28 + 6;
        for (int c = 0; c < DamageColumns.Length; c++)
        {
            var (title, x, w) = DamageColumns[c];
            _damageTotalCells[c] = UiKit.Label(_damageRoot, c == 0 ? "合计" : "", DamagePanelX + x, totalY, w, 30, 20, UiKit.Gold);
        }
        _damageHint = UiKit.Wrapped(_damageRoot, "", DamagePanelX + 20, totalY + 34, DamagePanelW - 40, 52, 18, UiKit.Muted);
    }

    private void RefreshDamage()
    {
        if (_damageRoot is null) return;
        var session = _battle.Session;
        double seconds = session.DamageStatsSeconds;
        // **所有已习得的法术都在列**（含 0 伤害的 buff——用户要求"列出来但效率为 0"，好一眼看出哪些还没接进统计），
        // 再并上实际产生过伤害的来源（普通攻击永远在列，它是最重要的对照项；宠物技能也走这里进来）。
        var ids = new List<string> { "" };
        ids.AddRange(session.State.Skills.Where(kv => kv.Value > 0).Select(kv => kv.Key));
        ids.AddRange(session.DamageStats.Rows.Keys);
        var rows = ids.Distinct()
            .Select(id => (Id: id, Row: session.DamageStats.Rows.GetValueOrDefault(id)))
            .OrderByDescending(x => x.Row?.Effective ?? 0).ThenBy(x => x.Id).ToArray();
        double effective = rows.Sum(x => x.Row?.Effective ?? 0);
        double overkill = rows.Sum(x => x.Row?.Overkill ?? 0);
        int hits = rows.Sum(x => x.Row?.Hits ?? 0);
        _damageSeconds.Text = $"统计时长 {seconds:0.#} 秒";
        for (int i = 0; i < DamageRowCount; i++)
        {
            int at = i * 6;
            if (i >= rows.Length)
            {
                for (int c = 0; c < 6; c++) _damageCells[at + c].Text = "";
                continue;
            }
            var (id, row) = rows[i];
            double own = row?.Effective ?? 0;
            _damageCells[at].Text = SourceName(id);
            _damageCells[at + 1].Text = UiKit.Number(own);
            _damageCells[at + 2].Text = effective > 0 && own > 0 ? (own / effective).ToString("P0") : "—";
            _damageCells[at + 3].Text = UiKit.Number(row?.Overkill ?? 0);
            _damageCells[at + 4].Text = own > 0 ? Dps(own, seconds) : "0";
            _damageCells[at + 5].Text = (row?.Hits ?? 0).ToString("0");   // 计数不能过 `Number`，它会截断成 K/M
        }
        _damageTotalCells[0].Text = "合计";
        _damageTotalCells[1].Text = UiKit.Number(effective);
        _damageTotalCells[2].Text = effective > 0 ? "100%" : "—";
        _damageTotalCells[3].Text = UiKit.Number(overkill);
        _damageTotalCells[4].Text = Dps(effective, seconds);
        _damageTotalCells[5].Text = hits.ToString("0");
        _damageHint.Text = rows.Length > DamageRowCount
            ? $"只列出前 {DamageRowCount} 项（共 {rows.Length} 项）"
            : "已习得的法术全部在列；0 的是还没接进统计的功能类（如攻速、暴击）。溢出 = 打在已死目标上的那部分。";
    }

    private static string Dps(double damage, double seconds) => seconds > 0 ? UiKit.Number(damage / seconds) : "—";

    /// <summary>
    /// 把来源 id 翻成给人看的名字。空串是**普通攻击**（`TickBasicAttack` 就是这么发的）；
    /// 宠物技能不在 `SwordSkill` 里，要另外查 `PetSkill`；**查不到就原样显示 id**，不静默吞掉。
    /// </summary>
    private string SourceName(string id)
    {
        if (id == "") return "普通攻击";
        var config = _battle.Session.Config;
        if (config.Skills.TryGetValue(id, out var skill)) return skill.Name;
        var pet = config.Rows("PetSkill").FirstOrDefault(r => r.Text("id") == id);
        return pet is null ? id : pet.Text("name");
    }
}
