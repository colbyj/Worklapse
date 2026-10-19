using Tmds.DBus.Protocol;

namespace Worklapse.Capture.Linux;

/// <summary>
/// Worklapse's link to KWin: a session-bus connection, and a KWin script (KWinScript.js) that reports the screens
/// and the active window to it whenever they change. The screen source and the foreground probe read the latest
/// reports from here; the screen source also calls KWin's screenshot interface over this connection.
/// </summary>
internal sealed class KWinSession : IDisposable
{
    public const string KWinService = "org.kde.KWin";

    /// <summary>Where the script's reports arrive.</summary>
    private const string ListenPath = "/app/worklapse/KWin";
    private const string ListenInterface = "app.worklapse.KWin";

    /// <summary>The script's name in KWin, so a script left behind by a crash is replaced rather than doubled.</summary>
    private const string ScriptName = "worklapse";

    /// <summary>How long to wait for KWin to run the script and report the screens.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    private readonly string _kwinOwner;
    private readonly string _scriptPath;
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _screensReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, (KWinScreen Screen, int Width, int Height)> _capturedSizes = [];
    private KWinScreens _screens = new([]);
    private ScreenLayout _layout = ScreenLayout.Empty;
    private KWinWindow? _window;

    private KWinSession(DBusConnection connection, string kwinOwner, string scriptPath)
    {
        Connection = connection;
        _kwinOwner = kwinOwner;
        _scriptPath = scriptPath;
    }

    public DBusConnection Connection { get; }

    /// <summary>The screens as last reported, placed in pixels.</summary>
    public ScreenLayout Layout
    {
        get
        {
            lock (_gate)
                return _layout;
        }
    }

    /// <summary>The active window as last reported; null when none has focus.</summary>
    public KWinWindow? ActiveWindow
    {
        get
        {
            lock (_gate)
                return _window;
        }
    }

    /// <summary>
    /// Connects to the session bus, loads the script into KWin and waits for its first report. Throws
    /// <see cref="PlatformNotSupportedException"/> when there is no session bus or no KWin on it.
    /// </summary>
    public static KWinSession Start() => Task.Run(StartAsync).GetAwaiter().GetResult();

    private static async Task<KWinSession> StartAsync()
    {
        var address = DBusAddress.Session
            ?? throw new PlatformNotSupportedException("There is no D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set).");
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);

            string kwinOwner;
            try
            {
                kwinOwner = await connection.GetNameOwnerAsync(KWinService).ConfigureAwait(false);
            }
            catch (DBusErrorReplyException)
            {
                throw new PlatformNotSupportedException("KWin isn't running: Worklapse records only in a KDE Plasma session so far.");
            }

            var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } dir ? dir : Path.GetTempPath();
            var session = new KWinSession(connection, kwinOwner, Path.Combine(runtimeDirectory, "worklapse-kwin.js"));
            connection.AddMethodHandler(new Listener(session));
            try
            {
                await session.LoadScriptAsync().ConfigureAwait(false);
                if (await Task.WhenAny(session._screensReported.Task, Task.Delay(StartTimeout)).ConfigureAwait(false)
                    != session._screensReported.Task)
                {
                    throw new InvalidOperationException(
                        "KWin loaded Worklapse's script but it didn't report the screens. KWin's log (journalctl --user -e) may say why.");
                }
            }
            catch
            {
                session.Dispose();
                throw;
            }
            return session;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task LoadScriptAsync()
    {
        await using (var resource = typeof(KWinSession).Assembly.GetManifestResourceStream("Worklapse.Capture.Linux.KWinScript.js")!)
        using (var reader = new StreamReader(resource))
        {
            var script = (await reader.ReadToEndAsync().ConfigureAwait(false))
                .Replace("%SERVICE%", Connection.UniqueName)
                .Replace("%PATH%", ListenPath)
                .Replace("%INTERFACE%", ListenInterface);
            await File.WriteAllTextAsync(_scriptPath, script).ConfigureAwait(false);
        }

        var loaded = await Connection.CallAsync(KWinService, "/Scripting", "org.kde.kwin.Scripting", "isScriptLoaded", "s",
            (ref MessageWriter w) => w.WriteString(ScriptName),
            static reply => reply.GetBodyReader().ReadBool()).ConfigureAwait(false);
        if (loaded)
            await UnloadScriptAsync().ConfigureAwait(false);

        var id = await Connection.CallAsync(KWinService, "/Scripting", "org.kde.kwin.Scripting", "loadScript", "ss",
            (ref MessageWriter w) =>
            {
                w.WriteString(_scriptPath);
                w.WriteString(ScriptName);
            },
            static reply => reply.GetBodyReader().ReadInt32()).ConfigureAwait(false);
        if (id < 0)
            throw new InvalidOperationException($"KWin couldn't load Worklapse's script ({_scriptPath}).");

        // Plasma 6 runs one script through its own object; starting all loaded scripts does it too.
        try
        {
            await Connection.CallAsync(KWinService, $"/Scripting/Script{id}", "org.kde.kwin.Script", "run").ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
            await Connection.CallAsync(KWinService, "/Scripting", "org.kde.kwin.Scripting", "start").ConfigureAwait(false);
        }
    }

    private Task UnloadScriptAsync() =>
        Connection.CallAsync(KWinService, "/Scripting", "org.kde.kwin.Scripting", "unloadScript", "s",
            (ref MessageWriter w) => w.WriteString(ScriptName));

    /// <summary>
    /// Tells the session the size a capture of a screen actually had, which places the screens more exactly than
    /// KWin's figures (see <see cref="ScreenLayout.From"/>).
    /// </summary>
    public void ReportCapturedSize(KWinScreen screen, int width, int height)
    {
        lock (_gate)
        {
            if (_capturedSizes.TryGetValue(screen.Name, out var known) && known == (screen, width, height))
                return;
            _capturedSizes[screen.Name] = (screen, width, height);
            UpdateLayout();
        }
    }

    private void UpdateLayout() =>
        _layout = ScreenLayout.From(_screens, screen =>
            _capturedSizes.TryGetValue(screen.Name, out var known) && known.Screen == screen
                ? (known.Width, known.Height)
                : null);

    private void Receive(string json)
    {
        if (!KWinReports.TryParse(json, out var window, out var screens))
            return;
        lock (_gate)
        {
            if (screens is not null)
            {
                _screens = screens;
                UpdateLayout();
            }
            else
            {
                _window = window;
            }
        }
        if (screens is not null)
            _screensReported.TrySetResult();
    }

    public void Dispose()
    {
        try
        {
            // KWin would otherwise keep running the script, calling a name that no longer exists.
            UnloadScriptAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // KWin went away first.
        }
        Connection.Dispose();
        try
        {
            File.Delete(_scriptPath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Receives the script's reports; only KWin may send them.</summary>
    private sealed class Listener(KWinSession session) : IPathMethodHandler
    {
        public string Path => ListenPath;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if (request.InterfaceAsString != ListenInterface || request.MemberAsString != "Update" || request.SignatureAsString != "s")
            {
                context.ReplyUnknownMethodError();
                return default;
            }
            if (request.SenderAsString != session._kwinOwner)
            {
                context.ReplyError("org.freedesktop.DBus.Error.AccessDenied", "Only KWin reports to Worklapse.");
                return default;
            }

            session.Receive(request.GetBodyReader().ReadString());
            if (!context.NoReplyExpected)
            {
                using var writer = context.CreateReplyWriter("");
                context.Reply(writer.CreateMessage());
            }
            return default;
        }
    }
}
