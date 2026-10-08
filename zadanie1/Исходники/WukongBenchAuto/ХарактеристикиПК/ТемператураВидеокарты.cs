using System.Diagnostics;
using System.Globalization;

namespace WukongBenchAuto.SystemInfo;

// температура + счётчики троттлинга из драйвера (мкс, копятся с загрузки системы)
internal sealed record GpuSensorSample(int TemperatureC, long ThermalSlowdownUs, long PowerCapUs);

// итог по одному тесту
internal sealed record GpuThermalStats(int StartTemperatureC, int MaxTemperatureC, TimeSpan ThermalSlowdown, TimeSpan PowerCap);

// Датчики читаем через nvidia-smi, он ставится с драйвером NVIDIA.
// На AMD/Intel его нет - тогда просто без контроля температуры.
internal static class NvidiaSmi
{
    private const string Query =
        "temperature.gpu," +
        "clocks_event_reasons_counters.sw_thermal_slowdown,clocks_event_reasons_counters.hw_thermal_slowdown," +
        "clocks_event_reasons_counters.sw_power_cap,clocks_event_reasons_counters.hw_power_brake_slowdown";

    private static readonly string? ExePath = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
        }
        .FirstOrDefault(File.Exists);

    public static bool Available => ExePath is not null && Sample() is not null;

    public static GpuSensorSample? Sample()
    {
        if (ExePath is null) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ExePath, $"--query-gpu={Query} --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var line = p.StandardOutput.ReadLine();
            if (!p.WaitForExit(5000)) p.Kill();
            return Parse(line);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    // строка вида "61, 160551243, 0, 39625, 721185" (это с моей 3050)
    public static GpuSensorSample? Parse(string? line)
    {
        var parts = line?.Split(',').Select(s => s.Trim()).ToArray();
        if (parts is not { Length: 5 }) return null;

        var values = new long[5];
        for (var i = 0; i < 5; i++)
        {
            // что драйвер не поддерживает, приходит как [N/A] - считаем нулём
            if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
            {
                if (i == 0) return null;
                values[i] = 0;
            }
        }
        return new GpuSensorSample((int)values[0], values[1] + values[2], values[3] + values[4]);
    }
}

// во время теста раз в 5 секунд смотрим температуру
internal sealed class GpuThermalMonitor : IDisposable
{
    private readonly GpuSensorSample _start;
    private readonly Timer _timer;
    private int _maxTemperature;

    private GpuThermalMonitor(GpuSensorSample start)
    {
        _start = start;
        _maxTemperature = start.TemperatureC;
        _timer = new Timer(_ =>
        {
            if (NvidiaSmi.Sample() is { } s) InterlockedMax(ref _maxTemperature, s.TemperatureC);
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public static GpuThermalMonitor? TryStart() => NvidiaSmi.Sample() is { } s ? new GpuThermalMonitor(s) : null;

    public GpuThermalStats Stop()
    {
        _timer.Dispose();
        var end = NvidiaSmi.Sample() ?? _start;
        InterlockedMax(ref _maxTemperature, end.TemperatureC);
        return new GpuThermalStats(
            _start.TemperatureC,
            _maxTemperature,
            TimeSpan.FromMilliseconds(Math.Max(0, end.ThermalSlowdownUs - _start.ThermalSlowdownUs) / 1000.0),
            TimeSpan.FromMilliseconds(Math.Max(0, end.PowerCapUs - _start.PowerCapUs) / 1000.0));
    }

    public void Dispose() => _timer.Dispose();

    // Перед тестом даём карте остыть, иначе второй тест стартует на горячей карте
    // и результат зависит от того, что шло перед ним.
    public static void WaitUntilCool(int targetC, TimeSpan maxWait, CancellationToken ct)
    {
        var s = NvidiaSmi.Sample();
        if (s is null || s.TemperatureC <= targetC) return;

        Log.Info($"Видеокарта горячая ({s.TemperatureC} °C) — жду остывания до {targetC} °C (не дольше {maxWait.TotalMinutes:0} мин)...");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < maxWait && s is not null && s.TemperatureC > targetC)
        {
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            ct.ThrowIfCancellationRequested();
            s = NvidiaSmi.Sample();
        }
        Log.Info($"Температура GPU перед проходом: {s?.TemperatureC} °C.");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
