using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Worklapse.App.ViewModels;

namespace Worklapse.App.Views;

/// <summary>
/// Export Timelapse: the choices, then the export's progress. Cancel stops an export under way; closing the window
/// stops it too.
/// </summary>
public partial class TimelapseExportWindow : Window
{
    // Needed by the XAML loader; the app always uses the other constructor.
    public TimelapseExportWindow() => InitializeComponent();

    public TimelapseExportWindow(TimelapseExportViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.ExportRequested += () => ChooseFileAndExport(viewModel);
        Closing += (_, _) => viewModel.Cancel();
    }

    private TimelapseExportViewModel ViewModel => (TimelapseExportViewModel)DataContext!;

    private async Task ChooseFileAndExport(TimelapseExportViewModel viewModel)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Timelapse",
            SuggestedFileName = viewModel.FileName,
            DefaultExtension = "mp4",
            FileTypeChoices = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4"] }],
            ShowOverwritePrompt = true,
        });
        if (file is null)
            return;
        string? path;
        using (file)
            path = file.TryGetLocalPath();
        if (path is null)
        {
            viewModel.ErrorText = "Choose a folder on this computer.";
            return;
        }
        await viewModel.ExportAsync(path);
    }

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SavedPath is { } path && File.Exists(path))
            await Launcher.LaunchFileInfoAsync(new FileInfo(path));
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SavedPath is { } path && Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
    }

    /// <summary>Cancel while exporting (the window stays, to choose again); otherwise Close.</summary>
    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsExporting)
            ViewModel.Cancel();
        else
            Close();
    }
}
