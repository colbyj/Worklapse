using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;

namespace Worklapse.App.ViewModels;

/// <summary>A colour on the palette projects and categories pick from.</summary>
public sealed class PaletteColor(int rgb)
{
    public int Rgb { get; } = rgb;
    public IBrush Brush { get; } = ProjectColors.Brush(rgb);

    public static IReadOnlyList<PaletteColor> All { get; } =
    [
        new(0x138D75), new(0x16A085), new(0x2E7D32), new(0x00838F), new(0x3949AB), new(0x7D3C98),
        new(0x8E44AD), new(0xC0397A), new(0xAD1457), new(0xD35400), new(0xB9770E), new(0x5D6D7E),
    ];

    public static PaletteColor? Find(int? rgb) => rgb is { } value ? All.FirstOrDefault(c => c.Rgb == value) : null;
}

internal static class ProjectColors
{
    public static IBrush Brush(int rgb) => new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
}

/// <summary>A line in the Projects tab's list: a category, a project, or the heading over projects without a category.</summary>
public abstract partial class ProjectsEntry : ObservableObject
{
    public abstract bool IsHeading { get; }
}

public sealed class NoCategoryHeading : ProjectsEntry
{
    public static NoCategoryHeading Instance { get; } = new();
    public override bool IsHeading => true;
    public string Name => "No category";
}

public sealed partial class CategoryItem : ProjectsEntry
{
    /// <summary>"None" in a project's category list.</summary>
    public static CategoryItem None { get; } = new(null, "None", ProjectStore.DefaultColor);

    public CategoryItem(long? id, string name, int color)
    {
        Id = id;
        _name = name;
        _color = color;
    }

    /// <summary>Null until it's saved.</summary>
    public long? Id { get; set; }

    public override bool IsHeading => true;
    public bool IsNone => ReferenceEquals(this, None);

    [ObservableProperty] private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush), nameof(SelectedPaletteColor))]
    private int _color;

    public IBrush Brush => ProjectColors.Brush(Color);

    public IReadOnlyList<PaletteColor> Palette => PaletteColor.All;

    public PaletteColor? SelectedPaletteColor
    {
        get => PaletteColor.Find(Color);
        set
        {
            if (value is not null)
                Color = value.Rgb;
        }
    }
}

public sealed partial class ProjectItem : ProjectsEntry
{
    private readonly ProjectsViewModel _owner;

    public ProjectItem(ProjectsViewModel owner, long? id, string name, CategoryItem category, int? ownColor, bool archived)
    {
        _owner = owner;
        Id = id;
        _name = name;
        _category = category;
        _ownColor = ownColor;
        _archived = archived;
        Watch(category);
    }

    /// <summary>"None" in the Applications tab's project lists.</summary>
    public static ProjectItem None { get; } = new(null!, null, "None", CategoryItem.None, null, false);

    /// <summary>Null until it's saved.</summary>
    public long? Id { get; set; }

    public override bool IsHeading => false;
    public bool IsNone => ReferenceEquals(this, None);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name;

    private CategoryItem _category;

