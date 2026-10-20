using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;

namespace Worklapse.Tests;

public class TitleRuleTests
{
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset Nine = new(2026, 10, 15, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15, 9, 0, 0)));
    private static readonly DateOnly Day = DateOnly.FromDateTime(Nine.LocalDateTime);

    [Fact]
    public void ARuleMatchesTextInTheTitleIgnoringCaseInAnyAppOrOne()
    {
        var any = new TitleRule(1, 1, "Worklapse", null);
        Assert.True(any.Matches("devenv.exe", "Worklapse (Running) - DayReviewViewModel.cs - Microsoft Visual Studio"));
        Assert.True(any.Matches("rider64.exe", "worklapse – FrameView.cs"));
        Assert.True(any.Matches(null, "WORKLAPSE"));
        Assert.False(any.Matches("rider64.exe", "Presayo – FrameView.cs"));
        Assert.False(any.Matches("rider64.exe", null));

        var vs = new TitleRule(2, 1, "Worklapse", "devenv.exe");
        Assert.True(vs.Matches("DevEnv.exe", "Worklapse - Microsoft Visual Studio")); // exe names ignore case too
        Assert.False(vs.Matches("rider64.exe", "Worklapse – FrameView.cs"));
        Assert.False(vs.Matches(null, "Worklapse"));

        Assert.False(new TitleRule(3, 1, "", null).Matches("a.exe", "anything"));
    }

    [Fact]
    public void ARuleForOneAppDecidesBeforeOneForAnyThenTheLongerTextThenTheOlderRule()
    {
        TitleRule Rule(long id, long project, string text, string? app = null) => new(id, project, text, app);
        var set = new TitleRuleSet([
            Rule(1, 10, "Worklapse"),
            Rule(2, 20, "feedback", "OUTLOOK.EXE"),
            Rule(3, 30, "Worklapse beta"),
            Rule(4, 40, "Presayo"),
            Rule(5, 50, "Presayo"),
            Rule(6, 60, ""),
        ]);

        Assert.Equal(10, set.ProjectOf("devenv.exe", "Worklapse - Microsoft Visual Studio"));
        Assert.Equal(20, set.ProjectOf("OUTLOOK.EXE", "RE: Worklapse beta feedback - Outlook")); // one app beats longer text
        Assert.Equal(30, set.ProjectOf("firefox.exe", "Worklapse beta feedback – Mozilla Firefox")); // longer text
        Assert.Equal(40, set.ProjectOf("rider64.exe", "Presayo – ProductionDocketView.cs"));    // a tie: the older rule
        Assert.Null(set.ProjectOf("explorer.exe", "Downloads"));                                 // an empty rule matches nothing
        Assert.Null(set.ProjectOf("explorer.exe", ""));
        Assert.True(new TitleRuleSet([Rule(1, 1, "")]).IsEmpty);
    }

    [Fact]
    public void RulesBeatSiteAndAppLinksInEveryReadOfHistory()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var recording = new RecordingStore(db);
        var history = new HistoryStore(db);
        var projects = new ProjectStore(db);
        var (worklapse, client, admin) = (projects.AddProject("Worklapse", null), projects.AddProject("Presayo", null), projects.AddProject("Admin", null));

        var devenv = recording.GetOrAddApp("devenv.exe", null);
        var firefox = recording.GetOrAddApp("firefox.exe", null);
        recording.InsertCapture(Nine, CaptureKind.Active, devenv, "Presayo - Microsoft Visual Studio", null);
        recording.InsertCapture(Nine.AddSeconds(10), CaptureKind.Active, devenv, "Worklapse - Microsoft Visual Studio", null);
        recording.InsertCapture(Nine.AddSeconds(20), CaptureKind.Active, firefox, "Presayo issues – GitHub", null, recording.GetOrAddSite("github.com"));
        recording.InsertCapture(Nine.AddSeconds(30), CaptureKind.Active, firefox, "Inbox", null, recording.GetOrAddSite("mail.example"));
        recording.InsertCapture(Nine.AddSeconds(40), CaptureKind.Idle, null, null, null);
        history.SetProject(new ProgramKey(ProgramKind.App, "devenv.exe"), worklapse);
        history.SetProject(new ProgramKey(ProgramKind.Site, "mail.example"), admin);

        long?[] Linked() => history.LoadDay(Day).Captures.Select(c => c.LinkedProjectId).ToArray();
        long?[] Sampled() => history.LoadSamples(Day, Day.AddDays(1)).Select(s => s.LinkedProjectId).ToArray();
        Assert.Equal([worklapse, worklapse, null, admin, null], Linked());

        // A rule beats the app's link and the site's; nothing else changes. Rules apply to history already recorded.
        projects.SetTitleRules(client, [(null, "presayo", null)]);
        Assert.Equal([client, worklapse, client, admin, null], Linked());
        Assert.Equal(Linked(), Sampled());
        Assert.Equal(client, history.LoadDay(Day).Captures[0].RuleProjectId);
        Assert.Equal(worklapse, history.LoadDay(Day).Captures[0].AppProjectId);

        // Only in one app: the browser's page isn't the client's any more.
        projects.SetTitleRules(client, [(projects.GetTitleRules().Single().Id, "Presayo", "DEVENV.EXE")]);
        Assert.Equal([client, worklapse, null, admin, null], Linked());
        Assert.Equal(Linked(), Sampled());

        // Deleting the project deletes its rules.
        projects.DeleteProject(client);
        Assert.Empty(projects.GetTitleRules());
        Assert.Equal([worklapse, worklapse, null, admin, null], Linked());
    }

    [Fact]
    public void SettingAProjectsRulesKeepsTheOnesWithIdsInTheirNewOrder()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);
        var (p, q) = (projects.AddProject("P", null), projects.AddProject("Q", null));

        var first = projects.SetTitleRules(p, [(null, " Alpha ", null), (null, "Beta", " devenv.exe "), (null, "Gamma", "")]);
        projects.SetTitleRules(q, [(null, "Other", null)]);
        Assert.Equal([new TitleRule(first[0], p, "Alpha", null), new TitleRule(first[1], p, "Beta", "devenv.exe"), new TitleRule(first[2], p, "Gamma", null)],
            projects.GetTitleRules().Where(r => r.ProjectId == p));

        // Gamma moves first and changes, Alpha goes, a new one comes; the other project's rule stays.
        var second = projects.SetTitleRules(p, [(first[2], "Gamma2", "code.exe"), (null, "Delta", null), (first[1], "Beta", null)]);
        Assert.Equal(first[2], second[0]);
        Assert.Equal(first[1], second[2]);
        Assert.Equal(["Gamma2", "Delta", "Beta"], projects.GetTitleRules().Where(r => r.ProjectId == p).Select(r => r.Text));
        Assert.Equal(["Other"], projects.GetTitleRules().Where(r => r.ProjectId == q).Select(r => r.Text));

        // A rule of another project can't be taken over, and a rule needs text; neither changes anything.
        Assert.Throws<InvalidOperationException>(() => projects.SetTitleRules(q, [(second[0], "Taken", null)]));
        Assert.Throws<ArgumentException>(() => projects.SetTitleRules(p, [(null, "  ", null)]));
        Assert.Equal(["Gamma2", "Delta", "Beta", "Other"], projects.GetTitleRules().Select(r => r.Text));

        projects.SetTitleRules(p, []);
        Assert.Equal([q], projects.GetTitleRules().Select(r => r.ProjectId));
    }

    [Fact]
    public void ALinksReachLeavesOutTimeARuleDecides()
    {
        CaptureRecord Of(int seconds, string title, long? rule) =>
            new(seconds, Nine.AddSeconds(seconds), CaptureKind.Active, "devenv.exe", null, title, null, false, null, false, [], [], 1, null, rule);
        CaptureRecord[] captures = [Of(0, "Worklapse", null), Of(10, "Presayo", 2), Of(20, "Worklapse", null)];
        var ranges = MarkReach.Link(captures, [], new ProgramKey(ProgramKind.App, "devenv.exe"), Max)
            .Select(r => ((int)(r.Start - Nine).TotalSeconds, (int)(r.End - Nine).TotalSeconds));
        Assert.Equal([(0, 10), (20, 35)], ranges);
    }

    [Fact]
    public void TitlesAreListedPerAppWithTheirActiveTime()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var recording = new RecordingStore(db);
        var history = new HistoryStore(db);

        var code = recording.GetOrAddApp("Code.exe", @"C:\Code\Code.exe");
        var portable = recording.GetOrAddApp("Code.exe", @"D:\Code\Code.exe"); // another copy: one app
        var terminal = recording.GetOrAddApp("WindowsTerminal.exe", null);
        recording.InsertCapture(Nine, CaptureKind.Active, code, "main.cs - Worklapse", null);
        recording.InsertCapture(Nine.AddSeconds(10), CaptureKind.Active, portable, "main.cs - Worklapse", null);
        recording.InsertCapture(Nine.AddSeconds(20), CaptureKind.Active, terminal, "main.cs - Worklapse", null);  // the same title elsewhere
        recording.InsertCapture(Nine.AddSeconds(30), CaptureKind.Active, terminal, "", null);                     // no title: left out
        recording.InsertCapture(Nine.AddSeconds(40), CaptureKind.Idle, null, null, null);
        recording.InsertCapture(Nine.AddSeconds(50), CaptureKind.Active, code, "gone.cs - Worklapse", null);
        history.SetPendingDelete(Nine.AddSeconds(50), Nine.AddSeconds(51), true);                                  // marked: counts for nothing
        recording.InsertCapture(Nine.AddMinutes(5), CaptureKind.Active, code, "main.cs - Worklapse", null);       // last: the maximum

        Assert.Equal([
            new TitleUsage("Code.exe", "main.cs - Worklapse", TimeSpan.FromSeconds(35)),
            new TitleUsage("WindowsTerminal.exe", "main.cs - Worklapse", TimeSpan.FromSeconds(10)),
        ], history.GetTitles(Max).OrderBy(t => t.App));
    }
}
