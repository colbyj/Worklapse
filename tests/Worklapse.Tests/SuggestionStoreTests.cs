using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;

namespace Worklapse.Tests;

/// <summary>Keeping suggested blocks and the decisions on them, and accepting them as ranges set by hand.</summary>
public class SuggestionStoreTests
{
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset Nine = new(2026, 6, 17, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 6, 17, 9, 0, 0)));
    private static readonly DateOnly Day = DateOnly.FromDateTime(Nine.LocalDateTime);

    private static DateTimeOffset At(int seconds) => Nine.AddSeconds(seconds);

    private static Suggestion Block(int from, int to, long project, params SuggestionPart[] parts) =>
        new(At(from), At(to), project, 0.9, TimeSpan.FromSeconds(to - from), parts, ["title:presets"]);

    private static SuggestionPart Break(int from, int to) => new(At(from), At(to), SuggestionPartKind.Break, null);

    private static SuggestionPart Switch(int from, int to, long project) => new(At(from), At(to), SuggestionPartKind.Switch, project);

    [Fact]
    public void WorkingADayOutAgainKeepsDecisionsAndDoesntOfferRejectedBlocksAgain()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var (presayo, frankensystem) = (projects.AddProject("Presayo", null), projects.AddProject("Frankensystem", null));
        var store = new SuggestionStore(db);

        store.Replace(Nine, Nine.AddDays(1), [Block(0, 600, presayo, Break(200, 300)), Block(1000, 1600, frankensystem)]);
        var first = store.Get(Nine, Nine.AddDays(1));
        store.SetPart(first[0].Id, At(200), decided: true);
        store.Reject(first[1].Id);

        // Worked out again: much the same blocks, the first a little longer.
        store.Replace(Nine, Nine.AddDays(1), [Block(0, 700, presayo, Break(200, 300)), Block(1000, 1500, frankensystem)]);
        var second = store.Get(Nine, Nine.AddDays(1));

        Assert.Equal(2, second.Count);
        var open = Assert.Single(second, s => s.State == SuggestionState.Open);
        Assert.Equal((At(700), true), (open.End, Assert.Single(open.Parts).Decided)); // the break is still counted
        Assert.Equal(SuggestionState.Rejected, Assert.Single(second, s => s.State == SuggestionState.Rejected).State);
        Assert.Equal(new Dictionary<DateOnly, int> { [Day] = 1 }, store.CountOpen([(Day, Nine.AddHours(-9), Nine.AddHours(15))]));
    }

    [Fact]
    public void AnUnchangedSuggestionKeepsItsIdAndAPartWhoseEdgesMovedKeepsItsDecision()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var presayo = new ProjectStore(db).AddProject("Presayo", null);
        var store = new SuggestionStore(db);
        store.Replace(Nine, Nine.AddDays(1), [Block(0, 600, presayo, Break(200, 300))]);
        var first = Assert.Single(store.Get(Nine, Nine.AddDays(1)));
        store.SetPart(first.Id, At(200), decided: true);

        // Worked out again with nothing changed (as when the window is activated): a click on the one shown still finds it.
        store.Replace(Nine, Nine.AddDays(1), [Block(0, 600, presayo, Break(200, 300))]);
        var same = Assert.Single(store.Get(Nine, Nine.AddDays(1)));
        Assert.Equal((first.Id, true), (same.Id, Assert.Single(same.Parts).Decided));

        // A capture later the break starts 10 s sooner: still the break counted.
        store.Replace(Nine, Nine.AddDays(1), [Block(0, 610, presayo, Break(190, 300))]);
        Assert.True(Assert.Single(Assert.Single(store.Get(Nine, Nine.AddDays(1))).Parts).Decided);
    }

    [Fact]
    public void ASmallRejectedBlockHidesItselfButNotALargerOneAroundIt()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var presayo = new ProjectStore(db).AddProject("Presayo", null);
        var store = new SuggestionStore(db);
        store.Replace(Nine, Nine.AddDays(1), [Block(1000, 1600, presayo)]);
        store.Reject(Assert.Single(store.Get(Nine, Nine.AddDays(1))).Id);

        store.Replace(Nine, Nine.AddDays(1), [Block(1050, 1550, presayo)]);
        Assert.Equal(0, store.CountOpen([(Day, Nine.AddHours(-9), Nine.AddHours(15))])[Day]);

        store.Replace(Nine, Nine.AddDays(1), [Block(0, 7200, presayo)]);
        var open = Assert.Single(store.Get(Nine, Nine.AddDays(1)), s => s.IsOpen);
        Assert.Equal((At(0), At(7200)), (open.Start, open.End));
    }

    [Fact]
    public void AcceptingAPieceWritesOnlyThatPieceAndLeavesTheSuggestionOpen()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var (presayo, admin) = (projects.AddProject("Presayo", null), projects.AddProject("Admin", null));
        var recording = new RecordingStore(db);
        var terminal = recording.GetOrAddApp("WindowsTerminal.exe", null);
        var outlook = recording.GetOrAddApp("olk.exe", null);
        new HistoryStore(db).SetProject(new ProgramKey(ProgramKind.App, "olk.exe"), admin);
        for (var s = 0; s < 1200; s += 10)
            recording.InsertCapture(At(s), CaptureKind.Active, s is >= 650 and < 700 ? outlook : terminal, "t", null);
        var store = new SuggestionStore(db);
        store.Replace(Nine, Nine.AddDays(1), [Block(0, 1200, presayo, Break(300, 500))]);

        var history = new HistoryStore(db);
        var (dayStart, dayEnd) = history.DayRange(Day);
        store.AcceptPiece(At(500), At(1200), presayo, history.LoadDay(Day).Captures, projects.GetSpans(dayStart, dayEnd), Max);

        Assert.Equal([(500, 650), (700, 1200)], // around Outlook, which keeps its link
            projects.GetSpans(dayStart, dayEnd).Select(s => ((int)(s.Start - Nine).TotalSeconds, (int)(s.End - Nine).TotalSeconds)));
        Assert.True(Assert.Single(store.Get(Nine, Nine.AddDays(1))).IsOpen);
    }

    [Fact]
    public void AProjectChosenForASuggestionSurvivesWorkingTheDayOutAgain()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var (presayo, frankensystem) = (projects.AddProject("Presayo", null), projects.AddProject("Frankensystem", null));
        var store = new SuggestionStore(db);
        store.Replace(Nine, Nine.AddDays(1), [Block(0, 600, presayo)]);
        store.SetProject(Assert.Single(store.Get(Nine, Nine.AddDays(1))).Id, frankensystem);

        // The classifier still says Presayo, about the same block: the developer's choice stands, and isn't offered twice.
        store.Replace(Nine, Nine.AddDays(1), [Block(0, 620, frankensystem), Block(10, 600, presayo)]);

        var kept = Assert.Single(store.Get(Nine, Nine.AddDays(1)));
        Assert.Equal((frankensystem, SuggestionState.Chosen, true), (kept.ProjectId, kept.State, kept.IsOpen));
        Assert.Equal(1, store.CountOpen([(Day, Nine.AddHours(-9), Nine.AddHours(15))])[Day]);
    }

    [Fact]
    public void AcceptingWritesTheBlockAroundTimeThatHasAProjectAndSplitsOutSwitches()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var (presayo, frankensystem, admin) = (projects.AddProject("Presayo", null), projects.AddProject("Frankensystem", null), projects.AddProject("Admin", null));
        var recording = new RecordingStore(db);
        var terminal = recording.GetOrAddApp("WindowsTerminal.exe", null);
        var outlook = recording.GetOrAddApp("olk.exe", null);
        new HistoryStore(db).SetProject(new ProgramKey(ProgramKind.App, "olk.exe"), admin);
        for (var s = 0; s < 1200; s += 10)
            recording.InsertCapture(At(s), CaptureKind.Active, s is >= 300 and < 400 ? outlook : terminal, "t", null); // Outlook: Admin by its link
        projects.SetSpan(At(1000), At(1100), null); // set to no project by hand

        var store = new SuggestionStore(db);
        store.Replace(Nine, Nine.AddDays(1), [Block(0, 1200, presayo, Break(500, 600), Break(700, 800), Switch(850, 950, frankensystem), Switch(1150, 1190, frankensystem))]);
        var suggestion = Assert.Single(store.Get(Nine, Nine.AddDays(1)));
        store.SetPart(suggestion.Id, At(700), decided: true);  // this break counts
        store.SetPart(suggestion.Id, At(1150), decided: true); // this switch stays in the block
        suggestion = Assert.Single(store.Get(Nine, Nine.AddDays(1)));

        var history = new HistoryStore(db);
        var (dayStart, dayEnd) = history.DayRange(Day);
        store.Accept(suggestion, history.LoadDay(Day).Captures, projects.GetSpans(dayStart, dayEnd), Max);

        Assert.Equal([
            (0, 300, (long?)presayo),      // up to Outlook, which keeps its link
            (400, 500, presayo),           // then up to the break not counted
            (600, 850, presayo),           // the counted break included
            (850, 950, frankensystem),     // the switch split out
            (950, 1000, presayo),
            (1000, 1100, null),            // as set by hand
            (1100, 1200, presayo),         // the kept switch included
        ], projects.GetSpans(dayStart, dayEnd).Select(s => ((int)(s.Start - Nine).TotalSeconds, (int)(s.End - Nine).TotalSeconds, s.ProjectId)));
        Assert.Equal(SuggestionState.Accepted, Assert.Single(store.Get(Nine, Nine.AddDays(1))).State);
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

        var suggestion = Assert.Single(engine.Refresh(history, Day, settings));
        Assert.Equal((presayo, SuggestionState.Open, Nine), (suggestion.ProjectId, suggestion.State, suggestion.Start));
        Assert.Contains("title:presets", suggestion.Evidence);

        // The same classifier while nothing it learns from changes; a new one when the labels change.
        var classifier = engine.Classifier(history, SuggestionOptOuts.None, settings.MaxCaptureLength);
        Assert.Same(classifier, engine.Classifier(history, SuggestionOptOuts.None, settings.MaxCaptureLength));
        projects.SetSpan(yesterday.AddSeconds(100), yesterday.AddSeconds(200), null);
        Assert.NotSame(classifier, engine.Classifier(history, SuggestionOptOuts.None, settings.MaxCaptureLength));

        Assert.Empty(engine.Refresh(history, Day, settings with { SuggestProjects = false }));
    }
}
