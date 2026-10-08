using System.Diagnostics;
using WukongBenchAuto.Install;
using WukongBenchAuto.Native;
using WukongBenchAuto.Results;

namespace WukongBenchAuto.Running;

internal sealed record RunOutcome(bool Success, BenchmarkResult? Result, string? Error, TimeSpan Duration)
{
    public static RunOutcome Fail(string error, TimeSpan duration) => new(false, null, error, duration);
}

// Запуск бенчмарка и ожидание результата.
// Ключа "сразу начать тест" у бенча нет, поэтому меню проходим сами:
// Enter (заставка) -> "Тест быстродействия" -> "Подтвердить".
// Тест закончился = в BenchMarkHistory появился новый json.
internal sealed class BenchmarkRunner
{
    private static readonly string[] ProcessNames = { "b1_benchmark", "b1-Win64-Shipping" };
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MenuStepInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MenuCyclePause = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FirstMenuAction = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);

    private readonly BenchmarkInstallation _install;
    private readonly Options _options;

    public BenchmarkRunner(BenchmarkInstallation install, Options options)
    {
        _install = install;
        _options = options;
    }

    // ini появляется только после первого запуска бенча. Если его нет - запускаем разок,
    // ждём, пока игра его создаст, и закрываем.
    public void EnsureConfigExists(CancellationToken ct)
    {
        if (File.Exists(_install.IniPath)) return;

        Log.Step("GameUserSettings.ini ещё не создан — первый запуск бенчмарка для генерации конфигурации...");
        CloseRunningInstances();
        StartBenchmark();
        var sw = Stopwatch.StartNew();
        try
        {
            while (sw.Elapsed < StartupGrace && !ct.IsCancellationRequested)
            {
                if (File.Exists(_install.IniPath) && new FileInfo(_install.IniPath).Length > 0) break;
                ct.WaitHandle.WaitOne(PollInterval);
            }
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
        }
        finally
        {
            CloseRunningInstances();
        }

        if (File.Exists(_install.IniPath)) Log.Ok("Конфигурация создана.");
        else Log.Warn("Бенчмарк не создал GameUserSettings.ini — файл будет создан инструментом.");
    }

    public RunOutcome Run(string title, CancellationToken ct)
    {
        CloseRunningInstances();
        var known = SnapshotHistory();
        var knownCrashes = SnapshotCrashReports();
        var sw = Stopwatch.StartNew();

        Log.Step($"{title}: запуск бенчмарка через Steam (steam://rungameid/{BenchmarkInstallation.SteamAppId})");
        StartBenchmark();

        var seenProcess = false;
        var lastSeen = TimeSpan.Zero;
        var nextMenuAction = FirstMenuAction;
        var nextProgress = ProgressInterval;
        var menuStep = 0;

        try
        {
            while (sw.Elapsed < _options.PassTimeout)
            {
                ct.ThrowIfCancellationRequested();

                // сначала смотрим результат, потом жмём: с экрана итогов можно случайно запустить тест заново
                if (FindNewResult(known) is { } result)
                {
                    Log.Ok($"{title}: результат получен за {sw.Elapsed:mm\\:ss} — {result.SourcePath}");
                    return new RunOutcome(true, result, null, sw.Elapsed);
                }

                var pids = RunningProcessIds();
                if (pids.Count > 0)
                {
                    seenProcess = true;
                    lastSeen = sw.Elapsed;
                }
                else if (seenProcess && sw.Elapsed - lastSeen > ExitGrace)
                {
                    var reason = NewCrashReason(knownCrashes);
                    return RunOutcome.Fail(reason is null
                        ? "процесс бенчмарка завершился, не сохранив результат (вылет или ручное закрытие)"
                        : $"игра аварийно завершилась: {reason}", sw.Elapsed);
                }
                else if (!seenProcess && sw.Elapsed > StartupGrace)
                {
                    return RunOutcome.Fail("процесс бенчмарка не запустился", sw.Elapsed);
                }

                if (!_options.ManualStart && pids.Count > 0 && sw.Elapsed >= nextMenuAction)
                    nextMenuAction = sw.Elapsed + (TryMenuStep(pids, ref menuStep) ? StepDelay(menuStep) : MenuStepInterval);

                if (sw.Elapsed >= nextProgress)
                {
                    Log.Info($"{title}: идёт прогон, прошло {sw.Elapsed:mm\\:ss}...");
                    nextProgress += ProgressInterval;
                }

                ct.WaitHandle.WaitOne(PollInterval);
            }
            return RunOutcome.Fail($"результат не получен за {_options.PassTimeout.TotalMinutes:0} мин", sw.Elapsed);
        }
        finally
        {
            // убиваем процесс, а не выходим нормально: при обычном выходе игра перезапишет ini своими настройками
            CloseRunningInstances();
        }
    }

    // Один шаг меню. Шаги идут по кругу, пока не появится результат: если экран ещё грузился
    // и нажатие ушло в пустоту, на следующем круге повторится.
    private bool TryMenuStep(IReadOnlySet<int> pids, ref int step)
    {
        var window = GameWindow.Find(pids);
        if (window == IntPtr.Zero || !GameWindow.Activate(window)) return false;

        var menu = _options.Menu;
        switch (step % 3)
        {
            case 0: GameWindow.PressKey(NativeMethods.VkReturn); break;
            case 1: GameWindow.ClickRelative(window, menu.Start.X, menu.Start.Y); break;
            default: GameWindow.ClickRelative(window, menu.Confirm.X, menu.Confirm.Y); break;
        }
        step++;
        return true;
    }

    // после "Подтвердить" ждём подольше, сцена грузится
    private static TimeSpan StepDelay(int nextStep) => nextStep % 3 == 0 ? MenuCyclePause : MenuStepInterval;

    // Только через steam://. Если запустить b1_benchmark.exe напрямую, он перезапускает себя через Steam
    // с параметром "b1", и Steam выкидывает окно "запустить с параметрами?" - на нём всё и встаёт.
    private static void StartBenchmark()
    {
        using var _ = Process.Start(new ProcessStartInfo($"steam://rungameid/{BenchmarkInstallation.SteamAppId}")
        {
            UseShellExecute = true,
        });
    }

    private static HashSet<int> RunningProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var name in ProcessNames)
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                if (!p.HasExited) ids.Add(p.Id);
            }
        }
        return ids;
    }

    public static void CloseRunningInstances()
    {
        foreach (var name in ProcessNames)
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(15_000);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // уже закрылся сам - ок
                }
            }
        }
    }

    // сюда UE складывает отчёты о вылетах: b1\Saved\Crashes\<id>\CrashContext.runtime-xml
    private string CrashDir => Path.Combine(_install.InstallDir, "b1", "Saved", "Crashes");

    private HashSet<string> SnapshotCrashReports() =>
        Directory.Exists(CrashDir)
            ? Directory.EnumerateDirectories(CrashDir).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // достаём текст ошибки из свежего отчёта о вылете (например "Out of video memory..."), чтобы в логе было видно, что случилось
    private string? NewCrashReason(HashSet<string> known)
    {
        var report = SnapshotCrashReports()
            .Where(d => !known.Contains(d))
            .Select(d => Path.Combine(d, "CrashContext.runtime-xml"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (report is null) return null;

        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(report), "<ErrorMessage>(.*?)</ErrorMessage>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            var text = m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
            // "LowLevelFatalError [File:...] [Line: 804] Out of video memory..." - путь к исходникам UE выкидываем
            text = System.Text.RegularExpressions.Regex.Replace(text, @"^LowLevelFatalError \[File:[^\]]*\] \[Line: \d+\]\s*", "");
            return text.Length > 0 ? $"{text} (отчёт: {Path.GetDirectoryName(report)})" : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private Dictionary<string, DateTime> SnapshotHistory()
    {
        var snapshot = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_install.HistoryDir)) return snapshot;
        foreach (var file in Directory.EnumerateFiles(_install.HistoryDir, "*", SearchOption.AllDirectories))
            snapshot[file] = File.GetLastWriteTimeUtc(file);
        return snapshot;
    }

    private BenchmarkResult? FindNewResult(Dictionary<string, DateTime> known)
    {
        if (!Directory.Exists(_install.HistoryDir)) return null;

        var candidates = Directory.EnumerateFiles(_install.HistoryDir, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Where(f => !known.TryGetValue(f.FullName, out var t) || f.LastWriteTimeUtc > t)
            .OrderByDescending(f => f.LastWriteTimeUtc);

        // если игра ещё пишет файл, json не распарсится - не страшно, проверим через секунду
        return candidates.Select(f => BenchmarkResult.TryLoad(f.FullName)).FirstOrDefault(r => r is not null);
    }
}
