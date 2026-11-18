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

            // Processes this app starts must not be hooked as well.
            foreach (var name in new[] { "RR_HOOK_TARGET", "RR_FLOW_PIPE", "RR_DEBUG_WAIT", "DOTNET_STARTUP_HOOKS" })
                Environment.SetEnvironmentVariable(name, null);

            if (!string.IsNullOrEmpty(pipe))
            {
                try { StartRecorder(pipe!); }
                catch { /* recording is optional */ }
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
    private static void StartRecorder(string pipe) => FlowRecorder.Start(pipe);

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
