using WukongBenchAuto.Native;

namespace WukongBenchAuto.Settings;

internal enum PassKind { Cpu, Gpu }

// Настройки графики для одного теста.
// Значения сверял с тем, что сам бенч (v1.0.3) пишет в ini, если выбрать эти пункты в меню.
internal sealed record BenchmarkProfile
{
    // меньше ползунок в меню не даёт
    public const int MinRenderScale = 33;

    // SuperResolutionSampling: 0 = FSR, 3 = TSR (остальные XeSS/DLSS)
    public const int UpscalerTsr = 3;

    public required PassKind Kind { get; init; }
    public required string Title { get; init; }
    // пункт "Разрешение экрана"
    public required int Width { get; init; }
    public required int Height { get; init; }
    // разрешение экрана; в "окне без рамок" окно всегда такого размера
    public required DisplayMode Native { get; init; }
    // номер пункта в списке разрешений, 0 = родное
    public required int ResolutionIndex { get; init; }
    // масштаб рендера в %, 33..100
    public required int RenderScale { get; init; }
    // пресет: 1 низкое, 2 среднее, 3 высокое, 4 ультра, 5 реалистичное (cinematic)
    public required int Quality { get; init; }
    // "Полная трассировка лучей" (Rtx)
    public required bool RayTracing { get; init; }
    // RtxLevel: 1 низкий, 2 средний, 3 ультра
    public int RayTracingLevel { get; init; } = 3;
    // 0 выкл, 1 малозаметное, 2 очень заметное
    public required int MotionBlur { get; init; }
    public required IReadOnlyList<string> Rationale { get; init; }
    public List<string> Notes { get; init; } = new();

    public int RenderWidth => (int)Math.Round(Width * RenderScale / 100.0);
    public int RenderHeight => (int)Math.Round(Height * RenderScale / 100.0);

    // WindowFullImageQuality = выходное разрешение / размер окна * 1 000 000, например 720/1080 -> 666666
    public int WindowScaleMillionths => (int)Math.Floor(1_000_000.0 * Height / Native.Height);

    public static string QualityName(int level) => level switch
    {
        1 => "Низкое", 2 => "Среднее", 3 => "Высокое", 4 => "Ультра", 5 => "Реалистичное (cinematic)",
        _ => $"уровень {level}",
    };

    public static string RayTracingLevelName(int level) => level switch
    {
        1 => "Низкий", 2 => "Средний", 3 => "Ультра",
        _ => $"уровень {level}",
    };

    public static string MotionBlurName(int level) => level switch
    {
        0 => "Выкл", 1 => "Малозаметное", 2 => "Очень заметное",
        _ => $"уровень {level}",
    };

    // для отчёта и лога
    public IReadOnlyList<(string Name, string Value)> Describe() => new List<(string, string)>
    {
        ("Режим отображения", "Окно без рамок"),
        ("Разрешение экрана", $"{Width}×{Height}"),
        ("Масштаб рендеринга", $"{RenderScale}% → рендер {RenderWidth}×{RenderHeight}"),
        ("Сглаживание / апскейлер", "TSR"),
        ("Набор настроек графики", QualityName(Quality)),
        ("Все 10 параметров качества¹", QualityName(Quality)),
        ("Полная трассировка лучей", RayTracing ? $"Вкл, уровень «{RayTracingLevelName(RayTracingLevel)}»" : "Выкл"),
        ("Генерация кадров", "Выкл"),
        ("Вертикальная синхронизация", "Выкл"),
        ("Ограничение FPS", "Нет"),
        ("Размытие в движении", MotionBlurName(MotionBlur)),
    };

    public BenchmarkProfile WithoutRayTracing(string reason) =>
        this with { RayTracing = false, Notes = new List<string>(Notes) { reason } };

