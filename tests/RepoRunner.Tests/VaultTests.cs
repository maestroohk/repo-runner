using System.Text;
using Microsoft.Data.Sqlite;
using RepoRunner.Core;

namespace RepoRunner.Tests;

public sealed class TempHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rr-tests", Guid.NewGuid().ToString("N"));

    public TempHome()
    {
        Directory.CreateDirectory(Path);
        Kdf.MemoryKb = 1024; // keep Argon2id fast in tests
        Kdf.Iterations = 1;
    }

    public string Dir(string name)
    {
        var dir = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

public class VaultTests
{
    [Fact]
    public void Values_round_trip_and_survive_reopen()
    {
        using var home = new TempHome();
        var repoDir = home.Dir("repo");
        using (var vault = Vault.Open(home.Dir("vault")))
        {
            var repo = vault.AddRepo(repoDir, "demo");
            vault.SetVar(Scope.Repo(repo.Id), vault.GetEnv("UAT"), "ApiPassword", "s3cret-value");
            vault.SetVar(Scope.Repo(repo.Id), vault.GetEnv("UAT"), "OpsApiBaseUrl", "https://uat.example.test");
        }
        using (var vault = Vault.Open(home.Dir("vault")))
        {
            var repo = vault.GetRepo("demo");
            var vars = vault.ListVars(Scope.Repo(repo.Id), vault.GetEnv("uat"), "repo");
            Assert.Equal(["ApiPassword", "OpsApiBaseUrl"], vars.Select(v => v.Name));
            Assert.Equal("s3cret-value", vars[0].Value);
            Assert.True(vars[0].Secret);
            Assert.False(vars[1].Secret);
            Assert.Empty(vault.ListVars(Scope.Repo(repo.Id), vault.GetEnv("LOCAL"), "repo"));
        }
    }

    [Fact]
    public void Nothing_readable_is_written_to_disk()
    {
        using var home = new TempHome();
        var vaultDir = home.Dir("vault");
        using (var vault = Vault.Open(vaultDir))
        {
            var repo = vault.AddRepo(home.Dir("repo"), "demo");
            vault.SetVar(Scope.Repo(repo.Id), vault.GetEnv("UAT"), "VERY_DISTINCT_NAME", "very-distinct-value-12345");
            vault.Audit("set", "demo", "UAT", "VERY_DISTINCT_NAME");
            Assert.Equal("VERY_DISTINCT_NAME", vault.AuditLog().Single().Detail);
        }
        foreach (var file in Directory.GetFiles(vaultDir))
        {
            var bytes = File.ReadAllBytes(file);
            foreach (var needle in new[] { "VERY_DISTINCT_NAME", "very-distinct-value-12345" })
            {
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(needle)), $"{needle} found in {file}");
                Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(needle)), $"{needle} (UTF-16) found in {file}");
            }
        }
    }

    [Fact]
    public void Names_are_case_insensitive_and_setting_again_overwrites()
    {
        using var home = new TempHome();
        using var vault = Vault.Open(home.Dir("vault"));
        var scope = Scope.Repo(vault.AddRepo(home.Dir("repo"), "demo").Id);
        var env = vault.GetEnv("UAT");
        vault.SetVar(scope, env, "EsbBaseUrl", "one");
        vault.SetVar(scope, env, "ESBBASEURL", "two");
        var vars = vault.ListVars(scope, env, "repo");
        Assert.Single(vars);
        Assert.Equal("two", vars[0].Value);
        Assert.True(vault.RemoveVar(scope, env, "esbbaseurl"));
        Assert.Empty(vault.ListVars(scope, env, "repo"));
    }

    [Fact]
    public void A_ciphertext_moved_to_another_slot_is_rejected()
    {
        using var home = new TempHome();
        var vaultDir = home.Dir("vault");
        using var vault = Vault.Open(vaultDir);
        var scope = Scope.Repo(vault.AddRepo(home.Dir("repo"), "demo").Id);
        vault.SetVar(scope, vault.GetEnv("UAT"), "Token", "uat-token");
        vault.SetVar(scope, vault.GetEnv("DEV"), "Token", "dev-token");

        using (var raw = new SqliteConnection($"Data Source={Path.Combine(vaultDir, "vault.db")};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                UPDATE variables SET value_enc = (SELECT value_enc FROM variables v2 WHERE v2.env_id = $dev)
                WHERE env_id = $uat
                """;
            cmd.Parameters.AddWithValue("$dev", vault.GetEnv("DEV").Id);
            cmd.Parameters.AddWithValue("$uat", vault.GetEnv("UAT").Id);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        Assert.Throws<VaultException>(() => vault.ListVars(scope, vault.GetEnv("UAT"), "repo"));
    }

    [Fact]
    public void Protected_environment_needs_the_right_passphrase()
    {
        using var home = new TempHome();
        var vaultDir = home.Dir("vault");
        var repoDir = home.Dir("repo");
        using (var vault = Vault.Open(vaultDir))
        {
            var scope = Scope.Repo(vault.AddRepo(repoDir, "demo").Id);
            Assert.Throws<VaultException>(() => vault.SetVar(scope, vault.GetEnv("PROD"), "Key", "v")); // no prompt wired
            vault.PassphrasePrompt = (_, isNew) => { Assert.True(isNew); return "correct horse"; };
            vault.SetVar(scope, vault.GetEnv("PROD"), "Data_Key", "prod-data-key");
        }
        using (var vault = Vault.Open(vaultDir))
        {
            var scope = Scope.Repo(vault.GetRepo("demo").Id);
            var prod = vault.GetEnv("PROD");
            Assert.True(prod.HasPassphrase);
            vault.PassphrasePrompt = (_, _) => "wrong guess!";
            Assert.Throws<VaultException>(() => vault.ListVars(scope, prod, "repo"));
            vault.PassphrasePrompt = (_, isNew) => { Assert.False(isNew); return "correct horse"; };
            Assert.Equal("prod-data-key", vault.ListVars(scope, prod, "repo").Single().Value);
        }
    }

    [Fact]
    public void Resolve_layers_groups_then_repo_then_overrides()
    {
        using var home = new TempHome();
        using var vault = Vault.Open(home.Dir("vault"));
        var repo = vault.AddRepo(home.Dir("repo"), "demo");
        var uat = vault.GetEnv("UAT");
        var first = vault.AddGroup("endpoints");
        var second = vault.AddGroup("extras");
        vault.Attach(repo, first);
        vault.Attach(repo, second);
        vault.SetVar(Scope.Group(first.Id), uat, "A", "group1");
        vault.SetVar(Scope.Group(first.Id), uat, "B", "group1");
        vault.SetVar(Scope.Group(first.Id), uat, "C", "group1");
        vault.SetVar(Scope.Group(second.Id), uat, "B", "group2");
        vault.SetVar(Scope.Repo(repo.Id), uat, "C", "repo");
        vault.SetVar(Scope.Group(first.Id), vault.GetEnv("DEV"), "D", "other-env");

        var target = new TargetDef { Name = "web", Kind = "dotnet", Urls = "http://localhost:5010" };
        var resolved = vault.Resolve(repo, uat, target, [new("A", "override")]);

        Assert.Equal("override", resolved.Values["A"]);
        Assert.Equal("group2", resolved.Values["B"]);
        Assert.Equal("repo", resolved.Values["c"]);
        Assert.False(resolved.Values.ContainsKey("D"));
        Assert.Equal("http://localhost:5010", resolved.Values["ASPNETCORE_URLS"]);
        Assert.Equal("Development", resolved.Values["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("group:extras", resolved.Origins["B"]);
    }

    [Fact]
    public void Bundle_restores_into_an_empty_vault_and_rejects_a_wrong_passphrase()
    {
        using var home = new TempHome();
        byte[] bundle;
        using (var vault = Vault.Open(home.Dir("vault-a")))
        {
            vault.PassphrasePrompt = (_, _) => "prod passphrase";
            var repo = vault.AddRepo(home.Dir("repo"), "demo");
            var group = vault.AddGroup("endpoints");
            vault.Attach(repo, group);
            vault.SaveTarget(repo, new TargetDef { Name = "web", Project = "Demo/Demo.csproj", Requires = ["A"] });
            vault.SetRepoDefaults(repo, "web", "UAT");
            vault.SetVar(Scope.Repo(repo.Id), vault.GetEnv("UAT"), "A", "1");
            vault.SetVar(Scope.Group(group.Id), vault.GetEnv("UAT"), "B", "2");
            vault.SetVar(Scope.Repo(repo.Id), vault.GetEnv("PROD"), "A", "prod-1");
            (bundle, var summary) = vault.Export("bundle passphrase", skipProtected: false);
            Assert.Equal(3, summary.Variables);
        }
        using (var vault = Vault.Open(home.Dir("vault-b")))
        {
            Assert.Throws<VaultException>(() => vault.Import(bundle, "not the passphrase"));
            vault.PassphrasePrompt = (_, _) => "new prod passphrase";
            vault.Import(bundle, "bundle passphrase");
            var repo = vault.GetRepo("demo");
            Assert.Equal("web", repo.DefaultTarget);
            Assert.Equal("UAT", repo.DefaultEnv);
            Assert.Equal("Demo/Demo.csproj", vault.GetTarget(repo, null).Project);
            var uat = vault.Resolve(repo, vault.GetEnv("UAT")).Values;
            Assert.Equal("1", uat["A"]);
            Assert.Equal("2", uat["B"]);
            Assert.Equal("prod-1", vault.Resolve(repo, vault.GetEnv("PROD")).Values["A"]);
        }
    }

    [Fact]
    public void Recordings_go_with_their_repo_and_never_pass_to_a_new_one()
    {
        using var home = new TempHome();
        using var vault = Vault.Open(home.Dir("vault"));
        var first = vault.AddRepo(home.Dir("repo-a"), "a");
        Directory.CreateDirectory(vault.RecordingsFolder(first));
        File.WriteAllText(Path.Combine(vault.RecordingsFolder(first), "x.rrp"), "recorded");

        vault.RemoveRepo(first);
        Assert.False(Directory.Exists(vault.RecordingsFolder(first)));

        // Left behind by a version without the line above: the next repo to get this id must not see it.
        Directory.CreateDirectory(vault.RecordingsFolder(first));
        File.WriteAllText(Path.Combine(vault.RecordingsFolder(first), "stale.rrp"), "orphan");
        var second = vault.AddRepo(home.Dir("repo-b"), "b");
        Assert.Equal(first.Id, second.Id);
        Assert.False(Directory.Exists(vault.RecordingsFolder(second)));
    }

    [Fact]
    public void Repo_is_found_from_a_subfolder()
    {
        using var home = new TempHome();
        using var vault = Vault.Open(home.Dir("vault"));
        var repoDir = home.Dir("repo");
        vault.AddRepo(repoDir, "demo");
        vault.AddRepo(home.Dir("repo-other"), "other");
        Assert.Equal("demo", vault.FindRepoForPath(Path.Combine(repoDir, "src", "deep"))?.Name);
        Assert.Null(vault.FindRepoForPath(home.Path));
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
