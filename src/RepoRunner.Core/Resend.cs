using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoRunner.Core;

/// <summary>
/// Decides whether sending a captured call again would only read data or would change something.
/// The decision rests on the verb a call is named with: the command in its body, or the last part of its address.
/// </summary>
public static partial class CallSafety
{
    private static readonly HashSet<string> Changes = new(StringComparer.OrdinalIgnoreCase)
    {
        "save", "submit", "finalize", "finalise", "review", "publish", "send", "delete", "update", "approve", "reject", "insert", "create",
        "add", "remove", "cancel", "set", "assign", "transfer", "upload", "post", "put", "patch", "notify", "register", "mark", "close",
        "reopen", "archive", "import", "upsert", "merge", "complete", "confirm", "reset", "revoke", "grant", "enable", "disable", "pay",
        "process", "apply", "move", "copy", "write", "store", "log", "push", "trigger", "start", "stop", "run", "generate",
    };

    private static readonly HashSet<string> Reads = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "list", "search", "count", "find", "read", "fetch", "lookup", "query", "load", "select", "check", "exists", "view",
        "detail", "details", "unread", "browse", "show", "preview", "validate", "verify", "resolve", "health", "ping", "status",
    };

    /// <summary>True when sending the call again could change data. Anything unrecognised that is not a GET counts as a change.</summary>
    public static bool Writes(string method, string name)
    {
        var verb = VerbOf(name);
        if (verb is not null && Changes.Contains(verb)) return true;
        if (verb is not null && Reads.Contains(verb)) return false;
        return !(method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The verb of a name: the first word of its last part. "RCU_SubmissionReviewHistory_Get" → "Get";
    /// "RCU_Submission_ReviewItem" → "Review"; "/api/Notifications/SendEmail" → "Send".
    /// </summary>
    public static string? VerbOf(string name)
    {
        var last = name.Split(['/', '?'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('?')[0]).LastOrDefault(p => p.Length > 0) ?? "";
        last = last.Split('_', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var match = FirstWord().Match(last);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"^([A-Z]+(?![a-z])|[A-Z]?[a-z]+)")]
    private static partial Regex FirstWord();
}

/// <summary>
/// A request body prepared for editing on screen: credentials and vault secrets are shown as bullets, and are put
/// back exactly as they were when the edited body is sent.
/// </summary>
public sealed class EditableBody
{
    public const string Hidden = "••••";

    private static readonly string[] SensitiveNames = ["password", "passwd", "pwd", "secret", "token", "apikey", "api_key", "authorization", "pin", "otp"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Dictionary<string, string> _byPath = [];
    private readonly List<(string Token, string Value)> _tokens = [];
    private readonly bool _json;

    /// <summary>What to show in the editor.</summary>
    public string Text { get; }

    public static bool IsSensitive(string name) => SensitiveNames.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));

    public EditableBody(string? body, IEnumerable<string> secrets)
    {
        body ??= "";
        var text = body;
        if (body.TrimStart() is ['{' or '[', ..])
        {
            try
            {
                var node = JsonNode.Parse(body);
                if (node is not null)
                {
                    Mask(node, "");
                    text = node.ToJsonString(Indented);
                    _json = true;
                }
            }
            catch (JsonException) { }
        }
        var n = 0;
        foreach (var secret in secrets.Where(s => s.Length >= 4).Distinct().OrderByDescending(s => s.Length))
        {
            if (!text.Contains(secret, StringComparison.Ordinal)) continue;
            var token = $"{Hidden}{++n}{Hidden[0]}";
            _tokens.Add((token, secret));
            text = text.Replace(secret, token, StringComparison.Ordinal);
        }
        Text = text;
    }

    /// <summary>The body to send: the edited text with every hidden value put back where it was.</summary>
    public string Restore(string edited)
    {
        foreach (var (token, value) in _tokens) edited = edited.Replace(token, value, StringComparison.Ordinal);
        if (!_json || _byPath.Count == 0) return edited;

        JsonNode? node;
        try { node = JsonNode.Parse(edited); }
        catch (JsonException ex) { throw new VaultException($"The edited body is not valid JSON: {ex.Message}"); }
        if (node is null) return edited;
        Unmask(node, "");
        return node.ToJsonString();
    }

    private void Mask(JsonNode node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    var childPath = $"{path}.{key}";
                    if (child is JsonValue value && value.TryGetValue<string>(out var text) && IsSensitive(key) && text.Length > 0)
                    {
                        _byPath[childPath] = text;
                        obj[key] = Hidden;
                    }
                    else if (child is not null) Mask(child, childPath);
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is { } item) Mask(item, $"{path}[{i}]");
                break;
        }
    }

    private void Unmask(JsonNode node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    var childPath = $"{path}.{key}";
                    if (child is JsonValue value && value.TryGetValue<string>(out var text) && text == Hidden && _byPath.TryGetValue(childPath, out var original))
                        obj[key] = original;
                    else if (child is not null) Unmask(child, childPath);
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is { } item) Unmask(item, $"{path}[{i}]");
                break;
        }
    }
}
