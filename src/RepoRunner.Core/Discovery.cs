using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RepoRunner.Core;

/// <summary>Something a process touches: a stored procedure, another system, or a setting it reads.</summary>
/// <param name="Kind">sp | db | http | env | config. db: a database call whose procedure or query is decided at run time.</param>
/// <param name="Title">The procedure, the system (OPSAPI, ESB) or the setting's name.</param>
/// <param name="Detail">For a call to another system: the address, as far as the code says.</param>
/// <param name="Changes">For a procedure or call: true when its name says it changes data, false when it says it reads, null when it does not say.</param>
/// <param name="Via">For a procedure: the system that runs it for this app (OPSAPI), when the app hands it over rather than running it itself.</param>
public sealed record Touch(string Kind, string Title, string? Detail, bool? Changes, string File, int Line, string? Verb = null, string? Via = null)
{
    /// <summary>True when the code builds the address at run time, so only the system is known.</summary>
    public bool AddressUnknown => Kind == "http" && (Detail is null || Discovery.IsPlaceholder(Detail));
}

/// <summary>One thing a person can do on a page or call on an API, and everything it touches on the way.</summary>
/// <param name="Method">The method in the code where it starts.</param>
/// <param name="Title">The same in words: "Save edit", "When the page opens".</param>
public sealed record ProcessStep(string Method, string Title, List<Touch> Touches, string File, int Line);

/// <summary>A page, endpoint or background job, with the steps it is made of.</summary>
/// <param name="Key">Stable across scans: kind and route (or name).</param>
/// <param name="Area">Where it sits, in words: "Operations › Digital form".</param>
/// <param name="Name">What it is called, in words: "Claim capture".</param>
/// <param name="Kind">page | endpoint | worker</param>
public sealed record FoundProcess(string Key, string Area, string Name, string Kind, string? Verb, string? Route, string File, int Line, List<ProcessStep> Steps)
{
    public IEnumerable<Touch> Touches => Steps.SelectMany(s => s.Touches);
}

/// <summary>What a scan found in a repo, kept so the repo is not read again until its code changes.</summary>
public sealed class ProcessMap
{
    public int Version { get; init; } = Discovery.FormatVersion;
    public DateTime Scanned { get; init; }
    public string Fingerprint { get; init; } = "";
    public int Files { get; init; }
    public TimeSpan Took { get; init; }
    public List<FoundProcess> Processes { get; init; } = [];
}

