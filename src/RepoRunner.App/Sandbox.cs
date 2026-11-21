using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RepoRunner.Core;

namespace RepoRunner.App;

/// <summary>
/// Answers every web call a sandboxed app makes, so that none of them reaches a real server. The answer is, in order
/// of preference: what the real service said to exactly this question in a recording; what it said to the same
/// command with other inputs; or a synthetic answer that says plainly it came from the sandbox. Nothing is ever
/// passed on. It listens on loopback only, behind a path no other program knows.
/// </summary>
public sealed class SandboxServer : IAsyncDisposable
{
    /// <summary>Hosts a sandboxed app may still reach, so people can sign in. Nothing else leaves the machine.</summary>
    public static readonly string[] SignInHosts = ["login.microsoftonline.com", "login.microsoft.com", "login.windows.net", "sts.windows.net"];

    /// <param name="Source">recording (the same question) | similar (the same command, other inputs) | synthetic</param>
    /// <param name="Into">When a copy of the call was also passed to another sandboxed app on this machine: where.</param>
    public sealed record Hit(DateTime At, string Method, string Host, string Label, string Source, int Status, string? From, string? Into = null);

    /// <summary>
    /// Where to pass a copy of a call, so another app of yours, also running in the sandbox here, can be traced
    /// handling it (OpsPortal's call to OpsApi, followed inside OpsApi). Given the method and path with query; returns
    /// a full address on this machine, or null. The app still gets the stand-in's answer, never the copy's.
    /// </summary>
    public Func<string, string, Uri?>? MirrorTo { get; set; }

    // Copies only ever go to this machine, where a development certificate is the norm, so it is not checked.
    private static readonly HttpClient Mirror = new(new SocketsHttpHandler
    {
        UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    private sealed record Recorded(int Status, string? ContentType, string? Body, string From);

    private readonly WebApplication _app;
    private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private readonly Dictionary<string, List<Recorded>> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Recorded> _similar = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _served = new(StringComparer.Ordinal);
    private readonly List<Hit> _hits = [];
    private int _port;

    /// <summary>The recording the user asked to use first, if any.</summary>
    public string? Preferred { get; }
    public int Recordings { get; }

    /// <summary>Where the app sends its web calls: {Base}/{scheme}/{host:port}/{path}.</summary>
    public string Base => $"http://127.0.0.1:{_port}/{_secret}/x";

    private SandboxServer(IReadOnlyList<SavedProcess> processes, string? preferredId)
    {
        var ordered = processes.Where(p => p.Id == preferredId).Concat(processes.Where(p => p.Id != preferredId)).ToList();
        Preferred = ordered.FirstOrDefault(p => p.Id == preferredId)?.Name;
        Recordings = ordered.Count;
        foreach (var process in ordered)
        {
            var exact = new Dictionary<string, List<Recorded>>(StringComparer.Ordinal);
            foreach (var flow in process.Flows.OrderBy(f => f.FirstSeen))
                foreach (var step in flow.Steps.Values.Where(s => s.Category == "http").OrderBy(s => s.Start))
                {
                    if (!Uri.TryCreate(step.Tag("rr.url", "url.full", "http.url"), UriKind.Absolute, out var uri)) continue;
                    flow.Bodies.TryGetValue(step.Id, out var body);
                    if (body?.Response is null && step.StatusCode is null) continue;
                    var method = step.Tag("http.request.method", "http.method") ?? "GET";
                    var recorded = new Recorded(step.StatusCode ?? 200, body?.ResponseType, body?.Response, process.Name);
                    var key = ExactKey(uri.Authority, method, uri.PathAndQuery, body?.Request);
                    if (!exact.TryGetValue(key, out var list)) exact[key] = list = [];
                    list.Add(recorded);
                    // Later answers win within a recording; earlier recordings (the preferred one) win across them.
                    var similar = SimilarKey(uri.Authority, method, uri.AbsolutePath, body?.Request);
                    if (!_similar.TryGetValue(similar, out var prior) || prior.From == process.Name) _similar[similar] = recorded;
                }
            foreach (var (key, list) in exact) _exact.TryAdd(key, list);
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Map("/{secret}/x/{scheme}/{authority}/{**rest}", Answer);
    }

    private static string ExactKey(string host, string method, string pathAndQuery, string? body) =>
        $"{host.ToLowerInvariant()}|{method.ToUpperInvariant()}|{pathAndQuery}|{RequestShape.Signature(body)}";

    private static string SimilarKey(string host, string method, string path, string? body) =>
        $"{host.ToLowerInvariant()}|{method.ToUpperInvariant()}|{path}|{RequestShape.Intent(body)}";

    public static async Task<SandboxServer> StartAsync(IReadOnlyList<SavedProcess> processes, string? preferredId)
    {
        var server = new SandboxServer(processes, preferredId);
        await server._app.StartAsync();
        var address = server._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server._port = new Uri(address).Port;
        return server;
    }

    public List<Hit> Hits()
    {
        lock (_hits) return [.. _hits];
    }

    private async Task Answer(HttpContext context, string secret, string scheme, string authority, string? rest)
    {
        if (secret != _secret)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var path = "/" + (rest ?? "");
        var pathAndQuery = path + context.Request.QueryString;
        var method = context.Request.Method;
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        var intent = RequestShape.Intent(body);
        var label = intent ?? path;

        Recorded? answer = null;
        var source = "synthetic";
        var exactKey = ExactKey(authority, method, pathAndQuery, body);
        if (_exact.TryGetValue(exactKey, out var list))
        {
            // The same question asked again is answered in recorded order; after the last, the last repeats.
            lock (_served)
            {
                var at = _served.GetValueOrDefault(exactKey);
                _served[exactKey] = at + 1;
                answer = list[Math.Min(at, list.Count - 1)];
            }
            source = "recording";
        }
        else if (_similar.TryGetValue(SimilarKey(authority, method, path, body), out var similar))
        {
            answer = similar;
            source = "similar";
        }

        int status;
        if (answer is not null)
        {
            status = answer.Status;
            context.Response.StatusCode = status;
            if (answer.Body is not null)
            {
                context.Response.ContentType = answer.ContentType ?? "application/json";
                await context.Response.WriteAsync(answer.Body);
            }
        }
        else
        {
            var writes = CallSafety.Writes(method, label);
            status = writes ? 200 : 404;
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(writes
                ? new { errorCode = "0", errorDescription = "Accepted by the RepoRunner sandbox. Nothing was saved or sent.", sandbox = true }
                : new { errorCode = "100", errorDescription = "No data found. The RepoRunner sandbox has no recorded answer for this call.", sandbox = true });
        }
        string? into = null;
        if (MirrorTo?.Invoke(method, pathAndQuery) is { } copyTo && copyTo.IsLoopback)
        {
            into = copyTo.GetLeftPart(UriPartial.Authority);
            _ = PassCopy(copyTo, method, body, context.Request.ContentType, context.Request.Headers);
        }
        lock (_hits) _hits.Add(new Hit(DateTime.Now, method, authority, label, source, status, answer?.From, into));
    }

    /// <summary>
    /// Sends the copy with the caller's trace context, so the other app's trace joins this one. Only to this machine
    /// (checked by the caller); what comes back is ignored.
    /// </summary>
    private static async Task PassCopy(Uri target, string method, string body, string? contentType, IHeaderDictionary headers)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), target);
            if (body.Length > 0 || method is "POST" or "PUT" or "PATCH")
                request.Content = new StringContent(body, System.Text.Encoding.UTF8);
            if (contentType is not null && request.Content is not null)
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            foreach (var name in new[] { "traceparent", "tracestate", "Authorization", "Accept" })
                if (headers.TryGetValue(name, out var value)) request.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)value);
            using var reply = await Mirror.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
        await _app.DisposeAsync();
    }
}

