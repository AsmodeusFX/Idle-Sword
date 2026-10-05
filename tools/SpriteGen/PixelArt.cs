using System.Text;
using System.Text.RegularExpressions;

namespace IdleSword.SpriteGen;

/// <summary>
/// 像素精灵的定义：字符网格 + 调色板。
///
/// 网格按 <see cref="Cells"/>×<see cref="Cells"/>（默认 32×32）设计，每个格子渲染成
/// Scale×Scale（默认 2×2）的一块，产物仍是 64×64 的 SVG——**与既有素材同尺寸、同底部中心锚点**，
/// 所以引擎侧的导入管线、<c>Sprite()</c> 绘制与命中判定都不需要改。
///
/// 为什么按 32×32 设计：主角在 136px 下显示，64 格等于 2.1px/格（糊），32 格等于 4.25px/格（正是
/// 粗颗粒像素风的读法）；而且 32 字符的行在源码里数得清。
///
/// 约定：'.' 一律表示透明；每行**可以写得比 Cells 短**，右侧自动按透明补齐（省得每行都数到 32）。
/// </summary>
internal sealed record SpriteDef(string Id, string[] Grid, IReadOnlyDictionary<char, string> Palette, int Scale = 2)
{
    public const int Canvas = 64;
    public const char Transparent = '.';
    public int Cells => Canvas / Scale;
}

/// <summary>SVG 里的一条 &lt;rect&gt;（回读校验用）。</summary>
internal sealed record SvgRect(int X, int Y, int W, int H, string Fill);

/// <summary>
/// 网格 ↔ SVG 的互转，以及回读校验。
/// 定义（网格）是唯一真源：写出是纯函数，校验则把磁盘上的文件重新解析回网格，两边必须逐格相等。
/// </summary>
internal static class PixelArt
{
    private static readonly Regex HeaderRe = new(
        "^<svg xmlns=\"http://www\\.w3\\.org/2000/svg\" width=\"(\\d+)\" height=\"(\\d+)\" viewBox=\"0 0 (\\d+) (\\d+)\" shape-rendering=\"crispEdges\">");

    private static readonly Regex RectRe = new(
        "<rect x=\"(\\d+)\" y=\"(\\d+)\" width=\"(\\d+)\" height=\"(\\d+)\" fill=\"(#[0-9a-f]{6})\"/>");

    /// <summary>把网格规整成 Cells 行 × Cells 列，并检查字符都在调色板里。行不足右侧补透明。</summary>
    internal static string[] Normalize(SpriteDef def)
    {
        if (def.Grid.Length != def.Cells)
            throw new InvalidDataException($"{def.Id}: 网格有 {def.Grid.Length} 行，应为 {def.Cells} 行");
        var rows = new string[def.Cells];
        for (int r = 0; r < def.Cells; r++)
        {
            string row = def.Grid[r];
            if (row.Length > def.Cells)
                throw new InvalidDataException($"{def.Id}: 第 {r} 行有 {row.Length} 格，超过 {def.Cells}");
            rows[r] = row.PadRight(def.Cells, SpriteDef.Transparent);
            for (int c = 0; c < def.Cells; c++)
            {
                char ch = rows[r][c];
                if (ch != SpriteDef.Transparent && !def.Palette.ContainsKey(ch))
                    throw new InvalidDataException($"{def.Id}: 第 {r} 行第 {c} 列出现调色板里没有的字符 '{ch}'");
            }
        }
        return rows;
    }

