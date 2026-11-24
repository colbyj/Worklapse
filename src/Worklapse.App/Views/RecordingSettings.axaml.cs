using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Worklapse.App.ViewModels;

namespace Worklapse.App.Views;

/// <summary>
/// What is captured, when capturing stops, how captures are stored and where: Settings' Recording tab, also shown by the
/// welcome window. Its window applies the changes (and disposes of the preview).
/// </summary>
public partial class RecordingSettings : UserControl
{
    public RecordingSettings() => InitializeComponent();

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext!;

    private TopLevel Top => TopLevel.GetTopLevel(this)!;

    /// <summary>Starts the preview of the stored picture; call once the control is laid out (it fills its box at 1:1).</summary>
    public void StartPreview()
    {
        if (ViewModel.Preview is not { } preview)
            return;
        var (format, quality) = ViewModel.ChosenStorage;
        var scaling = Top.RenderScaling;
        var size = PreviewBox.Bounds.Size; // includes its 1 px border
        preview.Start(scaling, (int)((size.Width - 2) * scaling), (int)((size.Height - 2) * scaling), format, quality,
            ViewModel.ChosenInterval);
    }

    private void OnDefaultFolder(object? sender, RoutedEventArgs e) => ViewModel.UseDefaultDataFolder();

    /// <summary>Imports into the history folder in use (not one typed in the box and not applied yet).</summary>
    private async void OnImportTimeSnapper(object? sender, RoutedEventArgs e)
    {
        var applied = ViewModel.AppliedSettings;
        var import = new TimeSnapperImportViewModel(applied.DataFolder, applied.MaxCaptureLength);
        await new TimeSnapperImportWindow { DataContext = import }.ShowDialog((Window)Top);
        if (import.Imported)
            await ViewModel.HistoryImportedAsync();
    }

    /// <summary>Shows the folder in the box (applied or not) in the file manager.</summary>
    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.DataFolderExists)
            await Top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(ViewModel.DataFolder.Trim()));
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await Top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the history folder",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            ViewModel.DataFolder = path;
    }
}
