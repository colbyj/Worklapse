using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Worklapse.App.ViewModels;

namespace Worklapse.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // The preview fills its box at 1:1, so it starts once the box has its size.
        Opened += (_, _) => Dispatcher.UIThread.Post(Recording.StartPreview, DispatcherPriority.Loaded);
        Closed += (_, _) =>
        {
            (DataContext as SettingsViewModel)?.Preview?.Dispose();
            // Read Past Days carries on in the background.
            (DataContext as SettingsViewModel)?.Detach();
        };
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

    /// <summary>A window of its own, not owned by this dialog, so it stays open once Settings is closed.</summary>
    private void OnOpenVideoFiles(object? sender, RoutedEventArgs e) => new VideoFilesWindow().Show();

    private async void OnOpenLicense(object? sender, RoutedEventArgs e) => await OpenFileAsync(AboutViewModel.LicensePath);

    private async void OnOpenNotices(object? sender, RoutedEventArgs e) => await OpenFileAsync(AboutViewModel.NoticesPath);

    /// <summary>Opens a text file beside the executable in the desktop's viewer for it.</summary>
    private async Task OpenFileAsync(string path)
    {
        if (File.Exists(path))
            await Launcher.LaunchFileInfoAsync(new FileInfo(path));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Deletes the selected category or project, asking first when it's in the history already.</summary>
    private async void OnDeleteProject(object? sender, RoutedEventArgs e)
    {
        var projects = ViewModel.Projects;
        var (question, detail) = projects.Selected switch
        {
            CategoryItem c => ($"Delete the category “{c.Name}”?", "Its projects are kept, without a category."),
            ProjectItem p => ($"Delete the project “{p.Name}”?", "Its time set by hand, notes and logged time go too; archiving keeps them."),
            _ => (null, null),
        };
        if (question is null)
            return;
        if (projects.SelectedIsSaved && !await ConfirmWindow.Ask(this, question, detail!, "Delete"))
            return;
        projects.DeleteCommand.Execute(null);
    }

    /// <summary>Adds a window title rule to the selected project and puts the cursor in its text.</summary>
    private void OnAddTitleRule(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.Projects.AddTitleRule() is not { } rule)
            return;
        var list = this.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Classes.Contains("titleRules"));
        if (list is null)
            return;
        list.ScrollIntoView(rule);
        Dispatcher.UIThread.Post(() => list.ContainerFromItem(rule)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus(),
            DispatcherPriority.Loaded);
    }

    private void OnRemoveTitleRule(object? sender, RoutedEventArgs e) => ViewModel.Projects.RemoveTitleRule();

    /// <summary>
    /// Typing in a rule's cell selects its row, so the titles under the grid are its: the cells take the click, the
    /// list doesn't see it.
    /// </summary>
    private void OnTitleRuleGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control { DataContext: TitleRuleItem rule } && sender is ListBox { DataContext: ProjectItem project })
            project.SelectedRule = rule;
    }
}
