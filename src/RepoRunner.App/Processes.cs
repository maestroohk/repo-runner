using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RepoRunner.Core;

namespace RepoRunner.App;

/// <summary>A business process as it was once performed: every step, call, payload and exception, in order.</summary>
/// <param name="Kind">recording (saved by someone) | failure (kept automatically when a step failed)</param>
public sealed record SavedProcess(string Id, string Name, string Env, DateTime Created, List<Flow> Flows, string Kind = "recording", string? Reason = null)
{
    public bool IsFailure => Kind == "failure";
}

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

    private string Folder(RepoInfo repo) => vaults.Use(v => v.RecordingsFolder(repo));

    public SavedProcess Save(RepoInfo repo, string name, string env, List<Flow> flows, string kind = "recording", string? reason = null)
    {
        var process = new SavedProcess(Guid.NewGuid().ToString("N"), name, env, DateTime.Now, flows, kind, reason);
        var sealedBytes = vaults.Use(v => v.Protect(JsonSerializer.SerializeToUtf8Bytes(process, Json), Purpose));
        Directory.CreateDirectory(Folder(repo));
        File.WriteAllBytes(Path.Combine(Folder(repo), process.Id + ".rrp"), sealedBytes);
        vaults.Use(v => v.Audit(kind == "failure" ? "failure-kept" : "process-save", repo.Name, env, name));
        return process;
    }

    /// <summary>Deletes the oldest records of one kind beyond the newest <paramref name="keep"/>. Other kinds are untouched.</summary>
    public void Prune(RepoInfo repo, string kind, int keep)
    {
        foreach (var old in List(repo).Where(p => p.Kind == kind).Skip(keep)) Delete(repo, old);
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

/// <summary>What a request is asking for, reduced to a form two runs can be compared by.</summary>
public static partial class RequestShape
{
    private static readonly string[] IntentFields =
        ["targetCommand", "command", "commandName", "operation", "operationName", "action", "procedure", "storedProcedure", "queryName", "method"];

    /// <summary>The command a request names in its body, when it has one.</summary>
    public static string? Intent(string? body)
    {
        if (body is null || body.Length > 200_000 || !body.TrimStart().StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var property in doc.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String && IntentFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                    && property.Value.GetString() is { Length: > 0 and < 80 } name)
                    return name;
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>
    /// A body as its fields in a fixed order, with values that change on every run (dates, times, generated
    /// identifiers) blanked. Two requests that ask the same thing have the same signature.
    /// </summary>
    public static string Signature(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var text = body.Trim();
        if (text[0] is '{' or '[' && text.Length < 2_000_000)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
                Flatten(doc.RootElement, "", fields, 0);
                return string.Join("\n", fields.Select(f => $"{f.Key}={(ChangesEveryRun().IsMatch(f.Value.Trim()) ? "~" : f.Value)}"));
            }
            catch (JsonException) { }
        }
        return text;
    }

    private static void Flatten(JsonElement element, string path, IDictionary<string, string> into, int depth)
    {
        if (into.Count >= 2000 || depth > 10) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Flatten(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", into, depth + 1);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray()) Flatten(item, $"{path}[{index++}]", into, depth + 1);
                break;
            default:
                into[path] = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
                break;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}.*)?|\d{1,2}:\d{2}:\d{2}.*|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$")]
    private static partial System.Text.RegularExpressions.Regex ChangesEveryRun();
}

/// <summary>
/// Stands in for the services an app depends on. A request that asks exactly what a recorded request asked is
/// answered with what the real service said then; anything else is passed on to the real service, so the app
/// still signs in and loads whatever the recording did not cover. It listens on loopback only, behind a path
/// no other program knows.
/// </summary>
public sealed class MockServer : IAsyncDisposable
{
    /// <param name="Source">recording | real | failed</param>
    public sealed record Hit(DateTime At, string Method, string Host, string Label, string Source, int Status);

    private sealed record Recorded(int Status, string? ContentType, string? Body);

    private readonly WebApplication _app;
    private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private readonly List<string> _hosts = [];
    private readonly Dictionary<string, string> _schemes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Recorded>> _recorded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _served = new(StringComparer.Ordinal);
    private readonly List<Hit> _hits = [];
    private readonly Action _changed;
    private int _port;

