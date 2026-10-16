using Avalonia.Controls;
using Avalonia.Interactivity;
using Worklapse.Core.Settings;

namespace Worklapse.App.Views;

/// <summary>Shown at startup when the settings file exists but can't be read.</summary>
public partial class SettingsErrorWindow : Window
{
    private readonly SettingsFileException _error = null!;

    // Needed by the XAML loader; the app always uses the other constructor.
    public SettingsErrorWindow() => InitializeComponent();

    public SettingsErrorWindow(SettingsFileException error) : this()
    {
        _error = error;
        PathText.Text = error.Path;
        ErrorText.Text = error.InnerException?.Message ?? error.Message;
        DefaultsText.Text = $"With default settings, Worklapse records all monitors to {WorklapseSettings.DefaultDataFolder}.";
    }

    /// <summary>True when the broken file was set aside and Worklapse should start from defaults.</summary>
    public bool StartWithDefaults { get; private set; }

    private void OnStartWithDefaults(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Gone already (fixed or removed by hand): nothing to set aside.
            if (File.Exists(_error.Path))
                SettingsStore.SetAside(_error.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetAsideError.Text = "Couldn't rename the file: " + ex.Message;
            SetAsideError.IsVisible = true;
            return;
        }
        StartWithDefaults = true;
        Close();
    }

    private void OnQuit(object? sender, RoutedEventArgs e) => Close();
}
