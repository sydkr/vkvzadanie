using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using WukongBenchAuto.Results;
using WukongBenchAuto.Running;
using WukongBenchAuto.Settings;
using WukongBenchAuto.SystemInfo;

namespace WukongBenchAuto.Reporting;

internal sealed class PassReport
{
    public required BenchmarkProfile Profile { get; init; }
    public RunOutcome? Outcome { get; init; }
    // температура GPU за тест; null, если не NVIDIA или нет nvidia-smi
    public GpuThermalStats? Thermal { get; init; }
    public BenchmarkResult? Result => Outcome?.Result;
    public List<string> Warnings { get; } = new();
}

internal sealed class SessionReport
{
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public required SystemSnapshot System { get; init; }
    public string? BenchmarkExe { get; init; }
    public PassReport? Cpu { get; set; }
    public PassReport? Gpu { get; set; }

    private IEnumerable<PassReport> Passes => new[] { Cpu, Gpu }.Where(p => p is not null)!;

    // Проверяем, что бенч реально применил то, что мы записали в ini (в результате он пишет свои настройки).
    public static void Verify(PassReport pass)
    {
        var r = pass.Result;
        if (r is null) return;
        var p = pass.Profile;

        void Check(string what, int? actual, int expected)
        {
            if (actual is { } a && a != expected)
                pass.Warnings.Add($"{what}: ожидалось {expected}, бенчмарк применил {a}.");
        }

        Check("Пресет качества (QualityLevel)", r.AppliedInt("QualityLevel"), p.Quality);
        // в результате Rtx = уровень (0 выкл, 1..3), а в ini это просто 0/1
        Check("Трассировка лучей (Rtx, уровень)", r.AppliedInt("Rtx"), p.RayTracing ? p.RayTracingLevel : 0);
        Check("Апскейлер (Dlss, 3 = TSR)", r.AppliedInt("Dlss"), BenchmarkProfile.UpscalerTsr);
        Check("Размытие в движении (MotionBlur)", r.AppliedInt("MotionBlur"), p.MotionBlur);
        Check("Генерация кадров (InsertFrame)", r.AppliedInt("InsertFrame"), 0);
        Check("Масштаб рендеринга (ImageQuality, %)", r.AppliedInt("ImageQuality"), p.RenderScale);

        // если больше 10% сцены карта троттлила от перегрева, FPS уже больше про охлаждение ноута, чем про сам GPU
        if (pass.Thermal is { } t && r.SceneDuration is { } scene && t.ThermalSlowdown.TotalSeconds > 0.1 * scene.TotalSeconds)
            pass.Warnings.Add($"Видеокарта перегревалась (до {t.MaxTemperatureC} °C) и снижала частоты " +
                              $"{t.ThermalSlowdown.TotalSeconds:0} с из ~{scene.TotalSeconds:0} с сцены — результат занижен. " +
                              "Дайте компьютеру остыть, включите режим максимальной производительности охлаждения и повторите тест.");

        if (r.VideoMemoryOverflow)
            pass.Warnings.Add($"Видеопамять переполнена ({r.VideoMemoryPeakGb:0.0} из {r.VideoMemoryTotalGb:0} ГБ, загрузка GPU " +
                              $"{r.GpuUsageAvg:0}%): FPS ограничен нехваткой VRAM. Закройте программы, использующие видеокарту, " +
                              "или запустите с --no-rt.");

        var m = Regex.Match(r.Applied("ScreenResolution") ?? "", @"(\d+)\D+(\d+)");
        if (m.Success && (m.Groups[1].Value != p.Width.ToString(CultureInfo.InvariantCulture) ||
                          m.Groups[2].Value != p.Height.ToString(CultureInfo.InvariantCulture)))
        {
            pass.Warnings.Add($"Разрешение: ожидалось {p.Width}×{p.Height}, бенчмарк применил {r.Applied("ScreenResolution")}. " +
                              "Если это CPU-тест, укажите правильный пункт меню: --cpu-res-index <n>.");
        }
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Black Myth: Wukong Benchmark Tool — отчёт");
        sb.AppendLine();
        sb.AppendLine($"Дата: {StartedAt:yyyy-MM-dd HH:mm}  ");
        var gameVersion = Passes.Select(p => p.Result?.GameVersion).FirstOrDefault(v => v is not null);
        if (gameVersion is not null) sb.AppendLine($"Версия бенчмарка: {gameVersion}  ");
        if (BenchmarkExe is not null) sb.AppendLine($"Исполняемый файл: `{BenchmarkExe}`");
        sb.AppendLine();

        sb.AppendLine("## Характеристики компьютера");
        sb.AppendLine();
        var sys = new MarkdownTable("Параметр", "Значение");
        foreach (var (name, value) in System.Rows) sys.Row(name, value);
        var benchGpu = Passes.Select(p => p.Result).FirstOrDefault(r => r?.GpuModel is not null);
        if (benchGpu is not null)
            sys.Row("GPU по данным бенчмарка", $"{benchGpu.GpuModel}, {benchGpu.VideoMemSize}");
        sb.Append(sys).AppendLine();

        sb.AppendLine("## Результаты");
        sb.AppendLine();
        sb.Append(ResultsTable()).AppendLine();
        sb.AppendLine("FPS 95% — значение, ниже которого опускаются 5% замеров (по данным бенчмарка). " +
                      "1% low, медиана, время кадра и доля CPU/GPU-bound рассчитаны инструментом по покадровым записям (Records).");
        sb.AppendLine();

        var conclusions = Conclusions().ToList();
        if (conclusions.Count > 0)
        {
            sb.AppendLine("### Выводы");
            sb.AppendLine();
            foreach (var c in conclusions) sb.AppendLine($"- {c}");
            sb.AppendLine();
        }

        sb.AppendLine("## Настройки");
        sb.AppendLine();
        sb.Append(SettingsTable()).AppendLine();
        sb.AppendLine("¹ Дальность прорисовки, сглаживание, постобработка, тени, текстуры, эффекты, материалы, " +
                      "растительность, глобальное освещение, отражения. Строки «Применено бенчмарком» — значения из файла результата.");
        sb.AppendLine();

        foreach (var pass in Passes)
        {
            sb.AppendLine($"### Почему такие настройки: {pass.Profile.Title}");
            sb.AppendLine();
            foreach (var line in pass.Profile.Rationale) sb.AppendLine($"- {line}");
            foreach (var note in pass.Profile.Notes) sb.AppendLine($"- **Примечание:** {note}");
            sb.AppendLine();
        }

        var warnings = Passes.SelectMany(p => p.Warnings.Select(w => $"{p.Profile.Title}: {w}")).ToList();
        if (warnings.Count > 0)
        {
            sb.AppendLine("## Предупреждения");
            sb.AppendLine();
            foreach (var w in warnings) sb.AppendLine($"- {w}");
            sb.AppendLine();
        }

        sb.AppendLine("## Исходные файлы результатов");
        sb.AppendLine();
        foreach (var pass in Passes)
            sb.AppendLine($"- {pass.Profile.Title}: {(pass.Result is null ? $"нет ({pass.Outcome?.Error ?? "не запускался"})" : $"`{pass.Result.SourcePath}`")}");
        return sb.ToString();
    }

