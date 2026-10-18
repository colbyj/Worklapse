using Worklapse.Core.Platform;
using Worklapse.Core.Recording;
using Worklapse.Core.Storage;

namespace Worklapse.Tests;

/// <summary>Records captures the way the recorder does: each frame together with the capture showing it.</summary>
internal static class TestRecording
{
    /// <summary>Adds a capture with a new frame of one monitor; returns the capture and its frame.</summary>
    public static (long CaptureId, FrameRef Frame) Append(RecordingStore store, IFrameSink sink, MonitorInfo monitor, long monitorId,
        byte[] i420, DateTimeOffset time)
    {
        using var transaction = store.BeginTransaction();
        var captureId = store.InsertCapture(time, CaptureKind.Active, null, null, null);
        var capture = new ScreenCapture { Monitor = monitor, Width = monitor.Bounds.Width, Height = monitor.Bounds.Height, Frame = i420, Hash = 0 };
        var frame = sink.Append(monitorId, capture, time);
        store.InsertFrame(captureId, monitorId, frame);
        transaction.Commit();
        sink.Committed();
        return (captureId, frame);
    }

    /// <summary>Adds a capture where the monitor didn't change: it shows an earlier frame.</summary>
    public static long AppendUnchanged(RecordingStore store, long monitorId, FrameRef frame, DateTimeOffset time)
    {
        using var transaction = store.BeginTransaction();
        var captureId = store.InsertCapture(time, CaptureKind.Active, null, null, null);
        store.InsertFrame(captureId, monitorId, frame);
        transaction.Commit();
        return captureId;
    }
}
