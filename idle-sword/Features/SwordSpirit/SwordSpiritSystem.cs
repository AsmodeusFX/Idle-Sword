using IdleSword.Core;

namespace IdleSword.Features;

/// <summary>独立养成功能的会话操作；统一扣费与持久化，不依赖界面节点。</summary>
public sealed partial class GameSession
{
    public bool DrawPet()
    {
        if (!Pay("gold", Config.Setting("pet_draw_cost"))) return Say($"{CurrencyName("gold")}不足。");
        var rows = Config.Rows("Pet"); double roll = _random.NextDouble() * rows.Sum(r => r.Number("weight"));
        var selected = rows[^1];
        foreach (var r in rows) { roll -= r.Number("weight"); if (roll < 0) { selected = r; break; } }
        string id = selected.Text("id");
        if (!State.Pets.Add(id)) { AddCurrency("gold", Config.Setting("pet_duplicate_gold")); return Changed("重复剑灵化为 " + Config.Setting("pet_duplicate_gold") + $" {CurrencyName("gold")}。"); }
        if (State.EquippedPets.Count < 3) State.EquippedPets.Add(id);
        return Changed("剑灵应召 · " + selected.Text("name"));
    }
    public bool TogglePet(string id)
    {
        if (!State.Pets.Contains(id)) return Say("尚未获得此剑灵。");
        if (State.EquippedPets.Remove(id)) return Changed("剑灵已休息。");
        if (State.EquippedPets.Count >= 3) return Say("最多出战 3 个剑灵。");
        State.EquippedPets.Add(id); return Changed("剑灵已出战。");
    }
    public bool EquipPetBuff(string pet, string buff)
    {
        if (!State.Pets.Contains(pet)) return Say("尚未获得此剑灵。");
        if (!State.PetBuffs.TryGetValue(pet, out var buffs)) State.PetBuffs[pet] = buffs = [];
        if (buffs.Contains(buff)) return Say("已装备此增强。");
        var r = Config.Row("PetEquip", buff);
        if (buffs.Count >= 3 || buffs.Any(b => Config.Row("PetEquip", b).Text("category") == r.Text("category"))) return Say("增强槽已满或类别重复。");
        if (!Pay("gold", r.Number("cost_gold"))) return Say($"{CurrencyName("gold")}不足。");
        buffs.Add(buff); return Changed("剑灵增强 · " + r.Text("name"));
    }
}
