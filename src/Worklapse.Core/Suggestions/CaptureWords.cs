using Worklapse.Core.History;

namespace Worklapse.Core.Suggestions;

/// <summary>
/// The words a capture is classified by: its app, its site, the words of its window title, and the words read off its
/// screen. Each kind is kept apart by a prefix, so "presayo" in a title and "presayo" on the screen weigh differently.
/// </summary>
public static class CaptureWords
{
    public const string AppPrefix = "app:";
    public const string SitePrefix = "site:";
    public const string TitlePrefix = "title:";
    public const string ScreenPrefix = "screen:";

    /// <summary>The shortest title word kept; shorter ones are mostly noise ("a", "-").</summary>
    private const int MinTitleWordLength = 2;

    /// <param name="screenWords">Words read off the capture's screen (<see cref="ScreenWords"/>), if any.</param>
    public static List<string> Of(CaptureRecord capture, IReadOnlyCollection<string>? screenWords = null) =>
        Of(capture.AppName, capture.Site, capture.WindowTitle, screenWords);

    public static List<string> Of(string? app, string? site, string? title, IReadOnlyCollection<string>? screenWords = null)
    {
        var words = new List<string>();
        if (!string.IsNullOrEmpty(app))
            words.Add(AppPrefix + app.ToLowerInvariant());
        if (!string.IsNullOrEmpty(site))
            words.Add(SitePrefix + site.ToLowerInvariant());
        foreach (var word in Split(title, MinTitleWordLength))
            words.Add(TitlePrefix + word);
        if (screenWords is not null)
            foreach (var word in screenWords)
                words.Add(ScreenPrefix + word);
        return words;
    }

    /// <summary>
    /// The words of a text, lower case, split at anything that isn't a letter or digit (so "bride-of-frankensystem" gives
    /// "bride", "of", "frankensystem"), at least <paramref name="minLength"/> long and not all digits.
    /// </summary>
    /// <param name="keepUnderscores">Underscores join words instead ("direct_thermal_preset" stays one).</param>
    public static IEnumerable<string> Split(string? text, int minLength, bool keepUnderscores = false)
    {
        if (string.IsNullOrEmpty(text))
            yield break;
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && (char.IsLetterOrDigit(text[i]) || keepUnderscores && text[i] == '_');
            if (inWord && start < 0)
                start = i;
            else if (!inWord && start >= 0)
            {
                var word = text[start..i];
                start = -1;
                if (word.Length >= minLength && !word.All(char.IsDigit))
                    yield return word.ToLowerInvariant();
            }
        }
    }
}

/// <summary>What is kept of the text read off a screen: its distinct words, for suggestions only.</summary>
public static class ScreenWords
{
    /// <summary>Shorter words read off a screen are mostly OCR noise.</summary>
    public const int MinLength = 3;

    /// <summary>
    /// The distinct words of some text read off a screen, as <see cref="CaptureWords.Split"/> gives them, identifiers with
    /// underscores kept whole: on screen they name things (tables, files) that tell projects apart.
    /// </summary>
    public static string[] Of(string text) => CaptureWords.Split(text, MinLength, keepUnderscores: true).Distinct().ToArray();
}
