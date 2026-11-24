using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velopack;
using Velopack.Sources;
using Worklapse.Core.Diagnostics;

namespace Worklapse.App.Updates;

/// <summary>
/// Updates from the repository's GitHub releases: looked for at start and then every few hours (Worklapse can stay in
/// the tray for days), downloaded in the background, then installed when Worklapse quits, or at once from the main
/// window's bar (which restarts it). Once one is downloaded it stops looking. Only an installed
/// Worklapse updates; one run from a build or a publish folder has nothing to update and never looks. Used from the UI
/// thread.
/// </summary>
public sealed partial class AppUpdates : ObservableObject
{
    private const string Repository = "https://github.com/colbyj/Worklapse";
    // GitHub allows 60 unauthenticated API requests an hour per address; this is far below that.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly Func<Task> _restart;
    private UpdateManager? _manager; // null until an installed Worklapse has looked
    private VelopackAsset? _downloaded;
    private DispatcherTimer? _timer;
    private bool _checking;

    /// <summary>This Worklapse's version (the release's, which <c>release/release.ps1</c> packs under).</summary>
    public static string CurrentVersion { get; } = typeof(AppUpdates).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <param name="restart">Quits Worklapse the usual way, then has <see cref="InstallAfterExit"/> start it again.</param>
    public AppUpdates(Func<Task> restart) => _restart = restart;

    /// <summary>The version downloaded and waiting to be installed; null while there's none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(ReadyText))]
    private string? _readyVersion;

    public bool IsReady => ReadyVersion is not null;

    public string ReadyText => $"Worklapse {ReadyVersion} is ready.";

    /// <summary>The releases were read and none is newer than this one.</summary>
    [ObservableProperty]
    private bool _isUpToDate;

    /// <summary>Looks for an update now, then every <see cref="CheckInterval"/> until one is downloaded.</summary>
    public void Start()
    {
        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += (_, _) => _ = CheckAsync();
        _ = CheckAsync();
    }

    /// <summary>
    /// Downloads a newer release if there is one. Failing (offline, say) is only logged; the next check tries again.
    /// </summary>
    private async Task CheckAsync()
    {
        if (_checking || _downloaded is not null)
            return;
        _checking = true;
        try
        {
            var manager = _manager
                ?? new UpdateManager(new GithubSource(Repository, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
            {
                _timer?.Stop(); // a build or a publish folder: there's nothing to update, now or later
                return;
            }
            _manager = manager;
            _timer?.Start();
            if (await manager.CheckForUpdatesAsync() is not { } update)
            {
                IsUpToDate = true;
                return;
            }
            IsUpToDate = false;
            await manager.DownloadUpdatesAsync(update);
            _downloaded = update.TargetFullRelease;
            _timer?.Stop();
            ReadyVersion = _downloaded.Version.ToString();
            Log.Info($"Worklapse {ReadyVersion} is downloaded; it installs when Worklapse quits.");
        }
        catch (Exception ex)
        {
            Log.Error("Looking for an update failed.", ex);
        }
        finally
        {
            _checking = false;
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