    /// <summary>写出 SVG：逐行把同色的连续格合并成一条 &lt;rect&gt;。纯函数，保证确定性。</summary>
    internal static string ToSvg(SpriteDef def)
    {
        var rows = Normalize(def);
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"64\" viewBox=\"0 0 64 64\" shape-rendering=\"crispEdges\">");
        for (int r = 0; r < def.Cells; r++)
        {
            int c = 0;
            while (c < def.Cells)
            {
                char ch = rows[r][c];
                if (ch == SpriteDef.Transparent) { c++; continue; }
                int start = c;
                while (c < def.Cells && rows[r][c] == ch) c++;
                int w = c - start;
                sb.Append($"<rect x=\"{start * def.Scale}\" y=\"{r * def.Scale}\" width=\"{w * def.Scale}\" height=\"{def.Scale}\" fill=\"{def.Palette[ch]}\"/>");
            }
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// 回读校验一条精灵，返回问题列表（空 = 通过）。
    /// 分四层：① 头部几何；② 每条 rect 合法且 fill 属于本精灵调色板；③ **占用图每格恰好被画一次**
    /// （专抓 run-merge 写错导致的漏画/重画）；④ 展开回网格后与定义逐格相等。
    /// </summary>
    internal static List<string> Verify(SpriteDef def, string svg)
    {
        var problems = new List<string>();
        void Bad(string msg) => problems.Add($"{def.Id}: {msg}");

        var header = HeaderRe.Match(svg);
        if (!header.Success) { Bad("头部不是预期的 64×64 crispEdges 格式"); return problems; }
        for (int i = 1; i <= 4; i++)
            if (header.Groups[i].Value != "64") Bad($"头部第 {i} 个尺寸不是 64");

        // ③ 占用图：每格记录被哪条 rect 画过。-1 = 没画过，>=0 = 画它的 rect 序号。
        var occupied = new int[SpriteDef.Canvas, SpriteDef.Canvas];
        for (int y = 0; y < SpriteDef.Canvas; y++)
            for (int x = 0; x < SpriteDef.Canvas; x++) occupied[x, y] = -1;

        var allowed = def.Palette.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = RectRe.Matches(svg);
        int index = 0;
        foreach (Match m in matches)
        {
            var rect = new SvgRect(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), m.Groups[5].Value);
            bool ok = true;
            if (rect.W <= 0 || rect.H <= 0) { Bad($"第 {index} 条 rect 宽高非正"); ok = false; }
            if (rect.X < 0 || rect.Y < 0 || rect.X + rect.W > SpriteDef.Canvas || rect.Y + rect.H > SpriteDef.Canvas)
            { Bad($"第 {index} 条 rect 越出 64×64"); ok = false; }
            if (!allowed.Contains(rect.Fill)) { Bad($"第 {index} 条 rect 的 fill {rect.Fill} 不在调色板里"); ok = false; }
            if (ok)
                for (int y = rect.Y; y < rect.Y + rect.H; y++)
                    for (int x = rect.X; x < rect.X + rect.W; x++)
                    {
                        if (occupied[x, y] >= 0) Bad($"像素 ({x},{y}) 被第 {occupied[x, y]} 与第 {index} 条 rect 重复绘制");
                        occupied[x, y] = index;
                    }
            index++;
        }

        // ④ 展开回网格：把 64×64 的占用图按 Scale 折叠回 Cells×Cells，与定义逐格比对。
        var rows = Normalize(def);
        for (int r = 0; r < def.Cells && problems.Count == 0; r++)
            for (int c = 0; c < def.Cells; c++)
            {
                char want = rows[r][c];
                int px = occupied[c * def.Scale, r * def.Scale];
                bool painted = px >= 0;
                if ((want == SpriteDef.Transparent) == painted)
                {
                    Bad($"第 {r} 行第 {c} 列：定义是 '{(want == SpriteDef.Transparent ? "透明" : want.ToString())}'，文件里是 '{(painted ? "有" : "无")}'");
                    break;
                }
            }

        if (matches.Count != RectRe.Matches(svg).Count) Bad("存在无法解析的 rect");
        return problems;
    }

    /// <summary>
    /// 把配件层叠在底模上：配件层里非透明的格子覆盖底模，其余保留。
    /// 有了它，"十只怪共用一个轮廓"就只需要维护一张底模 + 几个小配件（角、弓）。
    /// </summary>
    internal static string[] Merge(string[] baseGrid, string[] overlay)
    {
        var result = (string[])baseGrid.Clone();
        for (int r = 0; r < result.Length && r < overlay.Length; r++)
        {
            if (string.IsNullOrEmpty(overlay[r])) continue;
            var chars = result[r].PadRight(SpriteDef.Canvas / 2, SpriteDef.Transparent).ToCharArray();
            for (int c = 0; c < overlay[r].Length && c < chars.Length; c++)
                if (overlay[r][c] != SpriteDef.Transparent) chars[c] = overlay[r][c];
            result[r] = new string(chars).TrimEnd(SpriteDef.Transparent);
        }
        return result;
    }

    /// <summary>内容的最低不透明行（0 = 该格整行为空），用来核对"贴着底部锚点"。</summary>
    internal static int LowestOpaqueRow(SpriteDef def)
    {
        var rows = Normalize(def);
        for (int r = def.Cells - 1; r >= 0; r--)
            if (rows[r].Any(ch => ch != SpriteDef.Transparent)) return r;
        return -1;
    }

    /// <summary>内容的不透明格数，用来挡"生成了一个空精灵"。</summary>
    internal static int OpaqueCells(SpriteDef def) =>
        Normalize(def).Sum(row => row.Count(ch => ch != SpriteDef.Transparent));
}
