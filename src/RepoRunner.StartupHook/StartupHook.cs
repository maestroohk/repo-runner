using System;
using System.Diagnostics;
using System.Threading;

/// <summary>
/// Runs before Main in every .NET process that inherits DOTNET_STARTUP_HOOKS. It pauses only the process named
/// by RR_DEBUG_TARGET, announces its process id, and waits until a debugger attaches. The runtime requires this
/// exact type name, without a namespace.
/// </summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        try
        {
            var target = Environment.GetEnvironmentVariable("RR_DEBUG_TARGET");
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

            // Processes this app starts must not pause as well.
            Environment.SetEnvironmentVariable("RR_DEBUG_TARGET", null);
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);

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
        catch
        {
            // A debugging aid must never stop the app from starting.
        }
    }
}
