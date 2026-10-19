using Avalonia.Controls;
using Worklapse.App.ViewModels;

namespace Worklapse.App.Views;

/// <summary>Stands in for the Overview and the Timesheet until they're built.</summary>
public partial class PlaceholderView : UserControl
{
    public PlaceholderView() => InitializeComponent();

    public void Show(string title, string body, RecordingViewModel recording)
    {
        TitleText.Text = title;
        BodyText.Text = body;
        Messages.DataContext = recording;
        Indicator.DataContext = recording;
    }
}
