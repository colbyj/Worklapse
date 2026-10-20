using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Worklapse.App.ViewModels;

/// <summary>An app (exe name) a window title rule can be limited to, or <see cref="Any"/>.</summary>
public sealed record AppChoice(string? Name)
{
    public static AppChoice Any { get; } = new((string?)null);

    public bool IsAny => Name is null;
    public string DisplayName => Name ?? "Any app";
}

/// <summary>A line of a project's window titles grid on the Projects tab.</summary>
public sealed partial class TitleRuleItem : ObservableObject
{
    private readonly ProjectsViewModel _owner;

    public TitleRuleItem(ProjectsViewModel owner, long? id, string text, AppChoice app)
    {
        _owner = owner;
        Id = id;
        _text = text;
        _app = app;
    }

    /// <summary>Null until it's saved.</summary>
    public long? Id { get; set; }

    [ObservableProperty] private string _text;

    private AppChoice _app;

    /// <summary>The app it's limited to, or <see cref="AppChoice.Any"/> (also what a list that loses its selection means).</summary>
    public AppChoice App
    {
        get => _app;
        set => SetProperty(ref _app, value ?? AppChoice.Any);
    }

    /// <summary>The time of the recorded titles it matches, all history; a dash with no text to look for.</summary>
    [ObservableProperty] private string _matchedText = "—";

    /// <summary>What it looks for: surrounding spaces don't count (they don't show in the grid either).</summary>
    public string Trimmed => Text.Trim();

    /// <summary>The apps to pick from: "Any app", then every app with a recorded title.</summary>
    public IReadOnlyList<AppChoice> AppChoices => _owner.AppChoices;
}

/// <summary>A recorded title the selected rule matches, under the grid.</summary>
/// <param name="TakenBy">Another project whose rule decides for this title instead, if any.</param>
public sealed record TitleMatchRow(string App, string Title, string TimeText, ProjectItem? TakenBy)
{
    public bool IsTaken => TakenBy is not null;
    public string TakenByName => TakenBy?.Name ?? "";
    public IBrush TakenByBrush => TakenBy?.Brush ?? Brushes.Transparent;
}