/// <summary>Where on another app a sandboxed call belongs, by the routes that app's scan found.</summary>
public static class CopyRoutes
{
    /// <summary>
    /// The path and query on the other app for a call, or null when none of its endpoints takes it. The call's path may
    /// carry a prefix the other app does not (a gateway's "/OpsApis"), so its endings are tried, longest first.
    /// </summary>
    public static string? Match(ProcessMap map, string method, string pathAndQuery)
    {
        var cut = pathAndQuery.IndexOf('?');
        var (path, query) = cut < 0 ? (pathAndQuery, "") : (pathAndQuery[..cut], pathAndQuery[cut..]);
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var routes = map.Processes.Where(p => p.Kind == "endpoint" && p.Route is not null
            && (p.Verb is null || p.Verb.Equals(method, StringComparison.OrdinalIgnoreCase))).ToList();
        for (var i = 0; i < segments.Length; i++)
        {
            var candidate = "/" + string.Join('/', segments[i..]);
            if (routes.Any(p => FeatureScan.RouteRegex(p.Route!).IsMatch(candidate))) return candidate + query;
        }
        return null;
    }
}

/// <summary>
/// The operating system's own list of a process's TCP connections. The sandbox's second line: whatever the guard
/// inside the app does or misses, this sees every connection the process actually holds.
/// </summary>
public static class ConnectionWatch
{
    public sealed record Remote(IPAddress Address, int Port);

    public static List<Remote> Of(int pid)
    {
        var found = new List<Remote>();
        Read(pid, 2, 24, found);   // IPv4 rows: state, local addr, local port, remote addr, remote port, pid
        Read(pid, 23, 56, found);  // IPv6 rows: local addr(16), scope, port, remote addr(16), scope, port, state, pid
        return found;
    }

    private static void Read(int pid, int family, int rowSize, List<Remote> found)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, 5, 0) != 0) return;
            var rows = Marshal.ReadInt32(buffer);
            for (var i = 0; i < rows; i++)
            {
                var row = buffer + 4 + i * rowSize;
                int state, owner, port;
                IPAddress remote;
                if (family == 2)
                {
                    state = Marshal.ReadInt32(row);
                    remote = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row + 12)));
                    port = Port(Marshal.ReadInt32(row + 16));
                    owner = Marshal.ReadInt32(row + 20);
                }
                else
                {
                    var bytes = new byte[16];
                    Marshal.Copy(row + 24, bytes, 0, 16);
                    remote = new IPAddress(bytes);
                    port = Port(Marshal.ReadInt32(row + 44));
                    state = Marshal.ReadInt32(row + 48);
                    owner = Marshal.ReadInt32(row + 52);
                }
                const int listening = 2;
                if (owner != pid || state == listening || port == 0) continue;
                var plain = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
                if (IPAddress.IsLoopback(plain) || plain.Equals(IPAddress.Any) || plain.Equals(IPAddress.IPv6Any)) continue;
                found.Add(new Remote(plain, port));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
}
