using IdleSword.Core;

namespace IdleSword.Features;

public sealed partial class GameSession
{
    public double TalentBonus(string effect) => Config.Rows("Talent").Where(r => r.Text("effect") == effect).Sum(r => r.Number("effect_per_level") * State.Talents.GetValueOrDefault(r.Text("id")));
    /// <summary>
    /// 参悟加成：**按 `effect` 分类**求和（`value × 该行等级`）。
    ///
    /// 以前这里**不看 `effect`**——凡 `skill_id` 命中就把该技能四行的 `value × 等级` 一起加成。
    /// 两个后果：① 剑二十三那 4 行是 `inherit_percent`，却被当成"伤害加成"塞进了 `SkillPower`（**串味**）；
    /// ② 以后每加一种 `effect`（弹数 / 穿透概率 / 触发概率…）都会自动被卷进伤害里。
    /// 现在每种 effect 各走各的：伤害取 `damage_percent`、影分身继承比例取 `inherit_percent`，以此类推。
    /// </summary>
    public double SkillBonus(string skill, string effect) => Config.Rows("SwordUpgrade")
        .Where(r => r.Text("skill_id") == skill && r.Text("effect") == effect)
        .Sum(r => r.Number("value") * State.Upgrades.GetValueOrDefault(r.Text("id")));
    // TalentVisible / TalentCost / CanBuyTalent / BuyTalent 都搬去了 Features/Talent/TalentSystem.cs——
    // 它们现在要读 TalentLayout.csv 的几何与前置，跟星图放一起更好找。
    /// <summary>
    /// 货币的**显示名**，从 `item.csv` 取。凡是给玩家看的文案都要走它，不要在代码里写死——
    /// 「灵钱」改成「灵石」时漏掉的那几处不会报错，只会让界面上同时出现两个名字。
    /// </summary>
    public string CurrencyName(string id) => Config.Row("item", id).Text("name");
    private bool Pay(string currency, double amount)
    {
        if (State.Amount(currency) < amount) return false;
        State.Wallet[currency] = State.Amount(currency) - amount; return true;
    }
}
