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
public sealed partial class SandboxServer : IAsyncDisposable
{
    /// <summary>Hosts a sandboxed app may still reach, so people can sign in. Nothing else leaves the machine.</summary>
    public static readonly string[] SignInHosts = ["login.microsoftonline.com", "login.microsoft.com", "login.windows.net", "sts.windows.net"];

    /// <param name="Source">read (a lookup answered by the real server) | recording (the same question) | similar (the same command, other inputs) | synthetic</param>
    /// <param name="Into">When a copy of the call was also passed to another sandboxed app on this machine: where.</param>
    /// <param name="Key">Which kind of question it was; a made-up answer is edited and kept under it.</param>
    public sealed record Hit(DateTime At, string Method, string Host, string Label, string Source, int Status, string? From, string? Into = null, string? Problem = null, string? Key = null);

    /// <summary>
    /// The app's code, once read: it says what each call expects back, so a lookup nobody can answer gets a made-up
    /// answer of the right shape instead of "nothing found". Set a little after the sandbox starts.
    /// </summary>
    public CodeIndex? Code
    {
        get => _code;
        set
        {
            _code = value;
            if (value is null) return;
            lock (_pool) foreach (var (name, constant) in value.Constants) Remember(name, constant);
        }
    }
    private CodeIndex? _code;
    private Task<CodeIndex>? _reading;

    /// <summary>Hands over the reading of the app's code; calls that need it wait for it rather than go without.</summary>
    public void ReadCodeWith(Task<CodeIndex> reading)
    {
        _reading = reading;
        _ = reading.ContinueWith(t => { if (t.IsCompletedSuccessfully) Code = t.Result; }, TaskScheduler.Default);
    }

    /// <summary>Waits (up to half a minute) for the app's code to be read, so an early question still gets a made-up answer.</summary>
    private async Task WaitForCode()
    {
        if (_code is not null || _reading is null) return;
        await Task.WhenAny(_reading, Task.Delay(TimeSpan.FromSeconds(30)));
        if (_code is null && _reading.IsCompletedSuccessfully) Code = _reading.Result;
    }

    /// <summary>Values seen for each field name, in recordings and in the code: what made-up answers are filled with.</summary>
    private readonly Dictionary<string, List<string>> _pool = new(StringComparer.Ordinal);
    /// <summary>Recorded rows by an identifying field and value: ("resourcecode", "RCU.DASHBOARD") → every field of that row.</summary>
    private readonly Dictionary<(string, string), Dictionary<string, string>> _rows = [];
    private readonly Dictionary<string, string> _madeUp = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Taught> _taught = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _downUntil = new(StringComparer.OrdinalIgnoreCase);
    private string? _dataFile;

    /// <summary>An answer someone wrote or corrected for a kind of question, kept for every later run.</summary>
    public sealed record Taught(string Method, string Host, string Label, string Body);

    /// <summary>
    /// Where to pass a copy of a call, so another app of yours, also running in the sandbox here, can be traced
    /// handling it (OpsPortal's call to OpsApi, followed inside OpsApi). Given the method and path with query; returns
    /// a full address on this machine, or null. The app still gets the stand-in's answer, never the copy's.
    /// </summary>
    public Func<string, string, Uri?>? MirrorTo { get; set; }

    /// <summary>
    /// When true, a call whose name says it only looks something up (Get, Search, List...) is sent to the real server
    /// and its answer passed back, so the app has its reference data and your access. Anything that may save,
    /// approve, send or delete, or whose name does not say, never leaves this machine.
    /// </summary>
    public bool ReadThrough { get; set; }

