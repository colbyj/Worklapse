using Avalonia;
using Worklapse.Core.Settings;

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
            // Software rendering on Windows unless the settings ask for the GPU: GPU rendering (ANGLE on
            // Direct3D 11) cost ~80 MB more while recording and ~450 MB more while reviewing four monitors
            // (texture copies of each frame), and kept ~200 MB after the Day review closed.
            .With(new Win32PlatformOptions
            {
                RenderingMode = UseGpuRendering() ? [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software] : [Win32RenderingMode.Software],
            })
            // The same choice on Linux (X11, through XWayland on a Wayland desktop); not measured there yet.
            .With(new X11PlatformOptions
            {
                RenderingMode = UseGpuRendering() ? [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software] : [X11RenderingMode.Software],
            })
            .LogToTrace();

    private static bool UseGpuRendering()
    {
        try
        {
            return new SettingsStore().Current.UseGpuRendering;
        }
        catch (SettingsFileException)
        {
            return false; // the app reports the broken file once it starts
        }
    }
}
