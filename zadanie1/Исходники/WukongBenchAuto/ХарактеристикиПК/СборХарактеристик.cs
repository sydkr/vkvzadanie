using System.Globalization;
using System.Management;
using Microsoft.Win32;
using WukongBenchAuto.Native;

namespace WukongBenchAuto.SystemInfo;

internal sealed record GpuInfo(string Name, string? Driver, string? DriverDate, long? VideoMemoryBytes);

// Характеристики ПК (WMI + реестр).
internal sealed class SystemSnapshot
{
    public List<(string Name, string Value)> Rows { get; } = new();
    public List<GpuInfo> Gpus { get; } = new();

    // на ноуте видеокарт две, тест идёт на дискретной
    public GpuInfo? PrimaryGpu =>
        Gpus.OrderByDescending(g => IsDiscrete(g.Name)).ThenByDescending(g => g.VideoMemoryBytes ?? 0).FirstOrDefault();

    // аппаратный RT есть у NVIDIA RTX, AMD RX 6000+ и Intel Arc. Определяю по названию - грубо, но работает
    public bool RayTracingSupported => Gpus.Any(g =>
    {
        var n = g.Name.ToUpperInvariant();
        return n.Contains("RTX") || n.Contains("ARC") ||
               (n.Contains("RADEON") && System.Text.RegularExpressions.Regex.IsMatch(n, @"RX\s*(6|7|9)\d{3}"));
    });

    private static bool IsDiscrete(string name)
    {
        var n = name.ToUpperInvariant();
        return n.Contains("NVIDIA") || n.Contains("GEFORCE") || n.Contains("RADEON RX") || n.Contains("ARC");
    }
}

