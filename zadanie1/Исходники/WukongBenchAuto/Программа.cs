using System.Globalization;
using System.Text;
using WukongBenchAuto;
using WukongBenchAuto.Install;
using WukongBenchAuto.Native;
using WukongBenchAuto.Reporting;
using WukongBenchAuto.Results;
using WukongBenchAuto.Running;
using WukongBenchAuto.Settings;
using WukongBenchAuto.SystemInfo;

Console.OutputEncoding = Encoding.UTF8;
// числа в отчёте всегда с точкой, чтобы не зависеть от региональных настроек винды
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
// без этого при масштабе 125% клики по меню улетают мимо кнопок
NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2);

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException e)
{
    Log.Error(e.Message);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(Options.HelpText);
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // не выходим сразу: сначала закрыть игру и вернуть настройки
    e.Cancel = true;
    Log.Warn("Остановка по Ctrl+C: закрываю бенчмарк и восстанавливаю настройки...");
    cts.Cancel();
};

try
{
    if (options.ParseFiles.Count > 0) return ParseOnly(options);
    return RunBenchmarks(options, cts.Token);
}
catch (BenchmarkNotFoundException e)
{
    Log.Error(e.Message);
    return 3;
}
catch (OperationCanceledException)
{
    Log.Warn("Прервано пользователем.");
    return 130;
}

static int RunBenchmarks(Options options, CancellationToken ct)
{
    var outputDir = CreateOutputDir(options);
    var native = Display.Current();

    BenchmarkInstallation? install = null;
    try
    {
        install = BenchmarkInstallation.Locate(options.ExePath, options.IniPath);
        Log.Ok($"Бенчмарк найден: {install.InstallDir}");
    }
    catch (BenchmarkNotFoundException) when (options.DryRun)
    {
        Log.Warn("Бенчмарк не найден — dry-run запишет настройки в пустой ini.");
    }

    Log.Step("Сбор характеристик компьютера...");
    var system = SystemInfoCollector.Collect(install?.InstallDir);
    Log.Info($"CPU: {system.Rows.FirstOrDefault(r => r.Name == "CPU").Value}; GPU: {system.PrimaryGpu?.Name}; монитор: {native}");

    var cpuProfile = BenchmarkProfile.Cpu(native, options.CpuResolutionIndex);
    var gpuProfile = BenchmarkProfile.Gpu(native, system.RayTracingSupported, options.RayTracing);

    if (options.DryRun) return DryRun(install, outputDir, cpuProfile, gpuProfile);
    if (install is null) return 3;

    var report = new SessionReport { System = system, BenchmarkExe = install.ExePath };
    var runner = new BenchmarkRunner(install, options);

    Log.Warn("Не пользуйтесь мышью и клавиатурой до окончания тестов: инструмент сам управляет меню бенчмарка.");
    runner.EnsureConfigExists(ct);

    using (var settings = new SettingsSession(install.IniPath, restoreOnDispose: !options.KeepSettings))
    {
        try
        {
            if (options.RunCpu)
            {
                report.Cpu = RunPass(runner, settings, cpuProfile, ct);
                if (options.RunGpu) Cooldown(options, ct);
            }

            if (options.RunGpu)
            {
                report.Gpu = RunPass(runner, settings, gpuProfile, ct);

                // RT ест много видеопамяти. На 4 ГБ (моя 3050) игра с RT либо падает, либо упирается в память
                // и видеокарта простаивает (1 FPS при загрузке ~75%). Без RT нагрузка на GPU выше,
                // поэтому в таком случае перезапускаем тест без RT.
                var rtProblem = !gpuProfile.RayTracing || ct.IsCancellationRequested ? null
                    : report.Gpu.Result is null ? $"прогон с трассировкой лучей не удался ({report.Gpu.Outcome?.Error})"
                    : report.Gpu.Result.VideoMemoryOverflow ? VideoMemoryOverflowText(report.Gpu.Result)
                    : null;
                if (rtProblem is not null)
                {
                    Log.Warn($"GPU-тест: {rtProblem}. Повтор без трассировки лучей...");
                    Cooldown(options, ct);
                    report.Gpu = RunPass(runner, settings, gpuProfile.WithoutRayTracing($"{rtProblem}; использован прогон без RT."), ct);
                }
            }
        }
        finally
        {
            BenchmarkRunner.CloseRunningInstances();
            if (!options.KeepSettings) Log.Info("Исходный GameUserSettings.ini восстановлен.");
        }
    }

    return WriteReport(report, outputDir);
}

static PassReport RunPass(BenchmarkRunner runner, SettingsSession settings, BenchmarkProfile profile, CancellationToken ct)
{
    Log.Step($"=== {profile.Title} ===");
    foreach (var (name, value) in profile.Describe()) Log.Info($"  {name}: {value}");
    settings.Apply(profile);
    GpuThermalMonitor.WaitUntilCool(targetC: 65, maxWait: TimeSpan.FromMinutes(3), ct);

    RunOutcome outcome;
    GpuThermalStats? thermal;
    using (var monitor = GpuThermalMonitor.TryStart())
    {
        outcome = runner.Run(profile.Title, ct);
        thermal = monitor?.Stop();
    }
    if (!outcome.Success) Log.Error($"{profile.Title}: {outcome.Error}");
    if (thermal is not null)
        Log.Info($"{profile.Title}: температура GPU {thermal.StartTemperatureC} → макс. {thermal.MaxTemperatureC} °C, " +
                 $"снижение частот из-за перегрева {thermal.ThermalSlowdown.TotalSeconds:0} с");

    var pass = new PassReport { Profile = profile, Outcome = outcome, Thermal = thermal };
    SessionReport.Verify(pass);
    foreach (var w in pass.Warnings) Log.Warn(w);
    return pass;
}

