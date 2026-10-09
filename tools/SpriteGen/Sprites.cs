namespace IdleSword.SpriteGen;

/// <summary>
/// 全部精灵的定义表（对应 SoundGen 的 Sounds.cs）。
///
/// 一行 32 格，每格 2×2 像素，产出 64×64 的 SVG。行比 32 短时右侧按透明补齐，
/// 所以只写有内容的长度即可，但**左端的缩进必须准确**。
/// </summary>
internal static class Sprites
{
    private static readonly Dictionary<char, string> PlayerPalette = new()
    {
        ['K'] = Palette.Outline,   // 描边 / 眼睛
        ['H'] = Palette.SkinHi,    // 受光面
        ['B'] = Palette.SkinBase,  // 基色
        ['S'] = Palette.SkinShade, // 背光面
        ['D'] = Palette.SkinDeep,  // 凹坑（土豆眼）
        ['L'] = Palette.Leaf,
        ['V'] = Palette.LeafVein,
        ['W'] = Palette.White,     // 眼里的高光
        ['T'] = Palette.Steel,     // 剑身
        ['G'] = Palette.Gold,      // 剑柄 / 护手
        ['O'] = Palette.Boot,
        ['U'] = Palette.BootCuff,
    };

    /// <summary>
    /// 主角：一颗背着仙剑的椭圆土豆。
    ///
    /// 读法上的三个着力点：①**顶部发芽**（两片玉色叶 + 短茎）——和《土豆兄弟》那颗光秃圆土豆
    /// 拉开距离的地方；②**背上的仙剑**——金色剑首、深色握柄、一道横向护手、钢色剑身，从左侧垂到
    /// 将近脚边，即使缩到 136px 也能一眼认出背的是剑；③**暖象牙白 + 深描边**，压在压暗的背景上是
    /// 全场最亮的东西，而旧素材的青绿和背景同色相，正是"不跳"的原因。
    ///
    /// **剑必须是一条直轴**：剑首 / 握柄 / 护手 / 剑身都居中在同一条竖线上（cols 5–8，护手 cols 3–10
    /// 左右对称）。曾经让握柄逐行向左偏一列去"靠向肩头"，结果整把剑读成**弯的**——像素画里，
    /// 只有整把剑一起斜才是"斜挂"，只让柄斜就是"扭曲"。
    ///
    /// 脸朝右（精灵不会被翻转），眼睛两格高、带一格白高光；不画嘴，136px 下会糊。
    /// </summary>
    public static readonly SpriteDef Player = new("player",
    [
        "",                                 // 0
        "",                                 // 1
        "............LL....LL",             // 2  芽尖
        "...........LLLL..LLLL",            // 3
        ".....KGGK....LLLLLLLL",            // 4  剑首 + 双叶张开
        ".....KGGK....VLLLLLLV",            // 5
        ".....KOOK.....VVVV",               // 6  握柄（深色，和金色剑首分开）
        ".....KOOK......VV",                // 7
        ".....KOOK....KHHHHK",              // 8  头顶（马上接高光，免得糊成一顶黑帽）
        ".....KOOK...KHHHHHHK",             // 9
        ".....KOOK..KHHHHHHHHK",            // 10
        "...KGGGGGGKHHHHHHHHHHK",           // 11 护手横档（以剑轴为中心，左右对称）
        ".....KTTKKHHHHHHHHHHHHK",          // 12 剑身：与剑柄共用同一条竖轴
        ".....KTTKKHHHHHHHHHHHHK",          // 13
        ".....KTTKHHHHHHHHHHHHHHK",         // 14
        ".....KTTKHHHWKHHHHWKHHHK",         // 15 眼（含高光）
        ".....KTTKHHHKKHHHHKKHHHK",         // 16
        ".....KTTKHHHHHHHHHHHHHHK",         // 17
        ".....KTTKHHHHHHHHHHHHHHK",         // 18
        ".....KTTKHHDHHHHHHHHDHHK",         // 19 凹坑（放在受光面里，免得和眼睛挤成一张嘴）
        ".....KTTKHHHHHHHHHHHHHHK",         // 20
        ".....KTTKBBBBBBBBBBBBBBK",         // 21 受光面 → 基色
        ".....KTTKBBBBBBBBBBBBBBK",         // 22
        ".....KTTKKBBBBBBBBBBBBK",          // 23
        ".....KTTKKSSSSSSSSSSSSK",          // 24 基色 → 背光面
        ".....KTK..KSSSSSSSSSSK",           // 25 剑身收窄
        "......K....KKKKKKKKKK",            // 26 身体底沿 + 剑尖
        "...........KKK....KKK",            // 27 两只小靴
        "...........KOK....KOK",            // 28
        "...........KUK....KUK",            // 29
        "",                                 // 30
        "",                                 // 31
    ], PlayerPalette);

    // ── 天赋星图的节点图标 ───────────────────────────────────────────────
    // 这四个是 **UI 图标**，不是贴地的实体：它们是居中图形，所以 SpriteGen 的"必须贴底边"
    // 那条检查对 `node_` 前缀做了豁免（见 Program.cs）。显示尺寸只有 46px，所以画得很粗：
    // 细过 4 格的东西缩到 46px 就糊了。
    private static readonly Dictionary<char, string> IconPalette = new(PlayerPalette);