/// <summary>
/// Reads a repo and works out its processes without anyone describing them: every page, endpoint and job, what a
/// person can do on each, and which stored procedures, other systems and settings each step reaches. It follows the
/// code by reading it, not by running it, so calls the code makes by computed name (a procedure name passed in from
/// elsewhere, a URL built at run time) show as far as the source spells them out.
/// </summary>
public static partial class Discovery
{
    /// <summary>Goes up when what a scan finds changes shape, so maps saved by an older RepoRunner are made again.</summary>
    public const int FormatVersion = 3;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static ProcessMap Scan(string root)
    {
        var started = DateTime.UtcNow;
        var fingerprint = Fingerprint(root, out var files);
        var scan = FeatureScanner.Scan(root);
        var code = CodeIndex.Build(root);
        var processes = new List<FoundProcess>();
        foreach (var feature in scan.Features)
        {
            if (TypeOf(code, feature) is not { } type) continue;
            var steps = new List<ProcessStep>();
            foreach (var (owner, part, entry) in Parts(code, feature, type))
            {
                var trace = code.Trace(owner, entry);
                if (trace is null) continue;
                var touches = trace.All().Where(n => n.Kind is "sp" or "http" or "env" or "config" or "db")
                    .Select(n => new Touch(n.Kind, n.Title, n.Detail, Changes(n), n.File ?? feature.File, n.Line, n.Verb))
                    .DistinctBy(t => (t.Kind, t.Title, t.Detail))
                    .ToList();
                // A procedure run through another system (OpsApi's generic runner) shows as the procedure; the
                // call that carries it, whose address the code builds at run time, adds nothing.
                if (touches.Any(t => t.Kind == "sp"))
                {
                    var carrier = touches.FirstOrDefault(t => t.AddressUnknown && t.Title.All(c => char.IsUpper(c) || c == '_' || char.IsDigit(c)))?.Title;
                    if (carrier is not null)
                        touches = touches.Select(t => t.Kind == "sp" ? t with { Via = carrier } : t).ToList();
                    touches.RemoveAll(t => t.AddressUnknown || t.Kind == "db");
                }
                // Steps that reach nothing outside the app (toggling a panel, sorting a list) are left out; an
                // endpoint is kept whatever it does, since other apps call it.
                if (feature.Kind != "endpoint" && (touches.Count == 0 || touches.All(t => t.Kind is "env" or "config") && !IsLifecycle(entry))) continue;
                var title = StepTitle(feature, entry, part is not null);
                steps.Add(new ProcessStep(entry, part is null ? title : $"{part}: {char.ToLowerInvariant(title[0])}{title[1..]}", touches, trace.File ?? feature.File, trace.Line));
            }
            if (steps.Count == 0) continue;
            processes.Add(new FoundProcess(KeyOf(feature), AreaOf(feature), NameOf(feature), feature.Kind, feature.Verb, feature.Route,
                feature.File, feature.Line, steps));
        }
        return new ProcessMap
        {
            Scanned = DateTime.Now,
            Fingerprint = fingerprint,
            Files = files,
            Took = DateTime.UtcNow - started,
            Processes = processes.OrderBy(p => p.Area, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    public static string KeyOf(Feature feature) => $"{feature.Kind}:{feature.Route ?? feature.Name}";

    // ---------- keeping a scan ----------

    public static void Save(ProcessMap map, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(map, Json));
        File.Move(temp, file, overwrite: true);
    }

    public static ProcessMap? Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var map = JsonSerializer.Deserialize<ProcessMap>(File.ReadAllBytes(file), Json);
            return map?.Version == FormatVersion ? map : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// A short hash of every source file's name, size and last change. Cheap to work out (it reads no file), and
    /// different as soon as any code, page or project file is added, removed or edited.
    /// </summary>
    public static string Fingerprint(string root, out int files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        files = 0;
        foreach (var file in SourceFiles(root, 0).Order(StringComparer.OrdinalIgnoreCase))
        {
            FileInfo info;
            try { info = new FileInfo(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            files++;
            hash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(root, file)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset())[..24];
    }

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", "packages", "publish", "dist", "wwwroot", "TestResults", ".git", ".vs", ".idea" };

    private static IEnumerable<string> SourceFiles(string dir, int depth)
    {
        if (depth > 12) yield break;
        string[] files, subs;
        try
        {
            files = Directory.GetFiles(dir);
            subs = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var file in files)
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                yield return file;
        foreach (var sub in subs)
            if (!SkipDirs.Contains(Path.GetFileName(sub)))
                foreach (var file in SourceFiles(sub, depth + 1)) yield return file;
    }

    // ---------- reading the code ----------

    /// <summary>
    /// Every place a feature's work can start, page first, then each of the repo's own components the page is built
    /// from (a wizard's steps, a panel, a dialog), two levels down. Part is null for the page itself.
    /// </summary>
    private static IEnumerable<(string Owner, string? Part, string Entry)> Parts(CodeIndex code, Feature feature, string type)
    {
        foreach (var entry in Entries(code, feature, type)) yield return (type, null, entry);
        if (feature.Kind != "page") yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal) { type };
        var level = new List<string> { type };
        for (var depth = 0; depth < 2 && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var parent in level)
                foreach (var child in code.ComponentsUsedBy(parent))
                {
                    if (!seen.Add(child)) continue;
                    next.Add(child);
                    var part = Words(Strip(code.DisplayOf(child), "Page", "Component", "Dialog", "Panel"));
                    foreach (var entry in Entries(code, new Feature("component", null, child, null, child, 0), child))
                        yield return (child, part, entry);
                }
            level = next;
        }
    }

    /// <summary>The type a feature's code lives in.</summary>
    public static string? TypeOf(CodeIndex code, Feature feature)
    {
        var candidates = feature.Kind switch
        {
            "page" => new[] { CodeIndex.ComponentKey(feature.File) },
            "endpoint" => new[] { feature.Name.Split('.')[0] + "Controller", feature.Name.Split('.')[0] },
            _ => new[] { feature.Name },
        };
        return candidates.FirstOrDefault(code.HasType);
    }

    /// <summary>
    /// Where a feature's work starts. For an endpoint, its action. For a job, its loop. For a page, what runs when it
    /// opens, and each method nothing else in the page calls: those are what its buttons and links run.
    /// </summary>
    public static List<string> Entries(CodeIndex code, Feature feature, string type)
    {
        var methods = code.MethodsOf(type);
        switch (feature.Kind)
        {
            case "endpoint":
                return methods.Where(m => m == feature.Name.Split('.').Last()).ToList();
            case "worker":
                return methods.Where(m => m is "ExecuteAsync" or "StartAsync").ToList();
            default:
                var inner = code.CalledFromWithin(type);
                return methods.Where(m => IsLifecycle(m) || !inner.Contains(m) && !IsPlumbing(m))
                    .OrderBy(m => IsLifecycle(m) ? 0 : 1).ToList();
        }
    }

    private static bool IsLifecycle(string method) =>
        method.StartsWith("On", StringComparison.Ordinal) &&
        (method.Contains("Initialized") || method.Contains("ParametersSet") || method.Contains("AfterRender"));

    /// <summary>Framework and housekeeping methods that are never something a person does.</summary>
    private static bool IsPlumbing(string method) =>
        method is "Dispose" or "DisposeAsync" or "ShouldRender" or "BuildRenderTree" or "SetParametersAsync" or "ToString" or "Equals" or "GetHashCode"
        || method.StartsWith("get_", StringComparison.Ordinal);

    private static bool? Changes(CodeNode node) => node.Kind switch
    {
        "sp" => NameSays(node.Title),
        "http" => node.Verb switch
        {
            "GET" => false,
            "DELETE" or "PUT" or "PATCH" => true,
            _ => NameSays(node.Detail is { } address ? AddressPath(address) : ""),
        },
        _ => null,
    };

    private static readonly string[] ChangeWords =
        ["insert", "update", "delete", "save", "add", "create", "approve", "reject", "submit", "remove", "set", "upsert", "process",
         "assign", "reassign", "cancel", "close", "post", "upload", "send", "register", "finalize", "publish", "review", "edit", "change", "reset", "activate", "deactivate"];

    private static readonly string[] ReadWords =
        ["get", "select", "check", "search", "list", "find", "fetch", "validate", "count", "load", "view", "read", "verify", "lookup", "is", "has", "download", "paired", "report"];

    /// <summary>What a procedure's or address's own words say it does. Null when they do not say.</summary>
    private static bool? NameSays(string name)
    {
        var words = CamelSplit().Split(name.Replace('_', ' ').Replace('/', ' ').Replace('-', ' ').Replace('.', ' '))
            .Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToList();
        // All-capital names (PAS_CHECKGLOBALIDENTIFIER) are one word per segment: look for the verbs inside them.
        bool Has(string[] verbs) => words.Any(w => verbs.Any(v => w == v || w.Length > v.Length + 3 && w.StartsWith(v, StringComparison.Ordinal)));
        var changes = Has(ChangeWords);
        var reads = Has(ReadWords);
        return changes == reads ? null : changes;
    }

    public static bool IsPlaceholder(string text) => Placeholder().IsMatch(text);

    [GeneratedRegex(@"^\{[\w.]+\}$|^null$|^…$")]
    private static partial Regex Placeholder();

    private static string AddressPath(string address)
    {
        var cut = address.IndexOfAny(['?', '#']);
        return (cut < 0 ? address : address[..cut]).TrimEnd('/');
    }

    // ---------- in words ----------

    private static string StepTitle(Feature feature, string method, bool part = false)
    {
        if (feature.Kind == "endpoint") return $"{feature.Verb ?? "Call"} {feature.Route}";
        if (feature.Kind == "worker") return "Runs in the background";
        if (method.Contains("Initialized")) return part ? "When it opens" : "When the page opens";
        if (method.Contains("ParametersSet")) return "When the page is given new input";
        if (method.Contains("AfterRender")) return "Right after the page appears";
        return Words(method);
    }

    private static string NameOf(Feature feature) => feature.Kind switch
    {
        "page" => Words(Strip(System.IO.Path.GetFileNameWithoutExtension(feature.File.Replace(".razor.cs", ".razor")), "Page", "Component")),
        "endpoint" => Words(feature.Name.Split('.').Last()),
        _ => Words(Strip(feature.Name, "Service", "Worker", "Job", "HostedService", "BackgroundService")),
    };

    private static readonly HashSet<string> FolderNoise = new(StringComparer.OrdinalIgnoreCase)
        { "Components", "Pages", "Systems", "Views", "Features", "Areas", "src", "Shared", "Modules", "Steps" };

    private static string AreaOf(Feature feature)
    {
        if (feature.Kind == "endpoint") return "API · " + Words(Strip(feature.Name.Split('.')[0], "Controller"));
        if (feature.Kind == "worker") return "Background jobs";
        var folders = System.IO.Path.GetDirectoryName(feature.File)?.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) ?? [];
        // Skip the project folder and the usual containers; what remains is how the app's own menu is organised.
        var meaningful = folders.Skip(folders.Length > 1 ? 1 : 0).Where(f => !FolderNoise.Contains(f)).Select(Words).ToList();
        return meaningful.Count == 0 ? "Pages" : string.Join(" › ", meaningful);
    }

    private static string Strip(string name, params string[] suffixes)
    {
        foreach (var suffix in suffixes)
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)) return name[..^suffix.Length];
        return name;
    }

    /// <summary>SaveEditAsync → "Save edit"; NoNinRegistrations → "No nin registrations".</summary>
    public static string Words(string identifier)
    {
        var name = Strip(identifier.Trim('_'), "Async");
        if (name.StartsWith("Handle", StringComparison.Ordinal) && name.Length > 6) name = name[6..];
        if (name.StartsWith("On", StringComparison.Ordinal) && name.Length > 2 && char.IsUpper(name[2])) name = name[2..];
        var words = CamelSplit().Split(name.Replace('_', ' ')).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return identifier;
        var text = string.Join(" ", words.Select((w, i) => i == 0 ? w : w.All(char.IsUpper) && w.Length > 1 ? w : w.ToLowerInvariant()));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|\s+")]
    private static partial Regex CamelSplit();
}
