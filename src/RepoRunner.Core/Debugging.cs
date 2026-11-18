using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RepoRunner.Core;

/// <summary>
/// Debug runs: the app is started with a startup hook that pauses it before Main and prints its process id;
/// RepoRunner then hands that process to the IDE's debugger.
/// </summary>
public static partial class DebugSupport
{
    private const string HookResource = "RepoRunner.StartupHook.dll";

    /// <summary>Writes the embedded startup hook to disk once per version and returns its path.</summary>
    public static string HookPath()
    {
        using var resource = typeof(DebugSupport).Assembly.GetManifestResourceStream(HookResource)
            ?? throw new VaultException("The startup hook is missing from this build.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var folder = Path.Combine(Vault.DefaultHome, "hook", Convert.ToHexString(SHA256.HashData(bytes))[..12]);
        var path = Path.Combine(folder, HookResource);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, bytes);
        }
        return path;
    }

    /// <summary>Adds what a debug run needs to the child's environment.</summary>
    public static void Apply(IDictionary<string, string> environment, RepoInfo repo, TargetDef target)
    {
        if (target.Kind != "dotnet" || target.Project is null)
            throw new VaultException("Debug runs are available for .NET targets only for now.");
        var hook = HookPath();
        environment["DOTNET_STARTUP_HOOKS"] =
            environment.TryGetValue("DOTNET_STARTUP_HOOKS", out var existing) && existing.Length > 0 ? $"{hook};{existing}" : hook;
        environment["RR_DEBUG_TARGET"] = AssemblyName(Path.Combine(repo.Path, target.Project));
        if (!environment.ContainsKey("RR_DEBUG_TIMEOUT")) environment["RR_DEBUG_TIMEOUT"] = "300";
    }

    /// <summary>The process name the app will run under: AssemblyName from the project file, else the file name.</summary>
    public static string AssemblyName(string projectFile)
    {
        var match = AssemblyNameRegex().Match(File.ReadAllText(projectFile));
        return match.Success && !match.Groups[1].Value.Contains('$')
            ? match.Groups[1].Value.Trim()
            : Path.GetFileNameWithoutExtension(projectFile);
    }

    /// <summary>The process id from the hook's "waiting" line, or null for any other line.</summary>
    public static int? WaitingPid(string? line)
    {
        if (line is null) return null;
        var match = WaitingRegex().Match(line);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static readonly HashSet<string> SolutionSkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", "packages", "publish", "dist", "TestResults" };

    /// <summary>Every .sln and .slnx in the repo, nearest to the root first. Hidden folders such as .vs are skipped.</summary>
    public static List<string> Solutions(RepoInfo repo)
    {
        var found = new List<string>();
        void Walk(string dir, int depth)
        {
            try
            {
                found.AddRange(Directory.EnumerateFiles(dir, "*.sln").Concat(Directory.EnumerateFiles(dir, "*.slnx")));
                if (depth >= 3) return;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (!name.StartsWith('.') && !SolutionSkipDirs.Contains(name)) Walk(sub, depth + 1);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Walk(repo.Path, 0);
        return found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar)).ThenBy(f => f).ToList();
    }

    /// <summary>
    /// The solution to debug with. An explicit choice on the target wins. Otherwise the one Rider has open, so the
    /// session lands in the window that holds your breakpoints; a different file would open a second window.
    /// Failing that, the one Rider used most recently, then the nearest to the project.
    /// </summary>
    public static string? FindSolution(RepoInfo repo, TargetDef target)
    {
        if (target.Solution is { Length: > 0 })
        {
            var chosen = Path.GetFullPath(Path.Combine(repo.Path, target.Solution));
            if (File.Exists(chosen)) return chosen;
        }
        var candidates = Solutions(repo);
        if (candidates.Count <= 1) return candidates.FirstOrDefault();

        var rider = RiderRecentSolutions();
        var known = candidates.Where(rider.ContainsKey).ToList();
        if (known.Count > 0)
            return known.OrderByDescending(c => rider[c].Open).ThenByDescending(c => rider[c].Activated).First();

        var dir = Path.GetDirectoryName(Path.Combine(repo.Path, target.Project ?? ""));
        while (dir is not null && dir.Length >= repo.Path.Length)
        {
            var near = candidates.FirstOrDefault(c => string.Equals(Path.GetDirectoryName(c), dir, StringComparison.OrdinalIgnoreCase));
            if (near is not null) return near;
            dir = Path.GetDirectoryName(dir);
        }
        return candidates[0];
    }

    /// <summary>Solutions Rider knows, from its recent-solutions file: whether each is open now and when it was last focused.</summary>
    public static Dictionary<string, (bool Open, long Activated)> RiderRecentSolutions()
    {
        var result = new Dictionary<string, (bool, long)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var jetbrains = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetBrains");
            if (!Directory.Exists(jetbrains)) return result;
            var file = Directory.EnumerateDirectories(jetbrains, "Rider*")
                .Select(d => Path.Combine(d, "options", "recentSolutions.xml"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (file is null) return result;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var entry in System.Xml.Linq.XDocument.Load(file).Descendants("entry"))
            {
                var key = (string?)entry.Attribute("key");
                var meta = entry.Descendants("RecentProjectMetaInfo").FirstOrDefault();
                if (key is null || meta is null) continue;
                var path = Path.GetFullPath(key.Replace("$USER_HOME$", home));
                var activated = meta.Elements("option").FirstOrDefault(o => (string?)o.Attribute("name") == "activationTimestamp");
                long.TryParse((string?)activated?.Attribute("value"), out var when);
                result[path] = ((string?)meta.Attribute("opened") == "true", when);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
        return result;
    }

    public static string? FindRider()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JetBrains"),
            Path.Combine(local, "Programs"),
            Path.Combine(local, "JetBrains", "Toolbox", "apps"),
        ];
        return roots.Where(Directory.Exists)
            .SelectMany(root => SafeFiles(root, "rider64.exe"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static IEnumerable<string> SafeFiles(string root, string name)
    {
        try
        {
            return Directory.EnumerateFiles(root, name,
                new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true }).ToList();
        }
        catch (IOException) { return []; }
    }

    /// <summary>Asks Rider to open the solution and attach its .NET debugger to the process. Returns what was done.</summary>
    public static string AttachRider(int pid, string? solution)
    {
        var rider = FindRider() ?? throw new VaultException("Rider was not found. Attach by hand: Run > Attach to Process, pid " + pid + ".");
        var psi = new ProcessStartInfo(rider) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("attach-to-process");
        psi.ArgumentList.Add("netcore");
        psi.ArgumentList.Add(pid.ToString());
        if (solution is not null) psi.ArgumentList.Add(solution);
        try { Process.Start(psi)?.Dispose(); }
        catch (Win32Exception ex) { throw new VaultException($"Could not start Rider: {ex.Message}"); }
        return $"Asked Rider to attach to process {pid}" + (solution is null ? "." : $" with {Path.GetFileName(solution)}.");
    }

    [GeneratedRegex(@"<AssemblyName>\s*([^<]+?)\s*</AssemblyName>")]
    private static partial Regex AssemblyNameRegex();

    [GeneratedRegex(@"\[rr-debug\] waiting pid=(\d+)")]
    private static partial Regex WaitingRegex();
}

/// <summary>Passes output through and reports the moment the paused app announces its process id.</summary>
public sealed class DebugWriter(TextWriter inner, Action<int> onWaiting) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    public override void WriteLine(string? value)
    {
        inner.WriteLine(value);
        if (DebugSupport.WaitingPid(value) is { } pid) onWaiting(pid);
    }
}
