using System.Text;

namespace WukongBenchAuto.Reporting;

// Колонки выравниваю пробелами, чтобы таблица нормально читалась и в консоли, и в .md
internal sealed class MarkdownTable
{
    private readonly string[] _headers;
    private readonly List<string[]> _rows = new();

    public MarkdownTable(params string[] headers) => _headers = headers;

    public MarkdownTable Row(params string?[] cells)
    {
        _rows.Add(cells.Select(c => (c ?? "—").Replace("|", "\\|").Replace("\n", " ")).ToArray());
        return this;
    }

    public override string ToString()
    {
        var widths = _headers.Select((h, i) => Math.Max(h.Length, _rows.Select(r => i < r.Length ? r[i].Length : 0).DefaultIfEmpty(0).Max())).ToArray();
        var sb = new StringBuilder();
        void Line(IReadOnlyList<string> cells) =>
            sb.Append("| ").Append(string.Join(" | ", widths.Select((w, i) => (i < cells.Count ? cells[i] : "").PadRight(w)))).AppendLine(" |");

        Line(_headers);
        sb.Append('|').Append(string.Join("|", widths.Select(w => new string('-', w + 2)))).AppendLine("|");
        foreach (var row in _rows) Line(row);
        return sb.ToString();
    }
}
