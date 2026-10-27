using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Vault;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Data;

/// <summary>The SQLite file: connection factory, schema creation, migrations and first-run seeding.</summary>
internal sealed class Database
{
    public const int SchemaVersion = 1;
    public const string FileName = "cabinette.db";

    public Database(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        DataDirectory = dataDirectory;
        FilePath = Path.Combine(dataDirectory, FileName);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <summary>Cabinette's data folder (<see cref="LibraryOptions.DataDirectory"/>), which holds the database file.</summary>
    public string DataDirectory { get; }

    public string FilePath { get; }

    public string ConnectionString { get; }

    /// <summary>Opens a pooled connection with foreign keys on and a busy timeout.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        try
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA synchronous = NORMAL;";
            cmd.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Releases pooled connections (and with them the file handles).</summary>
    public void ClearPool()
    {
        using var connection = new SqliteConnection(ConnectionString);
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>Creates or migrates the schema and seeds the built-in categories on first run. Returns true when the database was created.</summary>
    public bool Initialize(DateTimeOffset now)
    {
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode = WAL;");

        var exists = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_info'") > 0;

        using var tx = connection.BeginTransaction();
        var version = exists ? connection.ExecuteScalar<long?>("SELECT MAX(version) FROM schema_info", transaction: tx) ?? 0 : 0;
        if (version > SchemaVersion)
        {
            throw new LibraryException(LibraryErrorCode.NotAllowed,
                "The library was created by a newer version of Cabinette. Update Cabinette to open it.");
        }

        // Before the schema script, which then recreates the activity log's indexes.
        RebuildActivityLogWithAutoincrement(connection, tx);

        // The schema script is idempotent (IF NOT EXISTS), so additive objects also reach existing databases.
        connection.Execute(LoadSchemaScript(), transaction: tx);

        var created = version == 0;
        if (created)
        {
            connection.Execute("DELETE FROM schema_info", transaction: tx);
            connection.Execute("INSERT INTO schema_info(version) VALUES (@v)", new { v = SchemaVersion }, tx);
            if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM categories", transaction: tx) == 0)
            {
                Seed(connection, tx, DbValues.ToText(now));
            }
        }
        else
        {
            Migrate(connection, tx, version);
        }

        EnsureDeletedCategory(connection, tx, DbValues.ToText(now));

        // A star whose item is gone (rows deleted with foreign keys off, or a hand-edited backup) must not attach itself to a
        // new item that gets the same id later.
        connection.Execute("DELETE FROM item_stars WHERE item_id NOT IN (SELECT id FROM items)", transaction: tx);

        // The same for a date taken: it belongs to the file that was read, not to whatever gets that id next.
        connection.Execute("DELETE FROM item_dates WHERE item_id NOT IN (SELECT id FROM items)", transaction: tx);
        tx.Commit();
        return created;
    }

    /// <summary>
    /// Gives an activity_log made before its id was AUTOINCREMENT (the first stage 4 builds) that definition, keeping every row
    /// and its id: without it SQLite hands out the largest id + 1, so once the prune has emptied the table ids start again at 1.
    /// Returns false, changing nothing, when there is no such table or it already has it. Run inside the initialize transaction,
    /// before the schema script, which recreates the indexes (dropped with the old table).
    /// </summary>
    /// <remarks>
    /// No version bump: the table keeps its name and columns, so earlier builds still open the library and its backups (the
    /// sqlite_sequence table AUTOINCREMENT adds is standard SQLite). Copying the rows with their ids sets the sequence to the
    /// largest. Nothing refers to activity_log (no foreign keys, views or triggers), so it is rebuilt the way SQLite documents for
    /// a change ALTER TABLE can't make: a new table, the rows copied, the old one dropped, the new one renamed.
    /// </remarks>
    internal static bool RebuildActivityLogWithAutoincrement(SqliteConnection connection, SqliteTransaction tx)
    {
        var sql = connection.ExecuteScalar<string?>(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'activity_log'", transaction: tx);
        if (sql is null || sql.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        connection.Execute(
            """
            CREATE TABLE activity_log_rebuild (
              id           INTEGER PRIMARY KEY AUTOINCREMENT,
              at           TEXT NOT NULL,
              source       TEXT NOT NULL,
              action       TEXT NOT NULL,
              details_json TEXT
            );
            INSERT INTO activity_log_rebuild(id, at, source, action, details_json)
              SELECT id, at, source, action, details_json FROM activity_log ORDER BY id;
            DROP TABLE activity_log;
            ALTER TABLE activity_log_rebuild RENAME TO activity_log;
            """, transaction: tx);
        return true;
    }

    /// <summary>Hook for future schema versions: apply each step in order, then bump schema_info.</summary>
    private static void Migrate(SqliteConnection connection, SqliteTransaction tx, long fromVersion)
    {
        // Example for the future:
        // if (fromVersion < 2) { connection.Execute("ALTER TABLE ...", transaction: tx); }
        if (fromVersion < SchemaVersion)
        {
            connection.Execute("UPDATE schema_info SET version = @v", new { v = SchemaVersion }, tx);
        }
    }

    private static void Seed(SqliteConnection connection, SqliteTransaction tx, string now)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var def in ExtensionMap.BuiltInCategories)
        {
            long? parentId = def.ParentKey is null ? null : ids[def.ParentKey];
            var kind = def.Key == ExtensionMap.DeletedKey ? CategoryKind.System : CategoryKind.BuiltIn;
            ids[def.Key] = InsertCategory(connection, tx, parentId, def.Name, NameRules.SanitizeFolderName(def.Name), kind, def.Key, def.IconKey, now);
        }
    }

    private static void EnsureDeletedCategory(SqliteConnection connection, SqliteTransaction tx, string now)
    {
        var exists = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM categories WHERE builtin_key = @k", new { k = ExtensionMap.DeletedKey }, tx) > 0;
        if (exists)
        {
            return;
        }

        var def = ExtensionMap.GetDefinition(ExtensionMap.DeletedKey)!;
        var tree = CategoryTree.Load(connection, tx);
        var names = tree.ChildrenOf(null).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = tree.ChildrenOf(null).Select(c => c.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = NameRules.NumberedNames(def.Name).First(n => !names.Contains(n));
        var folder = NameRules.NumberedNames(NameRules.SanitizeFolderName(name)).First(n => !folders.Contains(n));
        InsertCategory(connection, tx, null, name, folder, CategoryKind.System, def.Key, def.IconKey, now);
    }

    private static long InsertCategory(SqliteConnection connection, SqliteTransaction tx, long? parentId, string name, string folderName,
        CategoryKind kind, string? builtInKey, string? iconKey, string now)
        => connection.ExecuteScalar<long>(
            """
            INSERT INTO categories(parent_id, name, folder_name, kind, builtin_key, icon_key, icon_png, is_pinned, created_at)
            VALUES (@parentId, @name, @folderName, @kind, @builtInKey, @iconKey, NULL, 0, @now);
            SELECT last_insert_rowid();
            """,
            new { parentId, name, folderName, kind = (int)kind, builtInKey, iconKey, now }, tx);

    internal static string LoadSchemaScript()
    {
        var assembly = typeof(Database).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("Schema.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
