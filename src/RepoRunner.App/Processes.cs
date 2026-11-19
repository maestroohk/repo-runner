using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RepoRunner.Core;

namespace RepoRunner.App;

/// <summary>A business process as it was once performed: every step, call, payload and exception, in order.</summary>
public sealed record SavedProcess(string Id, string Name, string Env, DateTime Created, List<Flow> Flows);

/// <summary>
/// Keeps recorded processes beside the vault. They hold real request and response data, so each file is
/// encrypted with the vault key and is unreadable outside RepoRunner.
/// </summary>
public sealed class ProcessStore(VaultService vaults)
{
    private const string Purpose = "process-recording";

    private static readonly JsonSerializerOptions Json = new()
    {
        // Flows hold their steps in read-only collections; fill those rather than replace them.
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    private string Folder(RepoInfo repo) =>
        Path.Combine(Path.GetDirectoryName(vaults.VaultFile)!, "processes", repo.Id.ToString());

    public SavedProcess Save(RepoInfo repo, string name, string env, List<Flow> flows)
    {
        var process = new SavedProcess(Guid.NewGuid().ToString("N"), name, env, DateTime.Now, flows);
        var sealedBytes = vaults.Use(v => v.Protect(JsonSerializer.SerializeToUtf8Bytes(process, Json), Purpose));
        Directory.CreateDirectory(Folder(repo));
        File.WriteAllBytes(Path.Combine(Folder(repo), process.Id + ".rrp"), sealedBytes);
        vaults.Use(v => v.Audit("process-save", repo.Name, env, name));
        return process;
    }

    public List<SavedProcess> List(RepoInfo repo)
    {
        var result = new List<SavedProcess>();
        if (!Directory.Exists(Folder(repo))) return result;
        foreach (var file in Directory.EnumerateFiles(Folder(repo), "*.rrp"))
        {
            try
            {
                var json = vaults.Use(v => v.Unprotect(File.ReadAllBytes(file), Purpose));
                if (JsonSerializer.Deserialize<SavedProcess>(json, Json) is { } process) result.Add(process);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException) { /* unreadable: skip */ }
        }
        return result.OrderByDescending(p => p.Created).ToList();
    }

    public void Delete(RepoInfo repo, SavedProcess process)
    {
        var file = Path.Combine(Folder(repo), process.Id + ".rrp");
        if (File.Exists(file)) File.Delete(file);
        vaults.Use(v => v.Audit("process-delete", repo.Name, process.Env, process.Name));
    }
}

/// <summary>
/// Stands in for the services an app depends on, answering each call with what the real service answered when
/// the process was recorded. The app is pointed at it through its own address variables, so nothing in the app
/// changes. It listens on loopback only, behind a path no other program knows.
/// </summary>
public sealed class MockServer : IAsyncDisposable
{
    public sealed record Hit(DateTime At, string Method, string Host, string PathAndQuery, bool Matched, int Status);

    private sealed record Recorded(int Status, string? ContentType, string? Body);

    private readonly WebApplication _app;
    private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private readonly List<string> _hosts = [];
    private readonly Dictionary<string, List<Recorded>> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Recorded>> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _served = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Hit> _hits = [];
    private readonly Action _changed;
    private int _port;

    public string ProcessName { get; }
    public IReadOnlyList<string> Hosts => _hosts;
    public int Recordings { get; private set; }

    private MockServer(SavedProcess process, Action changed)
    {
        ProcessName = process.Name;
        _changed = changed;
        foreach (var flow in process.Flows.OrderBy(f => f.FirstSeen))
            foreach (var step in flow.Steps.Values.Where(s => s.Category == "http").OrderBy(s => s.Start))
            {
                if (!Uri.TryCreate(step.Tag("rr.url", "url.full", "http.url"), UriKind.Absolute, out var uri)) continue;
                var host = uri.Authority.ToLowerInvariant();
                if (!_hosts.Contains(host)) _hosts.Add(host);
                var method = step.Tag("http.request.method", "http.method") ?? "GET";
                flow.Bodies.TryGetValue(step.Id, out var body);
                var recorded = new Recorded(step.StatusCode ?? 200, body?.ResponseType, body?.Response);
                Add(_exact, $"{host}|{method}|{uri.PathAndQuery}", recorded);
                Add(_byPath, $"{host}|{method}|{uri.AbsolutePath}", recorded);
                Recordings++;
            }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Map("/{secret}/{index:int}/{**rest}", Answer);
    }

    private static void Add(Dictionary<string, List<Recorded>> map, string key, Recorded recorded)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(recorded);
    }

    public static async Task<MockServer> StartAsync(SavedProcess process, Action changed)
    {
        var server = new MockServer(process, changed);
        await server._app.StartAsync();
        var address = server._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server._port = new Uri(address).Port;
        return server;
    }

    /// <summary>The address to give the app in place of a real host.</summary>
    public string BaseFor(string host) => $"http://127.0.0.1:{_port}/{_secret}/{_hosts.IndexOf(host.ToLowerInvariant())}";

    public List<Hit> Hits()
    {
        lock (_hits) return [.. _hits];
    }

    private async Task Answer(HttpContext context, string secret, int index, string? rest)
    {
        if (secret != _secret || index < 0 || index >= _hosts.Count)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var host = _hosts[index];
        var path = "/" + (rest ?? "");
        var pathAndQuery = path + context.Request.QueryString;
        var method = context.Request.Method;

        // The same call made several times is answered in the order it was recorded; after the last, the last repeats.
        Recorded? Next(Dictionary<string, List<Recorded>> map, string key)
        {
            if (!map.TryGetValue(key, out var list)) return null;
            lock (_served)
            {
                var at = _served.GetValueOrDefault(key);
                _served[key] = at + 1;
                return list[Math.Min(at, list.Count - 1)];
            }
        }
        var recorded = Next(_exact, $"{host}|{method}|{pathAndQuery}") ?? Next(_byPath, $"{host}|{method}|{path}");

        lock (_hits) _hits.Add(new Hit(DateTime.Now, method, host, pathAndQuery, recorded is not null, recorded?.Status ?? 501));
        _changed();

        if (recorded is null)
        {
            // The app asked for something the recorded run never asked for: that is where the two runs part ways.
            context.Response.StatusCode = StatusCodes.Status501NotImplemented;
            await context.Response.WriteAsJsonAsync(new { error = "RepoRunner replay: this call was not in the recording.", method, host, path = pathAndQuery });
            return;
        }
        context.Response.StatusCode = recorded.Status;
        if (recorded.Body is null) return;
        context.Response.ContentType = recorded.ContentType ?? "application/json";
        await context.Response.WriteAsync(recorded.Body);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
        await _app.DisposeAsync();
    }
}