    /// <summary>True when an answer says it failed: an errorCode other than 0 (OpsApi's "100, nothing found" included).</summary>
    private static bool ReportsError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart() is not ['{', ..]) return false;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject;
            var code = node?.FirstOrDefault(p => p.Key.Equals("errorCode", StringComparison.OrdinalIgnoreCase)).Value?.ToString();
            return code is not null && code is not ("0" or "00" or "000" or "");
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static readonly HttpClient Real = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Connection", "Content-Length", "Transfer-Encoding", "Keep-Alive", "Upgrade", "Proxy-Connection", "Expect" };

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
                    // A recorded failure is not worth replaying: a made-up answer of the right shape serves the app better.
                    if (step.StatusCode >= 400 || ReportsError(body?.Response)) continue;
                    var method = step.Tag("http.request.method", "http.method") ?? "GET";
                    var recorded = new Recorded(step.StatusCode ?? 200, body?.ResponseType, body?.Response, process.Name);
                    lock (_pool) { Collect(body?.Request); Collect(body?.Response); }
                    var key = ExactKey(uri.Authority, method, uri.PathAndQuery, body?.Request);
                    if (!exact.TryGetValue(key, out var list)) exact[key] = list = [];
                    list.Add(recorded);
                    // Later answers win within a recording; earlier recordings (the preferred one) win across them.
                    var similar = SimilarKey(uri.Authority, method, uri.AbsolutePath + uri.Query, body?.Request);
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

    /// <summary>
    /// What makes two calls "the same kind of question": host, method, path, the command in the body, and the query
    /// values that choose what is asked (target=Employers) rather than about whom (targetId=NS0001).
    /// </summary>
    private static string SimilarKey(string host, string method, string pathAndQuery, string? body)
    {
        var cut = pathAndQuery.IndexOf('?');
        var path = cut < 0 ? pathAndQuery : pathAndQuery[..cut];
        var chosen = cut < 0 ? "" : string.Join("&", pathAndQuery[(cut + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(kv => CommandParameter().IsMatch(Uri.UnescapeDataString(kv[0])))
            .Select(kv => Uri.UnescapeDataString(kv[0]).ToLowerInvariant() + "=" + (kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : ""))
            .Order(StringComparer.Ordinal));
        return $"{host.ToLowerInvariant()}|{method.ToUpperInvariant()}|{path}|{chosen}|{RequestShape.Intent(body)}";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Permission|Role|Access|Grant|Resource|Capabilit|Scope|Privilege|Entitle", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AccessLookup();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(target|command|targetCommand|action|method|type|kind|procedure|sp|query|report|operation)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CommandParameter();

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
        string? problem = null;
        Recorded? real = null;
        // Your own app running in the sandbox here (a local OpsApi) answers first: it is fenced in too, so
        // whatever it is asked, changes included, goes no further than this machine.
        string? into = null;
        var kind = SimilarKey(authority, method, pathAndQuery, body);
        var reads = !CallSafety.Writes(method, label);
        Taught? taught;
        lock (_taught) _taught.TryGetValue(kind, out taught);
        if (taught is not null)
        {
            real = new Recorded(200, "application/json", taught.Body, "your sandbox data");
            source = "taught";
        }
        if (real is null && MirrorTo?.Invoke(method, pathAndQuery) is { } local && local.IsLoopback)
        {
            into = local.GetLeftPart(UriPartial.Authority);
            (real, problem) = await AskLocal(local, method, body, context.Request);
            // A local app that fails (it cannot reach its database in the sandbox) has not answered.
            if (real is { Status: >= 500 }) { problem = $"your app running here failed ({real.Status})"; real = null; }
            if (real is not null) source = "local";
        }
        if (real is null && ReadThrough && reads && !IsDown(authority))
        {
            (real, problem) = await ReadReal(scheme, authority, pathAndQuery, method, body, context.Request);
            if (real is null) MarkDown(authority);
            else source = "read";
        }
        // Who may do what: in the sandbox you may do everything. A recording of someone else's access would lock you
        // out, so access lookups get a made-up answer granting every resource the code knows (the real server, when
        // it answers, still wins above).
        if (real is null && reads && AccessLookup().IsMatch(label))
        {
            await WaitForCode();
            if (MadeUp(kind, method, path, context.Request.Query["target"].FirstOrDefault(), intent, body) is { } open)
            {
                real = new Recorded(200, "application/json", open, "made up: in the sandbox you have every permission");
                source = "madeup";
            }
            else problem = Code is null ? "the app's code is still being read" : Code.LastSampleError ?? $"no call in the code names {label}";
        }
        if (real is not null)
        {
            answer = real;
        }
        else if (_exact.TryGetValue(exactKey, out var list))
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
        else if (_similar.TryGetValue(kind, out var similar))
        {
            answer = similar;
            source = "similar";
        }
        else if (await WaitForCode().ContinueWith(_ => MadeUp(kind, method, path, context.Request.Query["target"].FirstOrDefault(), intent, body)) is { } invented)
        {
            // A change is still not sent anywhere; it just gets an answer the app can read, like a lookup does.
            answer = new Recorded(200, "application/json", invented, reads ? "made up from the code" : "held back; answer made up from the code");
            source = reads ? "madeup" : "heldmadeup";
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
        lock (_hits) _hits.Add(new Hit(DateTime.Now, method, authority, label, source, status, answer?.From, into, problem, kind));
    }

    // ---------- made-up and kept answers ----------

    /// <summary>
    /// An answer of the shape the app's code expects for this question, filled with values the app is known to use.
    /// The same question gets the same answer for the rest of the run, so it can be looked at and corrected.
    /// </summary>
    private string? MadeUp(string kind, string method, string path, string? target, string? intent, string? request = null)
    {
        lock (_madeUp) if (_madeUp.TryGetValue(kind, out var known)) return known;
        if (Code is not { } code) return null;
        // The call is named in the code by its command, or by the end of its path ("User/Profile", "Profile").
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var names = new[] { target, intent }.Concat(Enumerable.Range(1, Math.Min(3, segments.Length)).Reverse()
            .Select(n => string.Join('/', segments[^n..]))).Where(n => !string.IsNullOrEmpty(n)).Distinct();
        var type = names.Select(n => code.ResponseTypeFor(n!)).FirstOrDefault(t => t is not null);
        if (type is null) return null;
        // What the app just sent comes first: a made-up sign-in for system ACC answers for ACC, as the real one would.
        var sent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        lock (_pool)
        {
            var saved = new Dictionary<string, List<string>>(_pool);
            _pool.Clear();
            Collect(request);
            foreach (var (k, v) in _pool) sent[k] = v;
            _pool.Clear();
            foreach (var (k, v) in saved) _pool[k] = v;
        }
        IReadOnlyList<string> Known(string field)
        {
            var key = Norm(field);
            // A field called "role" holds what other answers call "roleCode". Codes come first, so a made-up sign-in
            // and a made-up list of roles name the same role; status-like fields keep their own values.
            var order = key is "status" or "state" or "error" or "response" or "result" or "message"
                ? new[] { key, key + "id", key + "name" }
                : new[] { key + "code", key, key + "id", key + "name" };
            foreach (var candidate in order)
            {
                if (sent.TryGetValue(candidate, out var echoed) && echoed.Count > 0) return echoed;
                List<string>? known;
                lock (_pool) known = _pool.TryGetValue(candidate, out var list) && list.Count > 0 ? list.ToList() : null;
                if (known is null) continue;
                // Asked for one system's (or role's) list: only values that belong to it.
                var fitting = known.Where(v => Belongs(candidate, v)).ToList();
                return fitting.Count > 0 ? fitting : known;
            }
            return [];
        }
        // A value belongs to what the app asked for when its recorded row agrees with every value the app sent, or,
        // for a dotted code nobody recorded (RCU.DASHBOARD), when it starts with a code the app sent and not another.
        bool Belongs(string field, string value)
        {
            if (sent.Count == 0 || !(field.EndsWith("code") || field.EndsWith("id") || field.EndsWith("key"))) return true;
            Dictionary<string, string>? row;
            lock (_pool) _rows.TryGetValue((field, value), out row);
            if (row is not null)
                return sent.All(s => !row.TryGetValue(s.Key, out var own) || s.Value.Any(v => v.Equals(own, StringComparison.OrdinalIgnoreCase)));
            var dot = value.IndexOf('.');
            if (dot <= 0) return true;
            var prefix = value[..dot];
            var sentCodes = sent.Where(s => s.Key.EndsWith("code")).SelectMany(s => s.Value).ToList();
            if (sentCodes.Any(c => c.Equals(prefix, StringComparison.OrdinalIgnoreCase))) return true;
            List<string>? systems;
            lock (_pool) _pool.TryGetValue("systemcode", out systems);
            // Another system's code (OPS.CLAIMS when RCU was asked for) does not belong.
            return !(systems?.Any(c => c.Equals(prefix, StringComparison.OrdinalIgnoreCase)) ?? false);
        }
        IReadOnlyDictionary<string, string>? RowOf(string field, string value)
        {
            lock (_pool)
            {
                if (!_rows.TryGetValue((CodeIndex.FieldKey(field), value), out var row)) return null;
                // What the app sent (this system, this role) still wins over what the recorded row says.
                return row.Where(p => !sent.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            }
        }
        var json = code.Sample(type, Known, RowOf)?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        if (json is null) return null;
        lock (_madeUp) _madeUp[kind] = json;
        return json;
    }

    /// <summary>The answer last given for a kind of question, for someone to read or correct.</summary>
    public string? AnswerFor(string kind)
    {
        lock (_taught) if (_taught.TryGetValue(kind, out var taught)) return taught.Body;
        lock (_madeUp) return _madeUp.GetValueOrDefault(kind);
    }

    /// <summary>Keeps an answer for a kind of question: used from now on, and in later runs.</summary>
    public void Teach(Hit hit, string body)
    {
        if (hit.Key is null) return;
        lock (_taught)
        {
            _taught[hit.Key] = new Taught(hit.Method, hit.Host, hit.Label, body);
            if (_dataFile is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_dataFile)!);
            File.WriteAllText(_dataFile, System.Text.Json.JsonSerializer.Serialize(_taught, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Where kept answers live for this repo; loads what is there.</summary>
    public void KeepDataIn(string file)
    {
        _dataFile = file;
        try
        {
            if (!File.Exists(file)) return;
            var kept = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Taught>>(File.ReadAllText(file));
            if (kept is null) return;
            lock (_taught) foreach (var (key, value) in kept) _taught[key] = value;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
    }

    public int Kept { get { lock (_taught) return _taught.Count; } }

    private static string Norm(string field)
    {
        var f = field.ToLowerInvariant();
        if (f.StartsWith("p_")) f = f[2..];
        return f.Replace("_", "");
    }

    private void Remember(string field, string value)
    {
        if (value.Length is 0 or > 80) return;
        var key = Norm(field);
        if (!_pool.TryGetValue(key, out var list)) _pool[key] = list = [];
        if (list.Count < 400 && !list.Contains(value)) list.Add(value);
    }

    /// <summary>Every field and value in a recorded body, including a generic request's paramNames paired with its paramValues.</summary>
    private void Collect(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.TrimStart() is not ['{' or '[', ..]) return;
        try { Walk(System.Text.Json.Nodes.JsonNode.Parse(json), 0); }
        catch (System.Text.Json.JsonException) { }
    }

    private void Walk(System.Text.Json.Nodes.JsonNode? node, int depth)
    {
        if (node is null || depth > 8) return;
        if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array.Take(60)) Walk(item, depth + 1);
            return;
        }
        if (node is not System.Text.Json.Nodes.JsonObject obj) return;
        if (obj["paramNames"] is System.Text.Json.Nodes.JsonArray names && obj["paramValues"] is System.Text.Json.Nodes.JsonArray given)
            for (var i = 0; i < Math.Min(names.Count, given.Count); i++)
                if (names[i]?.ToString() is { } n && given[i]?.ToString() is { } v) Remember(n, v);
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in obj)
        {
            if (value is System.Text.Json.Nodes.JsonValue leaf)
            {
                if (leaf.ToString() is { Length: > 0 } text) { Remember(name, text); row.TryAdd(CodeIndex.FieldKey(name), text); }
            }
            else Walk(value, depth + 1);
        }
        foreach (var (field, text) in row)
            if ((field.EndsWith("code") || field.EndsWith("id") || field.EndsWith("key") || field.EndsWith("no")) && text.Length <= 80)
                _rows.TryAdd((field, text), row);
        // A string holding JSON (OpsApi's formatted results) is looked into too.
        foreach (var (_, value) in obj)
            if (value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && s.TrimStart() is ['{' or '[', ..])
                Collect(s);
    }

    private bool IsDown(string host)
    {
        lock (_downUntil) return _downUntil.TryGetValue(host, out var until) && until > DateTime.UtcNow;
    }

    /// <summary>A server that did not answer is left alone for two minutes, so every lookup does not wait on it again.</summary>
    private void MarkDown(string host)
    {
        lock (_downUntil) _downUntil[host] = DateTime.UtcNow.AddMinutes(2);
    }

    /// <summary>Asks the real server a lookup on the app's behalf, with the app's own headers. Null if it cannot be reached.</summary>
    private static async Task<(Recorded?, string?)> ReadReal(string scheme, string authority, string pathAndQuery, string method, string body, HttpRequest incoming)
    {
        if (scheme is not ("http" or "https") || !Uri.TryCreate($"{scheme}://{authority}{pathAndQuery}", UriKind.Absolute, out var target)) return (null, "not a web address");
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), target);
            if (body.Length > 0) request.Content = new StringContent(body, System.Text.Encoding.UTF8);
            if (incoming.ContentType is { } type && request.Content is not null)
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(type);
            foreach (var (name, value) in incoming.Headers)
                if (!NotForwarded.Contains(name) && !name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)value);
            using var reply = await Real.SendAsync(request);
            var text = await reply.Content.ReadAsStringAsync();
            return (new Recorded((int)reply.StatusCode, reply.Content.Headers.ContentType?.ToString(), text, "the real server"), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (null, ex.InnerException?.Message is { } inner ? $"{ex.Message} ({inner})" : ex.Message);
        }
    }

    /// <summary>
    /// Asks your app running here, with the caller's trace context so its trace joins this one. Only to this machine
    /// (checked by the caller).
    /// </summary>
    private static async Task<(Recorded?, string?)> AskLocal(Uri target, string method, string body, HttpRequest incoming)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), target);
            if (body.Length > 0 || method is "POST" or "PUT" or "PATCH")
                request.Content = new StringContent(body, System.Text.Encoding.UTF8);
            if (incoming.ContentType is { } type && request.Content is not null)
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(type);
            foreach (var (name, value) in incoming.Headers)
                if (!NotForwarded.Contains(name) && !name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)value);
            using var reply = await Mirror.SendAsync(request);
            var text = await reply.Content.ReadAsStringAsync();
            return (new Recorded((int)reply.StatusCode, reply.Content.Headers.ContentType?.ToString(), text, "your app running here"), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (null, "your app running here did not answer: " + ex.Message);
        }
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
