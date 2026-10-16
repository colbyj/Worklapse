using Worklapse.Core.Platform;
using Worklapse.Core.Storage;

namespace Worklapse.Core.Recording;

/// <summary>Stores captured monitor images as video segments.</summary>
public interface IFrameSink : IDisposable
{
    /// <summary>
    /// Adds <paramref name="capture"/> as the next frame of the monitor's open segment, starting a new
    /// segment when none is open, the size changed, or the open one is full. Called inside the recorder's
    /// database transaction.
    /// </summary>
    FrameRef Append(long monitorId, ScreenCapture capture, DateTimeOffset timestamp);

    /// <summary>Called after the transaction that contained the latest <see cref="Append"/> calls commits.</summary>
    void Committed();

    /// <summary>Finishes all open segments so the next frame of each monitor starts a new one.</summary>
    void CloseAll();
}
