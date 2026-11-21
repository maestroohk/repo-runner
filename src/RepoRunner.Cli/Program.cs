using System.Text;
using RepoRunner.Core;

return await Cli.Run(args);

internal sealed class Args
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "reveal", "protected", "clean", "no-attach", "delete-source", "secret", "plain", "yes", "skip-protected", "help",
    };

    private static readonly Dictionary<string, string> Short = new() { ["-e"] = "env", ["-r"] = "repo", ["-g"] = "group", ["-h"] = "help" };

    public List<string> Positional { get; } = [];
    public List<string> Rest { get; } = [];
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] argv)
    {
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg == "--") { Rest.AddRange(argv[(i + 1)..]); break; }
            string? name = arg.StartsWith("--") ? arg[2..] : Short.GetValueOrDefault(arg);
            if (name is null) { Positional.Add(arg); continue; }

            string? value = null;
            var eq = name.IndexOf('=');
            if (eq > 0 && arg.StartsWith("--")) (name, value) = (name[..eq], name[(eq + 1)..]);
            if (value is null && !Flags.Contains(name))
                value = ++i < argv.Length ? argv[i] : throw new VaultException($"--{name} needs a value.");
            if (!_options.TryGetValue(name, out var list)) _options[name] = list = [];
            list.Add(value ?? "true");
        }
    }

    public string? Get(string name) => _options.TryGetValue(name, out var v) ? v[^1] : null;
    public List<string> All(string name) => _options.TryGetValue(name, out var v) ? v : [];
    public bool Has(string name) => _options.ContainsKey(name);
    public string? Pos(int index) => index < Positional.Count ? Positional[index] : null;
    public string Need(int index, string what) => Pos(index) ?? throw new VaultException($"Missing {what}. See 'rr help'.");
}

internal static class Cli
{
    private const string Help = """
        rr - run repos against an environment kept in an encrypted vault

        Repos
          rr add [path] [--name N]            register a repo and detect its run targets
          rr list                             registered repos
          rr targets                          run targets of this repo
          rr use [--target T] [--env E]       set this repo's default target and environment
          rr target add NAME [--cwd DIR] -- <command...>     add a custom target
          rr target remove NAME
          rr remove NAME --yes                forget a repo and delete its variables

        Variables
          rr set KEY [--env E] [--group G] [--secret|--plain]   prompts for the value (or reads one line from stdin)
          rr get KEY [--env E] [--reveal]
          rr unset KEY [--env E] [--group G]
          rr vars [--env E] [--group G] [--reveal]
          rr import --from FILE --env E [--profile P] [--group G] [--delete-source]
                                              FILE is a .env file or a launchSettings.json
          rr check [--env E]                  are the keys each target requires present?

        Environments and shared groups
          rr env list | add NAME [--protected] | remove NAME --yes | diff A B
          rr group list | add NAME | attach NAME | detach NAME

        Running
          rr run   [target] [--env E] [--set K=V] [--clean] [-- app args]
          rr watch [target] [--env E] ...     same, through dotnet watch
          rr debug [target] [--env E] ...     start paused, then attach Rider (--no-attach: attach yourself)
          rr exec  [--env E] [--clean] -- <command...>       any command with the environment

        Vault
          rr vault status | export FILE [--skip-protected] | import FILE
          rr audit

        Run inside a repo folder and the repo is inferred; otherwise pass --repo NAME (-r).
        -e is short for --env, -g for --group. --clean passes only an allow-list of your shell's variables.
        """;

