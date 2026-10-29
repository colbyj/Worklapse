using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velopack;
using Velopack.Sources;
using Worklapse.Core.Diagnostics;

namespace Worklapse.App.Updates;

/// <summary>
/// Updates from the repository's GitHub releases: looked for once at start and downloaded in the background, then
/// installed when Worklapse quits, or at once from the main window's bar (which restarts it). Only an installed
/// Worklapse updates; one run from a build or a publish folder has nothing to update and never looks. Used from the UI
/// thread.
/// </summary>
public sealed partial class AppUpdates : ObservableObject
{
    private const string Repository = "https://github.com/colbyj/Worklapse";

    private readonly Func<Task> _restart;
    private UpdateManager? _manager; // null until an installed Worklapse has looked
    private VelopackAsset? _downloaded;

    /// <param name="restart">Quits Worklapse the usual way, then has <see cref="InstallAfterExit"/> start it again.</param>
    public AppUpdates(Func<Task> restart) => _restart = restart;

    /// <summary>The version downloaded and waiting to be installed; null while there's none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(ReadyText))]
    private string? _readyVersion;

    public bool IsReady => ReadyVersion is not null;

    public string ReadyText => $"Worklapse {ReadyVersion} is ready.";

    /// <summary>Downloads a newer release if there is one. Failing (offline, say) is only logged.</summary>
    public async Task CheckAsync()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(Repository, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
                return;
            _manager = manager;
            if (await manager.CheckForUpdatesAsync() is not { } update)
                return;
            await manager.DownloadUpdatesAsync(update);
            _downloaded = update.TargetFullRelease;
            ReadyVersion = _downloaded.Version.ToString();
            Log.Info($"Worklapse {ReadyVersion} is downloaded; it installs when Worklapse quits.");
        }
        catch (Exception ex)
        {
            Log.Error("Looking for an update failed.", ex);
        }
    }

    [RelayCommand]
    private Task Restart() => _restart();

    /// <summary>
    /// Has the downloaded update installed once this process has exited (Velopack's updater waits up to a minute for
    /// that), then Worklapse started again if <paramref name="restart"/>. Does nothing when there's no update.
    /// </summary>
    public void InstallAfterExit(bool restart)
    {
        if (_manager is null || _downloaded is null)
            return;
        try
        {
            // Its progress window shows only when the developer asked for the restart.
            _manager.WaitExitThenApplyUpdates(_downloaded, silent: !restart, restart, restartArgs: []);
        }
        catch (Exception ex)
        {
            Log.Error("The update couldn't be started.", ex);
        }
    }
}
