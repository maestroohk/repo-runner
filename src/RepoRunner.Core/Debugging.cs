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

    /// <summary>The solution Rider should open: the nearest .sln or .slnx from the project folder up to the repo root.</summary>
    public static string? FindSolution(RepoInfo repo, TargetDef target)
    {
        var dir = Path.GetDirectoryName(Path.Combine(repo.Path, target.Project ?? ""));
        while (dir is not null && dir.Length >= repo.Path.Length)
        {
            var found = Directory.EnumerateFiles(dir, "*.sln").Concat(Directory.EnumerateFiles(dir, "*.slnx"))
                .OrderBy(f => f.Length).FirstOrDefault();
            if (found is not null) return found;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
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
