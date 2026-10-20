using System.Diagnostics;
using System.Text;

namespace Worklapse.Core.Import;

/// <summary>A row of TimeSnapper's PROGRAM table: an app by exe name (upper case), or a website by host.</summary>
/// <param name="Path">The exe's full path, upper case; for a website its name again.</param>
public sealed record TimeSnapperProgram(int Id, string Name, string? Path, bool IsSite);

/// <summary>A row of TimeSnapper's ACTIVITY table: one capture, about 10 s apart (idle time has none).</summary>
/// <param name="Time">Local wall-clock time, as TimeSnapper stores it.</param>
/// <param name="FileName">The picture's absolute path; null once deleted in TimeSnapper's Day Browser.</param>
/// <param name="Program">The exe name in its own case, or "(Unknown)".</param>
/// <param name="ProgramId">The program's <see cref="TimeSnapperProgram"/>.</param>
/// <param name="SiteId">The website's <see cref="TimeSnapperProgram"/>, for some browsers.</param>
public sealed record TimeSnapperActivity(int Id, DateTime Time, string? FileName, string? Program, string? Caption, int? ProgramId, int? SiteId);

/// <summary>What there is to import from a TimeSnapper database. The activities are read once, in id order.</summary>
public interface ITimeSnapperData
{
    IReadOnlyDictionary<int, TimeSnapperProgram> Programs { get; }

    /// <summary>The programs and sites TimeSnapper counts as productive.</summary>
    IReadOnlySet<int> Classified { get; }

    int ActivityCount { get; }

    IEnumerable<TimeSnapperActivity> Activities { get; }
}

/// <summary>
/// Reads a TimeSnapper database through the 32-bit helper next to Worklapse (Worklapse.TimeSnapperReader.exe), which
/// loads TimeSnapper's own Firebird engine from its program folder. Windows only. The database is copied first,
/// since TimeSnapper may be running and the engine writes to the file it opens; the copy is deleted on dispose.
/// </summary>
public sealed class TimeSnapperSource : ITimeSnapperData, IDisposable
{
    public const string ReaderFileName = "Worklapse.TimeSnapperReader.exe";

    private readonly string _copyFolder;
    private readonly Process _process;
    private readonly BinaryReader _reader;
    private readonly Task<string> _errors;
    private bool _read;

    private TimeSnapperSource(string copyFolder, Process process, BinaryReader reader, Task<string> errors)
    {
        _copyFolder = copyFolder;
        _process = process;
        _reader = reader;
        _errors = errors;
    }

    /// <summary>Where TimeSnapper keeps its database for this user.</summary>
    public static string DefaultDatabase => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimeSnapper", "Database", "TIMESNAPPER.FDB");