    /// <summary>Its category, or <see cref="CategoryItem.None"/> (also what a list that loses its selection means).</summary>
    public CategoryItem Category
    {
        get => _category;
        set
        {
            var old = _category;
            if (!SetProperty(ref _category, value ?? CategoryItem.None))
                return;
            if (!old.IsNone)
                old.PropertyChanged -= OnCategoryChanged;
            Watch(_category);
            OnPropertyChanged(nameof(Brush));
            OnPropertyChanged(nameof(CategoryBrush));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush), nameof(UsesCategoryColor), nameof(UsesOwnColor), nameof(SelectedPaletteColor))]
    private int? _ownColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private bool _archived;

    /// <summary>The name, marked when archived (it's still listed where it's in use).</summary>
    public string DisplayName => Archived ? $"{Name} (archived)" : Name;

    public IBrush Brush => ProjectColors.Brush(OwnColor ?? Category.Color);
    public IBrush CategoryBrush => Category.Brush;

    public bool UsesCategoryColor
    {
        get => OwnColor is null;
        set
        {
            if (value)
                OwnColor = null;
        }
    }

    public bool UsesOwnColor
    {
        get => OwnColor is not null;
        set
        {
            if (value && OwnColor is null)
                OwnColor = PaletteColor.All.FirstOrDefault(c => c.Rgb != Category.Color)?.Rgb ?? Category.Color;
        }
    }

    public IReadOnlyList<PaletteColor> Palette => PaletteColor.All;

    public PaletteColor? SelectedPaletteColor
    {
        get => PaletteColor.Find(OwnColor);
        set
        {
            if (value is not null)
                OwnColor = value.Rgb;
        }
    }

    public ObservableCollection<CategoryItem> CategoryChoices => _owner.CategoryChoices;

    /// <summary>The apps and sites linked to it on the Applications tab, as the dialog has them now.</summary>
    public string LinkedText => _owner.LinkedTextOf(this);

    internal void RefreshLinked() => OnPropertyChanged(nameof(LinkedText));

    // "None" never changes and outlives every dialog, so it isn't watched (it would keep the items alive).
    private void Watch(CategoryItem category)
    {
        if (!category.IsNone)
            category.PropertyChanged += OnCategoryChanged;
    }

    private void OnCategoryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CategoryItem.Color))
        {
            OnPropertyChanged(nameof(Brush));
            OnPropertyChanged(nameof(CategoryBrush));
        }
    }
}

/// <summary>
/// The Projects tab of the Settings dialog: categories and projects, their colours, and archiving. Like the other
/// pages its changes take effect on Apply or OK; the Applications tab can link apps to projects made here before then.
/// </summary>
public sealed partial class ProjectsViewModel : ObservableObject
{
    private readonly List<CategoryItem> _categories = [];
    private readonly List<ProjectItem> _projects = [];
    private readonly List<long> _deletedCategories = [];
    private readonly List<long> _deletedProjects = [];
    private int _loads;

    /// <summary>Raised on any change, for the dialog's Apply button.</summary>
    public event Action? Changed;

    /// <summary>Raised when a project is deleted in the dialog, so links to it can be dropped.</summary>
    public event Action<ProjectItem>? ProjectDeleted;

    /// <summary>Lists the apps and sites linked to a project; set by the dialog.</summary>
    public Func<ProjectItem, IEnumerable<string>>? LinkedNames { get; set; }

    /// <summary>The list on the left: each category with its projects, then projects without one.</summary>
    [ObservableProperty] private IReadOnlyList<ProjectsEntry> _entries = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    private ProjectsEntry? _selected;

    public bool CanDeleteSelected => CanDelete();

    [ObservableProperty] private bool _showArchived;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isDirty;

    /// <summary>"None" and every category, by name: the choices for a project's category.</summary>
    public ObservableCollection<CategoryItem> CategoryChoices { get; } = [CategoryItem.None];

    /// <summary>"None" and every project, by name: the choices for an app's or site's project.</summary>
    public ObservableCollection<ProjectItem> ProjectChoices { get; } = [ProjectItem.None];

    /// <summary>Every category and project has a name (they can't be saved without one).</summary>
    public bool IsValid => _categories.All(c => !string.IsNullOrWhiteSpace(c.Name)) && _projects.All(p => !string.IsNullOrWhiteSpace(p.Name));

    /// <summary>The project with this id, or <see cref="ProjectItem.None"/>.</summary>
    public ProjectItem Resolve(long? id) => id is { } value ? _projects.FirstOrDefault(p => p.Id == value) ?? ProjectItem.None : ProjectItem.None;

    public string LinkedTextOf(ProjectItem project)
    {
        var names = LinkedNames?.Invoke(project).ToList() ?? [];
        return names.Count == 0 ? "None" : string.Join(", ", names);
    }

    /// <summary>Tells the selected project its links may have changed.</summary>
    public void RefreshLinked() => (Selected as ProjectItem)?.RefreshLinked();

