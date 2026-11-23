using System.Text.Json;
using System.Text.RegularExpressions;
using RepoRunner.Core;

namespace RepoRunner.App;

/// <summary>What a recorded flow is, in terms the screens and the failure keeper share.</summary>
public static partial class FlowRules
{
    public const string BlazorHub = "Microsoft.AspNetCore.Components.Server.ComponentHub/";

    private static readonly string[] AssetEndings =
        [".js", ".css", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".woff", ".woff2", ".ttf", ".wasm", ".json"];

    public static bool IsRequest(Flow flow) => flow.Root?.Category == "request";

    /// <summary>
    /// Traffic that is plumbing rather than a feature: Blazor's circuit bookkeeping and connection requests, and
    /// static files. Anything that failed or threw is never treated as plumbing.
    /// </summary>
    public static bool IsChatter(Flow flow)
    {
        if (flow.Errors.Count > 0 || flow.Notes.Count > 0 || flow.Root is not { } root) return false;
        if (flow.Steps.Count == 1 && (root.Name.StartsWith(BlazorHub) || root.Name.StartsWith("Circuit "))) return true;
        if (root.Category != "request" || root.StatusCode >= 400 || flow.Steps.Values.Any(s => s.Failed)) return false;
        var path = root.Tag("url.path", "http.target") ?? "";
        return path.StartsWith("/_blazor") || path.StartsWith("/_framework") || path.StartsWith("/_content")
            || AssetEndings.Any(ending => path.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A step someone took: a page request, a click, a navigation. Everything else is background.</summary>
    public const string Round = "Background round ";

    /// <summary>
    /// A background service has no requests or clicks: its work is calls it starts by itself, round after round.
    /// When nothing in <paramref name="flows"/> is a step someone took, the calls are grouped into rounds (a pause of
    /// more than ten seconds starts a new one) and each round becomes one step, with every call of that round in it.
    /// </summary>
    public static List<Flow> WithRounds(IEnumerable<Flow> flows)
    {
        var all = flows.OrderBy(f => f.FirstSeen).ToList();
        if (all.Any(IsStage)) return all;
        var work = all.Where(f => f.Steps.Values.Any(s => s.Category is "http" or "db") || f.Errors.Count > 0).ToList();
        if (work.Count == 0) return all;

        // Calls, not traces, are what is grouped: a service's whole life is often one trace.
        var calls = work.SelectMany(f => f.Steps.Values.Where(s => s.Category is "http" or "db").Select(s => (Flow: f, Step: s)))
            .OrderBy(c => c.Step.Start).ToList();
        var rounds = new List<Flow>();
        var ends = new List<DateTime>();
        foreach (var (flow, step) in calls)
        {
            if (rounds.Count == 0 || (step.Start - ends[^1]).TotalSeconds > 10)
            {
                var opened = new Flow { TraceId = $"round-{flow.TraceId}-{step.Id}", App = flow.App, FirstSeen = step.Start };
                opened.Steps["round"] = new FlowStep
                {
                    Id = "round", Name = Round + step.Start.ToLocalTime().ToString("HH:mm:ss"), Kind = "Internal", Start = step.Start.AddMilliseconds(-1),
                };
                rounds.Add(opened);
                ends.Add(step.Start);
            }
            var round = rounds[^1];
            round.Steps[step.Id] = step;
            if (flow.Bodies.TryGetValue(step.Id, out var body)) round.Bodies[step.Id] = body;
            if (flow.Stacks.TryGetValue(step.Id, out var stack)) round.Stacks[step.Id] = stack;
            var end = step.Start.AddMilliseconds(step.Ms);
            if (end > ends[^1]) ends[^1] = end;
            round.Steps["round"].Ms = (ends[^1] - round.Steps["round"].Start).TotalMilliseconds;
        }
        // An exception belongs to the round it happened in: the last one that had started by then.
        foreach (var error in work.SelectMany(f => f.Errors).OrderBy(e => e.At))
        {
            if (rounds.Count == 0)
            {
                var alone = new Flow { TraceId = "round-errors", FirstSeen = error.At };
                alone.Steps["round"] = new FlowStep { Id = "round", Name = Round + error.At.ToLocalTime().ToString("HH:mm:ss"), Kind = "Internal", Start = error.At };
                rounds.Add(alone);
            }
            (rounds.LastOrDefault(r => r.Steps["round"].Start <= error.At.AddSeconds(2)) ?? rounds[0]).Errors.Add(error);
        }
        return rounds;
    }

    public static bool IsStage(Flow flow) =>
        IsRequest(flow) || (flow.Root is { } root && (root.Name.StartsWith("Event ") || root.Name.StartsWith("Route ") || root.Name.StartsWith(Round)
            || (root.Name.StartsWith(BlazorHub) && flow.Steps.Count > 1)));

    /// <summary>
    /// Why a flow counts as a failure, in a sentence, or null when it does not. A call another system answered
    /// badly comes first, because it is usually the cause of whatever the app did next. A lookup that found
    /// nothing, and an exception the app caught and handled, are not failures.
    /// </summary>
    public static string? Failure(Flow flow)
    {
        // A step that failed only because the sandbox refused a connection is the sandbox working, not a failure to keep.
        if (flow.Errors.Any(e => e.Message.Contains("RepoRunner sandbox", StringComparison.Ordinal))) return null;
        foreach (var step in flow.Steps.Values.Where(s => s.Category == "http").OrderBy(s => s.Start))
        {
            flow.Bodies.TryGetValue(step.Id, out var body);
            var name = RequestShape.Intent(body?.Request) ?? LastSegment(step);
            var code = step.StatusCode;
            if (code is null && step.Status == "Error") return $"{name} got no answer";
            if (code >= 500) return $"{name} failed on the other system (error {code})";
            if (code >= 400)
            {
                if (code == 404 && SaysNothingFound(body?.Response)) continue;
                return $"{name} was refused (error {code}: {code switch { 401 or 403 => "not allowed", 404 => "not found", _ => "rejected" }})";
            }
            if (ReportedError(body?.Response) is { } error) return $"{name} reported {error}";
        }
        if (flow.Errors.FirstOrDefault(e => e.Unhandled) is { } unhandled)
            return $"The app crashed with {unhandled.Type.Split('.').Last()}" + (unhandled.Origin is { File: { } file } origin ? $" in {Path.GetFileName(file)}, line {origin.Line}" : "");
        if (flow.Root is { Category: "request" } root && root.StatusCode >= 500)
            return $"{root.Tag("http.request.method", "http.method")} {root.Tag("url.path", "http.target")} returned {root.StatusCode}";
        return null;
    }

    private static string LastSegment(FlowStep step) =>
        Uri.TryCreate(step.Tag("rr.url", "url.full", "http.url"), UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.TrimEnd('/').Split('/').Last() is { Length: > 0 } last ? last : uri.Authority
            : step.Name;

    public static bool SaysNothingFound(string? text) => text is { Length: < 2000 } && NothingFound().IsMatch(text);

    /// <summary>"error 7: Invalid member" when a reply says it failed while answering 200.</summary>
    private static string? ReportedError(string? response)
    {
        if (response is null || response.Length > 2_000_000 || response.TrimStart() is not ['{', ..]) return null;
        try
        {
            using var doc = JsonDocument.Parse(response);
            string? Field(string name) => doc.RootElement.EnumerateObject()
                .Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText()).FirstOrDefault();
            var code = Field("errorCode");
            var description = Field("errorDescription") ?? Field("message") ?? Field("error");
            var failed = Field("success") is "false" || Field("isSuccess") is "false" || (code is not null && code is not ("0" or "00" or "" or "null"));
            if (!failed || SaysNothingFound(description)) return null;
            var text = code is null ? "an error" : $"error {code}";
            return description is { Length: > 0 } ? $"{text}: {(description.Length > 80 ? description[..79] + "…" : description)}" : text;
        }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"no (data|records?|results?|rows?|items?|match(es)?)( was| were)? found|\bnot found\b|no matching", RegexOptions.IgnoreCase)]
    private static partial Regex NothingFound();
}

/// <summary>
/// Watches every app RepoRunner records and, when a step fails, keeps that step together with the steps that led
/// up to it, so a failure can be studied after it happened without anyone having pressed Record.
/// </summary>
public sealed class FailureKeeper : IDisposable
{
    /// <summary>A flow is judged once nothing has been heard from it for this long.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    /// <summary>How far back the steps leading to a failure are kept.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(3);
    /// <summary>The same failure is kept once in this time.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);
    public const int MaxLeadSteps = 20;
    public const int MaxKept = 25;

