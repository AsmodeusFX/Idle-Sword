using IdleSword.Core;

namespace IdleSword.Features;

public sealed partial class GameSession
{
    private Dictionary<string, (string Id, double PerLevel)[]>? _talentEffects;

    /// <summary>只缓存配置的效果分组与每级值；等级每次从当前 State 读取，购买、调试和直接改状态均即时生效。</summary>
    public double TalentBonus(string effect)
    {
        _talentEffects ??= Config.Rows("Talent").GroupBy(r => r.Text("effect"))
            .ToDictionary(g => g.Key, g => g.Select(r => (r.Text("id"), r.Number("effect_per_level"))).ToArray());
        if (!_talentEffects.TryGetValue(effect, out var nodes)) return 0;
        double total = 0;
        foreach (var node in nodes) total += node.PerLevel * State.Talents.GetValueOrDefault(node.Id);
        return total;
    }
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