    public static async Task<int> Run(string[] argv)
    {
        try
        {
            var args = new Args(argv);
            var command = args.Pos(0)?.ToLowerInvariant();
            if (command is null or "help" || args.Has("help"))
            {
                Console.WriteLine(Help);
                return 0;
            }
            args.Positional.RemoveAt(0);

            using var vault = Vault.Open();
            vault.PassphrasePrompt = AskPassphrase;
            return command switch
            {
                "add" => Add(vault, args),
                "list" or "ls" => List(vault),
                "remove" => Remove(vault, args),
                "targets" => Targets(vault, args),
                "target" => Target(vault, args),
                "use" => Use(vault, args),
                "set" => Set(vault, args),
                "get" => Get(vault, args),
                "unset" => Unset(vault, args),
                "vars" => Vars(vault, args),
                "import" => Import(vault, args),
                "check" => Check(vault, args),
                "env" => Env(vault, args),
                "group" => Group(vault, args),
                "run" => await Start(vault, args, watch: false),
                "watch" => await Start(vault, args, watch: true),
                "debug" => await Start(vault, args, watch: false, debug: true),
                "exec" => await Exec(vault, args),
                "vault" => VaultCommand(vault, args),
                "audit" => Audit(vault),
                _ => throw new VaultException($"Unknown command '{command}'. See 'rr help'."),
            };
        }
        catch (VaultException ex)
        {
            Console.Error.WriteLine($"rr: {ex.Message}");
            return 1;
        }
    }

    // ---------- context helpers ----------

    private static RepoInfo RepoOf(Vault vault, Args args) =>
        args.Get("repo") is { } name
            ? vault.GetRepo(name)
            : vault.FindRepoForPath(Directory.GetCurrentDirectory())
              ?? throw new VaultException("This folder is not inside a registered repo. Run 'rr add' here, or pass --repo NAME.");

    private static EnvInfo EnvOf(Vault vault, Args args, RepoInfo? repo) =>
        vault.GetEnv(args.Get("env") ?? repo?.DefaultEnv
            ?? throw new VaultException("Pass --env NAME, or set a default with 'rr use --env NAME'."));

    private static (Scope Scope, string Label, RepoInfo? Repo) ScopeOf(Vault vault, Args args)
    {
        if (args.Get("group") is { } groupName)
        {
            var group = vault.GetGroup(groupName);
            return (Scope.Group(group.Id), $"group:{group.Name}", null);
        }
        var repo = RepoOf(vault, args);
        return (Scope.Repo(repo.Id), "repo", repo);
    }

    private static string ReadSecret(string prompt)
    {
        // PowerShell prepends a byte-order mark when it pipes text into a native program.
        if (Console.IsInputRedirected) return (Console.In.ReadLine() ?? "").TrimStart('﻿');
        Console.Error.Write(prompt);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return text.ToString();
    }

    private static string ReadNewSecret(string what)
    {
        var first = ReadSecret($"{what}: ");
        if (!Console.IsInputRedirected && ReadSecret("Repeat it: ") != first) throw new VaultException("The two entries differ.");
        return first;
    }

    private static string? AskPassphrase(string envName, bool isNew) =>
        isNew ? ReadNewSecret($"Choose a passphrase for the protected environment {envName} (8+ characters)")
              : ReadSecret($"Passphrase for {envName}: ");

    private static string Shown(Variable v, bool reveal) => v.Secret && !reveal ? "********" : v.Value;

    private static void Table(IEnumerable<string[]> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        var widths = Enumerable.Range(0, list[0].Length).Select(c => list.Max(r => r[c].Length)).ToArray();
        foreach (var row in list)
            Console.WriteLine(string.Join("  ", row.Select((cell, c) => c == row.Length - 1 ? cell : cell.PadRight(widths[c]))).TrimEnd());
    }

    // ---------- repos and targets ----------

    private static int Add(Vault vault, Args args)
    {
        var repo = vault.AddRepo(args.Pos(0) ?? Directory.GetCurrentDirectory(), args.Get("name"));
        var detected = Detector.Detect(repo.Path);
        foreach (var target in detected.Targets) vault.SaveTarget(repo, target);
        vault.SetRepoDefaults(repo, detected.DefaultTarget, args.Get("env") is { } e ? vault.GetEnv(e).Name : null);
        vault.Audit("add", repo.Name);

        Console.WriteLine($"Registered {repo.Name} ({repo.Path})");
        PrintTargets(vault, vault.GetRepo(repo.Name));

        var envFiles = PlaintextFiles.Find(repo.Path);
        if (envFiles.Count > 0)
        {
            Console.WriteLine("\nPlaintext variable files found. Import each with: rr import --from <file> --env <ENV>");
            foreach (var file in envFiles) Console.WriteLine($"  {Path.GetRelativePath(repo.Path, file)}");
        }
        return 0;
    }

