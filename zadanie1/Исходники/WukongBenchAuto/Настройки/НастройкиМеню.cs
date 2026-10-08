using System.Text.RegularExpressions;

namespace WukongBenchAuto.Settings;

// Строка UISettingData из ini, по сути состояние меню бенча:
// (("ScreenMode", "1"),("ScreenResolution", "0"),...)
// Порядок пар не трогаем, новые дописываем в конец.
internal sealed class UiSettingData
{
    private static readonly Regex PairRegex = new("\\(\\s*\"([^\"]*)\"\\s*,\\s*\"([^\"]*)\"\\s*\\)", RegexOptions.Compiled);

    private readonly List<KeyValuePair<string, string>> _pairs = new();

    public static UiSettingData Parse(string? raw)
    {
        var data = new UiSettingData();
        if (string.IsNullOrWhiteSpace(raw)) return data;
        foreach (Match m in PairRegex.Matches(raw))
            data._pairs.Add(new(m.Groups[1].Value, m.Groups[2].Value));
        return data;
    }

    public string? this[string key]
    {
        get => _pairs.FirstOrDefault(p => p.Key == key).Value;
        set
        {
            var i = _pairs.FindIndex(p => p.Key == key);
            if (value is null)
            {
                if (i >= 0) _pairs.RemoveAt(i);
            }
            else if (i >= 0) _pairs[i] = new(key, value);
            else _pairs.Add(new(key, value));
        }
    }

    public override string ToString() =>
        "(" + string.Join(",", _pairs.Select(p => $"(\"{p.Key}\", \"{p.Value}\")")) + ")";
}
