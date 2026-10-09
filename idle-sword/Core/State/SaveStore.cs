using System.Text.Json;

namespace IdleSword.Core;

/// <summary>整份快照原子替换，首杀标记与奖励一起提交。保留上一版，损坏时只从有效备份恢复。</summary>
public sealed class SaveStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string? Warning { get; private set; }

    /// <summary>
    /// **深拷贝一份玩家状态**（走与存档完全相同的序列化器，所以嵌套的 `BattleState`、字典与集合
    /// 都是新的实例）。供需要"一份独立板面"的工具使用——目前是 GM 技能预览的「跟随当前存档」练度：
    /// 沙盒会话会**推进、会结算**（靶子一旦被打死就掉钱、写解锁标记），
    /// 所以必须拷贝而不是共享引用，否则一开预览就会污染真实进度。
    /// </summary>
    public static PlayerState Clone(PlayerState state) =>
        JsonSerializer.Deserialize<PlayerState>(JsonSerializer.SerializeToUtf8Bytes(state, Options)) ?? new PlayerState();
    public PlayerState? Load(GameConfig config)
    {
        bool exists = false;
        string failure = "";
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            exists = true;
            try
            {
                var state = JsonSerializer.Deserialize<PlayerState>(File.ReadAllText(candidate)) ?? throw new InvalidDataException("存档为空");
                AdoptUntrackedCores(state);
                AdoptRetiredSystems(state);
                AdoptUnlockState(state);
                string pruned = PruneUnknownReferences(state, config);
                Validate(state, config);
                string note = (candidate.EndsWith(".bak") ? "主存档损坏，已从上一份备份恢复。" : "") + pruned;
                Warning = note.Length > 0 ? note : null;
                return state;
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException or NullReferenceException)
            { failure = "存档校验失败：" + e.Message; }
        }
        return exists ? Archive(failure) : null;
    }

    /// <summary>
    /// 主存档与备份**都**过不了校验时的退路：**改名归档、绝不删除**，然后让调用方以新档继续。
    ///
    /// 「为避免覆盖进度，停止加载」那条初衷是对的——别拿一份新进度盖掉真存档。但它的代价是
    /// **把人锁在门外**：一次配置改动就能让工程再也起不来，而磁盘上那份进度再也读不回来。
    /// 归档改名两头都顾上：文件还在（随时能捞回来），人也不至于进不去。
    /// </summary>
    private PlayerState? Archive(string reason)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var kept = new List<string>();
        foreach (string candidate in new[] { path, path + ".bak" })
            if (File.Exists(candidate))
            {
                string moved = $"{candidate}.rejected-{stamp}";
                File.Move(candidate, moved, overwrite: true);
                kept.Add(Path.GetFileName(moved));
            }
        Warning = reason + "　已归档为 " + string.Join(" / ", kept) + "，本次以新进度开始——**原文件没有被删除**。";
        return null;
    }
    /// <summary>
    /// 迁移：把**指向已经不在配置里的 id 的引用丢掉**，保住存档的其余部分，并报出丢了什么。
    ///
    /// 要分两类看。成长类的 id（修行节点 / 法术 / 已开境界 / 剑灵 / 货币）是**设计迭代的产物**——
    /// 删一个节点、重排一次修行树，旧存档里立刻出现查不到的 id。把这种"配置漂移"当成篡改整份拒掉，
    /// 结果是**每改一次配置就废一次档**（2026-10-06 就是这么把工程锁死的）。所以这里丢弃它们。
    ///
    /// **净化只会删、永远不会给**，因此它不构成一条作弊通道。真正的不一致——灵核账本对不上、
    /// 数值非法、位置越界，以及系统 id 拼错（那是**代码 bug**，不是配置漂移）——仍然由
    /// <see cref="Validate"/> 硬拒。
    /// </summary>
    private static string PruneUnknownReferences(PlayerState s, GameConfig c)
    {
        var known = new Dictionary<string, HashSet<string>>();
        bool Exists(string table, string id)
        {
            if (!known.TryGetValue(table, out var set))
                known[table] = set = c.Rows(table).Select(r => r.Text("id")).ToHashSet();
            return set.Contains(id);
        }
        var dropped = new List<string>();
        void PruneMap<T>(Dictionary<string, T> map, string table, string label)
        {
            var gone = map.Keys.Where(id => !Exists(table, id)).ToList();
            foreach (string id in gone) map.Remove(id);
            if (gone.Count > 0) dropped.Add($"{label} {gone.Count} 项");
        }
        void PruneSet(HashSet<string> set, string table, string label)
        {
            int n = set.RemoveWhere(id => !Exists(table, id));
            if (n > 0) dropped.Add($"{label} {n} 项");
        }

        PruneMap(s.Talents, "Talent", "修行节点");
        PruneMap(s.Skills, "SwordSkill", "法术");
        PruneSet(s.Realms, "SwordLevel", "已开境界");
        PruneSet(s.Pets, "Pet", "剑灵");
        // 货币也走净化。**不做这一步，退役任何一种货币都会废掉老档**：`Validate` 的
        // `References(s.Wallet.Keys, "item")` 会因为查不到那个 id 而整份拒收（参悟删除时
        // `intent_0..3` 正是这么踩的）。钱包里的键只可能来自已经过加载期外键校验的配置，
        // 所以出现未知 id 只可能是"配置删了这一行"，与上面的成长类 id 是同一类漂移。
        PruneMap(s.Wallet, "item", "货币");
        PruneMap(s.DebugGranted, "item", "调试发放");
        // 剑灵本体没了，出战与增强里对它的引用会变成悬空的——必须先摘引用再让 `Validate` 过。
        s.EquippedPets.RemoveAll(id => !s.Pets.Contains(id));
        foreach (string pet in s.PetBuffs.Keys.Where(id => !s.Pets.Contains(id)).ToList()) s.PetBuffs.Remove(pet);
        return dropped.Count == 0 ? "" : "存档里有已从配置移除的内容，已丢弃：" + string.Join("、", dropped) + "；其余进度保留。";
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
    /// 迁移：DebugGranted 是后加的字段，更早的存档里没有记录。若这类存档已被 GM 发过灵核，
    /// 校验会把超出首杀账本的部分判为篡改并拒绝整份存档（旧版即因此无法登录）。
    /// 这里只在「完全没有调试记录」时，把超出的灵核一次性补登记为调试发放，保留进度；
    /// 一旦存在记录便不再放行，避免持续掩盖真实的不一致。
    /// </summary>
    private static void AdoptUntrackedCores(PlayerState s)
    {
        if (s.DebugGranted.Count > 0) return;
        double excess = s.Amount("core") - s.FirstKills.Count;
        if (excess > 0) s.DebugGranted["core"] = excess;
    }
    /// <summary>
    /// 迁移：把**已退役的系统 id** 从 `UnlockedSystems` 里剔除（名单见 <see cref="Systems.Retired"/>）。
    ///
    /// 参悟（剑意）2026-10-09 整体删除之后，老存档里那一份 `"intent"` 会被 `Validate` 的未知 id 校验
    /// 判为非法、整档归档。这个方法是**定点**的，不是"白名单外一律净化"——理由见 `Systems.Retired` 的说明。
    ///
    /// 放在 <see cref="AdoptUnlockState"/> **之前**是有意的：若一份老档的 `UnlockedSystems` 恰好只有
    /// `"intent"` 一项，先剔除才能让它退化成"空集"，从而被后面的补全逻辑认出"这是个缺字段的旧档"；
    /// 顺序颠倒的话它会以"非空"的样子跳过补全，结果全部系统锁死。
    /// </summary>
    private static void AdoptRetiredSystems(PlayerState s) => s.UnlockedSystems.RemoveWhere(Systems.Retired.Contains);
    /// <summary>
    /// 迁移：`UnlockedSystems` 是后加的字段，更早的存档里没有，反序列化得到空集 = 全部锁着。
    /// 但那些存档**本来就已经走过了教学**（有首杀、有点过的修行节点、有学会的法术），
    /// 让它们回头去锁一遍是平白罚人。所以只在"这一个字段从没写过"且"存档确实有进度"时，
    /// 补成全部解锁；新档（什么都没有）保持全锁，教学照常。
    /// </summary>
    private static void AdoptUnlockState(PlayerState s)
    {
        if (s.UnlockedSystems.Count > 0) return;
        bool hasProgress = s.FirstKills.Count > 0 || s.Talents.Count > 0 || s.Skills.Count > 0
            || s.Realms.Count > 0 || s.UnlockedLevels.Count > 1 || s.Pets.Count > 0 || s.Weapon != "";
        if (hasProgress) s.UnlockedSystems.UnionWith(Systems.All);
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
        // **等级上限归"购买闸门"，不归存档校验**：`SwordRealmSystem` 已经用
        // `rank >= max_level` 拦住了正常途径，而**后期手段可以把等级顶过 `max_level`**（用户定的成长口径：
        // 那条公式线性不封顶）。所以这里只校验"是个合理的非负整数"——以前写 `rank > max_level` 直接拒档，
        // 会让那类存档**读回来就崩**。9999 是防呆（挡住手改存档的荒谬值），不是设计天花板。
        const int MaxReasonableRank = 9999;
        void Ranks(Dictionary<string, int> ranks, string table)
        {
            References(ranks.Keys, table);
            if (ranks.Any(p => p.Value < 0 || p.Value > MaxReasonableRank)) throw new InvalidDataException("存档等级非法: " + table);
        }
        // 系统解锁：只允许白名单里的 id。写错一个字母会让那个系统**永久锁死且不报错**，必须拦在加载期。
        if (s.UnlockedSystems.Any(id => !Systems.All.Contains(id))) throw new InvalidDataException("存档引用了未知的系统 id");
        Ranks(s.Skills, "SwordSkill"); Ranks(s.Talents, "Talent");
        References(s.Realms, "SwordLevel"); References(s.Pets, "Pet"); References(s.UnlockedLevels, "level"); References(s.Wallet.Keys, "item");
        if (!s.UnlockedLevels.Contains(s.Battle.LevelId)) throw new InvalidDataException("当前关卡未解锁");
        if (s.Weapon != "") References([s.Weapon], "Equip");
        if (s.WeaponLevel < 0 || !double.IsFinite(s.WeaponRoll) || s.WeaponRoll <= 0) throw new InvalidDataException("武器存档非法");
        if (s.Wallet.Any(p => !double.IsFinite(p.Value) || p.Value < 0)) throw new InvalidDataException("货币数值非法");
        References(s.DebugGranted.Keys, "item");
        if (s.DebugGranted.Values.Any(v => !double.IsFinite(v) || v < 0)) throw new InvalidDataException("调试发放记录非法");
        // 灵核累计投放量 = 首杀关卡数 + 调试发放量；两项之外的余额即为不一致。
        if (s.Amount("core") > s.FirstKills.Count + s.DebugGranted.GetValueOrDefault("core") || s.Amount("core") % 1 != 0) throw new InvalidDataException("灵核数量与首杀账本不符");
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
        foreach (var (cell, spawn) in s.Battle.Spawns)
            if (cell < 0 || cell >= level.Cells || !double.IsFinite(spawn.Timer) || spawn.Wave < 0) throw new InvalidDataException("刷怪点存档非法");
        foreach (var e in s.Battle.Enemies)
            if (!c.Monsters.ContainsKey(e.MonsterId) || !double.IsFinite(e.Hp) || e.Hp < 0 || !double.IsFinite(e.MaxHp) || e.MaxHp <= 0 || e.Hp > e.MaxHp || !double.IsFinite(e.X) || !double.IsFinite(e.Atk) || e.Kind != c.Monsters[e.MonsterId].Kind) throw new InvalidDataException("怪物存档非法");
    }
}
