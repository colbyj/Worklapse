namespace Worklapse.Core.Projects;

/// <summary>
/// Marks the active time of windows whose title contains <paramref name="Text"/> with a project: in any app, or only in
/// <paramref name="App"/>. Case is ignored, in the title and in the app's exe name.
/// </summary>
/// <param name="Id">Breaks ties between rules equally specific: the older one decides.</param>
/// <param name="App">An exe name, as apps are recorded; null for any app.</param>
public sealed record TitleRule(long Id, long ProjectId, string Text, string? App)
{
    public bool Matches(string? app, string? title) =>
        Text.Length > 0 && title is not null && title.Contains(Text, StringComparison.OrdinalIgnoreCase)
        && (App is null || string.Equals(app, App, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The order rules decide in when several match one title: a rule for one app before a rule for any app, then the
    /// longer text, then the older rule.
    /// </summary>
    public static IComparer<TitleRule> Precedence { get; } = Comparer<TitleRule>.Create((a, b) =>
    {
        var byApp = (b.App is not null).CompareTo(a.App is not null);
        if (byApp != 0)
            return byApp;
        var byLength = b.Text.Length.CompareTo(a.Text.Length);
        return byLength != 0 ? byLength : a.Id.CompareTo(b.Id);
    });
}

/// <summary>Every project's window title rules, in the order they decide (<see cref="TitleRule.Precedence"/>).</summary>
public sealed class TitleRuleSet
{
    private readonly TitleRule[] _rules;

    public TitleRuleSet(IEnumerable<TitleRule> rules) =>
        _rules = rules.Where(r => r.Text.Length > 0).Order(TitleRule.Precedence).ToArray();

    public static TitleRuleSet Empty { get; } = new([]);

    public bool IsEmpty => _rules.Length == 0;

    /// <summary>The rule that decides for a window of <paramref name="app"/> titled <paramref name="title"/>, if any.</summary>
    public TitleRule? Match(string? app, string? title)
    {
        if (string.IsNullOrEmpty(title))
            return null;
        foreach (var rule in _rules)
            if (rule.Matches(app, title))
                return rule;
        return null;
    }

    /// <summary>The project of the rule that decides for the window, if any.</summary>
    public long? ProjectOf(string? app, string? title) => Match(app, title)?.ProjectId;
}
