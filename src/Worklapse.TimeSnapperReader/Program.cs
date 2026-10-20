using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Worklapse.Core.Import;

namespace Worklapse.TimeSnapperReader;

/// <summary>
/// Writes what Worklapse imports from a TimeSnapper database to standard output (see <see cref="TimeSnapperStream"/>).
/// Usage: Worklapse.TimeSnapperReader.exe &lt;database&gt; &lt;TimeSnapper program folder&gt;. Open a copy of the
/// database: the engine writes to the file it opens. Errors go to standard error, with exit code 1.
/// </summary>
internal static class Program
{
    private const string ClientAssembly = "FirebirdSql.Data.FirebirdClient.dll";
    private const string ConnectionType = "FirebirdSql.Data.FirebirdClient.FbConnection";

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: Worklapse.TimeSnapperReader <database> <TimeSnapper program folder>");
            return 2;
        }

        try
        {
            var database = Path.GetFullPath(args[0]);
            var programFolder = Path.GetFullPath(args[1]);

            // The client finds the embedded engine (fbembed.dll), and the engine its messages, in TimeSnapper's folder.
            if (!SetDllDirectory(programFolder))
                throw new IOException("Could not use TimeSnapper's program folder: " + new System.ComponentModel.Win32Exception().Message);
            var client = Assembly.LoadFrom(Path.Combine(programFolder, ClientAssembly));
            var type = client.GetType(ConnectionType, throwOnError: true);

            // ServerType 1 = embedded. SYSDBA / masterkey is what TimeSnapper uses (embedded doesn't check it).
            var connectionString = $"Database={database}; Userid=SYSDBA; Password=masterkey; ServerType=1; Pooling=false";
            using (var connection = (DbConnection)Activator.CreateInstance(type, connectionString))
            {
                connection.Open();
                var output = new BinaryWriter(new BufferedStream(Console.OpenStandardOutput(), 1 << 16), new UTF8Encoding(false));
                Write(connection, output);
                output.Flush();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine((ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message);
            return 1;
        }
    }

    private static void Write(DbConnection connection, BinaryWriter output)
    {
        output.Write(TimeSnapperStream.Magic);
        output.Write(TimeSnapperStream.Version);

        var programs = new List<object[]>();
        Query(connection, "SELECT ID, NAME, PATH, PROGTYPE FROM PROGRAM", programs.Add);
        output.Write(programs.Count);
        foreach (var row in programs)
        {
            output.Write(Int(row[0])!.Value);
            output.Write(Text(row[1]) ?? "");
            output.WriteNullable(Text(row[2]));
            output.Write(Int(row[3]) ?? 0);
        }

        var classified = new List<object[]>();
        Query(connection, "SELECT DISTINCT PROGID FROM CLASSIFICATION WHERE PROGID IS NOT NULL", classified.Add);
        output.Write(classified.Count);
        foreach (var row in classified)
            output.Write(Int(row[0])!.Value);

        var count = 0;
        Query(connection, "SELECT COUNT(*) FROM ACTIVITY", row => count = Int(row[0])!.Value);
        output.Write(count);

        Query(connection, "SELECT ID, SNAPDATE, FILENAME, PROGRAM, PROGRAMCAPTION, PROGID, WEBPROGID FROM ACTIVITY ORDER BY ID", row =>
        {
            output.Write(TimeSnapperStream.MoreRows);
            output.Write(Int(row[0])!.Value);
            output.Write(((DateTime)row[1]).Ticks);
            output.WriteNullable(Text(row[2]));
            output.WriteNullable(Text(row[3]));
            output.WriteNullable(Text(row[4]));
            output.WriteNullable(Int(row[5]));
            output.WriteNullable(Int(row[6]));
        });
        output.Write(TimeSnapperStream.EndOfRows);
    }

    private static void Query(DbConnection connection, string sql, Action<object[]> onRow)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    // A new array per row: callers keep rows.
                    var row = new object[reader.FieldCount];
                    reader.GetValues(row);
                    onRow(row);
                }
            }
        }
    }

    /// <summary>Text with the padding of fixed-length columns taken off; null for NULL.</summary>
    private static string? Text(object value) => value is DBNull ? null : Convert.ToString(value).TrimEnd();

    private static int? Int(object value) => value is DBNull ? (int?)null : Convert.ToInt32(value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
}
