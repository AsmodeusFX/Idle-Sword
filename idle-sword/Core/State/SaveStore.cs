using System.Text.Json;

namespace IdleSword.Core;

/// <summary>整份快照原子替换，首杀标记与奖励一起提交。保留上一版，损坏时只从有效备份恢复。</summary>
public sealed class SaveStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string? Warning { get; private set; }
    public PlayerState? Load(GameConfig config)
    {
        bool exists = false;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            exists = true;
            try
            {
                var state = JsonSerializer.Deserialize<PlayerState>(File.ReadAllText(candidate)) ?? throw new InvalidDataException("存档为空");
                AdoptUntrackedCores(state);
                Validate(state, config);
                if (candidate.EndsWith(".bak")) Warning = "主存档损坏，已从上一份备份恢复。";
                return state;
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException or NullReferenceException)
            { Warning = "存档校验失败：" + e.Message; }
        }
        if (exists) throw new InvalidDataException("主存档和备份均不可用。为避免覆盖进度，停止加载。" + Warning);
        return null;
    }
    /// <summary>删除主存档与备份。仅用于"重置游戏进度"，调用方负责重新建立会话。</summary>
    public void Delete()
    {
        foreach (string candidate in new[] { path, path + ".bak", path + ".tmp" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }
    public void Save(PlayerState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, Options); stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
        else File.Move(tmp, path);
    }
    /// <summary>
    /// 迁移：DebugGranted 是后加的字段，更早的存档里没有记录。若这类存档已被 GM 发过妖核，
    /// 校验会把超出首杀账本的部分判为篡改并拒绝整份存档（旧版即因此无法登录）。
    /// 这里只在「完全没有调试记录」时，把超出的妖核一次性补登记为调试发放，保留进度；
    /// 一旦存在记录便不再放行，避免持续掩盖真实的不一致。
    /// </summary>
    private static void AdoptUntrackedCores(PlayerState s)
    {
        if (s.DebugGranted.Count > 0) return;
        double excess = s.Amount("core") - s.FirstKills.Count;
        if (excess > 0) s.DebugGranted["core"] = excess;
    }
    public static void Validate(PlayerState s, GameConfig c)
    {
        if (s.Version != 1) throw new InvalidDataException("不支持的存档版本");
        if (!c.Levels.Any(l => l.Id == s.Battle.LevelId)) throw new InvalidDataException("存档关卡不存在");
        var level = c.Levels.Single(l => l.Id == s.Battle.LevelId);
        void References(IEnumerable<string> ids, string table)
        {
            if (ids.Any(id => !c.Rows(table).Any(r => r.Text("id") == id))) throw new InvalidDataException("存档引用缺失: " + table);
        }
        // **等级上限归"购买闸门"，不归存档校验**：`SwordRealmSystem` / `SwordIntentSystem` 已经用
        // `rank >= max_level` 拦住了正常途径，而**后期手段可以把等级顶过 `max_level`**（用户定的成长口径：
        // 那条公式线性不封顶）。所以这里只校验"是个合理的非负整数"——以前写 `rank > max_level` 直接拒档，
        // 会让那类存档**读回来就崩**。9999 是防呆（挡住手改存档的荒谬值），不是设计天花板。
        const int MaxReasonableRank = 9999;
        void Ranks(Dictionary<string, int> ranks, string table)
        {
            References(ranks.Keys, table);
            if (ranks.Any(p => p.Value < 0 || p.Value > MaxReasonableRank)) throw new InvalidDataException("存档等级非法: " + table);
        }
        Ranks(s.Skills, "SwordSkill"); Ranks(s.Talents, "Talent"); Ranks(s.Upgrades, "SwordUpgrade");
        References(s.Realms, "SwordLevel"); References(s.Pets, "Pet"); References(s.UnlockedLevels, "level"); References(s.Wallet.Keys, "item");
        if (!s.UnlockedLevels.Contains(s.Battle.LevelId)) throw new InvalidDataException("当前关卡未解锁");
        if (s.Weapon != "") References([s.Weapon], "Equip");
        if (s.WeaponLevel < 0 || !double.IsFinite(s.WeaponRoll) || s.WeaponRoll <= 0) throw new InvalidDataException("武器存档非法");
        if (s.Wallet.Any(p => !double.IsFinite(p.Value) || p.Value < 0)) throw new InvalidDataException("货币数值非法");
        References(s.DebugGranted.Keys, "item");
        if (s.DebugGranted.Values.Any(v => !double.IsFinite(v) || v < 0)) throw new InvalidDataException("调试发放记录非法");
        // 妖核累计投放量 = 首杀关卡数 + 调试发放量；两项之外的余额即为不一致。
        if (s.Amount("core") > s.FirstKills.Count + s.DebugGranted.GetValueOrDefault("core") || s.Amount("core") % 1 != 0) throw new InvalidDataException("妖核数量与首杀账本不符");
        if (s.FirstKills.Any(id => !c.Levels.Any(l => l.Id == id))) throw new InvalidDataException("首杀关卡不存在");
        if (!double.IsFinite(s.Battle.PlayerX) || !double.IsFinite(s.Battle.PlayerHp) || s.Battle.PlayerHp < 0) throw new InvalidDataException("战斗状态非法");
        if (s.Battle.Cell < 0 || s.Battle.Cell >= level.Cells || s.Battle.PlayerX < 0 || s.Battle.PlayerX >= level.Cells * c.Setting("cell_width")) throw new InvalidDataException("关卡位置非法");
        if (!double.IsFinite(s.Battle.RespawnTimer) || s.Battle.RespawnTimer < 0 || s.Battle.Cooldowns.Values.Any(v => !double.IsFinite(v) || v < 0)) throw new InvalidDataException("战斗计时非法");
        if (s.EquippedPets.Count > 3 || s.EquippedPets.Distinct().Count() != s.EquippedPets.Count || s.EquippedPets.Any(p => !s.Pets.Contains(p))) throw new InvalidDataException("出战剑灵非法");
        foreach (var (pet, buffs) in s.PetBuffs)
        {
            if (!s.Pets.Contains(pet)) throw new InvalidDataException("增强引用未拥有的剑灵");
            References(buffs, "PetEquip");
            if (buffs.Count > 3 || buffs.Select(id => c.Row("PetEquip", id).Text("category")).Distinct().Count() != buffs.Count) throw new InvalidDataException("剑灵增强类别重复或过多");
        }
        foreach (var (item, amount) in s.PendingIntent)
        {
            var r = c.Rows("contemplation").SingleOrDefault(r => r.Text("item_id") == item);
            if (r is null || !double.IsFinite(amount) || amount < 0 || amount > r.Number("capacity")) throw new InvalidDataException("参悟积存非法");
        }
        if (s.IntentTimers.Values.Any(v => !double.IsFinite(v) || v < 0)) throw new InvalidDataException("参悟计时非法");
        foreach (var (cell, spawn) in s.Battle.Spawns)
            if (cell < 0 || cell >= level.Cells || !double.IsFinite(spawn.Timer) || spawn.Wave < 0) throw new InvalidDataException("刷怪点存档非法");
        foreach (var e in s.Battle.Enemies)
            if (!c.Monsters.ContainsKey(e.MonsterId) || !double.IsFinite(e.Hp) || e.Hp < 0 || !double.IsFinite(e.MaxHp) || e.MaxHp <= 0 || e.Hp > e.MaxHp || !double.IsFinite(e.X) || !double.IsFinite(e.Atk) || e.Kind != c.Monsters[e.MonsterId].Kind) throw new InvalidDataException("怪物存档非法");
    }
}
