using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Worklapse.App.Views;

/// <summary>Asks before something that can't be undone. Cancel is the default button.</summary>
public partial class ConfirmWindow : Window
{
    // Needed by the XAML loader; the app always uses the other constructor.
    public ConfirmWindow() => InitializeComponent();

    public ConfirmWindow(string question, string detail, string confirm) : this()
    {
        QuestionText.Text = question;
        DetailText.Text = detail;
        ConfirmButton.Content = confirm;
    }

    /// <summary>Shows the question over <paramref name="owner"/>; true when confirmed.</summary>
    public static Task<bool> Ask(Window owner, string question, string detail, string confirm) =>
        new ConfirmWindow(question, detail, confirm).ShowDialog<bool>(owner);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
