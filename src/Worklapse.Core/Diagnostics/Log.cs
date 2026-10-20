using System.Runtime.InteropServices;
using Worklapse.Core.Settings;

namespace Worklapse.Core.Diagnostics;

/// <summary>A crash recorded by <see cref="Log.Crash"/>, for the next start to report.</summary>
public sealed record CrashReport(DateTimeOffset At, string Message);

/// <summary>
/// Worklapse's log file: errors, and the few events that put them in context (start, quit). Nothing is written until
/// the app calls <see cref="Start"/>, so tests and tools write nowhere. It lives beside the settings (WORKLAPSE_CONFIG_DIR
/// moves it with them) and never leaves the computer.
/// </summary>
public static class Log
{
    /// <summary>Past this size the log starts over; the previous one is kept as <c>worklapse.old.log</c>.</summary>
    internal const long MaxBytes = 1024 * 1024;

    private static readonly Lock Gate = new();
    private static string? _folder; // under Gate; null until started

    public static string Folder => Path.Combine(SettingsStore.ConfigFolder, "Logs");

    private static string FilePath(string folder) => Path.Combine(folder, "worklapse.log");
    private static string OldFilePath(string folder) => Path.Combine(folder, "worklapse.old.log");
    private static string CrashPath(string folder) => Path.Combine(folder, "crash.txt");

    public static void Start(string version) => Start(version, Folder);

    /// <summary>Starts the log in a folder of its own (for tests).</summary>
    internal static void Start(string version, string folder)
    {
        lock (Gate)
            _folder = folder;
        Info($"Worklapse {version} started ({RuntimeInformation.OSDescription}, .NET {Environment.Version})");
    }

    /// <summary>Stops writing (for tests).</summary>
    internal static void Stop()
    {
        lock (Gate)
            _folder = null;
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>
    /// Logs the error that is ending the process and leaves a note for the next start to report (see
    /// <see cref="TakeCrashReport"/>), since nothing can be shown now.
    /// </summary>
    public static void Crash(Exception exception)
    {
        Write("FATAL", "Worklapse stopped because of an unexpected error.", exception);
        lock (Gate)
        {
            if (_folder is null)
                return;
            TryWrite(() => File.WriteAllText(CrashPath(_folder), $"{DateTimeOffset.Now:O}\n{exception.Message}"));
        }
    }

    /// <summary>The crash the last run ended with, if it did; reported once (the note is removed).</summary>
    public static CrashReport? TakeCrashReport()
    {
        lock (Gate)
        {
            if (_folder is null)
                return null;
            var path = CrashPath(_folder);
            CrashReport? report = null;
            TryWrite(() =>
            {
                if (!File.Exists(path))
                    return;
                var text = File.ReadAllText(path);
                File.Delete(path);
                var lineEnd = text.IndexOf('\n');
                if (lineEnd > 0 && DateTimeOffset.TryParse(text[..lineEnd], out var at))
                    report = new CrashReport(at, text[(lineEnd + 1)..]);
            });
            return report;
        }
    }

    private static void Write(string level, string message, Exception? exception)
    {
        var entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level,-5} {message}{Environment.NewLine}";
        if (exception is not null)
            entry += exception + Environment.NewLine;
        lock (Gate)
        {
            if (_folder is not { } folder)
                return;
            TryWrite(() =>
            {
                Directory.CreateDirectory(folder);
                var path = FilePath(folder);
                if (new FileInfo(path) is { Exists: true, Length: > MaxBytes })
                    File.Move(path, OldFilePath(folder), overwrite: true);
                File.AppendAllText(path, entry);
            });
        }
    }

    private static void TryWrite(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // There's nowhere to say the log can't be written; errors still reach the windows where they have a place.
        }
    }
}
