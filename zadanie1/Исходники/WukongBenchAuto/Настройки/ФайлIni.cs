using System.Text;

namespace WukongBenchAuto.Settings;

// Простой редактор ini для UE: меняем только свои ключи, порядок строк, комменты и кодировку не трогаем,
// чтобы остальные настройки игры остались как были.
internal sealed class IniFile
{
    private readonly List<string> _lines;
    private readonly Encoding _encoding;

    private IniFile(List<string> lines, Encoding encoding)
    {
        _lines = lines;
        _encoding = encoding;
    }

    public static IniFile Empty() => new(new List<string>(), new UTF8Encoding(false));

    public static IniFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Encoding encoding = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode
            : bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? new UTF8Encoding(true)
            : new UTF8Encoding(false);

        using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return new IniFile(lines, encoding);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join("\r\n", _lines) + "\r\n", _encoding);
        File.Move(tmp, path, overwrite: true);
    }

    public string? Get(string section, string key)
    {
        var (start, end) = FindSection(section);
        if (start < 0) return null;
        var i = FindKey(start, end, key);
        return i < 0 ? null : _lines[i][(_lines[i].IndexOf('=') + 1)..];
    }

    public void Set(string section, string key, string value)
    {
        var (start, end) = FindSection(section);
        if (start < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        var i = FindKey(start, end, key);
        if (i >= 0)
        {
            _lines[i] = $"{key}={value}";
            return;
        }

        // новый ключ - в конец секции, перед пустыми строками
        var insertAt = end;
        while (insertAt > start + 1 && _lines[insertAt - 1].Trim().Length == 0) insertAt--;
        _lines.Insert(insertAt, $"{key}={value}");
    }

    // (строка с [секцией], строка сразу после конца секции)
    private (int Start, int End) FindSection(string section)
    {
        var header = $"[{section}]";
        var start = _lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return (-1, -1);

        var end = start + 1;
        while (end < _lines.Count && !_lines[end].TrimStart().StartsWith('[')) end++;
        return (start, end);
    }

    private int FindKey(int start, int end, string key)
    {
        for (var i = start + 1; i < end; i++)
        {
            var eq = _lines[i].IndexOf('=');
            if (eq > 0 && _lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}
