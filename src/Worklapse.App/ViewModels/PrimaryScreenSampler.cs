using Worklapse.Core.Platform;

namespace Worklapse.App.ViewModels;

/// <summary>A monitor's pixels as captured (packed I420), and their hash: equal hashes mean an unchanged screen.</summary>
public sealed record CapturedScreen(byte[] I420, int Width, int Height, ulong Hash);

/// <summary>
/// Captures the primary monitor for the recording preview, with a screen source of its own: the recorder's is used on
/// the recorder's thread and reuses its buffers.
/// </summary>
public sealed class PrimaryScreenSampler(IScreenSource source) : IDisposable
{
    /// <summary>The primary monitor as it looks now (a copy); null if it can't be read.</summary>
    public CapturedScreen? Capture()
    {
        var monitors = source.GetMonitors();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        return primary is not null && source.Capture(primary) is { } capture
            ? new CapturedScreen(capture.Frame.ToArray(), capture.Width, capture.Height, capture.Hash)
            : null;
    }

    public void Dispose() => source.Dispose();
}