    private static void PrintTargets(Vault vault, RepoInfo repo)
    {
        var targets = vault.Targets(repo);
        if (targets.Count == 0) { Console.WriteLine("No run targets. Add one with: rr target add NAME -- <command>"); return; }
        Table(targets.Select(t => new[]
        {
            (t.Name.Equals(repo.DefaultTarget, StringComparison.OrdinalIgnoreCase) ? "* " : "  ") + t.Name,
            t.Kind,
            t.Kind switch { "dotnet" => t.Project ?? "", "node" => $"npm run {t.Script} ({t.Cwd})", _ => $"{t.Command} {string.Join(' ', t.Args)}" },
            t.Urls ?? "",
            t.Companions.Count > 0 ? $"+ {string.Join(", ", t.Companions)}" : "",
        }));
    }

    private static int List(Vault vault)
    {
        var repos = vault.Repos();
        if (repos.Count == 0) { Console.WriteLine("No repos yet. Run 'rr add' inside a repo folder."); return 0; }
        Table(repos.Select(r => new[] { r.Name, r.DefaultEnv ?? "-", r.DefaultTarget ?? "-", r.Path }));
        return 0;
    }

    private static int Remove(Vault vault, Args args)
    {
        var repo = vault.GetRepo(args.Need(0, "the repo name"));
        if (!args.Has("yes")) throw new VaultException($"This deletes every stored variable of '{repo.Name}'. Repeat with --yes.");
        vault.RemoveRepo(repo);
        vault.Audit("remove", repo.Name);
        Console.WriteLine($"Removed {repo.Name}. Files in {repo.Path} were not touched.");
        return 0;
    }

    private static int Targets(Vault vault, Args args)
    {
        PrintTargets(vault, RepoOf(vault, args));
        return 0;
    }

    private static int Target(Vault vault, Args args)
    {
        var repo = RepoOf(vault, args);
        var name = args.Need(1, "the target name");
        switch (args.Need(0, "'add' or 'remove'"))
        {
            case "add":
                if (args.Rest.Count == 0) throw new VaultException("Give the command after '--', e.g. rr target add api -- python -m uvicorn main:app");
                vault.SaveTarget(repo, new TargetDef { Name = name, Kind = "custom", Command = args.Rest[0], Args = args.Rest[1..], Cwd = args.Get("cwd") });
                Console.WriteLine($"Saved target {name}.");
                return 0;
            case "remove":
                if (!vault.RemoveTarget(repo, name)) throw new VaultException($"No target '{name}'.");
                Console.WriteLine($"Removed target {name}.");
                return 0;
            default:
                throw new VaultException("Use 'rr target add' or 'rr target remove'.");
        }
    }

    private static int Use(Vault vault, Args args)
    {
        var repo = RepoOf(vault, args);
        var target = args.Get("target") is { } t ? vault.GetTarget(repo, t).Name : null;
        var env = args.Get("env") is { } e ? vault.GetEnv(e).Name : null;
        if (target is null && env is null) throw new VaultException("Pass --target and/or --env.");
        vault.SetRepoDefaults(repo, target, env);
        repo = vault.GetRepo(repo.Name);
        Console.WriteLine($"{repo.Name}: default target {repo.DefaultTarget ?? "-"}, default environment {repo.DefaultEnv ?? "-"}");
        return 0;
    }

    // ---------- variables ----------