internal static class SystemInfoCollector
{
    public static SystemSnapshot Collect(string? benchmarkDir)
    {
        var s = new SystemSnapshot();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) s.Rows.Add((name, value.Trim()));
        }

        // Процессор
        foreach (var cpu in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize FROM Win32_Processor"))
        {
            Add("CPU", Str(cpu, "Name"));
            Add("Ядра / потоки", $"{Str(cpu, "NumberOfCores")} / {Str(cpu, "NumberOfLogicalProcessors")}");
            Add("Базовая частота", $"{Long(cpu, "MaxClockSpeed") / 1000.0:0.00} ГГц");
            var l2 = Long(cpu, "L2CacheSize");
            var l3 = Long(cpu, "L3CacheSize");
            if (l2 > 0 || l3 > 0) Add("Кэш L2 / L3", $"{l2 / 1024.0:0.#} МБ / {l3 / 1024.0:0.#} МБ");
        }

        // Видеокарты
        var vram = ReadVideoMemoryFromRegistry();
        foreach (var gpu in Query("SELECT Name, DriverVersion, DriverDate, AdapterRAM FROM Win32_VideoController"))
        {
            var name = Str(gpu, "Name") ?? "?";
            // AdapterRAM 32-битный и для карт больше 4 ГБ врёт, поэтому сначала берём из реестра
            long? memory = vram.TryGetValue(name, out var regBytes) ? regBytes : Long(gpu, "AdapterRAM") is var a and > 0 ? a : null;
            var date = Str(gpu, "DriverDate") is { Length: >= 8 } d ? $"{d[..4]}-{d[4..6]}-{d[6..8]}" : null;
            s.Gpus.Add(new GpuInfo(name, Str(gpu, "DriverVersion"), date, memory));
        }
        foreach (var gpu in s.Gpus.OrderByDescending(g => g == s.PrimaryGpu))
        {
            var label = s.Gpus.Count > 1 ? (gpu == s.PrimaryGpu ? "GPU (основная)" : "GPU (встроенная)") : "GPU";
            var mem = gpu.VideoMemoryBytes is { } b ? $", {b / 1024.0 / 1024 / 1024:0.#} ГБ" : "";
            Add(label, $"{gpu.Name}{mem}, драйвер {gpu.Driver}{(gpu.DriverDate is null ? "" : $" от {gpu.DriverDate}")}");
        }
        Add("Аппаратная трассировка лучей", s.RayTracingSupported ? "поддерживается" : "не поддерживается");

        // Память
        var totalRam = Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").Select(m => Long(m, "TotalPhysicalMemory")).FirstOrDefault();
        var modules = Query("SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType, Manufacturer, PartNumber FROM Win32_PhysicalMemory").ToList();
        var ramText = $"{totalRam / 1024.0 / 1024 / 1024:0.#} ГБ";
        if (modules.Count > 0)
        {
            var type = MemoryType(Long(modules[0], "SMBIOSMemoryType"));
            var speed = Long(modules[0], "ConfiguredClockSpeed") is var cs and > 0 ? cs : Long(modules[0], "Speed");
            var layout = string.Join(" + ", modules.Select(m => $"{Long(m, "Capacity") / 1024 / 1024 / 1024} ГБ"));
            ramText += $" ({layout}){(type is null ? "" : $", {type}")}{(speed > 0 ? $"-{speed} МТ/с" : "")}";
        }
        Add("RAM", ramText);

        // Платформа
        foreach (var cs in Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            Add("Компьютер", $"{Str(cs, "Manufacturer")} {Str(cs, "Model")}");
        foreach (var board in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            Add("Материнская плата", $"{Str(board, "Manufacturer")} {Str(board, "Product")}");

        // ОС
        var os = Query("SELECT Caption, BuildNumber, OSArchitecture FROM Win32_OperatingSystem").FirstOrDefault();
        var displayVersion = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;
        var ubr = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null);
        if (os is not null)
            Add("ОС", $"{Str(os, "Caption")} {displayVersion} (сборка {Str(os, "BuildNumber")}{(ubr is null ? "" : $".{ubr}")}, {Str(os, "OSArchitecture")})");

        // Монитор и питание
        Add("Монитор (основной)", Display.Current().ToString());
        Add("Питание", PowerSource());
        Add("Схема электропитания", ActivePowerPlan());

        // Накопитель с бенчмарком
        if (benchmarkDir is not null) Add("Диск с бенчмарком", DiskFor(benchmarkDir));

        return s;
    }

    private static IEnumerable<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return Array.Empty<ManagementBaseObject>();
        }
    }

    private static string? Str(ManagementBaseObject o, string prop) => o[prop]?.ToString()?.Trim();

    private static long Long(ManagementBaseObject o, string prop) =>
        o[prop] is { } v && long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0;

    private static string? MemoryType(long smbiosType) => smbiosType switch
    {
        24 => "DDR3", 26 => "DDR4", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5",
        _ => null,
    };

    // точный объём VRAM лежит в HardwareInformation.qwMemorySize (ветка видеоадаптеров)
    private static Dictionary<string, long> ReadVideoMemoryFromRegistry()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return result;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var key = cls.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string desc) continue;
                var bytes = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    int i => (uint)i,
                    _ => 0L,
                };
                if (bytes > 0) result[desc] = bytes;
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // нет доступа к реестру - ну и ладно, останется значение из WMI
        }
        return result;
    }

    private static string PowerSource()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status)) return "неизвестно";
        var battery = status.BatteryFlag == 128 ? "" : $", батарея {status.BatteryLifePercent}%";
        return status.AcLineStatus switch
        {
            1 => $"от сети{battery}",
            0 => $"ОТ БАТАРЕИ{battery} — результаты могут быть занижены",
            _ => "неизвестно",
        };
    }

    private static string? ActivePowerPlan()
    {
        var guid = (Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme", null) as string)?.ToLowerInvariant();
        return guid switch
        {
            null => null,
            "381b4222-f694-41f0-9685-ff5bb260df2e" => "Сбалансированная",
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "Высокая производительность",
            "a1841308-3541-4fab-bc81-f71556f20b4a" => "Экономия энергии",
            "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Максимальная производительность",
            _ => $"пользовательская ({guid})",
        };
    }

    private static string? DiskFor(string path)
    {
        var letter = Path.GetPathRoot(Path.GetFullPath(path))?.TrimEnd('\\', ':').ToUpperInvariant();
        if (string.IsNullOrEmpty(letter) || letter.Length != 1) return null;

        const string storage = @"root\Microsoft\Windows\Storage";
        var partition = Query($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'", storage).FirstOrDefault();
        if (partition is null) return $"{letter}:";

        var disk = Query($"SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk WHERE DeviceId = '{Long(partition, "DiskNumber")}'", storage)
            .FirstOrDefault();
        if (disk is null) return $"{letter}:";

        var media = Long(disk, "MediaType") switch { 3 => "HDD", 4 => "SSD", _ => null };
        var bus = Long(disk, "BusType") switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", _ => null };
        var kind = string.Join(" ", new[] { bus, media }.Where(x => x is not null));
        return $"{letter}: — {Str(disk, "FriendlyName")}{(kind.Length > 0 ? $" ({kind})" : "")}";
    }
}
