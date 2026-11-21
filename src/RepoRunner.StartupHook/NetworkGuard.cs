using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net;
using System.Net.Sockets;

/// <summary>Thrown inside the app when the sandbox refuses a connection. The app sees it as the cause of its own error.</summary>
internal sealed class SandboxBlockedException(string message) : InvalidOperationException(message);

/// <summary>
/// The sandbox's guarantee. From .NET 9 the runtime starts an activity for every outgoing TCP connection just before it
/// is made, and throwing from that activity's start stops the connection before a single packet is sent.
/// The decision rests on the host the connection is for, which HttpClient names on the connection-setup activity that
/// wraps it: this machine (RepoRunner's stand-in) and the named sign-in hosts are allowed. A connection with no such
/// host, such as a database, cache or message broker opening its own socket, is refused.
/// (The address of a connection is announced only after the activity starts, so it cannot be used for the decision.)
/// </summary>
internal static class NetworkGuard
{
    [ThreadStatic] private static bool _selfTest;

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase);
    private static Action<string, string?, bool>? _report;
    private static ActivityListener? _activities;

    /// <summary>Arms the guard and proves it works in this process. False means the app must not run.</summary>
    public static bool Arm(IEnumerable<string> allowedHosts, Action<string, string?, bool> report)
    {
        foreach (var host in allowedHosts) if (host.Trim().Length > 0) Allowed.Add(host.Trim());
        _report = report;
        if (Environment.Version.Major < 9) return false; // older runtimes do not announce connections

        _activities = new ActivityListener
        {
            // The connection-setup source must be listened to, or HttpClient does not create the activity that names the host.
            ShouldListenTo = source => source.Name is "Experimental.System.Net.Sockets" or "Experimental.System.Net.Http.Connections",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = Check,
            ActivityStopped = Reported,
        };
        ActivitySource.AddActivityListener(_activities);
        return SelfTest();
    }

    /// <summary>Opens a raw socket to an address reserved for documentation (it leads nowhere). Only a refusal by the guard passes.</summary>
    private static bool SelfTest()
    {
        _selfTest = true;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { Blocking = false };
            socket.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
            return false;
        }
        catch (SandboxBlockedException) { return true; }
        catch { return false; }
        finally { _selfTest = false; }
    }

    private static void Check(Activity activity)
    {
        if (activity.Source.Name != "Experimental.System.Net.Sockets") return;

        string? host = null;
        for (var parent = activity.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.OperationName.IndexOf("ConnectionSetup", StringComparison.Ordinal) < 0) continue;
            host = parent.GetTagItem("server.address") as string;
            break;
        }

        if (host is not null && IsThisMachine(host)) return;
        if (host is not null && Allowed.Contains(host))
        {
            activity.SetTag("rr.allowed", host); // reported once connected, with the address the watchdog will see
            return;
        }
        var shown = host ?? "a direct connection (a database, cache or similar)";
        if (!_selfTest) _report?.Invoke(shown, host, false);
        throw new SandboxBlockedException(
            $"RepoRunner sandbox: the connection to {shown} was refused before it was made. Nothing left this machine.");
    }

    /// <summary>Reports an allowed sign-in connection with the address it reached, which the runtime fills in by now.</summary>
    private static void Reported(Activity activity)
    {
        if (activity.GetTagItem("rr.allowed") is not string host) return;
        var address = activity.GetTagItem("network.peer.address")?.ToString();
        _report?.Invoke(address ?? host, host, true);
    }

    private static bool IsThisMachine(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var address)
            && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }
}
