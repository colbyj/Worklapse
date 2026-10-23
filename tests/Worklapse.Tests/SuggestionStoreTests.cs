using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;

namespace Worklapse.Tests;

/// <summary>Keeping suggested stretches and breaks and the decisions on them, and accepting them as ranges set by hand.</summary>
public class SuggestionStoreTests
{
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset Nine = new(2026, 6, 17, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 6, 17, 9, 0, 0)));
    private static readonly DateOnly Day = DateOnly.FromDateTime(Nine.LocalDateTime);

    private static DateTimeOffset At(int seconds) => Nine.AddSeconds(seconds);

    private static Suggestion Stretch(int from, int to, long project) =>
        new(At(from), At(to), project, 0.9, TimeSpan.FromSeconds(to - from), At(from), ["title:presets"]);

    private static SuggestedBreak Break(int from, int to, long before, long after) => new(At(from), At(to), before, after);

    private static DaySuggestions Computed(params object[] items) =>
        new(items.OfType<Suggestion>().ToList(), items.OfType<SuggestedBreak>().ToList());

    private static List<(int, int, long?)> Spans(ProjectStore projects, HistoryStore history)
    {
        var (dayStart, dayEnd) = history.DayRange(Day);
        return projects.GetSpans(dayStart, dayEnd).Select(s => ((int)(s.Start - Nine).TotalSeconds, (int)(s.End - Nine).TotalSeconds, s.ProjectId)).ToList();
    }

    private static int OpenToday(SuggestionStore store) => store.CountOpen([(Day, Nine.AddHours(-9), Nine.AddHours(15))])[Day];

    [Fact]
    public void WorkingADayOutAgainDoesntOfferWhatWasDecidedAgain()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var (presayo, frankensystem) = (projects.AddProject("Presayo", null), projects.AddProject("Frankensystem", null));
        var store = new SuggestionStore(db);

        store.Replace(Nine, Nine.AddDays(1), Computed(Stretch(0, 600, presayo), Break(600, 1000, presayo, frankensystem), Stretch(1000, 1600, frankensystem)));
        var first = store.Get(Nine, Nine.AddDays(1));
        Assert.Equal(3, first.OpenCount);
        store.Reject(first.Stretches[1].Id);
        store.LeaveOut(Assert.Single(first.Breaks).Id);

        // Worked out again: much the same, the first stretch a little longer.
        store.Replace(Nine, Nine.AddDays(1), Computed(Stretch(0, 700, presayo), Break(700, 1000, presayo, frankensystem), Stretch(1000, 1500, frankensystem)));
        var second = store.Get(Nine, Nine.AddDays(1));

        Assert.Equal([(At(0), At(700), SuggestionState.Open), (At(1000), At(1600), SuggestionState.Rejected)],
            second.Stretches.Select(s => (s.Start, s.End, s.State)));
        Assert.Equal(SuggestionState.Rejected, Assert.Single(second.Breaks).State);
        Assert.Equal(1, OpenToday(store));
    }

    [Fact]
    public void AnUnchangedSuggestionKeepsItsId()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var (presayo, frankensystem) = (projects.AddProject("Presayo", null), projects.AddProject("Frankensystem", null));
        var store = new SuggestionStore(db);
        var computed = Computed(Stretch(0, 600, presayo), Break(600, 900, presayo, frankensystem));
        store.Replace(Nine, Nine.AddDays(1), computed);
        var first = store.Get(Nine, Nine.AddDays(1));

        // Worked out again with nothing changed (as when the window is activated): a click on one shown still finds it.
        store.Replace(Nine, Nine.AddDays(1), computed);
        var same = store.Get(Nine, Nine.AddDays(1));
        Assert.Equal((first.Stretches[0].Id, first.Breaks[0].Id), (same.Stretches[0].Id, same.Breaks[0].Id));
    }

    [Fact]
    public void WorkingOutAgainChangesTheSuggestionsOnlyWhenOneIsAddedTakenAwayOrMoved()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var (presayo, frankensystem) = (projects.AddProject("Presayo", null), projects.AddProject("Frankensystem", null));
        var store = new SuggestionStore(db);
        store.Replace(Nine, Nine.AddDays(1), Computed(Stretch(0, 600, presayo), Break(600, 1000, presayo, frankensystem), Stretch(1000, 1600, frankensystem)));
        store.Reject(store.Get(Nine, Nine.AddDays(1)).Stretches[1].Id);

        // Only the certainty and evidence differ, and the rejected stretch is computed again: nothing to show.
        Assert.False(store.Changes(Nine, Nine.AddDays(1), Computed(Stretch(0, 600, presayo) with { Confidence = 0.5, Evidence = [] },
            Break(600, 1000, presayo, frankensystem), Stretch(1000, 1600, frankensystem))));
        Assert.True(store.Changes(Nine, Nine.AddDays(1), Computed(Stretch(0, 650, presayo), Break(600, 1000, presayo, frankensystem))));
        Assert.True(store.Changes(Nine, Nine.AddDays(1), Computed(Stretch(0, 600, presayo), Break(650, 1000, presayo, frankensystem))));
        Assert.True(store.Changes(Nine, Nine.AddDays(1), Computed(Stretch(0, 600, presayo))));
        Assert.True(store.Changes(Nine, Nine.AddDays(1), DaySuggestions.None));
    }

    [Fact]
    public void ASmallRejectedStretchHidesItselfButNotALargerOneAroundIt()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var presayo = new ProjectStore(db).AddProject("Presayo", null);
        var store = new SuggestionStore(db);
        store.Replace(Nine, Nine.AddDays(1), Computed(Stretch(1000, 1600, presayo)));
        store.Reject(Assert.Single(store.Get(Nine, Nine.AddDays(1)).Stretches).Id);

        store.Replace(Nine, Nine.AddDays(1), Computed(Stretch(1050, 1550, presayo)));
        Assert.Equal(0, OpenToday(store));

        store.Replace(Nine, Nine.AddDays(1), Computed(Stretch(0, 7200, presayo)));
        var open = Assert.Single(store.Get(Nine, Nine.AddDays(1)).Stretches, s => s.IsOpen);
        Assert.Equal((At(0), At(7200)), (open.Start, open.End));
    }

    [Fact]
    public void AProjectChosenForAStretchSurvivesWorkingTheDayOutAgain()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var (presayo, frankensystem) = (projects.AddProject("Presayo", null), projects.AddProject("Frankensystem", null));
        var store = new SuggestionStore(db);
        store.Replace(Nine, Nine.AddDays(1), Computed(Stretch(0, 600, presayo)));
        store.SetProject(Assert.Single(store.Get(Nine, Nine.AddDays(1)).Stretches).Id, frankensystem);

        // The classifier still says Presayo, about the same stretch: the developer's choice stands, and isn't offered twice.
        store.Replace(Nine, Nine.AddDays(1), Computed(Stretch(0, 620, frankensystem), Stretch(10, 600, presayo)));

        var kept = Assert.Single(store.Get(Nine, Nine.AddDays(1)).Stretches);
        Assert.Equal((frankensystem, SuggestionState.Chosen, true), (kept.ProjectId, kept.State, kept.IsOpen));
        Assert.Equal(1, OpenToday(store));
    }

    [Fact]
    public void AcceptingAStretchWritesItAroundTimeThatHasAnotherProject()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var (presayo, admin) = (projects.AddProject("Presayo", null), projects.AddProject("Admin", null));
        var recording = new RecordingStore(db);
        var terminal = recording.GetOrAddApp("WindowsTerminal.exe", null);
        var outlook = recording.GetOrAddApp("olk.exe", null);
        var history = new HistoryStore(db);
        history.SetProject(new ProgramKey(ProgramKind.App, "olk.exe"), admin);
        for (var s = 0; s < 1200; s += 10)
            recording.InsertCapture(At(s), CaptureKind.Active, s is >= 300 and < 400 ? outlook : terminal, "t", null); // Outlook: Admin by its link
        projects.SetSpan(At(1000), At(1100), null); // set to no project by hand

        var store = new SuggestionStore(db);
        store.Replace(Nine, Nine.AddDays(1), Computed(Stretch(0, 1200, presayo)));
        var (dayStart, dayEnd) = history.DayRange(Day);
        store.Accept(Assert.Single(store.Get(Nine, Nine.AddDays(1)).Stretches), history.LoadDay(Day).Captures, projects.GetSpans(dayStart, dayEnd), Max);

        Assert.Equal([(0, 300, (long?)presayo), (400, 1000, presayo), (1000, 1100, null), (1100, 1200, presayo)], Spans(projects, history));
        Assert.Equal(SuggestionState.Accepted, Assert.Single(store.Get(Nine, Nine.AddDays(1)).Stretches).State);
    }

    [Fact]
    public void CountingABreakWritesItAndLeavingOneOutIsRemembered()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var (presayo, frankensystem) = (projects.AddProject("Presayo", null), projects.AddProject("Frankensystem", null));
        var history = new HistoryStore(db);
        var store = new SuggestionStore(db);
        var computed = Computed(Break(300, 600, presayo, frankensystem), Break(900, 1200, frankensystem, frankensystem));
        store.Replace(Nine, Nine.AddDays(1), computed);
        var breaks = store.Get(Nine, Nine.AddDays(1)).Breaks;
        Assert.Equal([null, (long?)frankensystem], breaks.Select(b => b.Inside));

        var (dayStart, dayEnd) = history.DayRange(Day);
        store.Count(breaks[0], frankensystem, history.LoadDay(Day).Captures, projects.GetSpans(dayStart, dayEnd), Max);
        store.LeaveOut(breaks[1].Id);
        store.Replace(Nine, Nine.AddDays(1), computed);

        Assert.Equal([(300, 600, (long?)frankensystem)], Spans(projects, history));
        Assert.Equal([(SuggestionState.Accepted, (long?)frankensystem), (SuggestionState.Rejected, null)],
            store.Get(Nine, Nine.AddDays(1)).Breaks.Select(b => (b.State, b.CountedTo)));
        Assert.Equal(0, OpenToday(store));
    }

    [Fact]
    public void SubtractLeavesTheRangesOutsideTheHoles()
    {
        var result = SuggestionStore.Subtract([(At(0), At(100)), (At(200), At(300))], [(At(50), At(60)), (At(90), At(210)), (At(290), At(400))]);

        Assert.Equal([(0, 50), (60, 90), (210, 290)], result.Select(r => ((int)(r.Start - Nine).TotalSeconds, (int)(r.End - Nine).TotalSeconds)));
    }

    [Fact]
    public void TheEngineLearnsFromLabelledDaysAndSuggestsOnOthers()
    {
        using var folder = new TempFolder();
        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        var db = history.Database;
        var projects = new ProjectStore(db);
        var presayo = projects.AddProject("Presayo", null);
        var recording = new RecordingStore(db);
        var terminal = recording.GetOrAddApp("WindowsTerminal.exe", null);
        history.SetProductive(new ProgramKey(ProgramKind.App, "WindowsTerminal.exe"), true);
        var yesterday = Nine.AddDays(-1);
        for (var s = 0; s < 600; s += 10)
        {
            recording.InsertCapture(yesterday.AddSeconds(s), CaptureKind.Active, terminal, "Group label presets by printer type", null);
            recording.InsertCapture(Nine.AddSeconds(s), CaptureKind.Active, terminal, "Label presets: thermal printers", null);
        }
        projects.SetSpan(yesterday, yesterday.AddSeconds(600), presayo); // yesterday was labelled; today isn't
        var settings = new WorklapseSettings();
        var engine = new SuggestionEngine();

        var suggestion = Assert.Single(engine.Refresh(history, Day, settings).Stretches);
        Assert.Equal((presayo, SuggestionState.Open, Nine), (suggestion.ProjectId, suggestion.State, suggestion.Start));
        Assert.Contains("title:presets", suggestion.Evidence);

        // The same classifier while nothing it learns from changes; a new one when the labels change.
        var classifier = engine.Classifier(history, SuggestionOptOuts.None, settings.MaxCaptureLength);
        Assert.Same(classifier, engine.Classifier(history, SuggestionOptOuts.None, settings.MaxCaptureLength));
        projects.SetSpan(yesterday.AddSeconds(100), yesterday.AddSeconds(200), null);
        Assert.NotSame(classifier, engine.Classifier(history, SuggestionOptOuts.None, settings.MaxCaptureLength));

        Assert.Equal(0, engine.Refresh(history, Day, settings with { SuggestProjects = false }).Stretches.Count);
    }

    [Fact]
    public void TheEngineWorksADayOutAgainOnlyWhenSomethingItRestsOnChanged()
    {
        using var folder = new TempFolder();
        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        var db = history.Database;
        var projects = new ProjectStore(db);
        var presayo = projects.AddProject("Presayo", null);
        var recording = new RecordingStore(db);
        var terminal = recording.GetOrAddApp("WindowsTerminal.exe", null);
        history.SetProductive(new ProgramKey(ProgramKind.App, "WindowsTerminal.exe"), true);
        var yesterday = Nine.AddDays(-1);
        for (var s = 0; s < 600; s += 10)
        {
            recording.InsertCapture(yesterday.AddSeconds(s), CaptureKind.Active, terminal, "Group label presets by printer type", null);
            recording.InsertCapture(Nine.AddSeconds(s), CaptureKind.Active, terminal, "Label presets: thermal printers", null);
        }
        projects.SetSpan(yesterday, yesterday.AddSeconds(600), presayo);
        var settings = new WorklapseSettings();
        var engine = new SuggestionEngine();
        Assert.Single(engine.Refresh(history, Day, settings).Stretches);

        // Marked so that working it out again would show: left as it is while nothing changed.
        db.Execute("UPDATE suggestions SET evidence = 'title:marked';");
        Assert.Equal(["title:marked"], Assert.Single(engine.Refresh(history, Day, settings).Stretches).Evidence);

        // A new capture on the day: worked out again.
        recording.InsertCapture(Nine.AddSeconds(600), CaptureKind.Active, terminal, "Label presets: thermal printers", null);
        Assert.Contains("title:presets", Assert.Single(engine.Refresh(history, Day, settings).Stretches).Evidence);
    }

    [Fact]
    public async Task SuggestionsWorkedOutAgainAreStoredOnlyWhenAskedFor()
    {
        using var folder = new TempFolder();
        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        var db = history.Database;
        var projects = new ProjectStore(db);
        var presayo = projects.AddProject("Presayo", null);
        var recording = new RecordingStore(db);
        var terminal = recording.GetOrAddApp("WindowsTerminal.exe", null);
        history.SetProductive(new ProgramKey(ProgramKind.App, "WindowsTerminal.exe"), true);
        var yesterday = Nine.AddDays(-1);
        for (var s = 0; s < 600; s += 10)
        {
            recording.InsertCapture(yesterday.AddSeconds(s), CaptureKind.Active, terminal, "Group label presets by printer type", null);
            recording.InsertCapture(Nine.AddSeconds(s), CaptureKind.Active, terminal, "Label presets: thermal printers", null);
        }
        projects.SetSpan(yesterday, yesterday.AddSeconds(600), presayo);
        var settings = new WorklapseSettings();
        var engine = new SuggestionEngine();
        var store = new SuggestionStore(db);
        var (start, end) = history.DayRange(Day);

        var worked = await engine.WorkOutAsync(db.DataFolder, history.DayStart, Day, settings, () => true);
        Assert.Equal(presayo, Assert.Single(worked!.Suggestions.Stretches).ProjectId);
        Assert.Empty(store.Get(start, end).Stretches);
        Assert.True(store.Changes(start, end, worked.Suggestions));

        // Stored: shown as they are, and not worked out again while nothing changes.
        engine.Store(history, worked);
        Assert.Single(store.Get(start, end).Stretches);
        Assert.Null(await engine.WorkOutAsync(db.DataFolder, history.DayStart, Day, settings, () => true));
        Assert.Null(await engine.WorkOutAsync(db.DataFolder, history.DayStart, Day, settings with { SuggestProjects = false }, () => true));
    }

    [Fact]
    public void AProjectIsntSuggestedOutsideItsDates()
    {
        using var folder = new TempFolder();
        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        var db = history.Database;
        var projects = new ProjectStore(db);
        var (presayo, arduino) = (projects.AddProject("Presayo", null), projects.AddProject("Arduino", null));
        var recording = new RecordingStore(db);
        var terminal = recording.GetOrAddApp("WindowsTerminal.exe", null);
        history.SetProductive(new ProgramKey(ProgramKind.App, "WindowsTerminal.exe"), true);
        var later = Nine.AddDays(5);
        for (var s = 0; s < 600; s += 10)
        {
            recording.InsertCapture(Nine.AddDays(-1).AddSeconds(s), CaptureKind.Active, terminal, "Label presets: thermal printers", null);
            recording.InsertCapture(later.AddSeconds(s), CaptureKind.Active, terminal, "Servo sketch: arduino uno", null);
            recording.InsertCapture(Nine.AddSeconds(s), CaptureKind.Active, terminal, "Servo sketch: arduino uno", null);
        }
        projects.SetSpan(Nine.AddDays(-1), Nine.AddDays(-1).AddSeconds(600), presayo);
        projects.SetSpan(later, later.AddSeconds(600), arduino);
        var settings = new WorklapseSettings { MinimumCertainty = 0 };
        var engine = new SuggestionEngine();
        Assert.Equal(arduino, Assert.Single(engine.Refresh(history, Day, settings).Stretches).ProjectId);

        // Arduino starts later: that day's time isn't suggested as Arduino any more (the others share its probability).
        projects.SetDates(arduino, Day.AddDays(3), null);
        Assert.Equal((Day.AddDays(3), (DateOnly?)null), (projects.GetProjects().Single(p => p.Id == arduino).StartsOn,
            projects.GetProjects().Single(p => p.Id == arduino).EndsOn));
        Assert.DoesNotContain(engine.Refresh(history, Day, settings).Stretches, s => s.ProjectId == arduino && s.IsOpen);

        projects.SetDates(arduino, null, null);
        Assert.Equal(arduino, Assert.Single(engine.Refresh(history, Day, settings).Stretches, s => s.IsOpen).ProjectId);
    }

    [Fact]
    public void AStretchLessCertainThanTheLimitIsntOffered()
    {
        using var folder = new TempFolder();
        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        var db = history.Database;
        var projects = new ProjectStore(db);
        var (presayo, frankensystem) = (projects.AddProject("Presayo", null), projects.AddProject("Frankensystem", null));
        var recording = new RecordingStore(db);
        var terminal = recording.GetOrAddApp("WindowsTerminal.exe", null);
        history.SetProductive(new ProgramKey(ProgramKind.App, "WindowsTerminal.exe"), true);
        var yesterday = Nine.AddDays(-1);
        for (var s = 0; s < 600; s += 10)
        {
            // Yesterday the same words went to both projects in turn, so today's guess is far from sure.
            recording.InsertCapture(yesterday.AddSeconds(s), CaptureKind.Active, terminal, "Label presets: thermal printers", null);
            recording.InsertCapture(Nine.AddSeconds(s), CaptureKind.Active, terminal, "Label presets: thermal printers", null);
        }
        projects.SetSpan(yesterday, yesterday.AddSeconds(300), presayo);
        projects.SetSpan(yesterday.AddSeconds(300), yesterday.AddSeconds(610), frankensystem);
        var engine = new SuggestionEngine();
        var classifier = engine.Classifier(history, SuggestionOptOuts.None, Max);
        var captures = history.LoadDay(Day).Captures;
        List<Suggestion> At(double certainty) => SuggestionBuilder.ForDay(captures, [], classifier,
            SuggestionLimits.Default with { MinimumCertainty = certainty }, Max, SuggestionOptOuts.None).Stretches.ToList();

        var certain = Assert.Single(At(0)).Confidence;
        Assert.InRange(certain, 0.3, 0.9);
        Assert.Single(At(certain));
        Assert.Empty(At(certain + 0.01));
    }
}
