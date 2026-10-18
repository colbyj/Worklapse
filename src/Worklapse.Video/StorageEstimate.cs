using Worklapse.Core.Storage;

namespace Worklapse.Video;

/// <summary>
/// A running estimate of the space a format takes, from captures given to it as the recorder stores them: an
/// unchanged screen takes nothing (the capture shows the frame before), a changed one an image, or for video the next
/// frame of a segment. A segment starts with a key frame and its later frames store only what changed, so video's
/// average falls as captures come in. One monitor; use it from one thread.
/// </summary>
public sealed class StorageEstimate(FrameFormat format, int quality) : IDisposable
{
    private Av1Encoder? _encoder;
    private int _segmentFrames;

    public FrameFormat Format { get; } = format;
    public int Quality { get; } = quality;
    public int Captures { get; private set; }
    public long Bytes { get; private set; }
    public double AveragePerCapture => Captures == 0 ? 0 : (double)Bytes / Captures;

    /// <summary>Adds a capture; returns the bytes it takes.</summary>
    /// <param name="changed">False when the screen shows what it did at the capture before.</param>
    public int Add(ReadOnlySpan<byte> i420, int width, int height, bool changed, long timestampMs)
    {
        Captures++;
        if (!changed && Captures > 1)
            return 0;

        int bytes;
        if (Format == FrameFormat.Video)
        {
            // A new segment, as the recorder starts one, when the size changes or the segment is full.
            if (_encoder is null || _encoder.Width != width || _encoder.Height != height || _segmentFrames >= FrameSink.FramesPerSegment)
            {
                _encoder?.Dispose();
                _encoder = new Av1Encoder(width, height, Quality, threads: BatchEncoder.EncoderThreads);
                _segmentFrames = 0;
            }
            bytes = _encoder.Encode(i420, timestampMs).Data.Length;
            _segmentFrames++;
        }
        else
        {
            using var image = ImageCodec.Encode(Format, i420, width, height, Quality);
            bytes = image.Length;
        }
        Bytes += bytes;
        return bytes;
    }

    public void Dispose()
    {
        _encoder?.Dispose();
        _encoder = null;
    }
}
