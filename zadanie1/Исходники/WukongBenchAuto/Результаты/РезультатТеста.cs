using System.Globalization;
using System.Text.Json;

namespace WukongBenchAuto.Results;

// одна запись из Records (бенч пишет её примерно на каждый кадр)
internal sealed record FrameRecord(
    double TimeStampMs, double FrameRate, double CpuUsage, double GpuUsage, double CpuFrameTime, double GpuFrameTime, double VideoMemoryGb);

// Файл результата бенча: %TEMP%\b1\BenchMarkHistory\Tool\<unix-время>, внутри json без расширения.
// Имена полей читаю без учёта регистра, если поля нет - null.
internal sealed class BenchmarkResult
{
    public required string SourcePath { get; init; }

    // это считает сам бенч
    public double? FpsAvg { get; init; }
    public double? FpsMin { get; init; }
    public double? FpsMax { get; init; }
    public double? Fps95 { get; init; }
    public double? CpuUsageAvg { get; init; }
    public double? GpuUsageAvg { get; init; }
    public double? VideoMemGb { get; init; }

    // система глазами бенча
    public string? GameVersion { get; init; }
    public string? OsVersion { get; init; }
    public string? CpuModel { get; init; }
    public string? GpuModel { get; init; }
    public string? GpuDriver { get; init; }
    public string? VideoMemSize { get; init; }
    public string? SystemMemory { get; init; }

    // всё остальное из json (кроме метрик и инфы о системе) - настройки, с которыми реально шёл тест
    public required IReadOnlyDictionary<string, string> AppliedSettings { get; init; }

    public required IReadOnlyList<FrameRecord> Records { get; init; }

    // а это считаем сами по Records
    public double? Fps1Low => Percentile(Records.Select(r => r.FrameRate), 1);
    public double? FpsMedian => Percentile(Records.Select(r => r.FrameRate), 50);
    public double? CpuFrameTimeAvg => Average(Records.Select(r => r.CpuFrameTime));
    public double? GpuFrameTimeAvg => Average(Records.Select(r => r.GpuFrameTime));

    // VideoMemSize приходит строкой: "4GB", "12 GB"
    public double? VideoMemoryTotalGb =>
        VideoMemSize is { } s && System.Text.RegularExpressions.Regex.Match(s, @"[\d.]+") is { Success: true } m &&
        double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) && gb > 0
            ? gb
            : null;

    // Видеопамяти не хватило: пик >= 95% объёма и при этом GPU загружен меньше 90%.
    // Данные начинают гоняться через обычную RAM, карта ждёт, и FPS показывает нехватку VRAM, а не скорость GPU.
    // Пороги по моей 3050: с RT 3.89 из 4 ГБ и 74-84% загрузки, без RT 3.8 ГБ и 99%.
    public bool VideoMemoryOverflow =>
        VideoMemoryPeakGb is { } peak && VideoMemoryTotalGb is { } total && peak >= 0.95 * total &&
        GpuUsageAvg is { } usage && usage < 90;

    // сколько FPS дал бы CPU, если бы видеокарта не тормозила (1000 / время кадра)
    public double? CpuFpsCeiling => CpuFrameTimeAvg is > 0 and var t ? 1000 / t : null;
    public double? GpuFpsCeiling => GpuFrameTimeAvg is > 0 and var t ? 1000 / t : null;
    public double? VideoMemoryPeakGb => Records.Count == 0 ? null : Records.Max(r => r.VideoMemoryGb);

    // TimeStamp в записях - мс unix-времени
    public TimeSpan? SceneDuration =>
        Records.Count > 1 && Records[^1].TimeStampMs > Records[0].TimeStampMs
            ? TimeSpan.FromMilliseconds(Records[^1].TimeStampMs - Records[0].TimeStampMs)
            : null;

    // % кадров, где CPU считал дольше GPU, т.е. упёрлись в процессор
    public double? CpuBoundShare => Records.Count == 0
        ? null
        : 100.0 * Records.Count(r => r.CpuFrameTime > r.GpuFrameTime) / Records.Count;

    public double? GpuBoundShare => CpuBoundShare is { } cpu ? 100 - cpu : null;

    public int? AppliedInt(string key) =>
        AppliedSettings.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : null;

    public string? Applied(string key) => AppliedSettings.TryGetValue(key, out var v) ? v : null;

    private static readonly HashSet<string> NonSettingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "TimeStamp", "BenckMarkVersion", "GPULeak",
        "FPSAvg", "FPSMax", "FPSMin", "FPS95", "CPUAvg", "GPUAvg", "VideoMem", "GameVer", "SysVer",
        "CPUModel", "GPUModel", "GpuDriverVer", "VideoMemSize", "SysMem", "Records",
    };

    // null - если это не результат бенча или игра ещё не дописала файл
    public static BenchmarkResult? TryLoad(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
            return FromJson(doc.RootElement, path);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static BenchmarkResult? FromJson(JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var props = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in root.EnumerateObject()) props[p.Name] = p.Value;
        if (!props.ContainsKey("FPSAvg")) return null;

        double? Num(string key) => props.TryGetValue(key, out var e) ? ToDouble(e) : null;
        string? Text(string key) => props.TryGetValue(key, out var e) ? ToText(e) : null;

        var settings = props
            .Where(p => !NonSettingKeys.Contains(p.Key) && p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            .ToDictionary(p => p.Key, p => ToText(p.Value) ?? "", StringComparer.OrdinalIgnoreCase);

        var records = new List<FrameRecord>();
        if (props.TryGetValue("Records", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in array.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                var fields = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in r.EnumerateObject())
                    if (ToDouble(p.Value) is { } d) fields[p.Name] = d;
                double Field(string key) => fields.TryGetValue(key, out var v) ? v : 0;
                records.Add(new FrameRecord(Field("TimeStamp"), Field("FrameRate"), Field("CPUUsage"), Field("GPUUsage"),
                    Field("CPUFrameTime"), Field("GPUFrameTime"), Field("VideoMemoryUsage")));
            }
        }

        return new BenchmarkResult
        {
            SourcePath = path,
            FpsAvg = Num("FPSAvg"),
            FpsMin = Num("FPSMin"),
            FpsMax = Num("FPSMax"),
            Fps95 = Num("FPS95"),
            CpuUsageAvg = Num("CPUAvg"),
            GpuUsageAvg = Num("GPUAvg"),
            VideoMemGb = Num("VideoMem"),
            GameVersion = Text("GameVer"),
            OsVersion = Text("SysVer"),
            CpuModel = Text("CPUModel"),
            GpuModel = Text("GPUModel"),
            GpuDriver = Text("GpuDriverVer"),
            VideoMemSize = Text("VideoMemSize"),
            SystemMemory = Text("SysMem"),
            AppliedSettings = settings,
            Records = records,
        };
    }

    private static double? ToDouble(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static string? ToText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()?.Trim(),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        _ => null,
    };

    private static double? Average(IEnumerable<double> values)
    {
        var list = values.Where(v => v > 0).ToList();
        return list.Count == 0 ? null : list.Average();
    }

    private static double? Percentile(IEnumerable<double> values, double percent)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;
        var rank = percent / 100.0 * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
