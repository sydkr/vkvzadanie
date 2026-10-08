namespace WukongBenchAuto.Native;

internal sealed record DisplayMode(int Width, int Height, int RefreshRate)
{
    public override string ToString() => $"{Width}x{Height} @ {RefreshRate} Гц";
}

// Основной монитор: текущий режим и какие разрешения он умеет.
internal static class Display
{
    // Меню бенча показывает стандартные 16:9 разрешения не больше родного,
    // а в ini пишет не само разрешение, а номер пункта в этом списке.
    private static readonly (int W, int H)[] MenuResolutions =
    {
        (3840, 2160), (2560, 1440), (1920, 1080), (1600, 900), (1280, 720),
    };

    public static DisplayMode Current()
    {
        var mode = NativeMethods.DevMode.Create();
        return NativeMethods.EnumDisplaySettings(null, NativeMethods.EnumCurrentSettings, ref mode) && mode.PelsWidth > 0
            ? new DisplayMode(mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency)
            : new DisplayMode(1920, 1080, 60);
    }

    public static IReadOnlyCollection<(int W, int H)> SupportedResolutions()
    {
        var result = new HashSet<(int, int)>();
        var mode = NativeMethods.DevMode.Create();
        for (var i = 0; NativeMethods.EnumDisplaySettings(null, i, ref mode); i++)
            result.Add((mode.PelsWidth, mode.PelsHeight));
        return result;
    }

    // Номер разрешения в списке меню (0 - родное). Список собираю так же, как бенч; если промахнусь - есть --cpu-res-index
    public static int MenuIndexOf(int width, int height, DisplayMode native)
    {
        var supported = SupportedResolutions();
        var list = new List<(int W, int H)> { (native.Width, native.Height) };
        list.AddRange(MenuResolutions.Where(r =>
            r.W * r.H < native.Width * native.Height &&
            r.W <= native.Width && r.H <= native.Height &&
            (supported.Count == 0 || supported.Contains(r))));

        var index = list.FindIndex(r => r.W == width && r.H == height);
        return index >= 0 ? index : list.Count - 1;
    }
}
