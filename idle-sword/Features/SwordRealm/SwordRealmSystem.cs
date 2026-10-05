using IdleSword.Core;

namespace IdleSword.Features;

/// <summary>独立养成功能的会话操作；统一扣费与持久化，不依赖界面节点。</summary>
public sealed partial class GameSession
{
    public bool UnlockRealm(string id)
    {
        var r = Config.Row("SwordLevel", id);
        if (State.Realms.Contains(id)) return Say("境界已解锁。");
        if (Config.Rows("SwordLevel").Any(x => x.Int("order") < r.Int("order") && !State.Realms.Contains(x.Text("id")))) return Say("请先突破前一境界。");
        if (!Pay("gold", r.Number("cost_gold"))) return Say("灵钱不足。");
        State.Realms.Add(id); return Changed("境界突破 · " + r.Text("name"));
    }
    public double SkillCost(string id) { var r = Config.Skills[id]; return Math.Ceiling(r.Cost * Math.Pow(r.CostGrowth, State.Skills.GetValueOrDefault(id))); }
    public bool UpgradeSkill(string id)
    {
        var r = Config.Skills[id]; var rank = State.Skills.GetValueOrDefault(id);
        if (!State.Realms.Contains(r.Realm)) return Say("请先解锁所属境界。");
        if (rank >= r.MaxLevel) return Say("法术已满级。");
        if (!Pay("gold", SkillCost(id))) return Say("灵钱不足。");
        State.Skills[id] = rank + 1; return Changed("法术精进 · " + r.Name);
    }
}