    private string ResultsTable()
    {
        var t = new MarkdownTable("Метрика", "CPU-тест", "GPU-тест");
        var cpu = Cpu?.Result;
        var gpu = Gpu?.Result;
        void Row(string name, Func<BenchmarkResult, double?> f, string format, string unit = "") =>
            t.Row(name, Fmt(cpu, f, format, unit), Fmt(gpu, f, format, unit));

        t.Row("Статус", Status(Cpu), Status(Gpu));
        Row("Средний FPS", r => r.FpsAvg, "0.0");
        Row("Минимальный FPS", r => r.FpsMin, "0.0");
        Row("Максимальный FPS", r => r.FpsMax, "0.0");
        Row("FPS 95%", r => r.Fps95, "0.0");
        Row("1% low FPS", r => r.Fps1Low, "0.0");
        Row("Медианный FPS", r => r.FpsMedian, "0.0");
        Row("Среднее время кадра CPU", r => r.CpuFrameTimeAvg, "0.00", " мс");
        Row("Среднее время кадра GPU", r => r.GpuFrameTimeAvg, "0.00", " мс");
        Row("Потолок FPS по времени кадра CPU", r => r.CpuFpsCeiling, "0");
        Row("Потолок FPS по времени кадра GPU", r => r.GpuFpsCeiling, "0");
        Row("Кадров, ограниченных CPU", r => r.CpuBoundShare, "0", "%");
        Row("Кадров, ограниченных GPU", r => r.GpuBoundShare, "0", "%");
        // у меня бенч пишет загрузку CPU = 1 (видимо, не умеет её мерить), такое не показываем
        Row("Средняя загрузка CPU (по данным бенчмарка)", r => r.CpuUsageAvg > 1 ? r.CpuUsageAvg : null, "0.0", "%");
        Row("Средняя загрузка GPU", r => r.GpuUsageAvg, "0.0", "%");
        Row("Видеопамять (средняя)", r => r.VideoMemGb, "0.0", " ГБ");
        Row("Видеопамять (пик)", r => r.VideoMemoryPeakGb, "0.0", " ГБ");
        Row("Длительность сцены", r => r.SceneDuration?.TotalSeconds, "0", " с");
        Row("Замеров в Records", r => r.Records.Count, "0");
        t.Row("Температура GPU: старт → максимум", Temperature(Cpu), Temperature(Gpu));
        t.Row("Снижение частот GPU из-за перегрева", Throttle(Cpu), Throttle(Gpu));
        t.Row("Длительность прохода (с запуском)", Duration(Cpu), Duration(Gpu));
        return t.ToString();
    }

