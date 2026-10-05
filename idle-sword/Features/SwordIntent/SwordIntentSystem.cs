using IdleSword.Core;

namespace IdleSword.Features;

/// <summary>独立养成功能的会话操作；统一扣费与持久化，不依赖界面节点。</summary>
public sealed partial class GameSession
{
    public void ClickOre(string id)
    {
        var r = Config.Row("contemplation", id); ProduceIntent(r, r.Number("click_amount"));
    }
    private void ProduceIntent(CsvRow r, double amount)
    {
        string item = r.Text("item_id"); State.PendingIntent[item] = Math.Min(r.Number("capacity"), State.PendingIntent.GetValueOrDefault(item) + amount);
    }
    private void TickIntent(double dt)
    {
        if (TalentBonus("auto_intent") <= 0) return;
        foreach (var r in Config.Rows("contemplation"))
        {
            string item = r.Text("item_id");
            if (State.PendingIntent.GetValueOrDefault(item) >= r.Number("capacity")) continue;
            double timer = State.IntentTimers.GetValueOrDefault(item) + dt;
            while (timer >= r.Number("auto_interval")) { timer -= r.Number("auto_interval"); ProduceIntent(r, r.Number("click_amount")); }
            State.IntentTimers[item] = timer;
        }
    }
    public void CollectIntent(string item)
    {
        double amount = State.PendingIntent.GetValueOrDefault(item);
        if (amount <= 0) return;
        State.PendingIntent[item] = 0; AddCurrency(item, amount); Changed("已收取 " + amount + " 份剑意。");
    }
    public bool UpgradeIntent(string id)
    {
        var r = Config.Row("SwordUpgrade", id); int rank = State.Upgrades.GetValueOrDefault(id);
        if (rank >= r.Int("max_level")) return Say("已达到强化上限。");
        if (!Pay(r.Text("currency_id"), r.Number("cost") * (rank + 1))) return Say("对应剑意不足，请参悟并收取。");
        State.Upgrades[id] = rank + 1; return Changed("参悟强化 · " + r.Text("name"));
    }
}
