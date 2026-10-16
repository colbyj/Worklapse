using Avalonia;

namespace Worklapse.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Two recorders would write the same database and video folders; only one instance may run.
        using var instance = new Mutex(initiallyOwned: true, $"Worklapse-{Environment.UserName}", out var isFirst);
        if (!isFirst)
            return 1;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
