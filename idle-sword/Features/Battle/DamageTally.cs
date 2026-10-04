namespace IdleSword.Features;

/// <summary>
/// 按**来源技能**累计的伤害账本（GM 面板的「伤害统计」读它）。纯运行时，不落盘——与 `Effects` / `Enemies` 同性质。
///
/// 归因口径（与面板上那几列一一对应）：
/// - **有效** = 实际打掉的血。一次打掉 500 但目标只剩 120 血，有效就只记 120——这才是"贡献"。
/// - **溢出** = 名义伤害里超出剩余血量的那部分（上面那例就是 380）。单列一栏是为了看出"哪个技能经常砸在尸体上"。
/// - **命中** = 落血次数。`damage &lt;= 0` 的调用不进账（见 `Add`）。
///
/// 键是剑诀 / 宠物技能 id，**空串表示普通攻击**（`GameSession.TickBasicAttack` 就是这么发的）。
/// 影分身那一份的 id 与本体相同，所以两份自然合并到同一个技能名下。
/// </summary>
public sealed class DamageTally
{
    /// <summary>一个来源的账。</summary>
    public sealed class Row
    {
        public double Effective { get; internal set; }
        public double Overkill { get; internal set; }
        public int Hits { get; internal set; }
    }

    private readonly Dictionary<string, Row> _rows = [];
    // 统计起点（会话的 `Elapsed`）。`GameSession.Elapsed` 从不重置，所以分母必须记在这里——
    // 否则"重置"之后，每秒伤害会被统计开始之前那段时间稀释。
    private double _since;

    public IReadOnlyDictionary<string, Row> Rows => _rows;

    /// <summary>自上一次 <see cref="Reset"/> 起过去了多少**模拟**秒（真实暂停不计，因为 `Elapsed` 只在 Step 里走）。</summary>
    public double Seconds(double elapsed) => Math.Max(0, elapsed - _since);

    /// <summary>
    /// 记一次落血。**两个数都为 0 就直接返回**：苍穹剑陨落地派生的那个 `Damage = 0` 的"爆炸余韵"
    /// 照样会走到这里，不拦的话它会凭空给技能加一次命中。
    /// </summary>
    public void Add(string source, double effective, double overkill)
    {
        if (effective <= 0 && overkill <= 0) return;
        if (!_rows.TryGetValue(source, out var row)) _rows[source] = row = new();
        row.Effective += effective;
        row.Overkill += overkill;
        row.Hits++;
    }

    /// <summary>清空账本，并把统计起点挪到此刻。</summary>
    public void Reset(double elapsed)
    {
        _rows.Clear();
        _since = elapsed;
    }
}
