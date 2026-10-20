using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Worklapse.App.ViewModels;

namespace Worklapse.App.Views;

/// <summary>Imports TimeSnapper's history. Closing it while importing stops the import first.</summary>
public partial class TimeSnapperImportWindow : Window
{
    private Task? _import;

    public TimeSnapperImportWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private TimeSnapperImportViewModel ViewModel => (TimeSnapperImportViewModel)DataContext!;

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_import is not null)
            return;
        _import = ViewModel.ImportAsync();
        try
        {
            await _import;
        }
        finally
        {
            _import = null;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsRunning)
            ViewModel.Stop();
        else
            Close();
    }

    /// <summary>Waits for a running import to stop, then closes.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // IsRunning goes false before the import's task completes, so the Close below goes through.
        if (!ViewModel.IsRunning || _import is not { } import)
            return;
        e.Cancel = true;
        ViewModel.Stop();
        await import;
        Close();
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var current = Path.GetDirectoryName(ViewModel.DatabasePath.Trim());
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the TimeSnapper database",
            AllowMultiple = false,
            SuggestedStartLocation = Directory.Exists(current) ? await StorageProvider.TryGetFolderFromPathAsync(current) : null,
            FileTypeFilter = [new FilePickerFileType("TimeSnapper database") { Patterns = ["*.fdb"] }, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            ViewModel.DatabasePath = path;
    }
}
