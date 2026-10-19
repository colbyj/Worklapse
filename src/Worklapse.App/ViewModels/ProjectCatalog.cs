using Avalonia.Media;
using Avalonia.Media.Immutable;
using Worklapse.App.Theming;
using Worklapse.Core.Projects;

namespace Worklapse.App.ViewModels;

/// <summary>The projects and categories as read with a period's history, to name and colour what it shows.</summary>
public sealed record ProjectCatalog(IReadOnlyDictionary<long, Project> Projects, IReadOnlyDictionary<long, Category> Categories)
{
    public static ProjectCatalog Read(ProjectStore store) =>
        new(store.GetProjects().ToDictionary(p => p.Id), store.GetCategories().ToDictionary(c => c.Id));

    /// <summary>The project's name, marked when archived; projects deleted since are named as such.</summary>
    public string NameOf(long projectId) => Projects.TryGetValue(projectId, out var p)
        ? p.Archived ? $"{p.Name} (archived)" : p.Name
        : "Deleted project";

    public IBrush BrushOf(long projectId) => Projects.TryGetValue(projectId, out var p)
        ? ProjectColors.Brush(ProjectStore.ColorOf(p, Categories))
        : AppBrushes.NotRecorded;

    /// <summary>The project's category; null without one (or for a project deleted since).</summary>
    public Category? CategoryOf(long projectId) =>
        Projects.GetValueOrDefault(projectId)?.CategoryId is { } id ? Categories.GetValueOrDefault(id) : null;

    /// <summary>
    /// Projects grouped by category: categories by name, then the projects without one (a null category); projects by
    /// name inside each.
    /// </summary>
    public IEnumerable<(Category? Category, List<long> Projects)> ByCategory(IEnumerable<long> projects) =>
        projects.GroupBy(CategoryOf)
            .OrderBy(g => g.Key is null)
            .ThenBy(g => g.Key?.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (g.Key, g.OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase).ToList()));

    /// <summary>A category's colour; projects without a category share the default one.</summary>
    public static IBrush BrushOf(Category? category) => ProjectColors.Brush(category?.Color ?? ProjectStore.DefaultColor);
}

internal static class ProjectColors
{
    // One brush per colour, so views refreshed with the same projects get the same brushes back (nothing to redraw).
    // Project colours come from a small palette; used on the UI thread only.
    private static readonly Dictionary<int, IBrush> Brushes = [];

    public static IBrush Brush(int rgb)
    {
        if (!Brushes.TryGetValue(rgb, out var brush))
            Brushes[rgb] = brush = new ImmutableSolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return brush;
    }
}
