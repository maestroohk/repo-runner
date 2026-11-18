using System.IO.Pipes;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RepoRunner.App;

public sealed class FlowStep
{
    public required string Id { get; init; }
    public string? ParentId { get; set; }
    public string Source { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTime Start { get; set; }
    public double Ms { get; set; }
    public string Status { get; set; } = "Unset";
    public string? StatusText { get; set; }
    public Dictionary<string, string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Depth { get; set; }

    public string? Tag(params string[] names) => names.Select(n => Tags.GetValueOrDefault(n)).FirstOrDefault(v => v is not null);
    public int? StatusCode => int.TryParse(Tag("http.response.status_code", "http.status_code"), out var code) ? code : null;

    /// <summary>request | http | db | step</summary>
    public string Category =>
        Kind == "Server" ? "request"
        : Tag("db.system", "db.system.name", "db.statement", "db.query.text") is not null ? "db"
        : Tag("url.full", "http.url") is not null ? "http"
        : "step";

    public string Title => Category switch
    {
        "request" => $"{Tag("http.request.method", "http.method")} {Tag("url.path", "http.target", "http.route") ?? Name}".Trim(),
        "http" => $"{Tag("http.request.method", "http.method")} {Tag("url.full", "http.url")}".Trim(),
        "db" => Tag("db.statement", "db.query.text", "db.operation", "db.operation.name") ?? Name,
        _ => Name,
    };

    public bool Failed => Status == "Error" || StatusCode >= 500 || (Category == "http" && StatusCode >= 400);
}

public sealed record FlowNote(DateTime At, string? SpanId, string Label, string? Title, string Body);

public sealed record FlowFrame(string Method, string? File, int Line);

public sealed class FlowError
{
    public required DateTime At { get; init; }
    public required string Type { get; init; }
    public required string Message { get; init; }
    public string? SpanId { get; init; }
    public bool Unhandled { get; set; }
    public long Key { get; init; }
    public List<FlowFrame> Frames { get; init; } = [];
    public FlowFrame? Origin => Frames.FirstOrDefault(f => f.File is not null);
}

/// <summary>Everything observed for one trace: usually one incoming request and what it caused.</summary>
public sealed class Flow
{
    public required string TraceId { get; init; }
    public DateTime FirstSeen { get; init; } = DateTime.UtcNow;
    public Dictionary<string, FlowStep> Steps { get; } = [];
    public List<FlowNote> Notes { get; } = [];
    public List<FlowError> Errors { get; } = [];

    public FlowStep? Root => Steps.Values.Where(s => s.ParentId is null || !Steps.ContainsKey(s.ParentId)).OrderBy(s => s.Start).FirstOrDefault();

    public string Title =>
        Root is { } root ? (root.Title.Length > 0 ? root.Title : root.Name)
        : Errors.Count > 0 ? Errors[0].Type.Split('.').Last()
        : "Activity outside a request";

    public double Ms => Root?.Ms ?? 0;
    public int? StatusCode => Root?.StatusCode;

    /// <summary>failed | warn | ok</summary>
    public string Outcome =>
        Errors.Any(e => e.Unhandled) || Steps.Values.Any(s => s.Failed) ? "failed"
        : Errors.Count > 0 || StatusCode >= 400 ? "warn"
        : "ok";

    /// <summary>Steps in call order, each with its nesting depth.</summary>
    public List<FlowStep> Ordered()
    {
        var result = new List<FlowStep>();
        var children = Steps.Values.Where(s => s.ParentId is not null && Steps.ContainsKey(s.ParentId)).ToLookup(s => s.ParentId!);
        void Visit(FlowStep step, int depth)
        {
            step.Depth = depth;
            result.Add(step);
            foreach (var child in children[step.Id].OrderBy(c => c.Start)) Visit(child, depth + 1);
        }
        foreach (var top in Steps.Values.Where(s => s.ParentId is null || !Steps.ContainsKey(s.ParentId)).OrderBy(s => s.Start)) Visit(top, 0);
        return result;
    }
}

/// <summary>
/// Receives what the startup hook observes inside a running app, over a named pipe that only the current Windows
/// user can open, and keeps the most recent flows in memory. Nothing is stored on disk.
/// </summary>
public sealed partial class FlowHub : IDisposable
{
    private const int MaxFlows = 300;
    private const string Loose = "outside-any-request";

    private readonly Dictionary<string, Flow> _flows = [];
    private readonly List<string> _secrets;
    private readonly Action _changed;
    private readonly CancellationTokenSource _stop = new();

    public string PipeName { get; } = "rr-flow-" + Guid.NewGuid().ToString("N");
    public bool Connected { get; private set; }

    public FlowHub(IEnumerable<string> secretValues, Action changed)
    {
        _secrets = secretValues.Where(v => v.Length >= 6).Distinct().OrderByDescending(v => v.Length).ToList();
        _changed = changed;
        _ = Task.Run(Listen);
    }

    public List<Flow> Snapshot()
    {
        lock (_flows) return _flows.Values.OrderByDescending(f => f.FirstSeen).ToList();
    }

