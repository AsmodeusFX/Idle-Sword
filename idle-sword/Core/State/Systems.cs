namespace IdleSword.Core;

/// <summary>
/// 可**逐步解锁**的系统 id。开局全部锁着，靠游戏进程（首次击杀、修行节点）一个个打开——
/// 放置游戏的前期代入感就来自"从无到有"，而不是开局把五套成品系统一起摊在玩家面前。
///
/// **白名单硬编码在 Core**：存档校验要拿它核对 `PlayerState.UnlockedSystems`。
/// 不校验的话，代码里一个 id 拼错（比如 "cultivation" 写成 "cultivation "）会让那个系统
/// **永久锁死、且没有任何报错**——正是项目一直在拦的那类静默失败。
///
/// 剑灵（<see cref="Pet"/>）在 UI 上仍然没有入口（`Main.BuildShell` 的 `names` 里没有它），
/// 但它是白名单里的一员：解锁状态与入口是两件事，将来挂上入口时不必回头改存档校验。
/// </summary>
public static class Systems
{
    public const string Cultivation = "cultivation";   // 修行
    public const string Realm = "realm";               // 法术（页签名；里头仍是「境界突破」那条阶梯）
    public const string Forge = "forge";               // 铸造
    public const string Intent = "intent";             // 参悟
    public const string Pet = "pet";                   // 剑灵（暂无入口）

    /// <summary>全部系统 id，供存档校验与 GM「一键解锁」共用。</summary>
    public static readonly string[] All = [Cultivation, Realm, Forge, Intent, Pet];

    /// <summary>
    /// 修行节点的 `effect` → 它解锁的系统。**这是解锁类效果唯一的登记处**：
    /// `GameConfig` 拿它校验"每个 `*_system` 效果都真的接到了一个系统"，`GameSession` 拿它算解锁。
    ///
    /// 漏登记一个的后果是**那个系统永久锁死、且不报任何错**——加载期那条校验就是拦这个的。
    /// 反过来，映射是**推导**而不是"买节点时写进 `UnlockedSystems`"：读档回来、改配置回来都不会漂移，
    /// 也不必为它做存档迁移。
    /// </summary>
    public static readonly Dictionary<string, string> ByEffect = new()
    {
        ["realm_system"] = Realm,
        ["forge_system"] = Forge,
        ["intent_system"] = Intent,
    };
}