    // Unrecorded calls go on to the service the app was already talking to. Those are internal development
    // hosts, often addressed by IP with a certificate that cannot match, so the certificate is not checked here.
    private readonly HttpClient _real = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        AllowAutoRedirect = false,
    }) { Timeout = TimeSpan.FromSeconds(120) };

    public string ProcessName { get; }
    public IReadOnlyList<string> Hosts => _hosts;
    public int Recordings { get; private set; }
    /// <summary>When false, a call the recording does not hold is refused instead of reaching the real service.</summary>
    public bool PassThrough { get; set; } = true;

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
                _schemes[host] = uri.Scheme;
                flow.Bodies.TryGetValue(step.Id, out var body);
                // Without the recorded answer there is nothing to play back; let such a call reach the real service.
                if (body?.Response is null && step.StatusCode is null) continue;
                var key = Key(host, step.Tag("http.request.method", "http.method") ?? "GET", uri.PathAndQuery, body?.Request);
                if (!_recorded.TryGetValue(key, out var list)) _recorded[key] = list = [];
                list.Add(new Recorded(step.StatusCode ?? 200, body?.ResponseType, body?.Response));
                Recordings++;
            }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Map("/{secret}/{index:int}/{**rest}", Answer);
    }

    /// <summary>Two requests share a key when they go to the same place and ask the same thing.</summary>
    private static string Key(string host, string method, string pathAndQuery, string? body) =>
        $"{host}|{method.ToUpperInvariant()}|{pathAndQuery}|{RequestShape.Signature(body)}";

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
        var pathAndQuery = "/" + (rest ?? "") + context.Request.QueryString;
        var method = context.Request.Method;
        using var reader = new StreamReader(context.Request.Body);
        var requestBody = await reader.ReadToEndAsync();
        var label = RequestShape.Intent(requestBody) ?? pathAndQuery;

        Recorded? recorded = null;
        var key = Key(host, method, pathAndQuery, requestBody);
        if (_recorded.TryGetValue(key, out var list))
        {
            // The same question asked several times is answered in the order it was recorded; then the last repeats.
            lock (_served)
            {
                var at = _served.GetValueOrDefault(key);
                _served[key] = at + 1;
                recorded = list[Math.Min(at, list.Count - 1)];
            }
        }

        if (recorded is not null)
        {
            Note(new Hit(DateTime.Now, method, host, label, "recording", recorded.Status));
            context.Response.StatusCode = recorded.Status;
            if (recorded.Body is null) return;
            context.Response.ContentType = recorded.ContentType ?? "application/json";
            await context.Response.WriteAsync(recorded.Body);
            return;
        }

        if (!PassThrough)
        {
            Note(new Hit(DateTime.Now, method, host, label, "failed", 501));
            context.Response.StatusCode = StatusCodes.Status501NotImplemented;
            await context.Response.WriteAsJsonAsync(new { error = "RepoRunner replay: this call was not in the recording.", method, host, path = pathAndQuery });
            return;
        }

        try
        {
            using var forward = new HttpRequestMessage(new HttpMethod(method), $"{_schemes[host]}://{host}{pathAndQuery}");
            if (requestBody.Length > 0 || context.Request.ContentType is not null)
            {
                forward.Content = new StringContent(requestBody);
                forward.Content.Headers.Remove("Content-Type");
                if (context.Request.ContentType is { } type) forward.Content.Headers.TryAddWithoutValidation("Content-Type", type);
            }
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
                    || header.Key is "Host" or "Connection" or "Transfer-Encoding" or "Accept-Encoding") continue;
                forward.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            using var reply = await _real.SendAsync(forward, context.RequestAborted);
            var bytes = await reply.Content.ReadAsByteArrayAsync(context.RequestAborted);
            Note(new Hit(DateTime.Now, method, host, label, "real", (int)reply.StatusCode));
            context.Response.StatusCode = (int)reply.StatusCode;
            if (reply.Content.Headers.ContentType is { } contentType) context.Response.ContentType = contentType.ToString();
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Note(new Hit(DateTime.Now, method, host, label, "failed", 502));
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new { error = "RepoRunner replay: could not reach the real service for a call that was not in the recording.", detail = ex.Message });
        }
    }

    private void Note(Hit hit)
    {
        lock (_hits) _hits.Add(hit);
        _changed();
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
        await _app.DisposeAsync();
        _real.Dispose();
    }
}
