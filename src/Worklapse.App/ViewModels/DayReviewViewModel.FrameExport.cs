using Worklapse.App.Export;
using Worklapse.Core.Export;
using Worklapse.Video;

namespace Worklapse.App.ViewModels;

public sealed partial class DayReviewViewModel
{
    public bool CanExportFrame => Current >= 0 && Current < Captures.Count;

    /// <summary>A file name to suggest for Export Frame: the day and the capture's time.</summary>
    public string FrameFileName => CanExportFrame
        ? $"Worklapse {Captures[Current].Time.ToLocalTime():yyyy-MM-dd HH.mm.ss}"
        : "Worklapse";

    /// <summary>
    /// Saves the frame shown (its monitor choice and Focus on Active App, not the zoom) as an image at
    /// <paramref name="path"/>: JPEG for .jpg or .jpeg, else PNG.
    /// </summary>
    /// <exception cref="InvalidOperationException">The frame has nothing recorded to show.</exception>
    public async Task ExportFrameAsync(string path)
    {
        if (!CanExportFrame)
            return;
        var captureId = Captures[Current].Id;
        var monitor = SelectedMonitor?.Monitor;
        var focus = FocusOnApplication;
        var frame = TimelapsePlan.FrameAt(Day, Current, monitor, focus)
                    ?? throw new InvalidOperationException("Nothing was recorded on the monitors shown.");
        try
        {
            await FrameImageExporter.SaveAsync(_settings.Current.DataFolder, frame, path);
        }
        catch (FrameUnavailableException)
        {
            // Reclaiming the space of deleted captures, or encoding today's segment, moved the frame: find it again in
            // the day as it is now, once.
            var day = _history.LoadDay(Day.Day);
            var index = day.Captures.ToList().FindIndex(c => c.Id == captureId);
            if (index < 0 || TimelapsePlan.FrameAt(day, index, monitor, focus) is not { } moved)
                throw new InvalidOperationException("The frame is no longer stored.");
            await FrameImageExporter.SaveAsync(_settings.Current.DataFolder, moved, path);
        }
    }
}
