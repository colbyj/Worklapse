namespace Worklapse.Tests;

internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "worklapse-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // SQLite may hold the WAL files briefly after the connection closes.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