    private string SettingsTable()
    {
        var t = new MarkdownTable("Параметр", "CPU-тест", "GPU-тест");
        var cpu = Cpu?.Profile.Describe();
        var gpu = Gpu?.Profile.Describe();
        var names = (cpu ?? gpu ?? Array.Empty<(string, string)>()).Select(x => x.Item1).ToList();
        foreach (var name in names)
            t.Row(name, cpu?.First(x => x.Name == name).Value, gpu?.First(x => x.Name == name).Value);

        // что бенч применил на самом деле - из файла результата
        var keys = new[] { Cpu?.Result, Gpu?.Result }
            .Where(r => r is not null)
            .SelectMany(r => r!.AppliedSettings.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var key in keys)
            t.Row($"Применено бенчмарком: {key}", Cpu?.Result?.Applied(key), Gpu?.Result?.Applied(key));
        return t.ToString();
    }

    private IEnumerable<string> Conclusions()
    {
        if (Cpu?.Result is { CpuBoundShare: { } cpuBound } cpu)
        {
            yield return cpuBound >= 80
                ? $"CPU-тест корректен: {cpuBound:0}% кадров ограничены процессором, средний FPS {cpu.FpsAvg:0.0} отражает производительность CPU."
                : $"В CPU-тесте {cpuBound:0}% кадров ограничены процессором: даже при рендере {Cpu!.Profile.RenderWidth}×{Cpu.Profile.RenderHeight} " +
                  $"время кадра GPU ({cpu.GpuFrameTimeAvg:0.0} мс) больше времени CPU ({cpu.CpuFrameTimeAvg:0.0} мс) — у видеокарты " +
                  "есть не зависящая от разрешения нагрузка (типично для мобильных/слабых GPU). Средний FPS CPU-теста — оценка снизу; " +
                  $"по времени кадра процессор способен примерно на {cpu.CpuFpsCeiling:0} FPS.";
        }
        if (Gpu?.Result is { GpuBoundShare: { } gpuBound } gpu)
        {
            yield return gpu.VideoMemoryOverflow
                ? $"GPU-тест упёрся в объём видеопамяти ({gpu.VideoMemoryPeakGb:0.0} из {gpu.VideoMemoryTotalGb:0} ГБ, загрузка GPU " +
                  $"{gpu.GpuUsageAvg:0}%) — результат занижен нехваткой VRAM."
                : gpuBound >= 80
                ? $"GPU-тест корректен: {gpuBound:0}% кадров ограничены видеокартой, средняя загрузка GPU {gpu.GpuUsageAvg:0}%."
                : $"В GPU-тесте только {gpuBound:0}% кадров ограничены видеокартой — проверьте, не ограничен ли FPS процессором или лимитом.";
        }
        if (Cpu?.Result?.FpsAvg is { } c && Gpu?.Result?.FpsAvg is { } g && g > 0)
            yield return $"Результат CPU-теста ({c:0} FPS) в {c / g:0.0} раза выше результата видеокарты на максимальных настройках ({g:0} FPS).";
    }

