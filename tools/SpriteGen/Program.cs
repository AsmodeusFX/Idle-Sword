using System.Text;
using IdleSword.SpriteGen;

// 像素精灵生成器（产物对应 idle-sword/Assets/Sprites/*.svg）。
//   dotnet run --project tools/SpriteGen -- [输出目录]           生成全部素材
//   dotnet run --project tools/SpriteGen -- [输出目录] --verify   只回读校验，不重新生成
// 生成后必须跑一次 `godot --headless --path idle-sword --import`，否则引擎侧没有 .import，
// 运行时 GD.Load 会拿到 null（与 SoundGen 同一个坑）。
string output = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "idle-sword/Assets/Sprites";
bool verifyOnly = args.Contains("--verify");

SpriteDef[] sprites =
[
    Sprites.Player,
    Sprites.WhiteSilhouette(Sprites.Player),
    .. Monsters.All(),
];

int failures = 0;
Console.WriteLine($"{(verifyOnly ? "校验" : "生成")}目录: {Path.GetFullPath(output)}");
Console.WriteLine($"{"素材",-20}{"rect",6}{"字节",8}  判定");
foreach (var def in sprites)
{
    string path = Path.Combine(output, def.Id + ".svg");
    string svg;
    try
    {
        svg = PixelArt.ToSvg(def);
    }
    catch (InvalidDataException ex)
    {
        // 网格本身写错了（行数不对、超出宽度、用了调色板没有的字符）：直接报，不要写坏文件。
        failures++;
        Console.WriteLine($"{def.Id,-20}{"—",6}{"—",8}  {ex.Message}");
        continue;
    }

    if (!verifyOnly)
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(path, svg + "\n", new UTF8Encoding(false));
    }

    string note = "";
    if (!File.Exists(path)) note = "文件缺失";
    else
    {
        string onDisk = File.ReadAllText(path).TrimEnd('\n');
        note = onDisk != svg
            // 逐字节比对：既能抓"有人手改了 SVG"，也能证明写出是确定性的。
            ? "与定义不一致（被手改过？）"
            : FirstProblem(def, onDisk);
    }

    int rects = System.Text.RegularExpressions.Regex.Matches(svg, "<rect ").Count;
    if (note != "") failures++;
    Console.WriteLine($"{def.Id,-20}{rects,6}{svg.Length + 1,8}  {(note == "" ? "OK" : note)}");
}

static string FirstProblem(SpriteDef def, string svg)
{
    foreach (var problem in PixelArt.Verify(def, svg)) return problem;

    // 内容必须真的存在，且贴着底部锚点——锚点是"底部中心"，空精灵或浮在中间的精灵都会画歪。
    if (PixelArt.OpaqueCells(def) == 0) return "空精灵";
    int lowest = (PixelArt.LowestOpaqueRow(def) + 1) * def.Scale;
    if (lowest < 52) return $"内容离底边太远（最低像素 y={lowest}），会浮在地面之上";

    // 全族统一描边：白闪伴生图是纯白剪影，按定义不参与这条。
    if (!def.Id.EndsWith("_flash") && !def.Palette.Values.Contains(Palette.Outline))
        return "缺少全族统一的描边色";
    return "";
}

Console.WriteLine(failures == 0
    ? $"全部 {sprites.Length} 个素材通过。"
    : $"{failures} 个素材有问题。");
return failures == 0 ? 0 : 1;
