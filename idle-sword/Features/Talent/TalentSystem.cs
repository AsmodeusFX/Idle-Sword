using IdleSword.Core;

namespace IdleSword.Features;

/// <summary>独立养成功能的会话操作；统一扣费与持久化，不依赖界面节点。</summary>
public sealed partial class GameSession
{
    public bool BuyTalent(string id)
    {
        var r = Config.Row("Talent", id); int rank = State.Talents.GetValueOrDefault(id);
        if (!TalentVisible(id) || rank >= r.Int("max_level")) return Say("节点尚不可用或已满级。");
        double gold = r.Number("cost_gold") * (rank + 1), core = r.Number("cost_core");
        if (State.Amount("gold") < gold || State.Amount("core") < core) return Say("灵钱或灵核不足。");
        Pay("gold", gold); Pay("core", core); State.Talents[id] = rank + 1;
        return Changed("修行精进 · " + r.Text("name"));
    }
}
