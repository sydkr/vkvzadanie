using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WukongBenchAuto.Install;

// Где стоит бенчмарк и где он хранит настройки и результаты.
internal sealed record BenchmarkInstallation(string InstallDir, string ExePath, string IniPath, string HistoryDir)
{
    public const int SteamAppId = 3132990;
    private const string DefaultFolderName = "Black Myth Wukong Benchmark Tool";
    // это лаунчер, сама игра - b1\Binaries\Win64\b1-Win64-Shipping.exe
    private const string ExeName = "b1_benchmark.exe";

    // путь из --exe, иначе ищем по всем библиотекам Steam:
    // реестр -> libraryfolders.vdf -> appmanifest_3132990.acf
    public static BenchmarkInstallation Locate(string? exeOverride, string? iniOverride)
    {
        var exe = exeOverride ?? FindExe() ?? throw new BenchmarkNotFoundException(
            "Black Myth: Wukong Benchmark Tool не найден в библиотеках Steam.\n" +
            $"Установите его (steam://install/{SteamAppId}) или укажите путь: --exe \"...\\{ExeName}\"");
        if (!File.Exists(exe)) throw new BenchmarkNotFoundException($"Файл не найден: {exe}");

        var installDir = Path.GetDirectoryName(exe)!;
        return new BenchmarkInstallation(installDir, exe, iniOverride ?? DefaultIniPath(installDir), DefaultHistoryDir());
    }

    // настройки бенч хранит у себя в папке, а не в %LOCALAPPDATA% (там конфиг полной игры, не перепутать)
    public static string DefaultIniPath(string installDir) =>
        Path.Combine(installDir, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini");

    // сюда бенч пишет json после каждого прогона: %TEMP%\b1\BenchMarkHistory\Tool\<unix-время>
    public static string DefaultHistoryDir() => Path.Combine(Path.GetTempPath(), "b1", "BenchMarkHistory");

    private static string? FindExe()
    {
        foreach (var library in SteamLibraries())
        {
            var steamApps = Path.Combine(library, "steamapps");
            var folder = ReadInstallDir(Path.Combine(steamApps, $"appmanifest_{SteamAppId}.acf")) ?? DefaultFolderName;
            var dir = Path.Combine(steamApps, "common", folder);
            if (!Directory.Exists(dir)) continue;

            var exe = Path.Combine(dir, ExeName);
            if (File.Exists(exe)) return exe;

            exe = Directory.EnumerateFiles(dir, ExeName, SearchOption.AllDirectories).FirstOrDefault();
            if (exe is not null) return exe;
        }
        return null;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new[]
            {
                Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
                Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
                Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string,
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var libraries = new List<string>(roots);
        foreach (var root in roots)
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(Path.GetFullPath(m.Groups[1].Value.Replace(@"\\", @"\")));
        }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? ReadInstallDir(string manifest)
    {
        if (!File.Exists(manifest)) return null;
        var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }
}

internal sealed class BenchmarkNotFoundException : Exception
{
    public BenchmarkNotFoundException(string message) : base(message) { }
}
