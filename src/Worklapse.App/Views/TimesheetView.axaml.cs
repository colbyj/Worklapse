using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Worklapse.App.ViewModels;

namespace Worklapse.App.Views;

/// <summary>The Timesheet inside the main window: a line per project per day, with logged time typed into the grid.</summary>
public partial class TimesheetView : UserControl
{
    private readonly TimesheetViewModel _viewModel;

    // Needed by the XAML loader; the app always uses the other constructor.
    public TimesheetView() : this(null!, null!) { }

    public TimesheetView(TimesheetViewModel viewModel, RecordingViewModel recording)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        if (viewModel is null)
            return; // designer
        Messages.DataContext = recording;
        Indicator.DataContext = recording;
    }

    /// <summary>Double-clicking a day's heading opens it in Day review, like its date (whose own click already did).</summary>
    private void OnDayDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (sender is Control { DataContext: TimesheetDayRow day })
            day.Open.Execute(null);
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Timesheet",
            SuggestedFileName = _viewModel.ExportFileName,
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV file") { Patterns = ["*.csv"] }],
            ShowOverwritePrompt = true,
        });
        if (file is null)
            return;
        using (file)
            await _viewModel.ExportAsync(file.Name, file.OpenWriteAsync);
    }
}
