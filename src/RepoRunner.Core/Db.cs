using Microsoft.Data.Sqlite;

namespace RepoRunner.Core;

internal sealed class Db : IDisposable
{
    private readonly SqliteConnection _conn;

    public Db(string path)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _conn.Open();
        Exec("PRAGMA foreign_keys=ON");
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] args)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public int Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteNonQuery();
    }

    public long Insert(string sql, params (string, object?)[] args)
    {
        Exec(sql, args);
        using var cmd = Command("SELECT last_insert_rowid()", []);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    public SqliteTransaction Begin() => _conn.BeginTransaction();

    public void Dispose() => _conn.Dispose();

    public const string Schema = """
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value BLOB NOT NULL);
        CREATE TABLE IF NOT EXISTS environments (
            id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            protected INTEGER NOT NULL DEFAULT 0, salt BLOB, verifier BLOB, live INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS repos (
            id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            path TEXT NOT NULL UNIQUE COLLATE NOCASE, default_target TEXT, default_env TEXT);
        CREATE TABLE IF NOT EXISTS groups (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE);
        CREATE TABLE IF NOT EXISTS repo_groups (
            repo_id INTEGER NOT NULL, group_id INTEGER NOT NULL, position INTEGER NOT NULL,
            PRIMARY KEY (repo_id, group_id));
        CREATE TABLE IF NOT EXISTS targets (
            id INTEGER PRIMARY KEY, repo_id INTEGER NOT NULL, name TEXT NOT NULL COLLATE NOCASE,
            json TEXT NOT NULL, UNIQUE (repo_id, name));
        CREATE TABLE IF NOT EXISTS variables (
            id INTEGER PRIMARY KEY, scope_kind TEXT NOT NULL, scope_id INTEGER NOT NULL, env_id INTEGER NOT NULL,
            name_tag BLOB NOT NULL, name_enc BLOB NOT NULL, value_enc BLOB NOT NULL,
            secret INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL,
            UNIQUE (scope_kind, scope_id, env_id, name_tag));
        CREATE TABLE IF NOT EXISTS audit (
            id INTEGER PRIMARY KEY, time TEXT NOT NULL, action TEXT NOT NULL, repo TEXT, env TEXT, detail TEXT);
        """;
}
