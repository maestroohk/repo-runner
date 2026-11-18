using System.Text;
using RepoRunner.Core;

namespace RepoRunner.App;

/// <summary>
/// The one open vault of the app. Calls are serialised, and protected environments stay unlocked in memory
/// for a fixed window after the passphrase is entered.
/// </summary>
public sealed class VaultService : IDisposable
{
    public static readonly TimeSpan UnlockWindow = TimeSpan.FromMinutes(15);

    private readonly Vault _vault = Vault.Open();
    private readonly object _gate = new();
    private readonly Dictionary<long, DateTime> _unlockedUntil = [];

    public string VaultFile => Path.Combine(_vault.Home, "vault.db");

    public T Use<T>(Func<Vault, T> work)
    {
        lock (_gate)
        {
            ExpireLocks();
            return work(_vault);
        }
    }

    public void Use(Action<Vault> work) => Use(v => { work(v); return 0; });

    public bool IsUnlocked(EnvInfo env) => Use(v => v.IsUnlocked(env));

    public void Unlock(EnvInfo env, string passphrase)
    {
        lock (_gate)
        {
            _vault.Unlock(env, passphrase);
            _unlockedUntil[env.Id] = DateTime.UtcNow + UnlockWindow;
        }
    }

    public List<string> UnlockedNames() =>
        Use(v => v.Environments().Where(e => e.Protected && _unlockedUntil.ContainsKey(e.Id)).Select(e => e.Name).ToList());

    public void LockAll()
    {
        lock (_gate)
        {
            foreach (var env in _vault.Environments().Where(e => _unlockedUntil.ContainsKey(e.Id))) _vault.Lock(env);
            _unlockedUntil.Clear();
        }
    }

    private void ExpireLocks()
    {
        var expired = _unlockedUntil.Where(p => p.Value <= DateTime.UtcNow).Select(p => p.Key).ToList();
        if (expired.Count == 0) return;
        foreach (var env in _vault.Environments().Where(e => expired.Contains(e.Id))) _vault.Lock(env);
        foreach (var id in expired) _unlockedUntil.Remove(id);
    }

    public void Dispose() => _vault.Dispose();
}

public sealed class RunSession
{
    private readonly List<string> _lines = [];
    internal CancellationTokenSource Stop { get; } = new();

    public Guid Id { get; } = Guid.NewGuid();
    public required string Repo { get; init; }
    public required string Target { get; init; }
    public required string Env { get; init; }
    public required bool Protected { get; init; }
    public required string Labels { get; init; }
    public string? Url { get; init; }
    public DateTime Started { get; } = DateTime.Now;
    public DateTime? Ended { get; internal set; }
    public int? ExitCode { get; internal set; }
    public bool StopRequested { get; internal set; }
    public bool Running => Ended is null;

    internal void Append(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
            if (_lines.Count > 2000) _lines.RemoveRange(0, 500);
        }
    }

    public string Text(int last = 600)
    {
        lock (_lines) return string.Join('\n', _lines.Skip(Math.Max(0, _lines.Count - last)));
    }
}

/// <summary>Processes started from the window. They live as long as the app does.</summary>
public sealed class SessionManager
{
    private readonly List<RunSession> _sessions = [];
    private long _version;

    /// <summary>Goes up whenever a session starts, ends or prints a line; the page polls it.</summary>
    public long Version => Interlocked.Read(ref _version);

    public List<RunSession> All()
    {
        lock (_sessions) return [.. _sessions];
    }

    public RunSession Start(VaultService vaults, RepoInfo repo, EnvInfo env, TargetDef target, bool watch)
    {
        if (All().Any(s => s.Running && s.Repo == repo.Name && s.Target == target.Name))
            throw new VaultException($"{target.Name} is already running. Stop it first.");

        var (specs, block, secrets) = vaults.Use(vault =>
        {
            var resolved = vault.Resolve(repo, env, target);
            var list = new List<ProcessSpec> { Launcher.ForTarget(repo, target, watch, []) };
            foreach (var companion in target.Companions)
                list.Add(Launcher.ForTarget(repo, vault.GetTarget(repo, companion), watch: false, []));
            var environment = Launcher.BuildEnvironment(resolved.Values, clean: false);
            environment["RR_REPO"] = repo.Name;
            environment["RR_ENV"] = env.Name;
            environment["RR_TARGET"] = target.Name;
            vault.Audit(watch ? "watch" : "run", repo.Name, env.Name, target.Name);
            return (list, environment, resolved.SecretNames.Select(n => resolved.Values[n]).ToList());
        });

        var session = new RunSession
        {
            Repo = repo.Name,
            Target = target.Name,
            Env = env.Name,
            Protected = env.Protected,
            Labels = string.Join(" + ", specs.Select(s => s.Label)) + (watch ? " (watch)" : ""),
            Url = target.Urls?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
        };
        lock (_sessions) _sessions.Add(session);
        session.Append($"Starting {session.Labels} on {env.Name} with {block.Count(p => !p.Key.StartsWith("RR_"))} variables in the environment...");
        Interlocked.Increment(ref _version);

        _ = Task.Run(async () =>
        {
            try
            {
                session.ExitCode = await Launcher.RunAsync(specs, block, secrets, new LineWriter(session, this), session.Stop.Token,
                    grace: TimeSpan.Zero, hideWindows: true);
                session.Append(session.StopRequested ? "Stopped." : $"Exited with code {session.ExitCode}.");
            }
            catch (Exception ex)
            {
                session.Append($"Failed: {ex.Message}");
            }
            session.Ended = DateTime.Now;
            Interlocked.Increment(ref _version);
        });
        return session;
    }

    public void Stop(RunSession session)
    {
        session.StopRequested = true;
        session.Stop.Cancel();
    }

    public void Dismiss(RunSession session)
    {
        lock (_sessions) _sessions.Remove(session);
        Interlocked.Increment(ref _version);
    }

    public void StopAll()
    {
        foreach (var session in All().Where(s => s.Running)) Stop(session);
    }

    private sealed class LineWriter(RunSession session, SessionManager owner) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            session.Append(value ?? "");
            Interlocked.Increment(ref owner._version);
        }
    }
}
