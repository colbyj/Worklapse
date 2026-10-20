using CommunityToolkit.Mvvm.ComponentModel;
using Worklapse.Core.Import;
using Worklapse.Core.Storage;

namespace Worklapse.App.ViewModels;

/// <summary>Imports TimeSnapper's history into the data folder (see <see cref="TimeSnapperImport"/>), in the background.</summary>
/// <param name="maxCaptureLength">How long one of Worklapse's captures counts for at most: TimeSnapper's are left out there.</param>
public sealed partial class TimeSnapperImportViewModel(string dataFolder, TimeSpan maxCaptureLength) : ObservableObject
{
    private CancellationTokenSource? _cancel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private string _databasePath = TimeSnapperSource.DefaultDatabase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport), nameof(CloseText))]
    private bool _isRunning;

    /// <summary>0–100; shown once an import has started.</summary>
    [ObservableProperty] private double _progress;

    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _detail = "";

    public bool CanImport => !IsRunning && DatabasePath.Trim().Length > 0;

    public string CloseText => IsRunning ? "Stop" : "Close";

    /// <summary>Something was imported: the history has new captures, apps and sites.</summary>
    public bool Imported { get; private set; }

    public async Task ImportAsync()
    {
        if (!CanImport)
            return;
        IsRunning = true;
        HasProgress = true;
        Progress = 0;
        Status = "Reading the TimeSnapper database…";
        Detail = "";
        _cancel = new CancellationTokenSource();
        var cancel = _cancel.Token;
        var database = DatabasePath.Trim();
        var progress = new Progress<TimeSnapperProgress>(p =>
        {
            if (!IsRunning)
                return; // reported late, after the end
            Progress = p.Total > 0 ? 100.0 * p.Read / p.Total : 0;
            Status = $"Imported {p.Imported:N0} captures…";
            if (p.Imported > 0)
                Imported = true;
        });

        try
        {
            var result = await Task.Run(() =>
            {
                using var source = TimeSnapperSource.Open(database, TimeSnapperSource.DefaultProgramFolder, cancel);
                using var db = WorklapseDatabase.Open(dataFolder);
                return TimeSnapperImport.Run(db, source, maxCaptureLength, progress, cancel);
            }, CancellationToken.None);
            if (result.Imported > 0)
                Imported = true;
            Progress = 100;
            Status = result.Imported > 0 ? $"Imported {result.Imported:N0} captures." : "Nothing new to import.";
            Detail = Describe(result);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. Importing again goes on from there.";
        }
        catch (Exception ex) when (ex is TimeSnapperException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            HasProgress = false;
            Status = ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cancel.Dispose();
            _cancel = null;
        }
    }

    public void Stop() => _cancel?.Cancel();

    /// <summary>What was left out, and why.</summary>
    private static string Describe(TimeSnapperResult result)
    {
        var parts = new List<string>();
        if (result.WithoutPicture > 0)
            parts.Add($"{result.WithoutPicture:N0} imported without a picture");
        if (result.AlreadyImported > 0)
            parts.Add($"{result.AlreadyImported:N0} imported before");
        if (result.RecordedByWorklapse > 0)
            parts.Add($"{result.RecordedByWorklapse:N0} recorded by Worklapse");
        if (result.PictureDeleted > 0)
            parts.Add($"{result.PictureDeleted:N0} with the picture deleted");
        if (result.Unreadable > 0)
            parts.Add($"{result.Unreadable:N0} with a picture that couldn't be opened");
        if (parts.Count == 0)
            return "";
        parts[0] = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        return string.Join("; ", parts) + ".";
    }
}
