using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Worklapse.App.ViewModels;

namespace Worklapse.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // The preview fills its box at 1:1, so it starts once the box has its size.
        Opened += (_, _) => Dispatcher.UIThread.Post(StartPreview, DispatcherPriority.Loaded);
        Closed += (_, _) => (DataContext as SettingsViewModel)?.Preview?.Dispose();
    }

    private void StartPreview()
    {
        if (ViewModel.Preview is not { } preview)
            return;
        var (format, quality) = ViewModel.ChosenStorage;
        var size = PreviewBox.Bounds.Size; // includes its 1 px border
        preview.Start(RenderScaling, (int)((size.Width - 2) * RenderScaling), (int)((size.Height - 2) * RenderScaling), format, quality,
            ViewModel.ChosenInterval);
    }

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext!;

    private void OnDefaults(object? sender, RoutedEventArgs e) => ViewModel.RestoreDefaults();

    private void OnApply(object? sender, RoutedEventArgs e) => ViewModel.Apply();

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        // OK is disabled without a history folder; this holds however it gets pressed (it's the default button).
        if (!ViewModel.HasDataFolder)
            return;
        if (ViewModel.IsDirty)
            ViewModel.Apply();
        Close();
    }

    private void OnDefaultFolder(object? sender, RoutedEventArgs e) => ViewModel.UseDefaultDataFolder();

    /// <summary>Shows the folder in the box (applied or not) in the file manager.</summary>
    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.DataFolderExists)
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(ViewModel.DataFolder.Trim()));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the history folder",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            ViewModel.DataFolder = path;
    }
}
