using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Worklapse.App.ViewModels;

namespace Worklapse.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

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
