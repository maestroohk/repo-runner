namespace RepoRunner.Core;

public sealed record EnvInfo(long Id, string Name, bool Protected, bool HasPassphrase);

public sealed record RepoInfo(long Id, string Name, string Path, string? DefaultTarget, string? DefaultEnv);

public sealed record GroupInfo(long Id, string Name);

/// <summary>Owner of a variable: a repo or a shared group.</summary>
public sealed record Scope(string Kind, long Id)
{
    public static Scope Repo(long id) => new("repo", id);
    public static Scope Group(long id) => new("group", id);
}

public sealed record Variable(string Name, string Value, bool Secret, string Origin);

public sealed record AuditEntry(string Time, string Action, string? Repo, string? Env, string? Detail);

/// <summary>How to start one process of a repo. Stored in clear; never holds values.</summary>
public sealed class TargetDef
{
    public string Name { get; set; } = "";
    /// <summary>dotnet | node | custom</summary>
    public string Kind { get; set; } = "dotnet";
    /// <summary>dotnet: project file, relative to the repo root.</summary>
    public string? Project { get; set; }
    /// <summary>node/custom: working directory, relative to the repo root.</summary>
    public string? Cwd { get; set; }
    /// <summary>node: npm script name.</summary>
    public string? Script { get; set; }
    /// <summary>custom: executable or command.</summary>
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    /// <summary>dotnet: value for ASPNETCORE_URLS, taken from the launch profile.</summary>
    public string? Urls { get; set; }
    /// <summary>Names of other targets started alongside this one.</summary>
    public List<string> Companions { get; set; } = [];
    /// <summary>Variable names this target needs.</summary>
    public List<string> Requires { get; set; } = [];
}

public sealed class ResolvedEnvironment
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Origins { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SecretNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Put(Variable v)
    {
        Values[v.Name] = v.Value;
        Origins[v.Name] = v.Origin;
        if (v.Secret) SecretNames.Add(v.Name); else SecretNames.Remove(v.Name);
    }
}

public sealed class VaultException(string message) : Exception(message);
