using Avalonia.Controls;
using Avalonia.Interactivity;
using Worklapse.App.ViewModels;

namespace Worklapse.App.Views;

/// <summary>Read Past Days' progress. Closing it leaves the reading going.</summary>
public partial class PastReadingWindow : Window
{
    public PastReadingWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as PastReadingViewModel)?.Dispose();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
