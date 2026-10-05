namespace IdleSword.SpriteGen;

/// <summary>
/// 怪物 / 剑灵的定义。
///
/// 关键手法：**十只怪共用一张底模**，每只只换调色板和头顶配件。旧素材其实也是同一套矩形复制了十份，
/// 区别在于它有两点不管：**没有统一描边**、**填充偏灰**。压在压暗后的背景上，两样一起就糊成一团。
/// 这里补上 `K` 描边、把填充整体提亮，于是它们和主角读成同一套美术。
///
/// 字符含义：K 描边 / B 身体 / H 头顶与配件 / E 眼 / G 眼高光 / M 嘴 / F 脚。
/// </summary>
internal static class Monsters
{
    /// <summary>
    /// 底模：下宽上窄的团子，顶上一道浅色眉带，两只眼、一张嘴、两只脚。
    /// 所有行按 32 格写，右侧不足由 DSL 自动补透明。
    /// </summary>
    private static readonly string[] BlobGrid =
    [
        "",                                 // 0
        "",                                 // 1
        "",                                 // 2
        "",                                 // 3
        "",                                 // 4
        "",                                 // 5
        ".............KKKKKK",              // 6  眉带顶（比身体窄一圈，否则读成扣在圆身上的方盒子）
        "............KHHHHHHK",             // 7
        "...........KHHHHHHHHK",            // 8
        "...........KHHHHHHHHK",            // 9
        "...........KHHHHHHHHK",            // 10
        "...........KHHHHHHHHK",            // 11
        "...........KHHHHHHHHK",            // 12
        "..........KKKKKKKKKKKK",           // 13 眉带并入身体（肩线）
        ".......KBBBBBBBBBBBBBBBBK",        // 14
        "......KBBGEEEBBBBBBGEEEBBK",       // 15 眼（带高光）
        "......KBBEEEEBBBBBBEEEEBBK",       // 16
        ".....KBBBEEEEBBBBBBEEEEBBBK",      // 17
        "....KBBBBBBBBBBBBBBBBBBBBBBK",     // 18
        "....KBBBBBBBBBBBBBBBBBBBBBBK",     // 19
        "....KBBBBBBBBBMMMMMBBBBBBBBK",     // 20 嘴
        "....KBBBBBBBBBMMMMMBBBBBBBBK",     // 21
        "....KBBBBBBBBBMMMMMBBBBBBBBK",     // 22
        "....KBBBBBBBBBBBBBBBBBBBBBBK",     // 23
        "....KBBBBBBBBBBBBBBBBBBBBBBK",     // 24
        ".....KBBBBBBBBBBBBBBBBBBBBK",      // 25
        "......KBBBBBBBBBBBBBBBBBBK",       // 26
        ".......KKKKKKKKKKKKKKKKKK",        // 27 底沿
        "........KFFFFK....KFFFFK",         // 28 两只脚
        "........KFFFFK....KFFFFK",         // 29
        "........KKKKKK....KKKKKK",         // 30
        "",                                 // 31
    ];

    /// <summary>镇山妖王：一对向上收的角，接到眉带两肩。</summary>
    private static readonly string[] HornOverlay =
    [
        "", "",
        "........KK............KK",         // 2
        ".......KHHK..........KHHK",        // 3
        ".......KHHK..........KHHK",        // 4
        ".......KHHK..........KHHK",        // 5
        ".......KHHK..........KHHK",        // 6
        ".......KHHK..........KHHK",        // 7
        ".......KHHK..........KHHK",        // 8  接到眉带两肩
    ];

    /// <summary>山魈弓手：右手边一张竖弓，贴着身体右侧。</summary>
    private static readonly string[] BowOverlay =
    [
        "", "", "", "", "", "", "", "", "",
        ".............................K",   // 9
        "............................KHHK", // 10
        "............................KHHK", // 11
        "............................KHHK", // 12
        "............................KHHK", // 13
        "............................KHHK", // 14
        "............................KHHK", // 15
        "............................KHHK", // 16
        "............................KHHK", // 17
        "............................KHHK", // 18
        "............................KHHK", // 19
        "............................KHHK", // 20
        "............................KHHK", // 21
        "............................KHHK", // 22
        "............................KHHK", // 23
        ".............................K",   // 24
    ];

