using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Worklapse.Core.Diagnostics;

namespace Worklapse.App.Views;

/// <summary>An unexpected error, and where the log with its details is.</summary>
public partial class ErrorWindow : Window
{
    // Needed by the XAML loader; the app always uses the other constructor.
    public ErrorWindow() => InitializeComponent();

    /// <param name="heading">What happened, in a line.</param>
    /// <param name="message">The error's own message.</param>
    /// <param name="explanation">What it means for recording.</param>
    public ErrorWindow(string heading, string message, string explanation) : this()
    {
        HeadingText.Text = heading;
        MessageText.Text = message;
        ExplanationText.Text = $"{explanation} The details are in the log, in {Log.Folder}.";
    }

    private async void OnOpenLogFolder(object? sender, RoutedEventArgs e)
    {
        var folder = new DirectoryInfo(Log.Folder);
        if (!folder.Exists || !await Launcher.LaunchDirectoryInfoAsync(folder))
        {
            OpenError.Text = $"Couldn't open {folder.FullName}.";
            OpenError.IsVisible = true;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
