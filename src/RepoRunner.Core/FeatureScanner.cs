using System.Text.RegularExpressions;

namespace RepoRunner.Core;

/// <summary>Something a user or a schedule can trigger: a page, an API endpoint, or a background worker.</summary>
/// <param name="Kind">page | endpoint | worker</param>
public sealed record Feature(string Kind, string? Verb, string Name, string? Route, string File, int Line);

public sealed record EnvRead(string Name, string File, int Line);

public sealed class FeatureScan
{
    private readonly Dictionary<Feature, Regex> _routes = [];

    public required string Root { get; init; }
    public List<Feature> Features { get; init; } = [];
    /// <summary>Environment variable names the code reads by literal name, each with the first place it is read.</summary>
    public List<EnvRead> EnvReads { get; init; } = [];
    public int FilesRead { get; init; }

    /// <summary>The feature a request belongs to. A route with fewer parameters wins over a more general one.</summary>
    public Feature? Match(string? verb, string path)
    {
        Feature? best = null;
        var bestScore = int.MaxValue;
        foreach (var feature in Features)
        {
            if (feature.Route is null) continue;
            if (feature.Kind == "endpoint" && feature.Verb is not null && verb is not null
                && !feature.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase)) continue;
            if (!_routes.TryGetValue(feature, out var regex)) _routes[feature] = regex = RouteRegex(feature.Route);
            if (!regex.IsMatch(path)) continue;
            var score = feature.Route.Count(c => c == '{');
            if (score < bestScore) (best, bestScore) = (feature, score);
        }
        return best;
    }

    /// <summary>Turns a route template such as /claims/{id:int} into a pattern for real paths.</summary>
    public static Regex RouteRegex(string template)
    {
        var parts = template.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment =>
            segment.StartsWith("{*") || segment.StartsWith("{**") ? ".*"
            : segment.Contains('{') ? "[^/]+"
            : Regex.Escape(segment));
        return new Regex("^/" + string.Join("/", parts) + "/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>
/// Reads a repo's source and lists its features. This is a text scan, not a compile: it recognises the common
/// shapes (Blazor @page, attribute-routed controllers, minimal API maps, hosted services) and will miss unusual ones.
/// </summary>
public static partial class FeatureScanner
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", "packages", "publish", "dist", "wwwroot", "TestResults", "Migrations" };

    public static FeatureScan Scan(string root)
    {
        var features = new List<Feature>();
        var reads = new Dictionary<string, EnvRead>(StringComparer.OrdinalIgnoreCase);
        var files = 0;

        foreach (var file in SourceFiles(root, 0))
        {
            string text;
            try
            {
                if (new FileInfo(file).Length > 1_500_000) continue;
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            files++;
            var relative = Path.GetRelativePath(root, file);
            int LineOf(int index) => text.AsSpan(0, index).Count('\n') + 1;

            if (file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in PageRegex().Matches(text))
                    features.Add(new Feature("page", "GET", Path.GetFileNameWithoutExtension(file), m.Groups[1].Value, relative, LineOf(m.Index)));
            }
            else
            {
                var controller = ControllerRegex().Match(text);
                if (controller.Success)
                {
                    var name = controller.Groups[1].Value;
                    var short_ = name.EndsWith("Controller") ? name[..^10] : name;
                    var classRoute = RouteAttrRegex().Matches(text).FirstOrDefault(m => m.Index < controller.Index)?.Groups[1].Value ?? "";
                    foreach (Match m in HttpAttrRegex().Matches(text))
                    {
                        var method = MethodNameRegex().Match(text, m.Index + m.Length);
                        var action = method.Success ? method.Groups[1].Value : "";
                        // [ActionName("get-employers")] renames the action in the route; the method keeps its name.
                        var attributesFrom = Math.Max(text.LastIndexOf('}', m.Index), text.LastIndexOf(';', m.Index)) + 1;
                        var renamed = ActionNameRegex().Match(text[attributesFrom..(method.Success ? method.Index : m.Index)]);
                        var routeAction = renamed.Success ? renamed.Groups[1].Value : action;
                        var route = Combine(classRoute, m.Groups[2].Value).Replace("[controller]", short_).Replace("[action]", routeAction);
                        features.Add(new Feature("endpoint", m.Groups[1].Value.ToUpperInvariant(), $"{short_}.{action}".TrimEnd('.'), route, relative, LineOf(m.Index)));
                    }
                }
                foreach (Match m in MapRegex().Matches(text))
                    features.Add(new Feature("endpoint", m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value, Combine("", m.Groups[2].Value), relative, LineOf(m.Index)));
                foreach (Match m in WorkerRegex().Matches(text))
                    features.Add(new Feature("worker", null, m.Groups[1].Value, null, relative, LineOf(m.Index)));
            }

            foreach (Match m in EnvReadRegex().Matches(text))
                reads.TryAdd(m.Groups[1].Value, new EnvRead(m.Groups[1].Value, relative, LineOf(m.Index)));
        }

        return new FeatureScan
        {
            Root = root,
            Features = features.OrderBy(f => f.Kind).ThenBy(f => f.Route ?? f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Verb).ToList(),
            EnvReads = reads.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            FilesRead = files,
        };
    }

    private static string Combine(string prefix, string template)
    {
        if (template.StartsWith('/') || template.StartsWith("~/")) return "/" + template.TrimStart('~', '/');
        var joined = string.Join("/", new[] { prefix.Trim('/'), template.Trim('/') }.Where(p => p.Length > 0));
        return "/" + joined;
    }

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
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                yield return file;
        foreach (var sub in subs)
        {
            var name = Path.GetFileName(sub);
            // Test projects describe features; they are not features.
            if (name.StartsWith('.') || SkipDirs.Contains(name) || name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Test", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in SourceFiles(sub, depth + 1)) yield return file;
        }
    }

    [GeneratedRegex(@"^\s*@page\s+""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex PageRegex();

    [GeneratedRegex(@"\bclass\s+(\w+)\s*(?:\([^)]*\))?\s*:\s*[^{]*?\b\w*Controller(?:Base)?\b")]
    private static partial Regex ControllerRegex();

    [GeneratedRegex(@"\[Route\(\s*""([^""]*)""")]
    private static partial Regex RouteAttrRegex();

    [GeneratedRegex(@"\[Http(Get|Post|Put|Delete|Patch)(?:\(\s*""([^""]*)""[^)]*\)|\(\s*\))?\s*[\],]")]
    private static partial Regex HttpAttrRegex();

    [GeneratedRegex(@"\bpublic\s+[^;{(=]*?\b(\w+)\s*\(")]
    private static partial Regex MethodNameRegex();

    [GeneratedRegex(@"\[ActionName\(\s*""([^""]+)""")]
    private static partial Regex ActionNameRegex();

    [GeneratedRegex(@"\.Map(Get|Post|Put|Delete|Patch)\(\s*""([^""]+)""")]
    private static partial Regex MapRegex();

    [GeneratedRegex(@"\bclass\s+(\w+)\s*(?:\([^)]*\))?\s*:\s*[^{]*?\b(?:BackgroundService|IHostedService)\b")]
    private static partial Regex WorkerRegex();

    [GeneratedRegex(@"GetEnvironmentVariable\(\s*""([A-Za-z_][A-Za-z0-9_.:\-]*)""")]
    private static partial Regex EnvReadRegex();
}
