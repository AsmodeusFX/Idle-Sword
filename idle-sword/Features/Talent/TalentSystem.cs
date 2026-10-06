using IdleSword.Core;

namespace IdleSword.Features;

/// <summary>
/// 修行（天赋）星图的会话操作：解锁判定、扣费、加点。
///
/// 配置拆成两张表：`Talent.csv` 是**内容**（名字 / 等级 / 消耗 / 效果 / 图标），
/// `TalentLayout.csv` 是**几何与拓扑**（col / row / 前置）。**几何那张归工具整份拥有**
/// （下一轮的节点编辑器），所以这里只读、不写。
/// </summary>
public sealed partial class GameSession
{
    /// <summary>节点当前等级。</summary>
    public int TalentLevel(string id) => State.Talents.GetValueOrDefault(id);

    /// <summary>
    /// 该节点的前置清单。**任意一条点亮即可开放**，所以只返回 id——
    /// 曾经还有一档"这条前置必须满级"（`prereq_state` 列），已连同那一列一起删掉。
    /// </summary>
    public List<string> TalentPrereqs(string id) => Config.Row("TalentLayout", id).TextList("prereq");

    /// <summary>
    /// 节点该不该出现在星图上：**任意一条前置点亮就算开放**（不是"全部"）。
    ///
    /// ⚠️ **根节点要单独兜住**：它的前置是空集，而 `Any` 对空集返回 false——
    /// 照直写会让整张星图**开局连根节点都看不见**，玩家没有任何地方可点。
    /// </summary>
    public bool TalentVisible(string id)
    {
        if (TalentLevel(id) > 0) return true;
        var prereqs = TalentPrereqs(id);
        return prereqs.Count == 0 || prereqs.Any(p => TalentLevel(p) >= 1);
    }

    /// <summary>把该节点从 <paramref name="level"/> 点到下一级要花的钱（`cost` 列表的第 level 项）。</summary>
    public double TalentCost(string id, int level) => Config.Row("Talent", id).NumberList("cost")[level - 1];

    /// <summary>
    /// 现在能不能买。失败时**必须给出能读的中文原因**——悬停说明条直接把它显示给玩家，
    /// 所以每条分支都要说清"还差什么"，不能笼统地写"不可用"。
    /// </summary>
    public bool CanBuyTalent(string id, out string reason)
    {
        var r = Config.Row("Talent", id);
        int level = TalentLevel(id), max = r.Int("max_level");
        if (r.Text("effect") == "none") { reason = "该节点还没有配置效果。"; return false; }
        if (!TalentVisible(id)) { reason = "前置节点尚未点亮（任一点亮一个即可）。"; return false; }
        if (level >= max) { reason = "已达最高等级。"; return false; }
        string currency = r.Text("cost_currency");
        double cost = TalentCost(id, level + 1);
        if (State.Amount(currency) < cost)
        {
            reason = $"{Config.Row("item", currency).Text("name")}不足：需要 {cost:0.#}，持有 {State.Amount(currency):0.#}。";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// 点一次节点。**绝不部分生效**——买不起就什么都不改，不会扣掉一半的钱。
    ///
    /// `Pay` 只动 `State.Wallet`、**不写投放账本**：灵核是记账货币（累计投放量必须等于
    /// 首杀关卡数 + 调试发放量），花掉它不该回头去改投放记录，否则存档校验会直接拒绝整份存档。
    /// </summary>
    public bool BuyTalent(string id)
    {
        if (!CanBuyTalent(id, out string reason)) return Say(reason);
        var r = Config.Row("Talent", id);
        int level = TalentLevel(id) + 1;
        Pay(r.Text("cost_currency"), TalentCost(id, level));
        State.Talents[id] = level;
        return Changed($"{r.Text("name")} 提升至 {level} 级");
    }

    /// <summary>
    /// **调试用**：把「剑气」这个解锁在开 / 关之间翻转，用来对照近战与远程两种普攻形态
    /// （正常途径是在修行树上点它）。按 `effect` 找节点而不是写死 id——节点改名不会让它失效。
    /// </summary>
    public bool DebugToggleRangedBasic()
    {
        var node = Config.Rows("Talent").First(r => r.Text("effect") == "ranged_basic");
        bool on = State.Talents.GetValueOrDefault(node.Text("id")) > 0;
        State.Talents[node.Text("id")] = on ? 0 : 1;
        return !on;
    }
}
