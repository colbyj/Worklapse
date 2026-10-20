using Worklapse.Capture.Linux;

namespace Worklapse.Tests;

/// <summary>XDG_CONFIG_HOME is process-wide: these run on their own.</summary>
[CollectionDefinition(nameof(AutostartTests), DisableParallelization = true)]
public sealed class AutostartCollection;

[Collection(nameof(AutostartTests))]
public class AutostartTests
{
    [Fact]
    public void TheAutostartEntryIsWrittenSwitchedOffElsewhereAndRemoved()
    {
        using var folder = new TempFolder();
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", folder.Path);
        try
        {
            Assert.False(AutostartEntry.IsEnabled);

            AutostartEntry.Enable(null, "--autostart");
            Assert.Equal(Path.Combine(folder.Path, "autostart", "worklapse.desktop"), AutostartEntry.FilePath);
            Assert.True(AutostartEntry.IsEnabled);
            Assert.Contains(" --autostart\n", File.ReadAllText(AutostartEntry.FilePath));

            // The desktop's autostart settings switch it off without removing it.
            File.AppendAllText(AutostartEntry.FilePath, "Hidden=true\n");
            Assert.False(AutostartEntry.IsEnabled);

            AutostartEntry.Enable(null, "--autostart");
            Assert.True(AutostartEntry.IsEnabled);

            AutostartEntry.Disable();
            Assert.False(File.Exists(AutostartEntry.FilePath));
            Assert.False(AutostartEntry.IsEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
        }
    }
}
