using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RepoRunner.Core;

/// <summary>One process to start. RawArguments, when set, replaces Args (used for cmd.exe, which has its own quoting rules).</summary>
public sealed record ProcessSpec(string Label, string FileName, List<string> Args, string WorkingDir, string? RawArguments = null);

public static class Launcher
{
    /// <summary>Variables a child keeps from the calling shell when --clean is used.</summary>
    private static readonly HashSet<string> CleanAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "TEMP", "TMP", "USERPROFILE", "USERNAME",
        "USERDOMAIN", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)",
        "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "ALLUSERSPROFILE", "PUBLIC",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS", "COMPUTERNAME", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "TERM",
    };

    private static readonly string[] CleanAllowPrefixes = ["DOTNET_", "NUGET_", "NVM_", "npm_config_", "MSBuild"];

    /// <summary>
    /// The environment block for a child: the calling shell's variables (all of them, or the allow-list when
    /// <paramref name="clean"/>), overlaid with the resolved set.
    /// </summary>
    public static Dictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string> resolved, bool clean, IDictionary? inherited = null)
    {
        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in inherited ?? Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            if (clean && !CleanAllowList.Contains(name) && !CleanAllowPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;
            block[name] = (string?)entry.Value ?? "";
        }
        foreach (var (name, value) in resolved) block[name] = value;
        return block;
    }

    public static ProcessSpec ForTarget(RepoInfo repo, TargetDef target, bool watch, IReadOnlyList<string> extraArgs)
    {
        var cwd = Path.GetFullPath(Path.Combine(repo.Path, target.Cwd ?? "."));
        switch (target.Kind)
        {
            case "dotnet":
            {
                var project = Path.Combine(repo.Path, target.Project ?? throw new VaultException($"Target '{target.Name}' has no project."));
                if (!File.Exists(project)) throw new VaultException($"Project not found: {project}");
                // The launch profile is skipped on purpose: its environmentVariables would override the injected ones.
                List<string> args = watch
                    ? ["watch", "--project", project, "run", "--no-launch-profile"]
                    : ["run", "--project", project, "--no-launch-profile"];
                var appArgs = target.Args.Concat(extraArgs).ToList();
                if (appArgs.Count > 0) { args.Add("--"); args.AddRange(appArgs); }
                return new ProcessSpec(target.Name, "dotnet", args, Path.GetDirectoryName(project)!);
            }
            case "node":
            {
                List<string> args = ["run", target.Script ?? throw new VaultException($"Target '{target.Name}' has no script.")];
                if (extraArgs.Count > 0) { args.Add("--"); args.AddRange(extraArgs); }
                return ForCommand(target.Name, "npm", args, cwd);
            }
            case "custom":
                return ForCommand(target.Name, target.Command ?? throw new VaultException($"Target '{target.Name}' has no command."),
                    [.. target.Args, .. extraArgs], cwd);
            default:
                throw new VaultException($"Target '{target.Name}' has unknown kind '{target.Kind}'.");
        }
    }

    public static ProcessSpec ForCommand(string label, string command, IReadOnlyList<string> args, string cwd)
    {
        var resolved = FindExecutable(command, cwd) ?? throw new VaultException($"Command not found: {command}");
        switch (Path.GetExtension(resolved).ToLowerInvariant())
        {
            case ".exe" or ".com":
                return new ProcessSpec(label, resolved, [.. args], cwd);
            case ".cmd" or ".bat":
            {
                // cmd /s /c "<line>" strips exactly the outer quotes, leaving the inner quoting intact.
                var line = string.Join(" ", new[] { resolved }.Concat(args).Select(Quote));
                var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                return new ProcessSpec(label, comspec, [], cwd, $"/d /s /c \"{line}\"");
            }
            case ".ps1":
                return new ProcessSpec(label, "powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", resolved, .. args], cwd);
            default:
                throw new VaultException($"Don't know how to run '{resolved}'.");
        }
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.IndexOfAny([' ', '\t', '&', '|', '<', '>', '^', '(', ')', '"']) < 0 ? arg : $"\"{arg.Replace("\"", "\\\"")}\"";

    public static string? FindExecutable(string command, string cwd)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries).Append(".ps1").ToList();
        IEnumerable<string> Candidates(string basePath) =>
            Path.HasExtension(basePath) ? new[] { basePath }.Concat(extensions.Select(e => basePath + e)) : extensions.Select(e => basePath + e);

        if (command.Contains('\\') || command.Contains('/') || Path.IsPathRooted(command))
            return Candidates(Path.GetFullPath(command, cwd)).FirstOrDefault(File.Exists);

        var dirs = new[] { cwd }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            try
            {
                var hit = Candidates(Path.Combine(dir.Trim('"'), command)).FirstOrDefault(File.Exists);
                if (hit is not null) return hit;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>
    /// Starts every spec with the same environment and waits for the first one. Output is prefixed per process when
    /// there are several, and secret values are masked. Companions are stopped when the first process ends.
    /// </summary>
    public static async Task<int> RunAsync(IReadOnlyList<ProcessSpec> specs, IReadOnlyDictionary<string, string> environment,
        IEnumerable<string> secretValues, TextWriter output, CancellationToken stop)
    {
        var masks = secretValues.Where(v => v.Length >= 6).Distinct().OrderByDescending(v => v.Length).ToArray();
        var gate = new object();
        var width = specs.Max(s => s.Label.Length);
        using var job = new JobObject();
        var started = new List<Process>();
        try
        {
            foreach (var spec in specs)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = spec.FileName,
                    WorkingDirectory = spec.WorkingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                if (spec.RawArguments is not null) psi.Arguments = spec.RawArguments;
                else foreach (var arg in spec.Args) psi.ArgumentList.Add(arg);
                psi.Environment.Clear();
                foreach (var (name, value) in environment) psi.Environment[name] = value;

                var prefix = specs.Count > 1 ? $"[{spec.Label.PadRight(width)}] " : "";
                void Write(string? line)
                {
                    if (line is null) return;
                    foreach (var mask in masks) line = line.Replace(mask, "****");
                    lock (gate) output.WriteLine(prefix + line);
                }

                var process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, e) => Write(e.Data);
                process.ErrorDataReceived += (_, e) => Write(e.Data);
                try { process.Start(); }
                catch (Win32Exception ex) { throw new VaultException($"Could not start '{spec.Label}': {ex.Message}"); }
                job.Assign(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                started.Add(process);
            }

            var main = started[0];
            try { await main.WaitForExitAsync(stop); }
            catch (OperationCanceledException)
            {
                // Ctrl+C reached the children through the shared console; give the app a moment to shut down cleanly.
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await main.WaitForExitAsync(grace.Token); } catch (OperationCanceledException) { }
            }
            if (!main.HasExited) main.Kill(entireProcessTree: true);
            main.WaitForExit();
            return main.ExitCode;
        }
        finally
        {
            foreach (var process in started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                process.Dispose();
            }
        }
    }
}

/// <summary>A Windows job object that kills every process in it when RepoRunner exits, so nothing is orphaned.</summary>
internal sealed class JobObject : IDisposable
{
    private nint _handle;

    public JobObject()
    {
        _handle = CreateJobObjectW(0, null);
        if (_handle == 0) return;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            SetInformationJobObject(_handle, 9 /* JobObjectExtendedLimitInformation */, buffer, (uint)size);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Assign(Process process)
    {
        if (_handle != 0) AssignProcessToJobObject(_handle, process.Handle);
    }

    public void Dispose()
    {
        if (_handle != 0) CloseHandle(_handle);
        _handle = 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
}
