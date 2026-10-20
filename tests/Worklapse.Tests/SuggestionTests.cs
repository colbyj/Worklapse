using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;

namespace Worklapse.Tests;

public class SuggestionTests
{
    private const long Presayo = 1;
    private const long Frankensystem = 2;
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset Noon = new(2026, 6, 17, 12, 0, 0, TimeSpan.FromHours(-6));

    private static CaptureRecord Capture(int seconds, string app, string title, bool productive = true, long? link = null,
        CaptureKind kind = CaptureKind.Active, bool marked = false) =>
        new(seconds, Noon.AddSeconds(seconds), kind, app, null, title, null, productive, null, marked, [], [], RuleProjectId: link);

    /// <summary>One capture every 10 s from <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
    private static IEnumerable<CaptureRecord> Run(int from, int to, string app, string title, bool productive = true, long? link = null,
        CaptureKind kind = CaptureKind.Active)
    {
        for (var s = from; s < to; s += 10)
            yield return Capture(s, app, title, productive, link, kind);
    }

    private static ProjectSpan Span(int from, int to, long? project) => new(Noon.AddSeconds(from), Noon.AddSeconds(to), project);

    private static int Seconds(DateTimeOffset time) => (int)(time - Noon).TotalSeconds;

    private static ProjectClassifier Classifier() => ProjectClassifier.Train([
        new(CaptureWords.Of("rider64.exe", null, "Presayo – MigrateTo1_3.sql"), Presayo, 600),
        new(CaptureWords.Of("WindowsTerminal.exe", null, "Group label presets by printer type"), Presayo, 600),
        new(CaptureWords.Of("WindowsTerminal.exe", null, "Improve bride-of-frankensystem performance"), Frankensystem, 600),
        new(CaptureWords.Of("firefox.exe", "facebook.com", "Facebook"), null, 600),
    ]);

    [Fact]
    public void TitleWordsSplitAtAnythingButLettersAndDigits()
    {
        Assert.Equal(["bride", "of", "frankensystem", "v2"], CaptureWords.Split("bride-of-Frankensystem v2.1 a", 2));
        Assert.Equal(["app:rider64.exe", "title:presayo", "title:sql"], CaptureWords.Of("rider64.exe", null, "Presayo – x.sql"));
    }

    [Fact]
    public void ScreenWordsKeepIdentifiersWholeAndCountEachOnce()
    {
        Assert.Equal(["direct_thermal_preset", "abc"], ScreenWords.Of("direct_thermal_preset 123 ab Abc ABC"));
    }

    [Fact]
    public void ClassifierGuessesTheProjectWhoseWordsMatch()
    {
        var classifier = Classifier();
        var probabilities = classifier.Probabilities(CaptureWords.Of("WindowsTerminal.exe", null, "Add label presets"));
        var best = classifier.Labels[Array.IndexOf(probabilities, probabilities.Max())];

        Assert.Equal(Presayo, best);
        Assert.Equal(1, probabilities.Sum(), precision: 9);
    }

    [Fact]
    public void ScreenWordsCountLessThanTitleWords()
    {
        // The title says Frankensystem; the screen says Presayo once. The title wins.
        var classifier = ProjectClassifier.Train([
            new(["title:frankensystem", "screen:presayo"], Frankensystem, 600),
            new(["title:presayo", "screen:presayo"], Presayo, 600),
        ]);
        var probabilities = classifier.Probabilities(["title:frankensystem", "screen:presayo"]);

        Assert.True(probabilities[classifier.Labels.ToList().IndexOf(Frankensystem)] > 0.5);
    }

    [Fact]
    public void EvidenceIsTheWordsThatPointToTheProject()
    {
        var classifier = Classifier();
        var evidence = classifier.Evidence([CaptureWords.Of("WindowsTerminal.exe", null, "label presets")], Presayo, 3);

        Assert.Contains("title:presets", evidence);
        Assert.DoesNotContain("app:windowsterminal.exe", evidence); // both projects use the terminal
    }

