using IdleSword.Core;

namespace IdleSword.Features;

/// <summary>独立养成功能的会话操作；统一扣费与持久化，不依赖界面节点。</summary>
public sealed partial class GameSession
{
    public bool Craft(string id)
    {
        var r = Config.Row("Equip", id);
        if (!Pay("gold", r.Number("craft_cost"))) return Say($"{CurrencyName("gold")}不足。");
        State.Weapon = id; State.WeaponLevel = 0; State.WeaponRoll = 1;
        return Changed("已打造并装备 " + r.Text("name") + "。样例版会替换原武器。");
    }
    public bool Strengthen()
    {
        if (State.Weapon == "") return Say("请先打造武器。");
        if (!Pay("gold", Config.Row("Equip", State.Weapon).Number("upgrade_cost") * (State.WeaponLevel + 1))) return Say($"{CurrencyName("gold")}不足。");
        State.WeaponLevel++; return Changed("淬炼成功 · 武器 +" + State.WeaponLevel);
    }
    public bool Refine()
    {
        if (State.Weapon == "") return Say("请先打造武器。");
        if (!Pay("gold", Config.Row("Equip", State.Weapon).Number("refine_cost"))) return Say($"{CurrencyName("gold")}不足。");
        State.WeaponRoll = Math.Round(.9 + _random.NextDouble() * .4, 2); return Changed("洗练完成 · 攻击品质 " + State.WeaponRoll.ToString("P0"));
    }
}
