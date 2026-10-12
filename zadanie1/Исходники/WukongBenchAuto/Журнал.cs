namespace WukongBenchAuto;

// Лог в консоль: время + цвет по типу сообщения.
internal static class Log
{
    private static readonly object Sync = new();

    public static void Info(string message) => Write(ConsoleColor.Gray, message);
    public static void Step(string message) => Write(ConsoleColor.Cyan, message);
    public static void Ok(string message) => Write(ConsoleColor.Green, message);
    public static void Warn(string message) => Write(ConsoleColor.Yellow, message);
    public static void Error(string message) => Write(ConsoleColor.Red, message);

    private static void Write(ConsoleColor color, string message)
    {
        lock (Sync)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ForegroundColor = previous;
        }
    }
}