    [Fact]
    public void TrainingTakesRangesByHandThenLinksAndCountsUnproductiveTimeAsNoProject()
    {
        List<CaptureRecord> captures = [
            .. Run(0, 300, "rider64.exe", "Presayo – x.sql"),                                  // in the Presayo range
            .. Run(300, 400, "firefox.exe", "Facebook", productive: false),                    // in the range, but unproductive
            .. Run(400, 600, "SourceGit.exe", "bride-of-frankensystem", link: Frankensystem),  // a short switch inside the range
            .. Run(600, 700, "SourceGit.exe", "bride-of-frankensystem", link: Frankensystem),  // outside: its link teaches
            .. Run(700, 800, "explorer.exe", "Downloads"),                                     // nothing says anything
        ];
        captures.Add(Capture(800, "rider64.exe", "Presayo – x.sql", link: Presayo) with { PendingDelete = true });

        var examples = TrainingSet.FromDay(captures, [Span(0, 600, Presayo)], Max, SuggestionOptOuts.None).ToList();

        Assert.Equal(300, examples.Where(e => e.ProjectId == Presayo).Sum(e => e.Seconds));
        Assert.Equal(100, examples.Where(e => e.ProjectId is null).Sum(e => e.Seconds));
        Assert.Equal(100, examples.Where(e => e.ProjectId == Frankensystem).Sum(e => e.Seconds));
        Assert.DoesNotContain(examples, e => e.Words.Contains("app:explorer.exe"));
    }

    [Fact]
    public void ALongSwitchInsideARangeTeachesTheRangesProject()
    {
        var captures = Run(0, 900, "SourceGit.exe", "bride-of-frankensystem", link: Frankensystem).ToList(); // 15 min

        var examples = TrainingSet.FromDay(captures, [Span(0, 900, Presayo)], Max, SuggestionOptOuts.None).ToList();

        Assert.Equal(905, examples.Where(e => e.ProjectId == Presayo).Sum(e => e.Seconds)); // the last capture counts the maximum
    }

    [Fact]
    public void OptedOutProjectsAndAppsTeachNothing()
    {
        List<CaptureRecord> captures = [
            .. Run(0, 100, "slack.exe", "Total Label", link: Presayo),
            .. Run(100, 200, "rider64.exe", "Admin stuff", link: 3),
            .. Run(200, 300, "rider64.exe", "Presayo – x.sql", link: Presayo),
        ];
        var optOuts = new SuggestionOptOuts(new HashSet<long> { 3 }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Slack.exe" },
            new HashSet<string>());

        var examples = TrainingSet.FromDay(captures, [], Max, optOuts).ToList();

        Assert.All(examples, e => Assert.Contains("title:presayo", e.Words));
        Assert.Equal(105, examples.Sum(e => e.Seconds));
    }

    [Fact]
    public void StartersTeachEachProjectsNameLinksAndRules()
    {
        var examples = TrainingSet.Starters(
            [new Project(Presayo, "Presayo", null, null, false), new Project(9, "Old", null, null, Archived: true)],
            [new TitleRule(1, Presayo, "Label tools", null)],
            [new ProgramUsage(new ProgramKey(ProgramKind.App, "Presayo.exe"), true, false, TimeSpan.Zero, Presayo)],
            SuggestionOptOuts.None).ToList();

        var starter = Assert.Single(examples);
        Assert.Equal(["title:presayo", "title:label", "title:tools", "app:presayo.exe"], starter.Words);
    }

    [Fact]
    public void ThePathIgnoresFlickerButFollowsALongChange()
    {
        double[] A = [Math.Log(0.8), Math.Log(0.2)], B = [Math.Log(0.2), Math.Log(0.8)];

        Assert.Equal([0, 0, 0, 0, 0], BlockPath.Find([A, A, B, A, A], 6));
        // Each A step favours A by log 4 ≈ 1.39: two of them don't pay for a switch, five do.
        Assert.Equal([1, 1, 1, 1, 1, 1, 1, 1], BlockPath.Find([A, A, B, B, B, B, B, B], 6));
        Assert.Equal([0, 0, 0, 0, 0, 1, 1, 1, 1, 1], BlockPath.Find([A, A, A, A, A, B, B, B, B, B], 6));
    }

