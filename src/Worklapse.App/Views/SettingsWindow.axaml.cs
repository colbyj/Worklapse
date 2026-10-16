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
        if (ViewModel.IsDirty)
            ViewModel.Apply();
        Close();
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
