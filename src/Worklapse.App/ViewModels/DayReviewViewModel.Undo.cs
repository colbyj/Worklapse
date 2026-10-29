using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Worklapse.Core.Diagnostics;
using Worklapse.Core.Storage;

namespace Worklapse.App.ViewModels;

/// <summary>
/// Undo for Day review's edits: projects set on time, suggestions decided, apps and sites marked or linked, time marked
/// for deletion or restored, time marked on- or off-task, notes and logged time. Kept for the day shown while Day review
/// is; cleared when another day is opened, another view is shown, Settings open, or marked time is deleted for good.
/// </summary>
public sealed partial class DayReviewViewModel
{
    /// <summary>The tables Day review's edits change: undo puts back what an edit changed in them.</summary>
    private static readonly string[] UndoneTables =
        ["project_spans", "suggestions", "suggestion_breaks", "apps", "sites", "captures", "project_notes", "day_notes", "timesheet"];

    private readonly UndoHistory _undo;

    public bool CanUndo => _undo.CanUndo;

    public string UndoTip => _undo.NextLabel is { } label ? $"Undo {label} (Ctrl+Z)" : "Nothing to undo";

    /// <summary>Runs an edit as one step Undo can take back, named as the tool bar's Undo shows it.</summary>
    private void Edit(string label, Action edit)
    {
        _undo.Record(label, edit);
        UndoChanged();
    }

    /// <summary>Forgets the edits made so far: they can't be undone any more.</summary>
    public void ClearUndo()
    {
        _undo.Clear();
        UndoChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        try
        {
            _undo.Undo();
        }
        catch (SqliteException ex)
        {
            Log.Error("Undoing an edit in Day review failed; its undo history was cleared.", ex);
        }
        UndoChanged();
        // As after any edit, suggestions worked out again wait instead of replacing the list being worked through.
        _holdSuggestions = true;
        Reload(Current >= 0 && Current < Captures.Count ? Captures[Current].Id : null);
    }

    private void UndoChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoTip));
        UndoCommand.NotifyCanExecuteChanged();
    }
}