    private static SpriteDef Icon(string id, string[] grid) => new(id, grid, IconPalette);

    /// <summary>攻击：一柄竖立的剑——钢色剑身、金色护手与剑首、深色握柄。</summary>
    public static readonly SpriteDef NodeAttack = Icon("node_attack",
    [
        "", "", "", "", "", "",                       // 0-5
        "..............KTTK",                         // 6  剑尖
        ".............KTTTTK",                        // 7
        ".............KTTTTK",                        // 8
        ".............KTTTTK",                        // 9
        ".............KTTTTK",                        // 10
        ".............KTTTTK",                        // 11
        ".............KTTTTK",                        // 12
        ".............KTTTTK",                        // 13
        ".............KTTTTK",                        // 14
        ".............KTTTTK",                        // 15
        ".........KGGGGGGGGGGGGK",                    // 16 护手
        "..............KOOK",                         // 17
        "..............KOOK",                         // 18
        "..............KOOK",                         // 19
        ".............KGGGGK",                        // 20 剑首
        ".............KGGGGK",                        // 21
        "..............KKKK",                         // 22
        "", "", "", "", "", "", "", "", "",           // 23-31
    ]);

    /// <summary>防御：一面盾——玉色盾面、金色盾心。</summary>
    public static readonly SpriteDef NodeDefense = Icon("node_defense",
    [
        "", "", "", "", "", "", "", "",               // 0-7
        "...........KKKKKKKKKK",                      // 8  盾顶
        "..........KLLLLLLLLLLK",                     // 9
        ".........KLLLLLLLLLLLLK",                    // 10
        "........KLLLLLLLLLLLLLLK",                   // 11
        "........KLLLLGGGGGGLLLLK",                   // 12 盾心
        "........KLLLLGGGGGGLLLLK",                   // 13
        "........KLLLLGGGGGGLLLLK",                   // 14
        "........KLLLLLLLLLLLLLLK",                   // 15
        ".........KLLLLLLLLLLLLK",                    // 16
        ".........KLLLLLLLLLLLLK",                    // 17
        "..........KLLLLLLLLLLK",                     // 18
        "...........KLLLLLLLLK",                      // 19
        "............KLLLLLLK",                       // 20
        ".............KLLLLK",                        // 21
        "..............KLLK",                         // 22
        "...............KK",                          // 23
        "", "", "", "", "", "", "", "",               // 24-31
    ]);

    /// <summary>通用：一颗四角星芒（开关 / 解锁类节点）。</summary>
    public static readonly SpriteDef NodeUtility = Icon("node_utility",
    [
        "", "", "", "", "", "", "", "",               // 0-7
        "...............KK",                          // 8
        "..............KGGK",                         // 9
        "..............KGGK",                         // 10
        "..............KGGK",                         // 11
        ".............KGGGGK",                        // 12
        "............KGGWWGGK",                       // 13
        "...........KGGWWWWGGK",                      // 14
        ".........KGGGWWWWWWGGGK",                    // 15 横臂
        ".........KGGGWWWWWWGGGK",                    // 16
        "...........KGGWWWWGGK",                      // 17
        "............KGGWWGGK",                       // 18
        ".............KGGGGK",                        // 19
        "..............KGGK",                         // 20
        "..............KGGK",                         // 21
        "..............KGGK",                         // 22
        "...............KK",                          // 23
        "", "", "", "", "", "", "", "",               // 24-31
    ]);

    /// <summary>特殊：一颗灵核（六边宝石）——花灵核的节点用这个。</summary>
    public static readonly SpriteDef NodeSpecial = Icon("node_special",
    [
        "", "", "", "", "", "", "", "", "", "",       // 0-9
        "............KKKKKKKK",                       // 10
        "..........KGGGGGGGGGGK",                     // 11
        ".........KGGGGGGGGGGGGK",                    // 12
        ".........KGGLLLLLLLLGGK",                    // 13
        ".........KGGWLLLLLLLGGK",                    // 14 高光
        ".........KGGWLLLLLLLGGK",                    // 15
        ".........KGGLLLLLLLLGGK",                    // 16
        ".........KGGLLLLLLLLGGK",                    // 17
        ".........KGGGGGGGGGGGGK",                    // 18
        "..........KGGGGGGGGGGK",                     // 19
        "............KKKKKKKK",                       // 20
        "", "", "", "", "", "", "", "", "", "", "",    // 21-31
    ]);

    /// <summary>
    /// 受击白闪用的伴生图：与本体**逐格同轮廓**，只把所有不透明格换成纯白。
    /// 之所以要一张真图而不是靠 <c>modulate</c>：项目跑在 gl_compatibility（LDR）下，
    /// 顶点色乘不过 1，深色像素提不亮——乘不出一只全白的土豆。
    /// </summary>
    public static SpriteDef WhiteSilhouette(SpriteDef def) => new(
        def.Id + "_flash",
        def.Grid,
        def.Palette.Keys.ToDictionary(ch => ch, _ => Palette.White),
        def.Scale);
}