    public void Clear()
    {
        lock (_flows) _flows.Clear();
        _changed();
    }

    public void Dispose() => _stop.Cancel();

    private async Task Listen()
    {
        // The app reconnects after a restart (dotnet watch), so keep accepting until the session ends.
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                Connected = true;
                using var reader = new StreamReader(pipe);
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    try { Handle(line); }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { }
            finally { Connected = false; }
        }
    }

    private void Handle(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var e = doc.RootElement;
        string? Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Mask(v.GetString()!) : null;
        var type = Text("type");
        if (type is null or "hello") return;

        lock (_flows)
        {
            // Exceptions outside any request are unrelated to each other; give each its own entry.
            var traceId = Text("trace")
                ?? (type == "exception" && e.TryGetProperty("key", out var loose) ? $"exception-{loose.GetInt64()}" : Loose);
            if (!_flows.TryGetValue(traceId, out var flow))
            {
                if (_flows.Count >= MaxFlows)
                    _flows.Remove(_flows.Values.OrderBy(f => f.FirstSeen).First().TraceId);
                _flows[traceId] = flow = new Flow { TraceId = traceId };
            }

            switch (type)
            {
                case "span":
                {
                    // DNS, sockets and TLS handshakes are below the level a feature is debugged at.
                    if (Text("source") is { } origin && (origin.StartsWith("Experimental.") || origin.StartsWith("System.Net.NameResolution")
                        || origin.StartsWith("System.Net.Sockets") || origin.StartsWith("System.Net.Security")))
                    {
                        if (flow.Steps.Count == 0 && flow.Notes.Count == 0 && flow.Errors.Count == 0) _flows.Remove(traceId);
                        return;
                    }
                    var id = Text("id") ?? Guid.NewGuid().ToString("N");
                    if (!flow.Steps.TryGetValue(id, out var step)) flow.Steps[id] = step = new FlowStep { Id = id };
                    step.ParentId = Text("parent") ?? step.ParentId;
                    step.Source = Text("source") ?? step.Source;
                    if (Text("name") is { } name && (step.Name.Length == 0 || !name.StartsWith("Microsoft.AspNetCore.Hosting"))) step.Name = name;
                    // The same operation can be reported twice; "Server" or "Client" says more than "Internal".
                    if (Text("kind") is { } kind && (kind != "Internal" || step.Kind.Length == 0)) step.Kind = kind;
                    step.Start = e.GetProperty("start").GetDateTime();
                    step.Ms = e.GetProperty("ms").GetDouble();
                    if (Text("status") is { } status && status != "Unset") step.Status = status;
                    step.StatusText = Text("statusText") ?? step.StatusText;
                    if (e.TryGetProperty("tags", out var tags))
                        foreach (var tag in tags.EnumerateObject())
                            if (tag.Value.ValueKind == JsonValueKind.String) step.Tags[tag.Name] = Mask(tag.Value.GetString()!);
                    break;
                }
                case "data":
                    flow.Notes.Add(new FlowNote(e.GetProperty("at").GetDateTime(), Text("id"), Text("label") ?? "Data", Text("title"),
                        Redact(Text("body") ?? "")));
                    break;
                case "exception":
                {
                    var key = e.TryGetProperty("key", out var k) ? k.GetInt64() : 0;
                    var unhandled = e.TryGetProperty("unhandled", out var u) && u.GetBoolean();
                    var known = flow.Errors.FirstOrDefault(x => x.Key == key && key != 0);
                    if (known is not null) { known.Unhandled |= unhandled; break; }
                    var error = new FlowError
                    {
                        At = e.GetProperty("at").GetDateTime(),
                        Type = Text("exType") ?? "Exception",
                        Message = Text("message") ?? "",
                        SpanId = Text("id"),
                        Unhandled = unhandled,
                        Key = key,
                    };
                    if (e.TryGetProperty("frames", out var frames))
                        foreach (var f in frames.EnumerateArray())
                            error.Frames.Add(new FlowFrame(f.GetProperty("m").GetString() ?? "",
                                f.TryGetProperty("f", out var file) ? file.GetString() : null,
                                f.TryGetProperty("l", out var l) ? l.GetInt32() : 0));
                    flow.Errors.Add(error);
                    break;
                }
            }
        }
        _changed();
    }

    /// <summary>Vault secrets never reach the screen, whatever part of the app's data they turn up in.</summary>
    private string Mask(string text)
    {
        foreach (var secret in _secrets)
            if (text.Contains(secret, StringComparison.Ordinal)) text = text.Replace(secret, "****");
        return text;
    }

    /// <summary>Captured objects can hold what a user typed; blank the fields that are secret by name.</summary>
    private static string Redact(string json) => SensitiveField().Replace(json, m => $"{m.Groups[1].Value}\"••••\"");

    [GeneratedRegex(@"(""[^""]*(?:password|passwd|pwd|pin|secret|token|otp|apikey|api_key|authorization)[^""]*""\s*:\s*)""(?:[^""\\]|\\.)*""", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveField();
}