    /// <summary>Reads the categories and projects from the history in <paramref name="dataFolder"/>, off the UI thread.</summary>
    public async Task LoadAsync(string dataFolder)
    {
        var load = ++_loads;
        List<Category> categories;
        List<Project> projects;
        try
        {
            (categories, projects) = await Task.Run(() =>
            {
                using var db = WorklapseDatabase.Open(dataFolder);
                var store = new ProjectStore(db);
                return (store.GetCategories(), store.GetProjects());
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            if (load == _loads)
                Status = "Couldn't read the history: " + ex.Message;
            return;
        }
        if (load != _loads)
            return;

        foreach (var category in _categories)
            category.PropertyChanged -= OnItemChanged;
        foreach (var project in _projects)
            project.PropertyChanged -= OnItemChanged;
        _categories.Clear();
        _projects.Clear();
        _deletedCategories.Clear();
        _deletedProjects.Clear();

        foreach (var category in categories)
            Track(new CategoryItem(category.Id, category.Name, category.Color));
        foreach (var project in projects)
        {
            var category = _categories.FirstOrDefault(c => c.Id == project.CategoryId) ?? CategoryItem.None;
            Track(new ProjectItem(this, project.Id, project.Name, category, project.Color, project.Archived));
        }
        RebuildChoices();
        Rebuild();
        Selected = Entries.FirstOrDefault(e => e is not NoCategoryHeading);
        IsDirty = false;
    }

    /// <summary>Writes the changes to the history in <paramref name="dataFolder"/>; new items get their ids.</summary>
    public void Apply(string dataFolder)
    {
        if (!IsDirty)
            return;
        using var db = WorklapseDatabase.Open(dataFolder);
        var store = new ProjectStore(db);
        using (var transaction = db.BeginTransaction())
        {
            foreach (var id in _deletedProjects)
                store.DeleteProject(id);
            foreach (var id in _deletedCategories)
                store.DeleteCategory(id);
            foreach (var category in _categories)
            {
                if (category.Id is { } id)
                    store.UpdateCategory(new Category(id, category.Name, category.Color));
                else
                    category.Id = store.AddCategory(category.Name, category.Color);
            }
            foreach (var project in _projects)
            {
                var categoryId = project.Category.IsNone ? null : project.Category.Id;
                project.Id ??= store.AddProject(project.Name, categoryId, project.OwnColor);
                store.UpdateProject(new Project(project.Id.Value, project.Name, categoryId, project.OwnColor, project.Archived));
            }
            transaction.Commit();
        }
        _deletedProjects.Clear();
        _deletedCategories.Clear();
        IsDirty = false;
    }

    [RelayCommand]
    private void NewProject()
    {
        var category = Selected switch
        {
            CategoryItem c => c,
            ProjectItem p => p.Category,
            _ => _categories.FirstOrDefault() ?? CategoryItem.None,
        };
        var project = new ProjectItem(this, null, UniqueName("New project", _projects.Select(p => p.Name)), category, null, false);
        Track(project);
        RebuildChoices();
        Rebuild();
        Selected = project;
        MarkChanged();
    }

    [RelayCommand]
    private void NewCategory()
    {
        var used = _categories.Select(c => c.Color).ToHashSet();
        var color = PaletteColor.All.FirstOrDefault(c => !used.Contains(c.Rgb))?.Rgb ?? PaletteColor.All[0].Rgb;
        var category = new CategoryItem(null, UniqueName("New category", _categories.Select(c => c.Name)), color);
        Track(category);
        RebuildChoices();
        Rebuild();
        Selected = category;
        MarkChanged();
    }

    /// <summary>Whether deleting the selection needs confirming: it's in the history already.</summary>
    public bool SelectedIsSaved => Selected switch
    {
        CategoryItem c => c.Id is not null,
        ProjectItem p => p.Id is not null,
        _ => false,
    };

    private bool CanDelete() => Selected is CategoryItem or ProjectItem;

    /// <summary>Deletes the selected category (its projects stay, without one) or project.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        var index = Entries.ToList().IndexOf(Selected!);
        switch (Selected)
        {
            case CategoryItem category:
                category.PropertyChanged -= OnItemChanged;
                _categories.Remove(category);
                if (category.Id is { } categoryId)
                    _deletedCategories.Add(categoryId);
                foreach (var project in _projects.Where(p => p.Category == category))
                    project.Category = CategoryItem.None;
                break;
            case ProjectItem project:
                project.PropertyChanged -= OnItemChanged;
                _projects.Remove(project);
                if (project.Id is { } projectId)
                    _deletedProjects.Add(projectId);
                ProjectDeleted?.Invoke(project);
                break;
        }
        RebuildChoices();
        Rebuild();
        Selected = Entries.Count == 0 ? null : Entries[Math.Clamp(index, 0, Entries.Count - 1)];
        MarkChanged();
    }

    partial void OnShowArchivedChanged(bool value)
    {
        var selected = Selected;
        Rebuild();
        Selected = selected is not null && Entries.Contains(selected) ? selected : Entries.FirstOrDefault();
    }

    partial void OnSelectedChanged(ProjectsEntry? value) => (value as ProjectItem)?.RefreshLinked();

    private void Track(CategoryItem category)
    {
        _categories.Add(category);
        category.PropertyChanged += OnItemChanged;
    }

    private void Track(ProjectItem project)
    {
        _projects.Add(project);
        project.PropertyChanged += OnItemChanged;
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only what's saved counts as a change. The list is redrawn when an item moves to another category or hides;
        // not while a name is typed (redrawing would take the focus away), so renamed items get sorted next time.
        if (e.PropertyName is nameof(CategoryItem.Name) or nameof(CategoryItem.Color) or nameof(ProjectItem.Category)
            or nameof(ProjectItem.OwnColor) or nameof(ProjectItem.Archived))
        {
            MarkChanged();
            if (e.PropertyName is nameof(ProjectItem.Category) or nameof(ProjectItem.Archived))
            {
                var selected = Selected;
                Rebuild();
                if (selected is not null && Entries.Contains(selected))
                    Selected = selected;
            }
        }
    }

    private void MarkChanged()
    {
        IsDirty = true;
        OnPropertyChanged(nameof(IsValid));
        Changed?.Invoke();
    }

    /// <summary>
    /// Puts the choice lists in step with the items: removes what's gone and inserts what's new in name order, never
    /// moving an item already there (a dropdown showing a moved item can drop its selection, and with it a link).
    /// </summary>
    private void RebuildChoices()
    {
        Sync(CategoryChoices, _categories, c => c.Name);
        Sync(ProjectChoices, _projects, p => p.Name);
    }

    private static void Sync<T>(ObservableCollection<T> choices, List<T> items, Func<T, string> name) where T : class
    {
        // The first entry is always "None".
        for (var i = choices.Count - 1; i >= 1; i--)
            if (!items.Contains(choices[i]))
                choices.RemoveAt(i);
        foreach (var item in items.Where(i => !choices.Contains(i)).OrderBy(name, StringComparer.CurrentCultureIgnoreCase))
        {
            var at = 1;
            while (at < choices.Count && StringComparer.CurrentCultureIgnoreCase.Compare(name(choices[at]), name(item)) <= 0)
                at++;
            choices.Insert(at, item);
        }
    }

    private void Rebuild()
    {
        var entries = new List<ProjectsEntry>();
        IEnumerable<ProjectItem> Visible(CategoryItem category) => _projects
            .Where(p => p.Category == category && (ShowArchived || !p.Archived))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var category in _categories.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            entries.Add(category);
            entries.AddRange(Visible(category));
        }
        var loose = Visible(CategoryItem.None).ToList();
        if (loose.Count > 0)
        {
            entries.Add(NoCategoryHeading.Instance);
            entries.AddRange(loose);
        }
        Entries = entries;

        var archived = _projects.Count(p => p.Archived);
        Status = _projects.Count == 0 && _categories.Count == 0 ? "No projects yet."
            : $"{_categories.Count} {Plural(_categories.Count, "category", "categories")} · {_projects.Count} {Plural(_projects.Count, "project", "projects")}"
              + (archived > 0 ? $" · {archived} archived" : "");
    }

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    private static string UniqueName(string name, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!used.Contains(name))
            return name;
        for (var i = 2; ; i++)
            if (!used.Contains($"{name} {i}"))
                return $"{name} {i}";
    }
}
