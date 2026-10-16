using Worklapse.Core.Settings;

namespace Worklapse.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData("""{"DataFolder": "C:\Users\me"}""")] // unescaped backslashes
    [InlineData("""{"CaptureMode": "EverythingPlease"}""")]
    [InlineData("""{"IntervalSeconds": "ten"}""")]
    [InlineData("")]
    [InlineData("null")]
    public void UnreadableFilesAreReportedWithTheirPath(string json)
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, json);

        var error = Assert.Throws<SettingsFileException>(() => new SettingsStore(path));
        Assert.Equal(path, error.Path);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void SettingAsideABrokenFileStartsFromDefaultsAndKeepsIt()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, "{ broken");

        var aside = SettingsStore.SetAside(path);

        var defaults = new WorklapseSettings();
        var loaded = new SettingsStore(path).Current;
        Assert.Equal(defaults with { SelectedMonitors = loaded.SelectedMonitors }, loaded);
        Assert.Equal("{ broken", File.ReadAllText(aside));
        Assert.StartsWith(path + ".broken-", aside);
    }

    [Fact]
    public void GpuRenderingIsOffUnlessTheFileTurnsItOn()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, """{"IntervalSeconds": 10}""");
        Assert.False(new SettingsStore(path).Current.UseGpuRendering);

        File.WriteAllText(path, """{"UseGpuRendering": true}""");
        var store = new SettingsStore(path);
        Assert.True(store.Current.UseGpuRendering);

        // Saving other changes keeps it, and writes it out where it can be found.
        store.Save(store.Current with { IdleMinutes = 7 });
        Assert.True(new SettingsStore(path).Current.UseGpuRendering);
        Assert.Contains("\"UseGpuRendering\": true", File.ReadAllText(path));
    }

    [Fact]
    public void NullValuesInAHandEditedFileFallBackToDefaults()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, """{"SelectedMonitors": null, "DataFolder": null, "IdleMinutes": 9999}""");

        var settings = new SettingsStore(path).Current;

        Assert.Empty(settings.SelectedMonitors);
        Assert.Equal(WorklapseSettings.DefaultDataFolder, settings.DataFolder);
        Assert.Equal(120, settings.IdleMinutes);
    }
}