    /// <summary>Where TimeSnapper installs itself: its engine is needed to read the database.</summary>
    public static string DefaultProgramFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "TimeSnapper");

    public IReadOnlyDictionary<int, TimeSnapperProgram> Programs { get; private set; } = new Dictionary<int, TimeSnapperProgram>();
    public IReadOnlySet<int> Classified { get; private set; } = new HashSet<int>();
    public int ActivityCount { get; private set; }

    public IEnumerable<TimeSnapperActivity> Activities
    {
        get
        {
            if (_read)
                throw new InvalidOperationException("The activities can be read only once.");
            _read = true;
            return ReadActivities();
        }
    }

    /// <summary>Copies the database, starts the reader on the copy and reads the programs.</summary>
    /// <exception cref="TimeSnapperException">TimeSnapper's program folder or the database is missing, or it can't be read.</exception>
    public static TimeSnapperSource Open(string database, string programFolder, CancellationToken cancel = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new TimeSnapperException("TimeSnapper's database can only be read on Windows.");
        if (!File.Exists(database))
            throw new TimeSnapperException("The TimeSnapper database wasn't found.");
        if (!File.Exists(Path.Combine(programFolder, "fbembed.dll")))
            throw new TimeSnapperException("TimeSnapper isn't installed; its database engine is needed to read it.");
        var helper = Path.Combine(AppContext.BaseDirectory, ReaderFileName);
        if (!File.Exists(helper))
            throw new TimeSnapperException($"{ReaderFileName} is missing next to Worklapse.");

        var copyFolder = Path.Combine(Path.GetTempPath(), "Worklapse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copyFolder);
        Process? process = null;
        try
        {
            var copy = Path.Combine(copyFolder, "timesnapper.fdb");
            CopyFile(database, copy, cancel);

            var start = new ProcessStartInfo(helper)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add(copy);
            start.ArgumentList.Add(programFolder);
            process = Process.Start(start) ?? throw new TimeSnapperException("Couldn't start " + ReaderFileName + ".");
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var reader = new BinaryReader(process.StandardOutput.BaseStream, new UTF8Encoding(false));
            var source = new TimeSnapperSource(copyFolder, process, reader, errors);
            try
            {
                source.ReadHeader();
            }
            catch
            {
                source.Dispose();
                throw;
            }
            return source;
        }
        catch when (process is null)
        {
            TryDeleteFolder(copyFolder);
            throw;
        }
    }

    private void ReadHeader()
    {
        try
        {
            if (_reader.ReadInt32() != TimeSnapperStream.Magic || _reader.ReadInt32() != TimeSnapperStream.Version)
                throw new TimeSnapperException("The reader isn't the one this Worklapse expects.");

            var programs = new Dictionary<int, TimeSnapperProgram>();
            for (var count = _reader.ReadInt32(); count > 0; count--)
            {
                var id = _reader.ReadInt32();
                programs[id] = new TimeSnapperProgram(id, _reader.ReadString(), _reader.ReadNullableString(), IsSite: _reader.ReadInt32() == 1);
            }
            Programs = programs;

            var classified = new HashSet<int>();
            for (var count = _reader.ReadInt32(); count > 0; count--)
                classified.Add(_reader.ReadInt32());
            Classified = classified;

            ActivityCount = _reader.ReadInt32();
        }
        catch (EndOfStreamException)
        {
            throw Failed();
        }
    }

    private IEnumerable<TimeSnapperActivity> ReadActivities()
    {
        while (true)
        {
            TimeSnapperActivity activity;
            try
            {
                if (_reader.ReadByte() == TimeSnapperStream.EndOfRows)
                    break;
                activity = new TimeSnapperActivity(
                    _reader.ReadInt32(),
                    new DateTime(_reader.ReadInt64(), DateTimeKind.Local),
                    _reader.ReadNullableString(),
                    _reader.ReadNullableString(),
                    _reader.ReadNullableString(),
                    _reader.ReadNullableInt32(),
                    _reader.ReadNullableInt32());
            }
            catch (EndOfStreamException)
            {
                throw Failed();
            }
            yield return activity;
        }
        _process.WaitForExit();
        if (_process.ExitCode != 0)
            throw Failed();
    }

    /// <summary>The reader stopped early: what it said, if anything.</summary>
    private TimeSnapperException Failed()
    {
        _process.WaitForExit(5000);
        var message = _errors.IsCompleted ? _errors.Result.Trim() : "";
        return new TimeSnapperException("Couldn't read the TimeSnapper database" + (message.Length > 0 ? ": " + message : "."));
    }

    /// <summary>Copies in chunks, so a cancel doesn't wait for the whole file.</summary>
    private static void CopyFile(string from, string to, CancellationToken cancel)
    {
        try
        {
            // TimeSnapper keeps its database open while it runs.
            using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[1 << 20];
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TimeSnapperException("Couldn't copy the TimeSnapper database: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill();
            _process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // it has exited
        }
        _reader.Dispose();
        _process.Dispose();
        TryDeleteFolder(_copyFolder);
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left in the temp folder
        }
    }
}

/// <summary>TimeSnapper's database couldn't be read; the message says why, for the user.</summary>
public sealed class TimeSnapperException(string message) : Exception(message);
