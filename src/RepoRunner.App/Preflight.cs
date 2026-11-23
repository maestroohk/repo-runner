using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using RepoRunner.Core;

namespace RepoRunner.App;

/// <summary>What was clicked on a process map: a procedure, a system, the settings, the page or the person.</summary>
public sealed record MapPick(string Kind, string Name);

/// <summary>
/// "Before you run": the other systems an app's settings point at, and whether each is answering right now.
/// Only a connection is opened and closed; nothing is sent. Never used on a protected environment.
/// </summary>
public static partial class Preflight
{
    /// <param name="Names">The settings that point at this system.</param>
    public sealed record Target(string Names, string Host, int Port);

    public sealed record Result(string Names, string Host, int Port, bool Up, int Ms);

    /// <summary>Every distinct host and port named by a setting: web addresses, "host:port" values and Server=…;Port=… strings.</summary>
    public static List<Target> TargetsOf(IEnumerable<Variable> variables)
    {
        var found = new List<(string Name, string Host, int Port)>();
        foreach (var variable in variables)
        {
            var value = variable.Value.Trim().Trim('"');
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0)
                found.Add((variable.Name, uri.Host, uri.Port));
            else if (HostPort().Match(value) is { Success: true } plain)
                found.Add((variable.Name, plain.Groups[1].Value, int.Parse(plain.Groups[2].Value)));
            else if (ServerSetting().Match(value) is { Success: true } server)
                found.Add((variable.Name, server.Groups[1].Value,
                    PortSetting().Match(value) is { Success: true } port ? int.Parse(port.Groups[1].Value) : 0));
        }
        return found
            .Where(t => t.Port is > 0 and < 65536 && !IsThisMachine(t.Host))
            .GroupBy(t => (t.Host.ToLowerInvariant(), t.Port))
            .Select(g => new Target(string.Join(", ", g.Select(t => t.Name).Distinct()), g.First().Host, g.Key.Port))
            .OrderBy(t => t.Names, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<List<Result>> Check(IReadOnlyList<Target> targets, int timeoutMs = 2500)
    {
        var checks = targets.Select(async target =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                using var cancel = new CancellationTokenSource(timeoutMs);
                await client.ConnectAsync(target.Host, target.Port, cancel.Token);
                return new Result(target.Names, target.Host, target.Port, true, (int)clock.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
            {
                return new Result(target.Names, target.Host, target.Port, false, (int)clock.ElapsedMilliseconds);
            }
        });
        return [.. await Task.WhenAll(checks)];
    }

    private static bool IsThisMachine(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);

    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9.-]*):(\d{2,5})$")]
    private static partial Regex HostPort();

    [GeneratedRegex(@"(?:Server|Data Source|Host)\s*=\s*([A-Za-z0-9][A-Za-z0-9.-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ServerSetting();

    [GeneratedRegex(@"Port\s*=\s*(\d{2,5})", RegexOptions.IgnoreCase)]
    private static partial Regex PortSetting();
}
