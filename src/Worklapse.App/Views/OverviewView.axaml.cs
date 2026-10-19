using Avalonia.Controls;
using Worklapse.App.ViewModels;

namespace Worklapse.App.Views;

/// <summary>The Overview inside the main window: the week's timelines or the month's days, totals and notes.</summary>
public partial class OverviewView : UserControl
{
    // Needed by the XAML loader; the app always uses the other constructor.
    public OverviewView() : this(null!, null!) { }

    public OverviewView(OverviewViewModel viewModel, RecordingViewModel recording)
    {
        InitializeComponent();
        DataContext = viewModel;
        if (viewModel is null)
            return; // designer
        Messages.DataContext = recording;
        Indicator.DataContext = recording;
    }
}