    private static int Set(Vault vault, Args args)
    {
        var name = args.Need(0, "the variable name");
        if (name.Contains('=')) throw new VaultException("Give only the name; rr asks for the value so it stays out of your shell history.");
        var (scope, label, repo) = ScopeOf(vault, args);
        var env = EnvOf(vault, args, repo);
        var value = ReadSecret($"Value for {name} [{env.Name}]: ");
        bool? secret = args.Has("secret") ? true : args.Has("plain") ? false : null;
        vault.SetVar(scope, env, name, value, secret);
        vault.Audit("set", repo?.Name ?? label, env.Name, name);
        Console.WriteLine($"Stored {name} for {env.Name} ({(repo is null ? label : repo.Name)}).");
        return 0;
    }

    private static int Unset(Vault vault, Args args)
    {
        var name = args.Need(0, "the variable name");
        var (scope, label, repo) = ScopeOf(vault, args);
        var env = EnvOf(vault, args, repo);
        if (!vault.RemoveVar(scope, env, name)) throw new VaultException($"{name} is not set for {env.Name}.");
        vault.Audit("unset", repo?.Name ?? label, env.Name, name);
        Console.WriteLine($"Removed {name} from {env.Name}.");
        return 0;
    }

    private static int Get(Vault vault, Args args)
    {
        var name = args.Need(0, "the variable name");
        var repo = RepoOf(vault, args);
        var env = EnvOf(vault, args, repo);
        var resolved = vault.Resolve(repo, env);
        if (!resolved.Values.TryGetValue(name, out var value)) throw new VaultException($"{name} is not set for {repo.Name} in {env.Name}.");
        var secret = resolved.SecretNames.Contains(name);
        if (secret && args.Has("reveal")) vault.Audit("reveal", repo.Name, env.Name, name);
        Console.WriteLine(secret && !args.Has("reveal") ? "******** (secret; add --reveal to show it)" : value);
        return 0;
    }

