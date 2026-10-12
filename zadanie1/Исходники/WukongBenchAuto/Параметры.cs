using System.Globalization;

namespace WukongBenchAuto;

// Параметры командной строки. Без параметров - оба теста.
internal sealed class Options
{
    public string? ExePath { get; private set; }
    public string? IniPath { get; private set; }
    // null - значит --out не задан, тогда папку results ищет Программа.cs (корень проекта)
    public string? OutputRoot { get; private set; }
    public TimeSpan PassTimeout { get; private set; } = TimeSpan.FromMinutes(20);
    public TimeSpan Cooldown { get; private set; } = TimeSpan.FromSeconds(15);
    public bool RunCpu { get; private set; } = true;
    public bool RunGpu { get; private set; } = true;
    public bool RayTracing { get; private set; } = true;
    public bool ManualStart { get; private set; }
    public bool DryRun { get; private set; }
    public bool KeepSettings { get; private set; }
    public bool ShowHelp { get; private set; }
    public int? CpuResolutionIndex { get; private set; }
    public List<string> ParseFiles { get; } = new();
    public MenuLayout Menu { get; private set; } = MenuLayout.Default;

    public const string HelpText = """
        WukongBenchAuto — автоматический CPU/GPU прогон Black Myth: Wukong Benchmark Tool.

        Использование:
          WukongBenchAuto [параметры]

        Основные параметры:
          --only cpu|gpu          выполнить только один проход
          --no-rt                 не включать трассировку лучей в GPU-тесте
          --timeout <мин>         таймаут одного прохода (по умолчанию 20)
          --cooldown <сек>        пауза между проходами для остывания (по умолчанию 15)
          --out <папка>           куда сохранять отчёты (по умолчанию results в папке проекта)

        Нестандартная установка:
          --exe <путь>            путь к b1_benchmark.exe, если бенчмарк не найден в библиотеках Steam
          --ini <путь>            путь к GameUserSettings.ini бенчмарка
          --cpu-res-index <n>     индекс 1280x720 в списке разрешений меню (если определён неверно)
          --start-button x,y      положение пункта «Тест быстродействия» (доли окна, 0.10,0.45)
          --confirm-button x,y    положение кнопки «Подтвердить» (доли окна, 0.39,0.59)
          --manual-start          не нажимать кнопки меню, только ждать результат

        Отладка без запуска игры:
          --dry-run               записать настройки обоих проходов в results/... и выйти
          --parse <cpu.json> [gpu.json]   построить отчёт по готовым файлам результатов
          --keep-settings         не восстанавливать исходный GameUserSettings.ini
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"После {arg} ожидается значение.");

            switch (arg.ToLowerInvariant())
            {
                case "-h": case "--help": case "/?": o.ShowHelp = true; break;
                case "--exe": o.ExePath = Path.GetFullPath(Next()); break;
                case "--ini": o.IniPath = Path.GetFullPath(Next()); break;
                case "--out": o.OutputRoot = Next(); break;
                case "--timeout": o.PassTimeout = TimeSpan.FromMinutes(ParseDouble(Next(), arg)); break;
                case "--cooldown": o.Cooldown = TimeSpan.FromSeconds(ParseDouble(Next(), arg)); break;
                case "--no-rt": o.RayTracing = false; break;
                case "--manual-start": o.ManualStart = true; break;
                case "--dry-run": o.DryRun = true; break;
                case "--keep-settings": o.KeepSettings = true; break;
                case "--cpu-res-index": o.CpuResolutionIndex = (int)ParseDouble(Next(), arg); break;
                case "--start-button": o.Menu = o.Menu with { Start = ParsePoint(Next(), arg) }; break;
                case "--confirm-button": o.Menu = o.Menu with { Confirm = ParsePoint(Next(), arg) }; break;
                case "--only":
                    var which = Next().ToLowerInvariant();
                    if (which is not ("cpu" or "gpu")) throw new ArgumentException("--only принимает cpu или gpu.");
                    o.RunCpu = which == "cpu";
                    o.RunGpu = which == "gpu";
                    break;
                case "--parse":
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        o.ParseFiles.Add(Path.GetFullPath(args[++i]));
                    if (o.ParseFiles.Count == 0) throw new ArgumentException("--parse: укажите хотя бы один файл.");
                    break;
                default:
                    throw new ArgumentException($"Неизвестный параметр: {arg}. Справка: --help");
            }
        }
        return o;
    }

    private static double ParseDouble(string value, string arg) =>
        double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0
            ? d
            : throw new ArgumentException($"{arg}: некорректное число «{value}».");

    private static (double X, double Y) ParsePoint(string value, string arg)
    {
        var parts = value.Split(new[] { ',', ';', 'x' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) throw new ArgumentException($"{arg}: ожидается формат x,y (доли окна, например 0.10,0.45).");
        var x = ParseDouble(parts[0], arg);
        var y = ParseDouble(parts[1], arg);
        if (x > 1 || y > 1) throw new ArgumentException($"{arg}: координаты задаются долями окна от 0 до 1.");
        return (x, y);
    }
}

// Где кнопки в меню бенчмарка, в долях окна (0..1), чтобы не зависеть от разрешения.
// Если после обновления бенча кнопки съедут - поправить тут или через --start-button / --confirm-button.
internal sealed record MenuLayout((double X, double Y) Start, (double X, double Y) Confirm)
{
    public static MenuLayout Default { get; } = new((0.10, 0.45), (0.39, 0.59));
}