static string VideoMemoryOverflowText(BenchmarkResult r) =>
    $"с трассировкой лучей видеопамять переполнилась ({r.VideoMemoryPeakGb:0.0} из {r.VideoMemoryTotalGb:0} ГБ): " +
    $"загрузка GPU упала до {r.GpuUsageAvg:0}%, средний FPS {r.FpsAvg:0} — результат отражал нехватку VRAM, а не мощность GPU";

static void Cooldown(Options options, CancellationToken ct)
{
    if (options.Cooldown <= TimeSpan.Zero) return;
    Log.Info($"Пауза {options.Cooldown.TotalSeconds:0} с между проходами (остывание)...");
    ct.WaitHandle.WaitOne(options.Cooldown);
    ct.ThrowIfCancellationRequested();
}

static int ParseOnly(Options options)
{
    var results = options.ParseFiles.Select(f => (File: f, Result: BenchmarkResult.TryLoad(f))).ToList();
    foreach (var (file, result) in results.Where(r => r.Result is null))
        Log.Error($"Не удалось разобрать файл результата: {file}");
    if (results.Any(r => r.Result is null)) return 4;

    var native = Display.Current();
    var system = SystemInfoCollector.Collect(null);
    var report = new SessionReport { System = system };

    PassReport Pass(BenchmarkProfile profile, BenchmarkResult result)
    {
        var pass = new PassReport { Profile = profile, Outcome = new RunOutcome(true, result, null, TimeSpan.Zero) };
        SessionReport.Verify(pass);
        return pass;
    }

    report.Cpu = Pass(BenchmarkProfile.Cpu(native, options.CpuResolutionIndex), results[0].Result!);
    if (results.Count > 1)
    {
        var gpu = results[1].Result!;
        var gpuProfile = BenchmarkProfile.Gpu(native, system.RayTracingSupported, options.RayTracing);
        // если в файле RT выключен, это был повтор без RT - сверяем с таким профилем, а не ругаемся на Rtx
        if (gpuProfile.RayTracing && gpu.AppliedInt("Rtx") == 0)
            gpuProfile = gpuProfile.WithoutRayTracing("В файле результата трассировка выключена: это прогон без RT.");
        report.Gpu = Pass(gpuProfile, gpu);
    }

    return WriteReport(report, CreateOutputDir(options));
}

static int DryRun(BenchmarkInstallation? install, string outputDir, BenchmarkProfile cpu, BenchmarkProfile gpu)
{
    foreach (var profile in new[] { cpu, gpu })
    {
        var ini = install is not null && File.Exists(install.IniPath) ? IniFile.Load(install.IniPath) : IniFile.Empty();
        SettingsWriter.Apply(ini, profile);
        var path = Path.Combine(outputDir, $"GameUserSettings.{profile.Kind.ToString().ToLowerInvariant()}.ini");
        ini.Save(path);
        Log.Ok($"{profile.Title}: настройки записаны в {path}");
        foreach (var (name, value) in profile.Describe()) Log.Info($"  {name}: {value}");
    }
    Log.Info("Dry-run: бенчмарк не запускался, исходный GameUserSettings.ini не изменён.");
    return 0;
}

static string CreateOutputDir(Options options)
{
    var root = options.OutputRoot ?? DefaultResultsDir();
    var dir = Path.GetFullPath(Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")));
    Directory.CreateDirectory(dir);
    return dir;
}

// Отчёты всегда в <проект>\results, как ни запускай: из VS exe лежит в bin\Debug\..., а запуск.cmd
// стартует из корня. Поднимаемся от exe до папки с WukongBenchAuto.sln; если её нет (exe скопировали
// отдельно) - results рядом с exe.
static string DefaultResultsDir()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "WukongBenchAuto.sln")))
            return Path.Combine(dir.FullName, "results");
    return Path.Combine(AppContext.BaseDirectory, "results");
}

static int WriteReport(SessionReport report, string outputDir)
{
    foreach (var pass in new[] { report.Cpu, report.Gpu })
    {
        if (pass?.Result is null) continue;
        var copy = Path.Combine(outputDir, $"{pass.Profile.Kind.ToString().ToLowerInvariant()}_raw.json");
        File.Copy(pass.Result.SourcePath, copy, overwrite: true);
    }

    var markdown = report.ToMarkdown();
    File.WriteAllText(Path.Combine(outputDir, "report.md"), markdown, new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(outputDir, "report.json"), report.ToJson(), new UTF8Encoding(false));

    Console.WriteLine();
    Console.WriteLine(markdown);
    Log.Ok($"Отчёт сохранён: {Path.Combine(outputDir, "report.md")} (и report.json)");

    var failed = new[] { report.Cpu, report.Gpu }.Any(p => p is not null && p.Result is null);
    return failed ? 1 : 0;
}
