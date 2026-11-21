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

