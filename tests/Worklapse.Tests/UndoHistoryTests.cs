using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;

namespace Worklapse.Tests;

/// <summary>Undoing the edits made through one connection, and only those.</summary>
public class UndoHistoryTests
{
    private static readonly DateTimeOffset Nine = new(2026, 6, 17, 9, 0, 0, TimeSpan.FromHours(-6));
    private static readonly DateOnly Day = new(2026, 6, 17);

    private static DateTimeOffset At(int seconds) => Nine.AddSeconds(seconds);

    private static UndoHistory Undo(WorklapseDatabase db) =>
        new(db, ["project_spans", "captures", "project_notes", "day_notes", "apps"]);

    private static List<(int, int, long?)> Spans(ProjectStore projects) =>
        projects.GetSpans(Nine.AddHours(-1), Nine.AddHours(1)).Select(s => ((int)(s.Start - Nine).TotalSeconds, (int)(s.End - Nine).TotalSeconds, s.ProjectId)).ToList();

    [Fact]
    public void UndoingPutsBackWhatAnEditChangedLastFirst()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var (presayo, frankensystem) = (projects.AddProject("Presayo", null), projects.AddProject("Frankensystem", null));
        projects.SetSpan(At(0), At(600), presayo);
        var undo = Undo(db);

        undo.Record("Set Project: Frankensystem", () => projects.SetSpan(At(200), At(400), frankensystem)); // splits the Presayo range
        undo.Record("Clear Project", () => projects.SetSpan(At(0), At(100), null));
        Assert.Equal("Clear Project", undo.NextLabel);

        Assert.True(undo.Undo());
        Assert.Equal([(0, 200, (long?)presayo), (200, 400, frankensystem), (400, 600, presayo)], Spans(projects));
        Assert.True(undo.Undo());
        Assert.Equal([(0, 600, (long?)presayo)], Spans(projects));
        Assert.False(undo.CanUndo);
        Assert.False(undo.Undo());
    }

    [Fact]
    public void ChangesOutsideAnEditAndOnOtherConnectionsStay()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var recording = new RecordingStore(db);
        var app = recording.GetOrAddApp("WindowsTerminal.exe", null);
        var id = recording.InsertCapture(At(0), CaptureKind.Active, app, "t", null);
        var history = new HistoryStore(db);
        var undo = Undo(db);

        undo.Record("Mark for Deletion", () => history.SetPendingDelete(At(0), At(10), pending: true));
        // Meanwhile the recorder, on its own connection, marks the capture idle; and a day note is written outside an edit.
        using (var other = WorklapseDatabase.Open(folder.Path))
            other.Execute($"UPDATE captures SET kind = 1 WHERE id = {id};");
        new ProjectStore(db).SetDayNote(Day, "Lunch");

        undo.Undo();

        using var check = db.Command($"SELECT kind || ',' || pending_delete FROM captures WHERE id = {id};");
        Assert.Equal("1,0", check.ExecuteScalar()); // the mark undone, the recorder's change kept
        Assert.Equal("Lunch", new ProjectStore(db).GetDayNote(Day));
    }

    [Fact]
    public void AnEditInsideAnotherIsPartOfItAndAnEditChangingNothingIsntKept()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var presayo = projects.AddProject("Presayo", null);
        var undo = Undo(db);

        undo.Record("Nothing", () => { });
        Assert.False(undo.CanUndo);

        undo.Record("Accept All", () =>
        {
            undo.Record("Accept", () => projects.SetSpan(At(0), At(100), presayo));
            projects.SetNote(Day, presayo, "Dockets"); // a table without rowid, keyed by day and project
        });
        Assert.Equal("Accept All", undo.NextLabel);

        undo.Undo();
        Assert.Empty(Spans(projects));
        Assert.Equal("", projects.GetNotes(Day, Day.AddDays(1)).FirstOrDefault(n => n.ProjectId == presayo)?.Text ?? "");
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void OnlyTheLastStepsAreKept()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var presayo = projects.AddProject("Presayo", null);
        var undo = Undo(db);

        for (var i = 0; i < UndoHistory.MaxSteps + 5; i++)
            undo.Record($"Set {i}", () => projects.SetSpan(At(i * 10), At(i * 10 + 5), presayo));
        var undone = 0;
        while (undo.Undo())
            undone++;

        Assert.Equal(UndoHistory.MaxSteps, undone);
        Assert.Equal(5, Spans(projects).Count); // the first five stay
    }
}
