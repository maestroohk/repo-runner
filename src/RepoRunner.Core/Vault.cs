using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace RepoRunner.Core;

/// <summary>
/// The encrypted store. Variable names and values are sealed with AES-256-GCM under a vault key that DPAPI
/// wraps for the current Windows user. Protected environments need a passphrase-derived key as well.
/// </summary>
public sealed partial class Vault : IDisposable
{
    public static readonly string[] DefaultEnvironments = ["LOCAL", "DEV", "UAT", "STAGING", "PROD"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly string[] SecretHints =
        ["PASSWORD", "PWORD", "PWD", "SECRET", "TOKEN", "KEY", "CREDENTIAL", "CONNECTIONSTRING", "SIGNING"];

    private readonly Db _db;
    private readonly byte[] _encKey;
    private readonly byte[] _macKey;
    private readonly Dictionary<long, byte[]> _envKeys = [];

    public string Home { get; }

    /// <summary>Asks for a protected environment's passphrase: (environment name, isNewPassphrase) → passphrase, or null to cancel.</summary>
    public Func<string, bool, string?>? PassphrasePrompt { get; set; }

    public static string DefaultHome =>
        Environment.GetEnvironmentVariable("RR_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepoRunner");

    private Vault(string home, Db db, byte[] vaultKey)
    {
        Home = home;
        _db = db;
        _encKey = Kdf.Derive(vaultKey, "rr/enc");
        _macKey = Kdf.Derive(vaultKey, "rr/mac");
    }

    public static Vault Open(string? home = null)
    {
        home ??= DefaultHome;
        Directory.CreateDirectory(home);
        var db = new Db(Path.Combine(home, "vault.db"));
        try
        {
            db.Exec(Db.Schema);
            // Vaults made before environments had a live switch: add it, off.
            if (db.Query("SELECT COUNT(*) FROM pragma_table_info('environments') WHERE name='live'", r => r.GetInt64(0)).Single() == 0)
                db.Exec("ALTER TABLE environments ADD COLUMN live INTEGER NOT NULL DEFAULT 0");
            var wrapped = db.Query("SELECT value FROM meta WHERE key='vault_key'", r => (byte[])r[0]).FirstOrDefault();
            byte[] vaultKey;
            if (wrapped is null)
            {
                vaultKey = RandomNumberGenerator.GetBytes(32);
                db.Exec("INSERT INTO meta(key, value) VALUES('vault_key', $v)", ("$v", KeyProtector.Wrap(vaultKey)));
                foreach (var name in DefaultEnvironments)
                    db.Exec("INSERT INTO environments(name, protected) VALUES($n, $p)", ("$n", name), ("$p", name == "PROD" ? 1 : 0));
            }
            else
            {
                try { vaultKey = KeyProtector.Unwrap(wrapped); }
                catch (CryptographicException)
                {
                    throw new VaultException(
                        "This vault was created by another Windows user or profile and cannot be opened here. " +
                        "Restore from an export bundle with 'rr vault import'.");
                }
            }
            return new Vault(home, db, vaultKey);
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public void Dispose() => _db.Dispose();

    // ---------- environments ----------

    private static EnvInfo MapEnv(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0, !r.IsDBNull(3), r.GetInt64(4) != 0);

    /// <summary>Lets apps run on this environment reach real servers, or confines them to the sandbox again.</summary>
    public void SetLive(EnvInfo env, bool live)
    {
        _db.Exec("UPDATE environments SET live=$l WHERE id=$id", ("$l", live ? 1 : 0), ("$id", env.Id));
        Audit(live ? "env-live-on" : "env-live-off", null, env.Name);
    }

    public List<EnvInfo> Environments() =>
        _db.Query("SELECT id, name, protected, salt, live FROM environments ORDER BY id", MapEnv);

    public EnvInfo GetEnv(string name) =>
        _db.Query("SELECT id, name, protected, salt, live FROM environments WHERE name=$n", MapEnv, ("$n", name)).FirstOrDefault()
        ?? throw new VaultException($"Unknown environment '{name}'. Known: {string.Join(", ", Environments().Select(e => e.Name))}.");

    public EnvInfo AddEnv(string name, bool isProtected)
    {
        if (!NameRegex().IsMatch(name)) throw new VaultException($"'{name}' is not a valid environment name.");
        if (Environments().Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new VaultException($"Environment '{name}' already exists.");
        _db.Exec("INSERT INTO environments(name, protected) VALUES($n, $p)", ("$n", name.ToUpperInvariant()), ("$p", isProtected ? 1 : 0));
        return GetEnv(name);
    }

    public int RemoveEnv(EnvInfo env)
    {
        var removed = _db.Exec("DELETE FROM variables WHERE env_id=$e", ("$e", env.Id));
        _db.Exec("DELETE FROM environments WHERE id=$e", ("$e", env.Id));
        _db.Exec("UPDATE repos SET default_env=NULL WHERE default_env=$n", ("$n", env.Name));
        return removed;
    }

    private byte[] KeyFor(EnvInfo env)
    {
        if (!env.Protected) return _encKey;
        if (_envKeys.TryGetValue(env.Id, out var cached)) return cached;

        var prompt = PassphrasePrompt
            ?? throw new VaultException($"Environment '{env.Name}' is protected and needs its passphrase.");
        var (salt, verifier) = _db.Query("SELECT salt, verifier FROM environments WHERE id=$id",
            r => (r.IsDBNull(0) ? null : (byte[])r[0], r.IsDBNull(1) ? null : (byte[])r[1]), ("$id", env.Id)).Single();

        byte[] key;
        if (salt is null)
        {
            var pass = prompt(env.Name, true) ?? throw new VaultException("Cancelled.");
            if (pass.Length < 8) throw new VaultException("A passphrase needs at least 8 characters.");
            salt = RandomNumberGenerator.GetBytes(16);
            key = DeriveEnvKey(pass, salt, env);
            _db.Exec("UPDATE environments SET salt=$s, verifier=$v WHERE id=$id",
                ("$s", salt), ("$v", Aead.SealText(key, "rr-verify", $"verify:{env.Id}")), ("$id", env.Id));
        }
        else
        {
            var pass = prompt(env.Name, false) ?? throw new VaultException("Cancelled.");
            key = DeriveEnvKey(pass, salt, env);
            try { Aead.Open(key, verifier!, $"verify:{env.Id}"); }
            catch (CryptographicException) { throw new VaultException($"Wrong passphrase for '{env.Name}'."); }
        }
        _envKeys[env.Id] = key;
        return key;
    }

    /// <summary>True when values of this environment can be read without asking for a passphrase.</summary>
    public bool IsUnlocked(EnvInfo env) => !env.Protected || _envKeys.ContainsKey(env.Id);

    /// <summary>Unlocks a protected environment for this vault instance, or sets its passphrase when it has none yet.</summary>
    public void Unlock(EnvInfo env, string passphrase)
    {
        var previous = PassphrasePrompt;
        PassphrasePrompt = (_, _) => passphrase;
        try { KeyFor(env); }
        finally { PassphrasePrompt = previous; }
    }

    /// <summary>Forgets the cached key of a protected environment.</summary>
    public void Lock(EnvInfo env) => _envKeys.Remove(env.Id);

    private byte[] DeriveEnvKey(string passphrase, byte[] salt, EnvInfo env) =>
        Kdf.Derive([.. _encKey, .. Kdf.FromPassphrase(passphrase, salt)], $"rr/env/{env.Id}");

    // ---------- repos ----------

    private static RepoInfo MapRepo(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));

    private const string RepoColumns = "id, name, path, default_target, default_env";

    public static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public List<RepoInfo> Repos() => _db.Query($"SELECT {RepoColumns} FROM repos ORDER BY name", MapRepo);

    public RepoInfo GetRepo(string name) =>
        _db.Query($"SELECT {RepoColumns} FROM repos WHERE name=$n", MapRepo, ("$n", name)).FirstOrDefault()
        ?? throw new VaultException($"No repo named '{name}'. Run 'rr list' to see registered repos.");

    public RepoInfo? FindRepoForPath(string directory)
    {
        var dir = NormalizePath(directory);
        return Repos()
            .Where(r => dir.Equals(r.Path, StringComparison.OrdinalIgnoreCase)
                        || dir.StartsWith(r.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();
    }

    public RepoInfo AddRepo(string path, string? name = null, bool mustExist = true)
    {
        path = NormalizePath(path);
        if (mustExist && !Directory.Exists(path)) throw new VaultException($"Folder not found: {path}");
        name ??= Path.GetFileName(path);
        if (!NameRegex().IsMatch(name)) throw new VaultException($"'{name}' is not a valid repo name. Pass --name.");
        var clash = Repos().FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                              || r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (clash is not null) throw new VaultException($"Already registered as '{clash.Name}' ({clash.Path}).");
        _db.Exec("INSERT INTO repos(name, path) VALUES($n, $p)", ("$n", name), ("$p", path));
        var added = GetRepo(name);
        // Repo ids are reused. Recordings left under this id by an earlier repo are not this repo's.
        DeleteRecordings(added);
        return added;
    }

    /// <summary>The folder a repo's recorded processes are kept in.</summary>
    public string RecordingsFolder(RepoInfo repo) => Path.Combine(Home, "processes", repo.Id.ToString());

    private void DeleteRecordings(RepoInfo repo)
    {
        var folder = RecordingsFolder(repo);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    public void SetRepoDefaults(RepoInfo repo, string? target, string? env) =>
        _db.Exec("UPDATE repos SET default_target=COALESCE($t, default_target), default_env=COALESCE($e, default_env) WHERE id=$id",
            ("$t", target), ("$e", env), ("$id", repo.Id));

    public void RemoveRepo(RepoInfo repo)
    {
        _db.Exec("DELETE FROM variables WHERE scope_kind='repo' AND scope_id=$id", ("$id", repo.Id));
        _db.Exec("DELETE FROM targets WHERE repo_id=$id", ("$id", repo.Id));
        _db.Exec("DELETE FROM repo_groups WHERE repo_id=$id", ("$id", repo.Id));
        _db.Exec("DELETE FROM repos WHERE id=$id", ("$id", repo.Id));
        DeleteRecordings(repo);
    }

    // ---------- targets ----------

    public List<TargetDef> Targets(RepoInfo repo) =>
        _db.Query("SELECT json FROM targets WHERE repo_id=$r ORDER BY id",
            r => JsonSerializer.Deserialize<TargetDef>(r.GetString(0), Json)!, ("$r", repo.Id));

    public void SaveTarget(RepoInfo repo, TargetDef target)
    {
        if (!NameRegex().IsMatch(target.Name)) throw new VaultException($"'{target.Name}' is not a valid target name.");
        _db.Exec("""
            INSERT INTO targets(repo_id, name, json) VALUES($r, $n, $j)
            ON CONFLICT(repo_id, name) DO UPDATE SET json=excluded.json
            """, ("$r", repo.Id), ("$n", target.Name), ("$j", JsonSerializer.Serialize(target, Json)));
    }

    public bool RemoveTarget(RepoInfo repo, string name) =>
        _db.Exec("DELETE FROM targets WHERE repo_id=$r AND name=$n", ("$r", repo.Id), ("$n", name)) > 0;

    public TargetDef GetTarget(RepoInfo repo, string? name)
    {
        var all = Targets(repo);
        if (all.Count == 0) throw new VaultException($"Repo '{repo.Name}' has no run targets. Run 'rr add' again or 'rr target add'.");
        name ??= repo.DefaultTarget ?? (all.Count == 1 ? all[0].Name : null)
            ?? throw new VaultException($"Name a target: {string.Join(", ", all.Select(t => t.Name))}.");
        return all.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new VaultException($"No target '{name}' in '{repo.Name}'. Targets: {string.Join(", ", all.Select(t => t.Name))}.");
    }

    // ---------- shared groups ----------

    public List<GroupInfo> Groups() => _db.Query("SELECT id, name FROM groups ORDER BY name", r => new GroupInfo(r.GetInt64(0), r.GetString(1)));

    public GroupInfo GetGroup(string name) =>
        Groups().FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new VaultException($"No shared group '{name}'. Create it with 'rr group add {name}'.");

    public GroupInfo AddGroup(string name)
    {
        if (!NameRegex().IsMatch(name)) throw new VaultException($"'{name}' is not a valid group name.");
        if (Groups().Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new VaultException($"Group '{name}' already exists.");
        _db.Exec("INSERT INTO groups(name) VALUES($n)", ("$n", name));
        return GetGroup(name);
    }

    public void Attach(RepoInfo repo, GroupInfo group) =>
        _db.Exec("""
            INSERT OR IGNORE INTO repo_groups(repo_id, group_id, position)
            VALUES($r, $g, (SELECT COALESCE(MAX(position), 0) + 1 FROM repo_groups WHERE repo_id=$r))
            """, ("$r", repo.Id), ("$g", group.Id));

    public void Detach(RepoInfo repo, GroupInfo group) =>
        _db.Exec("DELETE FROM repo_groups WHERE repo_id=$r AND group_id=$g", ("$r", repo.Id), ("$g", group.Id));

    public List<GroupInfo> GroupsOf(RepoInfo repo) =>
        _db.Query("""
            SELECT g.id, g.name FROM groups g JOIN repo_groups rg ON rg.group_id=g.id
            WHERE rg.repo_id=$r ORDER BY rg.position
            """, r => new GroupInfo(r.GetInt64(0), r.GetString(1)), ("$r", repo.Id));

    // ---------- variables ----------

    public static bool LooksSecret(string name)
    {
        var upper = name.ToUpperInvariant();
        return SecretHints.Any(upper.Contains);
    }

    private byte[] Tag(string name) => HMACSHA256.HashData(_macKey, Encoding.UTF8.GetBytes(name.ToUpperInvariant()));

    private static string Aad(Scope scope, EnvInfo env, byte[] tag, string field) =>
        $"{scope.Kind}:{scope.Id}:{env.Id}:{Convert.ToHexString(tag)}:{field}";

    public void SetVar(Scope scope, EnvInfo env, string name, string value, bool? secret = null)
    {
        if (!VarRegex().IsMatch(name)) throw new VaultException($"'{name}' is not a valid variable name.");
        var key = KeyFor(env);
        var tag = Tag(name);
        _db.Exec("""
            INSERT INTO variables(scope_kind, scope_id, env_id, name_tag, name_enc, value_enc, secret, updated_at)
            VALUES($k, $s, $e, $t, $n, $v, $x, $u)
            ON CONFLICT(scope_kind, scope_id, env_id, name_tag)
            DO UPDATE SET name_enc=excluded.name_enc, value_enc=excluded.value_enc, secret=excluded.secret, updated_at=excluded.updated_at
            """,
            ("$k", scope.Kind), ("$s", scope.Id), ("$e", env.Id), ("$t", tag),
            ("$n", Aead.SealText(key, name, Aad(scope, env, tag, "name"))),
            ("$v", Aead.SealText(key, value, Aad(scope, env, tag, "value"))),
            ("$x", (secret ?? LooksSecret(name)) ? 1 : 0), ("$u", DateTime.UtcNow.ToString("o")));
    }

    public bool RemoveVar(Scope scope, EnvInfo env, string name) =>
        _db.Exec("DELETE FROM variables WHERE scope_kind=$k AND scope_id=$s AND env_id=$e AND name_tag=$t",
            ("$k", scope.Kind), ("$s", scope.Id), ("$e", env.Id), ("$t", Tag(name))) > 0;

    /// <summary>Changes only whether a variable is masked; the value is untouched.</summary>
    public bool SetSecret(Scope scope, EnvInfo env, string name, bool secret) =>
        _db.Exec("UPDATE variables SET secret=$x WHERE scope_kind=$k AND scope_id=$s AND env_id=$e AND name_tag=$t",
            ("$x", secret ? 1 : 0), ("$k", scope.Kind), ("$s", scope.Id), ("$e", env.Id), ("$t", Tag(name))) > 0;

    public int CountVars(Scope scope, EnvInfo env) =>
        _db.Query("SELECT COUNT(*) FROM variables WHERE scope_kind=$k AND scope_id=$s AND env_id=$e", r => r.GetInt32(0),
            ("$k", scope.Kind), ("$s", scope.Id), ("$e", env.Id)).Single();

    public List<Variable> ListVars(Scope scope, EnvInfo env, string origin)
    {
        var rows = _db.Query("""
            SELECT name_tag, name_enc, value_enc, secret FROM variables
            WHERE scope_kind=$k AND scope_id=$s AND env_id=$e
            """, r => ((byte[])r[0], (byte[])r[1], (byte[])r[2], r.GetInt64(3) != 0),
            ("$k", scope.Kind), ("$s", scope.Id), ("$e", env.Id));
        if (rows.Count == 0) return [];
        var key = KeyFor(env);
        try
        {
            return rows
                .Select(r => new Variable(
                    Aead.OpenText(key, r.Item2, Aad(scope, env, r.Item1, "name")),
                    Aead.OpenText(key, r.Item3, Aad(scope, env, r.Item1, "value")),
                    r.Item4, origin))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (CryptographicException)
        {
            throw new VaultException("A vault record failed its integrity check. The vault file was modified outside RepoRunner.");
        }
    }

    public Variable? GetVar(Scope scope, EnvInfo env, string name, string origin) =>
        ListVars(scope, env, origin).FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Merges shared groups, then the repo's own variables, then target defaults, then overrides.</summary>
    public ResolvedEnvironment Resolve(RepoInfo repo, EnvInfo env, TargetDef? target = null,
        IEnumerable<KeyValuePair<string, string>>? overrides = null)
    {
        var resolved = new ResolvedEnvironment();
        foreach (var group in GroupsOf(repo))
            foreach (var v in ListVars(Scope.Group(group.Id), env, $"group:{group.Name}"))
                resolved.Put(v);
        foreach (var v in ListVars(Scope.Repo(repo.Id), env, "repo"))
            resolved.Put(v);

        if (target is { Kind: "dotnet" })
        {
            // --no-launch-profile drops what launchSettings.json used to supply, so restore the two that matter.
            if (target.Urls is { Length: > 0 } && !resolved.Values.ContainsKey("ASPNETCORE_URLS"))
                resolved.Put(new Variable("ASPNETCORE_URLS", target.Urls, false, "target"));
            if (!resolved.Values.ContainsKey("ASPNETCORE_ENVIRONMENT") && !resolved.Values.ContainsKey("DOTNET_ENVIRONMENT"))
            {
                resolved.Put(new Variable("ASPNETCORE_ENVIRONMENT", "Development", false, "default"));
                resolved.Put(new Variable("DOTNET_ENVIRONMENT", "Development", false, "default"));
            }
        }
        foreach (var (name, value) in overrides ?? [])
            resolved.Put(new Variable(name, value, false, "override"));
        return resolved;
    }

    // ---------- sealing other data ----------

    /// <summary>Encrypts data that belongs beside the vault, such as a recorded process, under the vault key.</summary>
    public byte[] Protect(byte[] data, string purpose) => Aead.Seal(_encKey, data, "blob:" + purpose);

    public byte[] Unprotect(byte[] blob, string purpose) => Aead.Open(_encKey, blob, "blob:" + purpose);

    // ---------- audit ----------

    public void Audit(string action, string? repo = null, string? env = null, string? detail = null) =>
        _db.Exec("INSERT INTO audit(time, action, repo, env, detail) VALUES($t, $a, $r, $e, $d)",
            ("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")), ("$a", action), ("$r", repo), ("$e", env),
            // The detail often names a variable, and names are never stored in clear.
            ("$d", detail is null ? null : Aead.SealText(_encKey, detail, "audit")));

    public List<AuditEntry> AuditLog(int take = 50) =>
        _db.Query("SELECT time, action, repo, env, detail FROM audit ORDER BY id DESC LIMIT $n",
            r => new AuditEntry(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : Aead.OpenText(_encKey, (byte[])r[4], "audit")), ("$n", take));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.:\-]{0,254}$")]
    private static partial Regex VarRegex();
}
