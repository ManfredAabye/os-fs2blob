using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MySql.Data.MySqlClient;

namespace OpenSim.Tools.FsAssetsToBlobDump;

internal static class FsAssetsToBlobDumpTool
{
    private sealed class Options
    {
        public string DbHost { get; set; } = "127.0.0.1";
        public int DbPort { get; set; } = 3306;
        public string DbUser { get; set; } = string.Empty;
        public string DbPassword { get; set; } = string.Empty;
        public string DbName { get; set; } = string.Empty;
        public string FsTable { get; set; } = "fsassets";
        public string AssetsTable { get; set; } = "assets";
        public string BaseDirectory { get; set; } = string.Empty;
        public string SpoolDirectory { get; set; } = string.Empty;
        public bool UseOsgridFormat { get; set; }
        public string OutputDir { get; set; } = "./blob-dumps";
        public string WhereClause { get; set; } = string.Empty;
    }

    private sealed class FsRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public int AssetType { get; init; }
        public string Hash { get; init; } = string.Empty;
        public int CreateTime { get; init; }
        public int AccessTime { get; init; }
        public int AssetFlags { get; init; }
    }

    private static int Main(string[] args)
    {
        try
        {
            Options options = ParseArgs(args);
            ValidateOptions(options);
            RunExport(options);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 1;
        }
    }

    private static void RunExport(Options options)
    {
        Directory.CreateDirectory(options.OutputDir);

        string connStr = new MySqlConnectionStringBuilder
        {
            Server = options.DbHost,
            Port = (uint)options.DbPort,
            UserID = options.DbUser,
            Password = options.DbPassword,
            Database = options.DbName,
            SslMode = MySqlSslMode.Disabled,
            AllowZeroDateTime = true,
            ConvertZeroDateTime = true
        }.ConnectionString;

        var writers = new Dictionary<int, StreamWriter>();
        var countByType = new Dictionary<int, int>();

        int totalProcessed = 0;
        int missingFiles = 0;

        using var conn = new MySqlConnection(connStr);
        conn.Open();

        string sql = BuildSelectSql(options);
        using var cmd = new MySqlCommand(sql, conn);
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            FsRow row = new()
            {
                Id = reader["id"].ToString() ?? string.Empty,
                Name = reader["name"].ToString() ?? string.Empty,
                Description = reader["description"].ToString() ?? string.Empty,
                AssetType = Convert.ToInt32(reader["type"], CultureInfo.InvariantCulture),
                Hash = reader["hash"].ToString() ?? string.Empty,
                CreateTime = Convert.ToInt32(reader["create_time"], CultureInfo.InvariantCulture),
                AccessTime = Convert.ToInt32(reader["access_time"], CultureInfo.InvariantCulture),
                AssetFlags = Convert.ToInt32(reader["asset_flags"], CultureInfo.InvariantCulture)
            };

            byte[] data = ReadAssetBytes(options, row.Hash);
            if (data is null)
            {
                missingFiles++;
                continue;
            }

            StreamWriter writer = GetWriter(options, writers, row.AssetType);
            writer.WriteLine(BuildUpsertSql(options.AssetsTable, row, data));

            totalProcessed++;
            countByType[row.AssetType] = countByType.TryGetValue(row.AssetType, out int count) ? count + 1 : 1;

            if (totalProcessed % 200 == 0)
                Console.WriteLine($"Fortschritt: {totalProcessed} Assets verarbeitet");
        }

        foreach (StreamWriter writer in writers.Values)
        {
            writer.WriteLine("COMMIT;");
            writer.Dispose();
        }

        Console.WriteLine("Fertig.");
        Console.WriteLine($"Verarbeitet: {totalProcessed}");
        Console.WriteLine($"Fehlend (Datei fuer Hash nicht gefunden): {missingFiles}");

        foreach (var kvp in countByType.OrderBy(k => k.Key))
            Console.WriteLine($"assetType {kvp.Key}: {kvp.Value}");

        if (totalProcessed == 0)
            Console.WriteLine("Warnung: Keine Assets exportiert. Bitte Parameter/Verzeichnisse pruefen.");
    }

    private static StreamWriter GetWriter(Options options, Dictionary<int, StreamWriter> writers, int assetType)
    {
        if (writers.TryGetValue(assetType, out StreamWriter existing))
            return existing;

        string filePath = Path.Combine(options.OutputDir, $"assets_type_{assetType}.sql");
        var writer = new StreamWriter(filePath, false, new UTF8Encoding(false));
        writer.WriteLine("-- Auto-generated by OpenSim.Tools.FsAssetsToBlobDump");
        writer.WriteLine($"-- assetType={assetType}");
        writer.WriteLine("START TRANSACTION;");

        writers[assetType] = writer;
        return writer;
    }

    private static string BuildSelectSql(Options options)
    {
        string where = string.IsNullOrWhiteSpace(options.WhereClause)
            ? string.Empty
            : " WHERE " + options.WhereClause;

        return $"SELECT id, name, description, type, hash, create_time, access_time, asset_flags FROM {options.FsTable}{where} ORDER BY id";
    }

    private static string BuildUpsertSql(string assetsTable, FsRow row, byte[] data)
    {
        string id = EscapeSql(row.Id);
        string name = EscapeSql(row.Name);
        string desc = EscapeSql(row.Description);
        string hexData = Convert.ToHexString(data);

        return
            $"INSERT INTO `{assetsTable}` (" +
            "`id`,`name`,`description`,`assetType`,`local`,`temporary`,`data`,`create_time`,`access_time`,`asset_flags`,`CreatorID`) VALUES (" +
            $"'{id}','{name}','{desc}',{row.AssetType},0,0,X'{hexData}',{row.CreateTime},{row.AccessTime},{row.AssetFlags},'') " +
            "ON DUPLICATE KEY UPDATE " +
            "`name`=VALUES(`name`),`description`=VALUES(`description`),`assetType`=VALUES(`assetType`)," +
            "`local`=VALUES(`local`),`temporary`=VALUES(`temporary`),`data`=VALUES(`data`)," +
            "`create_time`=VALUES(`create_time`),`access_time`=VALUES(`access_time`)," +
            "`asset_flags`=VALUES(`asset_flags`),`CreatorID`=VALUES(`CreatorID`);";
    }

    private static byte[] ReadAssetBytes(Options options, string hash)
    {
        if (!string.IsNullOrWhiteSpace(options.SpoolDirectory))
        {
            string spoolFile = Path.Combine(options.SpoolDirectory, hash + ".asset");
            if (File.Exists(spoolFile))
                return File.ReadAllBytes(spoolFile);
        }

        string relPath = HashToRelativePath(hash, options.UseOsgridFormat);
        if (relPath is null)
            return null;

        string rawPath = Path.Combine(options.BaseDirectory, relPath);
        string gzPath = rawPath + ".gz";

        if (File.Exists(gzPath))
        {
            using var fs = File.OpenRead(gzPath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            return ms.ToArray();
        }

        if (File.Exists(rawPath))
            return File.ReadAllBytes(rawPath);

        return null;
    }

    private static string HashToRelativePath(string hash, bool useOsgridFormat)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length < 10)
            return null;

        if (useOsgridFormat)
            return Path.Combine(hash[..3], hash.Substring(3, 3), hash);

        return Path.Combine(hash[..2], hash.Substring(2, 2), hash.Substring(4, 2), hash.Substring(6, 4), hash);
    }

    private static string EscapeSql(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\0", string.Empty, StringComparison.Ordinal);
    }

    private static Options ParseArgs(string[] args)
    {
        var options = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unbekanntes Argument: {arg}");

            string key = arg;
            string value = null;

            int eq = arg.IndexOf('=');
            if (eq > 0)
            {
                key = arg[..eq];
                value = arg[(eq + 1)..];
            }
            else if (key != "--use-osgrid-format")
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Wert fehlt fuer {key}");

                value = args[++i];
            }

            switch (key)
            {
                case "--db-host":
                    options.DbHost = value ?? options.DbHost;
                    break;
                case "--db-port":
                    options.DbPort = int.Parse(value ?? "3306", CultureInfo.InvariantCulture);
                    break;
                case "--db-user":
                    options.DbUser = value ?? string.Empty;
                    break;
                case "--db-password":
                    options.DbPassword = value ?? string.Empty;
                    break;
                case "--db-name":
                    options.DbName = value ?? string.Empty;
                    break;
                case "--fs-table":
                    options.FsTable = value ?? options.FsTable;
                    break;
                case "--assets-table":
                    options.AssetsTable = value ?? options.AssetsTable;
                    break;
                case "--base-directory":
                    options.BaseDirectory = value ?? string.Empty;
                    break;
                case "--spool-directory":
                    options.SpoolDirectory = value ?? string.Empty;
                    break;
                case "--use-osgrid-format":
                    options.UseOsgridFormat = true;
                    break;
                case "--output-dir":
                    options.OutputDir = value ?? options.OutputDir;
                    break;
                case "--where":
                    options.WhereClause = value ?? string.Empty;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unbekanntes Argument: {key}");
            }
        }

        return options;
    }

    private static void ValidateOptions(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.DbUser))
            throw new ArgumentException("--db-user ist erforderlich");

        if (string.IsNullOrWhiteSpace(options.DbName))
            throw new ArgumentException("--db-name ist erforderlich");

        if (string.IsNullOrWhiteSpace(options.BaseDirectory))
            throw new ArgumentException("--base-directory ist erforderlich");

        if (!Directory.Exists(options.BaseDirectory))
            throw new ArgumentException($"BaseDirectory existiert nicht: {options.BaseDirectory}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("OpenSim.Tools.FsAssetsToBlobDump");
        Console.WriteLine("Konvertiert FSAssets (Verzeichnis + fsassets) zu SQL-Dumps fuer assets.data (Blob)");
        Console.WriteLine("Die Ausgabe wird in separate SQL-Dateien pro AssetType aufgeteilt (assets_type_N.sql)");
        Console.WriteLine();
        Console.WriteLine("Optionen:");
        Console.WriteLine("  --db-host HOST              Database Host (default: 127.0.0.1)");
        Console.WriteLine("  --db-port PORT              Database Port (default: 3306)");
        Console.WriteLine("  --db-user USER              Database Benutzer (erforderlich)");
        Console.WriteLine("  --db-password PASSWORD      Database Passwort");
        Console.WriteLine("  --db-name DATABASE          Database Name (erforderlich)");
        Console.WriteLine("  --fs-table TABLE            FSAssets Tabelle (default: fsassets)");
        Console.WriteLine("  --assets-table TABLE        Assets Tabelle (default: assets)");
        Console.WriteLine("  --base-directory DIR        Basis-Verzeichnis fuer Assets (erforderlich)");
        Console.WriteLine("  --spool-directory DIR       Spool-Verzeichnis fuer Assets");
        Console.WriteLine("  --use-osgrid-format         OSGrid-Pfadformat verwenden");
        Console.WriteLine("  --output-dir DIR            Ausgabe-Verzeichnis fuer SQL-Dateien (default: ./blob-dumps)");
        Console.WriteLine("  --where CLAUSE              SQL WHERE Bedingung zum Filtern");
        Console.WriteLine("  --help, -h                  Diese Hilfe anzeigen");
        Console.WriteLine();
        Console.WriteLine("Beispiel:");
        Console.WriteLine("  dotnet run --project addon-modules/os-fs2blob/OpenSim.Tools.FsAssetsToBlobDump.csproj -- \\");
        Console.WriteLine("    --db-host 127.0.0.1 --db-port 3306 --db-user USER --db-password PASS --db-name DBNAME \\");
        Console.WriteLine("    --base-directory /opt/opensim/fsassets/data --spool-directory /opt/opensim/fsassets/tmp \\");
        Console.WriteLine("    --output-dir ./blob-dumps");
    }
}