    [Fact]
    public void ABlockJoinsAProjectsTimeAndListsWhatToDecide()
    {
        List<CaptureRecord> captures = [
            .. Run(0, 300, "rider64.exe", "Presayo – x.sql", link: Presayo),             // already Presayo, by its rule
            .. Run(300, 600, "WindowsTerminal.exe", "Group label presets by printer type"),
            .. Run(600, 900, "firefox.exe", "Facebook", productive: false),             // 5 min away: a break
            .. Run(900, 1200, "WindowsTerminal.exe", "Group label presets by printer type"),
            .. Run(1200, 1500, "WindowsTerminal.exe", "Improve bride-of-frankensystem performance"), // a switch
            .. Run(1500, 1800, "WindowsTerminal.exe", "Group label presets by printer type"),
            .. Run(1800, 1860, "WindowsTerminal.exe", "", kind: CaptureKind.Idle),    // 1 min away: part of the block
            .. Run(1860, 2100, "WindowsTerminal.exe", "Group label presets by printer type"),
            // 40 minutes not recorded: past the split limit, so a new block.
            .. Run(4500, 4800, "WindowsTerminal.exe", "Improve bride-of-frankensystem performance"),
        ];

        var suggestions = SuggestionBuilder.ForDay(captures, [], Classifier(), SuggestionLimits.Default, Max, SuggestionOptOuts.None);

        Assert.Equal(2, suggestions.Count);
        var presayo = suggestions[0];
        Assert.Equal((Presayo, 0, 2105), (presayo.ProjectId, Seconds(presayo.Start), Seconds(presayo.End)));
        Assert.Equal(TimeSpan.FromSeconds(300 + 300 + 300 + 60 + 245), presayo.Adds); // not the rule's 5 minutes
        Assert.Equal([
            (SuggestionPartKind.Break, 600, 900, (long?)null),
            (SuggestionPartKind.Switch, 1200, 1500, Frankensystem),
        ], presayo.Parts.Select(p => (p.Kind, Seconds(p.Start), Seconds(p.End), p.ProjectId)));
        Assert.Contains("title:presets", presayo.Evidence);
        Assert.False(presayo.IsUnsure);

        var frankensystem = suggestions[1];
        Assert.Equal((Frankensystem, 4500, 4805), (frankensystem.ProjectId, Seconds(frankensystem.Start), Seconds(frankensystem.End)));
        Assert.Empty(frankensystem.Parts);
    }

    [Fact]
    public void TimeSetByHandIsLeftAloneEvenWhenSetToNoProject()
    {
        List<CaptureRecord> captures = [
            .. Run(0, 300, "WindowsTerminal.exe", "Group label presets by printer type"),
            .. Run(300, 600, "WindowsTerminal.exe", "Group label presets by printer type"),   // set to no project by hand
            .. Run(600, 900, "WindowsTerminal.exe", "Group label presets by printer type"),
        ];

        var suggestion = Assert.Single(SuggestionBuilder.ForDay(captures, [Span(300, 600, null)], Classifier(), SuggestionLimits.Default,
            Max, SuggestionOptOuts.None));

        Assert.Equal(TimeSpan.FromSeconds(300 + 305), suggestion.Adds);
        Assert.Empty(suggestion.Parts); // the range by hand is neither a break nor part of the block's time to add
    }

    [Fact]
    public void NothingIsSuggestedWhereEverythingHasAProject()
    {
        var captures = Run(0, 600, "rider64.exe", "Presayo – x.sql", link: Presayo).ToList();

        Assert.Empty(SuggestionBuilder.ForDay(captures, [], Classifier(), SuggestionLimits.Default, Max, SuggestionOptOuts.None));
    }

    [Fact]
    public void AProjectNeverSuggestedIsntSuggested()
    {
        var captures = Run(0, 600, "WindowsTerminal.exe", "Group label presets by printer type").ToList();
        var optOuts = new SuggestionOptOuts(new HashSet<long> { Presayo }, new HashSet<string>(), new HashSet<string>());

        Assert.DoesNotContain(SuggestionBuilder.ForDay(captures, [], Classifier(), SuggestionLimits.Default, Max, optOuts),
            s => s.ProjectId == Presayo);
    }
}