    private readonly SessionManager _sessions;
    private readonly ProcessStore _store;
    private readonly VaultService _vaults;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HashSet<string>> _judged = [];
    private readonly Dictionary<string, DateTime> _recent = [];
    private long _version;

    public bool Enabled { get; set; } = true;
    public long Version => Interlocked.Read(ref _version);
    /// <summary>The last failure kept: the repo it belongs to and why it was kept.</summary>
    public (string Repo, string Reason)? Last { get; private set; }

    public FailureKeeper(SessionManager sessions, ProcessStore store, VaultService vaults)
    {
        (_sessions, _store, _vaults) = (sessions, store, vaults);
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void Tick()
    {
        if (!Enabled || !Monitor.TryEnter(_gate)) return;
        try
        {
            foreach (var session in _sessions.All().Where(s => s.Flows is not null))
            {
                if (!_judged.TryGetValue(session.Id, out var judged)) _judged[session.Id] = judged = [];
                var flows = session.Flows!.Snapshot().OrderBy(f => f.FirstSeen).ToList();
                foreach (var flow in flows)
                {
                    if (judged.Contains(flow.TraceId)) continue;
                    // A flow still receiving events may be about to fail; wait until it has gone quiet.
                    if (session.Running && DateTime.UtcNow - flow.LastEvent < Settle) continue;
                    judged.Add(flow.TraceId);
                    if (!FlowRules.IsStage(flow) || FlowRules.IsChatter(flow)) continue;
                    if (FlowRules.Failure(flow) is not { } reason) continue;
                    Keep(session, flow, reason, flows);
                }
            }
        }
        finally { Monitor.Exit(_gate); }
    }

    private void Keep(RunSession session, Flow failed, string reason, List<Flow> flows)
    {
        var signature = $"{session.Repo}|{reason}";
        if (_recent.TryGetValue(signature, out var when) && DateTime.UtcNow - when < Quiet) return;
        _recent[signature] = DateTime.UtcNow;

        var lead = flows.Where(f => f != failed && f.FirstSeen < failed.FirstSeen && f.FirstSeen >= failed.FirstSeen - Lead
                                    && FlowRules.IsStage(f) && !FlowRules.IsChatter(f))
            .TakeLast(MaxLeadSteps).Append(failed).ToList();
        try
        {
            var repo = _vaults.Use(v => v.GetRepo(session.Repo));
            var name = reason.Length > 110 ? reason[..109] + "…" : reason;
            _store.Save(repo, name, session.Env, lead, kind: "failure", reason: reason);
            _store.Prune(repo, "failure", MaxKept);
            Last = (session.Repo, reason);
            Interlocked.Increment(ref _version);
        }
        // A flow can change while it is being written out; it will not be judged again, which is acceptable
        // for a best-effort keeper. Anything else is left for the next tick.
        catch (Exception ex) when (ex is VaultException or IOException or InvalidOperationException) { }
    }

    public void Dispose() => _timer.Dispose();
}
