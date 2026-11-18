using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RepoRunner.Core;

/// <summary>Passphrase-encrypted export of the whole vault, for backup or a move to another machine or Windows profile.</summary>
public sealed partial class Vault
{
    private static readonly byte[] BundleMagic = "RRB1"u8.ToArray();

    private sealed class Bundle
    {
        public List<BundleEnv> Environments { get; set; } = [];
        public List<string> Groups { get; set; } = [];
        public List<BundleRepo> Repos { get; set; } = [];
        public List<BundleVar> Variables { get; set; } = [];
    }

    private sealed record BundleEnv(string Name, bool Protected);
    private sealed record BundleRepo(string Name, string Path, string? DefaultTarget, string? DefaultEnv, List<TargetDef> Targets, List<string> Groups);
    private sealed record BundleVar(string ScopeKind, string ScopeName, string Env, string Name, string Value, bool Secret);

    public sealed record BundleSummary(int Repos, int Groups, int Variables, List<string> SkippedEnvironments);

    public (byte[] Data, BundleSummary Summary) Export(string passphrase, bool skipProtected)
    {
        if (passphrase.Length < 8) throw new VaultException("A bundle passphrase needs at least 8 characters.");
        var bundle = new Bundle { Groups = Groups().Select(g => g.Name).ToList() };
        var skipped = new List<string>();
        var scopes = new List<(Scope Scope, string Kind, string Name)>();

        foreach (var repo in Repos())
        {
            bundle.Repos.Add(new BundleRepo(repo.Name, repo.Path, repo.DefaultTarget, repo.DefaultEnv, Targets(repo),
                GroupsOf(repo).Select(g => g.Name).ToList()));
            scopes.Add((Scope.Repo(repo.Id), "repo", repo.Name));
        }
        scopes.AddRange(Groups().Select(g => (Scope.Group(g.Id), "group", g.Name)));

        foreach (var env in Environments())
        {
            bundle.Environments.Add(new BundleEnv(env.Name, env.Protected));
            if (env.Protected && skipProtected)
            {
                if (scopes.Any(s => CountVars(s.Scope, env) > 0)) skipped.Add(env.Name);
                continue;
            }
            foreach (var (scope, kind, name) in scopes)
                bundle.Variables.AddRange(ListVars(scope, env, "").Select(v => new BundleVar(kind, name, env.Name, v.Name, v.Value, v.Secret)));
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var sealedJson = Aead.Seal(Kdf.FromPassphrase(passphrase, salt), JsonSerializer.SerializeToUtf8Bytes(bundle, Json), "rr-bundle");
        return ([.. BundleMagic, .. salt, .. sealedJson], new BundleSummary(bundle.Repos.Count, bundle.Groups.Count, bundle.Variables.Count, skipped));
    }

    /// <summary>Merges a bundle into this vault. Existing values with the same owner, environment and name are overwritten.</summary>
    public BundleSummary Import(byte[] data, string passphrase)
    {
        if (data.Length < 20 || !data.AsSpan(0, 4).SequenceEqual(BundleMagic))
            throw new VaultException("Not a RepoRunner bundle.");
        Bundle bundle;
        try
        {
            var json = Aead.Open(Kdf.FromPassphrase(passphrase, data[4..20]), data[20..], "rr-bundle");
            bundle = JsonSerializer.Deserialize<Bundle>(json, Json) ?? throw new VaultException("The bundle is empty.");
        }
        catch (CryptographicException)
        {
            throw new VaultException("Wrong bundle passphrase, or the file is damaged.");
        }

        var envs = Environments();
        foreach (var e in bundle.Environments.Where(e => !envs.Any(x => x.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase))))
            AddEnv(e.Name, e.Protected);
        foreach (var g in bundle.Groups.Where(g => !Groups().Any(x => x.Name.Equals(g, StringComparison.OrdinalIgnoreCase))))
            AddGroup(g);
        foreach (var r in bundle.Repos)
        {
            var repo = Repos().FirstOrDefault(x => x.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase))
                       ?? AddRepo(r.Path, r.Name, mustExist: false);
            SetRepoDefaults(repo, r.DefaultTarget, r.DefaultEnv);
            foreach (var t in r.Targets) SaveTarget(repo, t);
            foreach (var g in r.Groups) Attach(repo, GetGroup(g));
        }
        foreach (var v in bundle.Variables)
        {
            var scope = v.ScopeKind == "group" ? Scope.Group(GetGroup(v.ScopeName).Id) : Scope.Repo(GetRepo(v.ScopeName).Id);
            SetVar(scope, GetEnv(v.Env), v.Name, v.Value, v.Secret);
        }
        return new BundleSummary(bundle.Repos.Count, bundle.Groups.Count, bundle.Variables.Count, []);
    }
}
