namespace IdleSword.SpriteGen;

/// <summary>
/// 全族共用的调色板常量。精灵定义只引用这里，改一处就换掉一整族——这正是让主角、怪物、
/// 剑灵读成"同一套美术"的手段（<c>--verify</c> 可以据此断言每个实体精灵都含统一描边色）。
///
/// 配色口径：**描边比压暗后的背景更暗**，填充则统一提亮。背景走 `#0f1c26` 一档的冷暗色，
/// 所以暖色系的填充能直接把实体从背景里拽出来——旧素材读不出轮廓，就是因为它和背景同色相。
/// </summary>
internal static class Palette
{
    /// <summary>统一描边。比背景最暗处（#0a141a）还暗一档，保证在暗背景上依然是黑边。</summary>
    public const string Outline = "#101823";
    /// <summary>纯白：眼睛高光、剑刃反光、受击白闪的伴生图。</summary>
    public const string White = "#ffffff";

    // 主角土豆：暖象牙白一系。刻意用背景完全没有的暖色，明度也拉到全场最高。
    public const string SkinHi = "#fdf6e3";
    public const string SkinBase = "#f2e2b8";
    public const string SkinShade = "#cdb07e";
    public const string SkinDeep = "#a98c60";

    // 点缀：直接复用 UI 的玉色与金色，让角色和界面互相呼应。
    public const string Leaf = "#8cc4b1";
    public const string LeafVein = "#5f9a89";
    public const string Gold = "#d9bc82";
    public const string Steel = "#dae6ee";

    public const string Boot = "#3a3f4a";
    public const string BootCuff = "#5a6270";

    // 怪物共用的五官色：沿用旧素材的取值，免得换皮之后认不出是同一只怪。
    public const string Eye = "#142739";
    public const string Glint = "#e9d8ab";
    public const string Mouth = "#233044";
}