    private static string Status(PassReport? p) => p switch
    {
        null => "не запускался",
        { Result: not null } => "OK",
        _ => $"ошибка: {p.Outcome?.Error ?? "нет результата"}",
    };

    private static string? Temperature(PassReport? p) =>
        p?.Thermal is { } t ? $"{t.StartTemperatureC} → {t.MaxTemperatureC} °C" : null;

    private static string? Throttle(PassReport? p) =>
        p?.Thermal is { } t ? $"{t.ThermalSlowdown.TotalSeconds:0} с" : null;

    private static string? Duration(PassReport? p) =>
        p?.Outcome is { Duration.TotalSeconds: > 0 } o ? $"{(int)o.Duration.TotalMinutes}:{o.Duration.Seconds:00}" : null;

    private static string? Fmt(BenchmarkResult? r, Func<BenchmarkResult, double?> f, string format, string unit) =>
        r is not null && f(r) is { } v ? v.ToString(format, CultureInfo.InvariantCulture) + unit : null;

    public string ToJson()
    {
        object? Pass(PassReport? p) => p is null ? null : new
        {
            p.Profile.Title,
            Settings = p.Profile.Describe().ToDictionary(x => x.Name, x => x.Value),
            p.Profile.Notes,
            Success = p.Result is not null,
            p.Outcome?.Error,
            DurationSeconds = p.Outcome?.Duration.TotalSeconds,
            Metrics = p.Result is not { } r ? null : new
            {
                r.FpsAvg, r.FpsMin, r.FpsMax, r.Fps95, r.Fps1Low, r.FpsMedian,
                r.CpuFrameTimeAvg, r.GpuFrameTimeAvg, r.CpuFpsCeiling, r.GpuFpsCeiling, r.CpuBoundShare, r.GpuBoundShare,
                r.CpuUsageAvg, r.GpuUsageAvg, r.VideoMemGb, r.VideoMemoryPeakGb,
                SceneSeconds = r.SceneDuration?.TotalSeconds, Samples = r.Records.Count,
            },
            AppliedSettings = p.Result?.AppliedSettings,
            ResultFile = p.Result?.SourcePath,
            Thermal = p.Thermal is not { } th ? null : new
            {
                th.StartTemperatureC, th.MaxTemperatureC,
                ThermalSlowdownSeconds = th.ThermalSlowdown.TotalSeconds, PowerCapSeconds = th.PowerCap.TotalSeconds,
            },
            p.Warnings,
        };

        var doc = new
        {
            StartedAt,
            BenchmarkExe,
            System = System.Rows.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(x => x.Value))),
            Cpu = Pass(Cpu),
            Gpu = Pass(Gpu),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
