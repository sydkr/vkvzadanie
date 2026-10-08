using System.Globalization;

namespace WukongBenchAuto.Settings;

// Пишем настройки в GameUserSettings.ini так же, как это делает само меню бенча.
// Настройки лежат в двух местах и менять надо оба, иначе при старте игра перезапишет одно по другому:
//   UISettingData - пункты меню (качество 1..5)
//   [ScalabilityGroups] - то же самое для движка (0..4)
internal static class SettingsWriter
{
    public const string MainSection = "/Script/GSGameSettings.GSGameUserSettings";
    public const string ScalabilitySection = "ScalabilityGroups";

    private static readonly string[] UiQualityKeys =
    {
        "ViewDistance", "AntiAliasing", "PostProcessing", "ShadowQuality", "TextureQuality",
        "FxQuality", "MaterialQuality", "VegetationQuality", "GlobalIllumination", "ReflectionQuality",
    };

    private static readonly string[] ScalabilityQualityKeys =
    {
        "sg.ViewDistanceQuality", "sg.AntiAliasingQuality", "sg.ShadowQuality", "sg.GlobalIlluminationQuality",
        "sg.ReflectionQuality", "sg.PostProcessQuality", "sg.TextureQuality", "sg.EffectsQuality",
        "sg.FoliageQuality", "sg.ShadingQuality",
    };

    public static void Apply(IniFile ini, BenchmarkProfile p)
    {
        var ui = UiSettingData.Parse(ini.Get(MainSection, "UISettingData"));

        // "Окно без рамок": окно всегда размером с экран, а разрешение задаётся пунктом меню
        // + долей WindowFullImageQuality. Сверял с тем, что пишет сам бенч.
        ui["ScreenMode"] = "1";
        ui["ScreenResolution"] = Str(p.ResolutionIndex);
        ui["WindowFullImageQuality"] = Str(p.WindowScaleMillionths);
        ui["LockFrameRate"] = "0";
        ui["Vsync"] = "0";
        ui["MotionBlur"] = Str(p.MotionBlur);
        ui["ImageQuality"] = Str(p.RenderHeight);
        ui["SuperResolutionSampling"] = Str(BenchmarkProfile.UpscalerTsr);
        ui["InsertFrame"] = "0";
        ui["Rtx"] = p.RayTracing ? "1" : "0";
        ui["RtxLevel"] = Str(p.RayTracingLevel);
        ui["QualityLevel"] = Str(p.Quality);
        foreach (var key in UiQualityKeys) ui[key] = Str(p.Quality);
        ini.Set(MainSection, "UISettingData", ui.ToString());

        // ВАЖНО: тут всегда разрешение экрана, как ставит меню. Если оставить меньше (у меня было 1280x720),
        // оно ещё умножается на WindowFullImageQuality и масштаб рендера, кадр получается крошечный
        // и игра падает на старте с EXCEPTION_ACCESS_VIOLATION.
        ini.Set(MainSection, "DesiredScreenWidth", Str(p.Native.Width));
        ini.Set(MainSection, "DesiredScreenHeight", Str(p.Native.Height));
        ini.Set(MainSection, "LastUserConfirmedDesiredScreenWidth", Str(p.Native.Width));
        ini.Set(MainSection, "LastUserConfirmedDesiredScreenHeight", Str(p.Native.Height));

        ini.Set(MainSection, "FullscreenMode", "1");
        ini.Set(MainSection, "LastConfirmedFullscreenMode", "1");
        ini.Set(MainSection, "PreferredFullscreenMode", "1");
        ini.Set(MainSection, "ResolutionSizeX", Str(p.Native.Width));
        ini.Set(MainSection, "ResolutionSizeY", Str(p.Native.Height));
        ini.Set(MainSection, "LastUserConfirmedResolutionSizeX", Str(p.Native.Width));
        ini.Set(MainSection, "LastUserConfirmedResolutionSizeY", Str(p.Native.Height));
        ini.Set(MainSection, "bUseVSync", "False");
        ini.Set(MainSection, "bUseDynamicResolution", "False");
        ini.Set(MainSection, "FrameRateLimit", "0.000000");

        ini.Set(ScalabilitySection, "sg.ResolutionQuality", Str(p.RenderScale));
        foreach (var key in ScalabilityQualityKeys) ini.Set(ScalabilitySection, key, Str(p.Quality - 1));

        // Эти секции меню тоже пишет. EnableInGame обязательно вместе с Rtx: при Rtx=1 и EnableInGame=False
        // бенч пишет в результате, что RT включён, а по факту нагрузка даже ниже, чем без RT
        // (16 FPS против 4-5 на 3050). Похоже, растеризацию он упрощает, а сам RT не включается.
        ini.Set(RayTracingSection, "r.RayTracing.EnableInGame", p.RayTracing ? "True" : "False");
        if (StreamingPoolSizeMb.TryGetValue(p.Quality, out var pool))
            ini.Set(RenderSection, "GSStreamingPoolSize", Str(pool));
    }

    public const string RayTracingSection = "RayTracing";
    public const string RenderSection = "GSRenderSetting";

    // Пул стриминга текстур (МБ), который ставит меню для пресета. Значения с 3050 Laptop, на других картах могут быть другие.
    private static readonly Dictionary<int, int> StreamingPoolSizeMb = new() { [1] = 384, [5] = 640 };

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);
}
