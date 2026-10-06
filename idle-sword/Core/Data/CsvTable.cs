using System.Globalization;
using System.Text;

namespace IdleSword.Core;

/// <summary>标准引号 CSV 读写；保留行号供配置校验定位，支持 BOM、逗号、换行与双引号转义。</summary>
public sealed class CsvRow(string file, int line, Dictionary<string, string> cells)
{
    public string File { get; } = file;
    public int Line { get; } = line;
    public string Text(string field) => cells.TryGetValue(field, out var value)
        ? value : throw Error(field, "缺少字段");
    public double Number(string field)
    {
        if (double.TryParse(Text(field), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)) return value;
        throw Error(field, "需要有限数值");
    }
    public int Int(string field)
    {
        if (int.TryParse(Text(field), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        throw Error(field, "需要整数");
    }
    public bool Flag(string field) => Text(field) switch { "1" => true, "0" => false, _ => throw Error(field, "需要 0 或 1") };
    /// <summary>
    /// 按 `|` 切分的字符串列表。**空串或纯空白 → 空列表**——"整列留空"在配置里是合法写法
    /// （例：TalentLayout 的 `prereq` 留空 = 它是根节点）。每一项都会 Trim，空项直接丢掉，
    /// 所以 `a||b` 与 `a|b` 等价。
    /// </summary>
    public List<string> TextList(string field) => Text(field)
        .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .ToList();
    /// <summary>按 `|` 切分的数值列表。逐项解析，报错会带上那一项的实际内容，便于定位是第几个写错了。</summary>
    public List<double> NumberList(string field)
    {
        var result = new List<double>();
        foreach (string item in TextList(field))
        {
            if (!double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw Error(field, $"需要有限数值，实际是 '{item}'");
            result.Add(value);
        }
        return result;
    }
    public InvalidDataException Error(string field, string message) => new($"{File}:{Line} [{field}] {message}");
}

public static class CsvTable
{
    public static List<CsvRow> Parse(string file, string text)
    {
        var records = new List<(int Line, List<string> Cells)>();
        var cells = new List<string>();
        var value = new StringBuilder();
        bool quoted = false, closed = false;
        int line = 1, start = 1;
        text = text.TrimStart('\uFEFF');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                    else { quoted = false; closed = true; }
                }
                else { value.Append(c); if (c == '\n') line++; }
                continue;
            }
            if (c == '"' && value.Length == 0 && !closed) { quoted = true; continue; }
            if (c == ',' || c == '\r' || c == '\n')
            {
                cells.Add(value.ToString()); value.Clear(); closed = false;
                if (c == ',') continue;
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                if (cells.Count > 1 || cells[0].Length > 0) records.Add((start, cells));
                cells = []; line++; start = line;
            }
            else
            {
                if (closed || c == '"') throw new InvalidDataException($"{file}:{line} CSV 引号格式错误");
                value.Append(c);
            }
        }
        if (quoted) throw new InvalidDataException($"{file}:{start} CSV 引号未闭合");
        if (value.Length > 0 || cells.Count > 0 || closed) { cells.Add(value.ToString()); records.Add((start, cells)); }
        if (records.Count == 0) throw new InvalidDataException($"{file}: 缺少表头");
        var headers = records[0].Cells;
        if (headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct().Count() != headers.Count)
            throw new InvalidDataException($"{file}:1 表头为空或重复");
        var result = new List<CsvRow>();
        foreach (var record in records.Skip(1))
        {
            if (record.Cells.Count != headers.Count) throw new InvalidDataException($"{file}:{record.Line} 列数与表头不一致");
            result.Add(new CsvRow(file, record.Line, headers.Zip(record.Cells).ToDictionary(x => x.First, x => x.Second)));
        }
        return result;
    }

    public static string Write(IEnumerable<IEnumerable<string>> rows) => string.Join("\n", rows.Select(row =>
        string.Join(",", row.Select(cell => cell.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell)))) + "\n";
}