    /// <summary>
    /// 悬浮的小光团，给剑灵与召唤物用：没有脚，身子是个圆球。
    /// 它们飘在半空，用带脚的底模会读成"站在空中"。
    /// </summary>
    private static readonly string[] WispGrid =
    [
        "", "", "", "", "", "", "", "", "", "", "", "",
        "..........KKKKKKKKKKKK",           // 12
        ".........KBBBBBBBBBBBBK",          // 13
        "........KBBBBBBBBBBBBBBK",         // 14
        ".......KBBBBBBBBBBBBBBBBK",        // 15
        "......KBBBBBBBBBBBBBBBBBBK",       // 16
        "......KBBGEEEBBBBBBGEEEBBK",       // 17
        "......KBBEEEEBBBBBBEEEEBBK",       // 18
        "......KBBEEEEBBBBBBEEEEBBK",       // 19
        ".....KBBBBBBBBBBBBBBBBBBBBK",      // 20
        ".....KBBBBBBBBBBBBBBBBBBBBK",      // 21
        ".....KBBBBBBBBBBBBBBBBBBBBK",      // 22
        "......KBBBBBBBBBBBBBBBBBBK",       // 23
        ".......KBBBBBBBBBBBBBBBBK",        // 24
        "........KBBBBBBBBBBBBBBK",         // 25
        ".........KBBBBBBBBBBBBK",          // 26
        "..........KKKKKKKKKKKK",           // 27
        "",                                 // 28
        "",                                 // 29
        "",                                 // 30
        "",                                 // 31
    ];

    /// <summary>
    /// 十只怪 + 三只剑灵。色值只动**填充**：每只保留自己的色相身份，统一提亮一档，
    /// 这样描边才立得住（描边压在暗背景上、填充又暗的话，轮廓会消失在暗角里）。
    /// 时空裂隙是物件不是生物，保留更亮的"核心"读法。
    /// </summary>
    public static IEnumerable<SpriteDef> All()
    {
        yield return Monster("slime", "#5fb39c", "#8fd8bd");   // 青苔妖
        yield return Monster("archer", "#9a8f6d", "#d8c98d", BowOverlay);  // 山魈弓手
        yield return Monster("tank", "#8a7d68", "#a99a80");    // 石甲兽
        yield return Monster("swift", "#9a7ce8", "#bca6f2");   // 疾影妖
        yield return Monster("bat", "#8266c4", "#5b4590");     // 夜枭
        yield return Monster("hawk", "#5a84c4", "#7ba6dd");    // 飞蝠
        yield return Monster("mage", "#8878bd", "#b39ccf");    // 幽火妖
        yield return Monster("elite", "#c9705f", "#e2936f");   // 赤甲妖
        yield return Monster("boss", "#9c6480", "#e08d9b", HornOverlay);  // 镇山妖王
        yield return Monster("rift", "#66a8c9", "#b7e4dd");    // 时空裂隙

        yield return Wisp("pet_azure", "#97d3c2", "#c6efe2");
        yield return Wisp("pet_crimson", "#eaa38a", "#ffd0bb");
        yield return Wisp("pet_snow", "#b5c4e5", "#dbe6fb");
        yield return Wisp("summon_shadow", "#9fdcc4", "#cdf0e2");
        yield return Wisp("summon_taixu", "#b49ae0", "#d9c9f5");
        yield return Wisp("summon_zhuxie", "#d9bc82", "#f0dcae");
    }

    private static SpriteDef Monster(string id, string body, string head, string[]? overlay = null) =>
        new(id, overlay is null ? BlobGrid : PixelArt.Merge(BlobGrid, overlay), MonsterPalette(body, head));

    private static SpriteDef Wisp(string id, string body, string head) =>
        new(id, WispGrid, MonsterPalette(body, head));

    private static Dictionary<char, string> MonsterPalette(string body, string head) => new()
    {
        ['K'] = Palette.Outline,
        ['B'] = body,
        ['H'] = head,
        ['E'] = Palette.Eye,
        ['G'] = Palette.Glint,
        ['M'] = Palette.Mouth,
        ['F'] = body,
    };
}