    public static BenchmarkProfile Cpu(DisplayMode native, int? resolutionIndexOverride)
    {
        // 1280x720 - минимум в меню. Если экран меньше (вряд ли), берём родное.
        var (w, h) = native.Width >= 1280 && native.Height >= 720 ? (1280, 720) : (native.Width, native.Height);
        var scale = MinRenderScale;
        return new BenchmarkProfile
        {
            Kind = PassKind.Cpu,
            Title = "CPU-тест",
            Width = w,
            Height = h,
            Native = native,
            ResolutionIndex = resolutionIndexOverride ?? Display.MenuIndexOf(w, h, native),
            RenderScale = scale,
            Quality = 1,
            RayTracing = false,
            MotionBlur = 0,
            Rationale = new[]
            {
                $"Минимальное разрешение меню (1280×720) и минимальный масштаб рендеринга ({scale}%): видеокарта считает " +
                    $"всего ~{w * scale / 100}×{h * scale / 100} пикселей — примерно в 20 раз меньше, чем в 1080p при 100%. " +
                    "Время кадра на GPU становится минимальным.",
                "Работа процессора от разрешения не зависит: игровая логика, анимация, физика, стриминг мира и " +
                    "подготовка вызовов отрисовки выполняются в том же объёме, поэтому FPS определяется процессором.",
                "Набор «Низкое» для всех 10 параметров: тени, глобальное освещение, отражения, постобработка и эффекты — " +
                    "в основном пиксельная нагрузка на GPU, их снижение дополнительно убирает видеокарту из уравнения.",
                "Трассировка лучей выключена — это самая тяжёлая для GPU часть кадра.",
                "Генерация кадров выключена: вставленные кадры рисует GPU без участия CPU, они завысили бы FPS.",
                "VSync и ограничение FPS выключены, иначе FPS упёрся бы в частоту монитора или лимит, а не в процессор.",
                "Размытие в движении выключено — лишний полноэкранный проход на GPU.",
                "TSR — встроенный в движок апскейлер, работает на любой видеокарте, результаты сравнимы между NVIDIA/AMD/Intel.",
                "Проверка: отчёт показывает долю кадров, где время кадра CPU больше времени кадра GPU (CPU-bound).",
            },
        };
    }

    public static BenchmarkProfile Gpu(DisplayMode native, bool rayTracingSupported, bool rayTracingRequested)
    {
        var profile = new BenchmarkProfile
        {
            Kind = PassKind.Gpu,
            Title = "GPU-тест",
            Width = native.Width,
            Height = native.Height,
            Native = native,
            ResolutionIndex = 0,
            RenderScale = 100,
            Quality = 5,
            RayTracing = true,
            RayTracingLevel = 3,
            MotionBlur = 2,
            Rationale = new[]
            {
                "Родное (максимальное) разрешение монитора и масштаб рендеринга 100%: апскейлер не снижает внутреннее " +
                    "разрешение, каждый пиксель считается «честно» — нагрузка на GPU растёт пропорционально числу пикселей.",
                "Набор «Реалистичное» (cinematic, максимальный) для всех 10 параметров: тени, глобальное освещение (Lumen), " +
                    "отражения, растительность, эффекты, текстуры — всё на максимуме.",
                "Полная трассировка лучей NVIDIA на уровне «Ультра» (path tracing) — самая тяжёлая для видеокарты нагрузка " +
                    "в игре. Включается только на GPU с аппаратным RT. Если прогон с RT завершится сбоем или упрётся в объём " +
                    "видеопамяти (память заполнена на 95% и больше, а загрузка GPU ниже 90%), инструмент повторит его без RT: " +
                    "при нехватке VRAM видеокарта простаивает, и без трассировки нагрузка на неё оказывается выше.",
                "Генерация кадров выключена: она добавляет интерполированные кадры и скрывает реальную скорость GPU.",
                "VSync и ограничение FPS выключены — у видеокарты нет простоев в ожидании кадра, загрузка ~100%.",
                "Размытие в движении «Очень заметное» — дополнительный полноэкранный проход постобработки.",
                "TSR при 100% работает как нативное временное сглаживание и одинаково доступен на любой видеокарте.",
                "Проверка: отчёт показывает долю GPU-bound кадров и среднюю загрузку GPU.",
            },
        };

        if (!rayTracingRequested) return profile.WithoutRayTracing("Трассировка лучей отключена параметром --no-rt.");
        if (!rayTracingSupported)
            return profile.WithoutRayTracing("Трассировка лучей отключена: видеокарта не поддерживает аппаратный RT (DXR).");
        return profile;
    }
}
