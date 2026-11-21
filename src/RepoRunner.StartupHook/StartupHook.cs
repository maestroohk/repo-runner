using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

/// <summary>
/// Runs before Main in every .NET process that inherits DOTNET_STARTUP_HOOKS. It acts only in the process named
/// by RR_HOOK_TARGET: there it can record what the app does (RR_FLOW_PIPE) and wait for a debugger (RR_DEBUG_WAIT).
/// The runtime requires this exact type name, without a namespace.
/// </summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        try
        {
            var target = Environment.GetEnvironmentVariable("RR_HOOK_TARGET");
            if (string.IsNullOrEmpty(target)) return;

            int pid;
            bool isTarget;
            using (var self = Process.GetCurrentProcess())
            {
                pid = self.Id;
                isTarget = string.Equals(self.ProcessName, target, StringComparison.OrdinalIgnoreCase);
            }
            if (!isTarget)
            {
                // Apps without their own .exe run as "dotnet App.dll".
                foreach (var arg in Environment.GetCommandLineArgs())
                    if (arg.EndsWith(target + ".dll", StringComparison.OrdinalIgnoreCase)) isTarget = true;
            }
            if (!isTarget) return; // dotnet, MSBuild, the compiler: carry on untouched

            var pipe = Environment.GetEnvironmentVariable("RR_FLOW_PIPE");
            var wait = Environment.GetEnvironmentVariable("RR_DEBUG_WAIT") == "1";
            var sandbox = Environment.GetEnvironmentVariable("RR_SANDBOX") == "1";
            var sandboxTarget = Environment.GetEnvironmentVariable("RR_SANDBOX_TARGET");
            var allowed = (Environment.GetEnvironmentVariable("RR_SANDBOX_ALLOW") ?? "").Split(',');

            // Processes this app starts must not be hooked as well.
            foreach (var name in new[] { "RR_HOOK_TARGET", "RR_FLOW_PIPE", "RR_SANDBOX", "RR_SANDBOX_TARGET", "RR_SANDBOX_ALLOW", "RR_DEBUG_WAIT", "DOTNET_STARTUP_HOOKS" })
                Environment.SetEnvironmentVariable(name, null);

            // The guard goes first and is not optional: a sandboxed app that cannot be guarded does not run.
            if (sandbox && !ArmGuard(allowed))
            {
                Console.Error.WriteLine("[rr-sandbox] RepoRunner could not guard this app's network connections (it needs .NET 9 or later), so it was stopped before it started. Nothing left this machine.");
                Console.Error.Flush();
                Environment.Exit(87);
            }

            if (!string.IsNullOrEmpty(pipe))
            {
                try { StartRecorder(pipe!, sandbox ? sandboxTarget : null, allowed, sandbox); }
                catch { /* recording is optional; the guard above still holds */ }
            }
            if (wait) WaitForDebugger(pid);
        }
        catch
        {
            // A development aid must never stop the app from starting.
        }
    }

    // Kept out of line so a missing diagnostics type cannot break Initialize itself.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartRecorder(string pipe, string? sandboxTarget, string[] allowed, bool sandbox) =>
        FlowRecorder.Start(pipe, sandboxTarget, allowed, sandbox);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ArmGuard(string[] allowed)
    {
        try { return NetworkGuard.Arm(allowed, FlowRecorder.Connection); }
        catch { return false; }
    }

    private static void WaitForDebugger(int pid)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("RR_DEBUG_TIMEOUT"), out var seconds) || seconds <= 0) seconds = 300;
        Console.Out.WriteLine("[rr-debug] waiting pid=" + pid);
        Console.Out.Flush();

        var clock = Stopwatch.StartNew();
        while (!Debugger.IsAttached && clock.Elapsed.TotalSeconds < seconds) Thread.Sleep(150);

        Console.Out.WriteLine(Debugger.IsAttached
            ? "[rr-debug] debugger attached, starting the app"
            : "[rr-debug] no debugger after " + seconds + "s, starting the app without one");
        Console.Out.Flush();
    }
}