    private static int Vars(Vault vault, Args args)
    {
        var (scope, label, repo) = ScopeOf(vault, args);
        var env = EnvOf(vault, args, repo);
        var reveal = args.Has("reveal");
        List<Variable> vars;
        if (repo is null) vars = vault.ListVars(scope, env, label);
        else
        {
            var resolved = vault.Resolve(repo, env);
            vars = resolved.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => new Variable(p.Key, p.Value, resolved.SecretNames.Contains(p.Key), resolved.Origins[p.Key])).ToList();
        }
        if (vars.Count == 0) { Console.WriteLine($"No variables for {env.Name}."); return 0; }
        if (reveal && vars.Any(v => v.Secret)) vault.Audit("reveal", repo?.Name ?? label, env.Name, "all");
        Table(vars.Select(v => new[] { v.Name, v.Origin, Shown(v, reveal) }));
        return 0;
    }

    private static int Import(Vault vault, Args args)
    {
        var file = Path.GetFullPath(args.Get("from") ?? args.Need(0, "--from FILE"));
        if (!File.Exists(file)) throw new VaultException($"File not found: {file}");
        var (scope, label, repo) = ScopeOf(vault, args);
        var env = EnvOf(vault, args, repo);

        List<KeyValuePair<string, string>> pairs;
        var isLaunchSettings = Path.GetFileName(file).Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase);
        if (isLaunchSettings)
        {
            var profiles = LaunchSettings.Read(file).Where(p => p.Variables.Count > 0).ToList();
            var wanted = args.Get("profile");
            var profile = wanted is not null
                ? profiles.FirstOrDefault(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                : profiles.Count == 1 ? profiles[0] : null;
            if (profile is null)
                throw new VaultException($"Pick a profile with --profile. Profiles with variables: {string.Join(", ", profiles.Select(p => $"\"{p.Name}\""))}");
            pairs = profile.Variables;
        }
        else pairs = DotEnv.Parse(File.ReadAllText(file));

        if (pairs.Count == 0) throw new VaultException("No variables found in that file.");
        foreach (var (name, value) in pairs) vault.SetVar(scope, env, name, value);

        // Read back before anything is deleted.
        var stored = vault.ListVars(scope, env, label).ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
        var expected = pairs.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
        var bad = expected.Where(p => !stored.TryGetValue(p.Key, out var v) || v != p.Value).Select(p => p.Key).ToList();
        if (bad.Count > 0) throw new VaultException($"Verification failed for: {string.Join(", ", bad)}. The source file was left alone.");

        vault.Audit("import", repo?.Name ?? label, env.Name, $"{expected.Count} from {Path.GetFileName(file)}");
        Console.WriteLine($"Imported and verified {expected.Count} variables into {env.Name} ({(repo is null ? label : repo.Name)}):");
        Console.WriteLine("  " + string.Join(", ", expected.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));

        if (args.Has("delete-source"))
        {
            if (isLaunchSettings) Console.WriteLine("launchSettings.json was not edited. Remove the imported keys from that profile by hand.");
            else { File.Delete(file); Console.WriteLine($"Deleted {file}"); }
        }
        else if (!isLaunchSettings)
            Console.WriteLine($"The file is still on disk and your loaders will prefer it. Delete it, or repeat with --delete-source: {file}");
        return 0;
    }

    private static int Check(Vault vault, Args args)
    {
        var repo = RepoOf(vault, args);
        var targets = vault.Targets(repo);
        var envs = args.Get("env") is { } name ? [vault.GetEnv(name)] : vault.Environments();
        var problems = 0;
        foreach (var env in envs)
        {
            if (env.Protected && !args.Has("env")) { Console.WriteLine($"{env.Name,-8} skipped (protected; check it with --env {env.Name})"); continue; }
            var resolved = vault.Resolve(repo, env);
            if (resolved.Values.Count == 0) { Console.WriteLine($"{env.Name,-8} no variables"); continue; }
            var missing = targets
                .SelectMany(t => t.Requires.Where(k => !resolved.Values.ContainsKey(k)).Select(k => $"{k} ({t.Name})"))
                .Distinct().ToList();
            problems += missing.Count;
            Console.WriteLine(missing.Count == 0
                ? $"{env.Name,-8} ok, {resolved.Values.Count} variables"
                : $"{env.Name,-8} missing: {string.Join(", ", missing)}");
        }
        return problems == 0 ? 0 : 2;
    }

    // ---------- environments and groups ----------

    private static int Env(Vault vault, Args args)
    {
        switch (args.Pos(0) ?? "list")
        {
            case "list":
                Table(vault.Environments().Select(e => new[]
                {
                    e.Name, e.Protected ? (e.HasPassphrase ? "protected" : "protected (passphrase not set yet)") : "",
                }));
                return 0;
            case "add":
                var added = vault.AddEnv(args.Need(1, "the environment name"), args.Has("protected"));
                Console.WriteLine($"Added {added.Name}{(added.Protected ? " (protected)" : "")}.");
                return 0;
            case "remove":
                var env = vault.GetEnv(args.Need(1, "the environment name"));
                if (!args.Has("yes")) throw new VaultException($"This deletes every variable stored for {env.Name}, in all repos and groups. Repeat with --yes.");
                Console.WriteLine($"Removed {env.Name} and {vault.RemoveEnv(env)} variables.");
                vault.Audit("env-remove", null, env.Name);
                return 0;
            case "diff":
                var repo = RepoOf(vault, args);
                var a = vault.GetEnv(args.Need(1, "the first environment"));
                var b = vault.GetEnv(args.Need(2, "the second environment"));
                var left = vault.Resolve(repo, a).Values;
                var right = vault.Resolve(repo, b).Values;
                var rows = left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .Select(k => new[]
                    {
                        k,
                        !right.ContainsKey(k) ? $"only in {a.Name}" : !left.ContainsKey(k) ? $"only in {b.Name}" : left[k] == right[k] ? "same" : "different",
                    });
                Table(rows);
                return 0;
            default:
                throw new VaultException("Use: rr env list | add NAME [--protected] | remove NAME --yes | diff A B");
        }
    }

    private static int Group(Vault vault, Args args)
    {
        switch (args.Pos(0) ?? "list")
        {
            case "list":
                var groups = vault.Groups();
                if (groups.Count == 0) { Console.WriteLine("No shared groups. Create one with: rr group add NAME"); return 0; }
                var repos = vault.Repos();
                Table(groups.Select(g => new[]
                {
                    g.Name, string.Join(", ", repos.Where(r => vault.GroupsOf(r).Any(x => x.Id == g.Id)).Select(r => r.Name)),
                }));
                return 0;
            case "add":
                Console.WriteLine($"Added group {vault.AddGroup(args.Need(1, "the group name")).Name}. Fill it with: rr set KEY --group {args.Pos(1)} --env ENV");
                return 0;
            case "attach":
            case "detach":
                var repo = RepoOf(vault, args);
                var group = vault.GetGroup(args.Need(1, "the group name"));
                if (args.Pos(0) == "attach") vault.Attach(repo, group); else vault.Detach(repo, group);
                var attached = string.Join(", ", vault.GroupsOf(repo).Select(g => g.Name));
                Console.WriteLine($"{repo.Name}: groups = {(attached.Length > 0 ? attached : "none")}");
                return 0;
            default:
                throw new VaultException("Use: rr group list | add NAME | attach NAME | detach NAME");
        }
    }

    // ---------- running ----------

    private static List<KeyValuePair<string, string>> Overrides(Args args) =>
        args.All("set").Select(s =>
        {
            var eq = s.IndexOf('=');
            return eq > 0 ? new KeyValuePair<string, string>(s[..eq], s[(eq + 1)..]) : throw new VaultException($"--set expects KEY=value, got '{s}'.");
        }).ToList();

    private static async Task<int> Start(Vault vault, Args args, bool watch, bool debug = false)
    {
        var repo = RepoOf(vault, args);
        var env = EnvOf(vault, args, repo);
        var target = vault.GetTarget(repo, args.Pos(0));
        RequireLive(env, $"run {target.Name}");
        var resolved = vault.Resolve(repo, env, target, Overrides(args));

        var specs = new List<ProcessSpec> { Launcher.ForTarget(repo, target, watch, args.Rest) };
        foreach (var companion in target.Companions)
            specs.Add(Launcher.ForTarget(repo, vault.GetTarget(repo, companion), watch: false, []));

        var missing = target.Requires.Where(k => !resolved.Values.ContainsKey(k)).ToList();
        if (missing.Count > 0) Console.Error.WriteLine($"rr: warning: {env.Name} is missing {string.Join(", ", missing)}");

        vault.Audit(debug ? "debug" : watch ? "watch" : "run", repo.Name, env.Name, target.Name);
        return await Launch(vault, specs, resolved, repo.Name, env, target.Name, args.Has("clean"), debug ? (repo, target) : null, args.Has("no-attach"));
    }

    private static async Task<int> Exec(Vault vault, Args args)
    {
        if (args.Rest.Count == 0) throw new VaultException("Give the command after '--', e.g. rr exec --env UAT -- dotnet ef database update");
        var repo = RepoOf(vault, args);
        var env = EnvOf(vault, args, repo);
        RequireLive(env, "run commands");
        var resolved = vault.Resolve(repo, env, null, Overrides(args));
        var spec = Launcher.ForCommand(args.Rest[0], args.Rest[0], args.Rest[1..], Directory.GetCurrentDirectory());
        vault.Audit("exec", repo.Name, env.Name, args.Rest[0]);
        return await Launch(vault, [spec], resolved, repo.Name, env, "exec", args.Has("clean"));
    }

    /// <summary>
    /// The command line has no sandbox: what it starts reaches whatever its settings point at. So it starts nothing on an
    /// environment that is sandbox-only; the window runs those apps in the sandbox.
    /// </summary>
    private static void RequireLive(EnvInfo env, string what)
    {
        if (!env.Live)
            throw new VaultException($"{env.Name} is sandbox-only, and rr cannot {what} in the sandbox. Start it from the RepoRunner window, " +
                $"which runs it in the sandbox, or allow real servers for {env.Name} there first.");
    }

    private static async Task<int> Launch(Vault vault, List<ProcessSpec> specs, ResolvedEnvironment resolved, string repoName,
        EnvInfo env, string targetName, bool clean, (RepoInfo Repo, TargetDef Target)? debug = null, bool noAttach = false)
    {
        var block = Launcher.BuildEnvironment(resolved.Values, clean);
        TextWriter output = Console.Out;
        if (debug is { } d)
        {
            DebugSupport.Apply(block, d.Repo, d.Target, waitForDebugger: true);
            var solution = DebugSupport.FindSolution(d.Repo, d.Target);
            output = new DebugWriter(Console.Out, pid =>
            {
                if (noAttach) { Console.Error.WriteLine($"rr: attach your debugger to process {pid}"); return; }
                try { Console.Error.WriteLine("rr: " + DebugSupport.AttachRider(pid, solution)); }
                catch (VaultException ex) { Console.Error.WriteLine("rr: " + ex.Message); }
            });
        }
        block["RR_REPO"] = repoName;
        block["RR_ENV"] = env.Name;
        block["RR_TARGET"] = targetName;
        var secrets = resolved.SecretNames.Select(n => resolved.Values[n]).ToList();
        vault.Dispose(); // nothing below needs the vault; release the file while the app runs

        if (env.Protected) Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"rr: {repoName} / {string.Join(" + ", specs.Select(s => s.Label))} on {env.Name}, {resolved.Values.Count} variables injected");
        Console.ResetColor();

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        return await Launcher.RunAsync(specs, block, secrets, output, stop.Token);
    }

    // ---------- vault ----------

    private static int VaultCommand(Vault vault, Args args)
    {
        switch (args.Pos(0) ?? "status")
        {
            case "status":
                Console.WriteLine($"Vault:         {Path.Combine(vault.Home, "vault.db")}");
                Console.WriteLine($"Repos:         {vault.Repos().Count}");
                Console.WriteLine($"Shared groups: {vault.Groups().Count}");
                Console.WriteLine($"Environments:  {string.Join(", ", vault.Environments().Select(e => e.Protected ? e.Name + " (protected)" : e.Name))}");
                Console.WriteLine("Encryption:    AES-256-GCM; vault key wrapped by Windows DPAPI for this user");
                return 0;
            case "export":
            {
                var file = Path.GetFullPath(args.Need(1, "the bundle file path"));
                if (File.Exists(file)) throw new VaultException($"{file} already exists. Choose another name.");
                var pass = ReadNewSecret("Choose a passphrase for this bundle (8+ characters)");
                var (data, summary) = vault.Export(pass, args.Has("skip-protected"));
                File.WriteAllBytes(file, data);
                vault.Audit("export", null, null, Path.GetFileName(file));
                Console.WriteLine($"Wrote {file}: {summary.Repos} repos, {summary.Groups} groups, {summary.Variables} variables.");
                if (summary.SkippedEnvironments.Count > 0) Console.WriteLine($"Not included (protected): {string.Join(", ", summary.SkippedEnvironments)}");
                Console.WriteLine("Without the passphrase this file cannot be opened. Keep both somewhere safe.");
                return 0;
            }
            case "import":
            {
                var file = Path.GetFullPath(args.Need(1, "the bundle file path"));
                if (!File.Exists(file)) throw new VaultException($"File not found: {file}");
                var summary = vault.Import(File.ReadAllBytes(file), ReadSecret("Bundle passphrase: "));
                vault.Audit("vault-import", null, null, Path.GetFileName(file));
                Console.WriteLine($"Merged {summary.Repos} repos, {summary.Groups} groups, {summary.Variables} variables.");
                return 0;
            }
            default:
                throw new VaultException("Use: rr vault status | export FILE [--skip-protected] | import FILE");
        }
    }

    private static int Audit(Vault vault)
    {
        Table(vault.AuditLog().Select(a => new[] { a.Time, a.Action, a.Repo ?? "-", a.Env ?? "-", a.Detail ?? "" }));
        return 0;
    }
}
