using System.Text.Json;
using System.Text.RegularExpressions;

namespace RepoRunner.Core;

/// <summary>Reads a repo folder and proposes run targets: runnable .NET projects and npm dev/watch scripts.</summary>
public static partial class Detector
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode", ".next", "dist", "build", "publish",
        "packages", ".venv", "venv", "__pycache__", "TestResults", ".scratch", ".claude", "wwwroot",
    };

    public sealed record Result(List<TargetDef> Targets, string? DefaultTarget);

    public static Result Detect(string root)
    {
        var files = new List<string>();
        Walk(root, 0, files);

        var targets = new List<TargetDef>();
        var byDir = new Dictionary<string, TargetDef>(StringComparer.OrdinalIgnoreCase);
        string? firstWeb = null;

        foreach (var csproj in files.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Length))
        {
            var text = File.ReadAllText(csproj);
            var isWeb = text.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
            var runnable = isWeb
                || text.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)
                || ExeRegex().IsMatch(text);
            var isTest = text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                || text.Contains("xunit", StringComparison.OrdinalIgnoreCase)
                || text.Contains("NUnit", StringComparison.OrdinalIgnoreCase);
            if (!runnable || isTest) continue;

            var dir = Path.GetDirectoryName(csproj)!;
            var target = new TargetDef
            {
                Name = Unique(targets, Slug(Path.GetFileNameWithoutExtension(csproj))),
                Kind = "dotnet",
                Project = Path.GetRelativePath(root, csproj),
                Urls = LaunchUrl(dir),
                Requires = ExampleKeys(dir, root),
            };
            targets.Add(target);
            byDir[dir] = target;
            if (isWeb) firstWeb ??= target.Name;
        }

        foreach (var package in files.Where(f => Path.GetFileName(f).Equals("package.json", StringComparison.OrdinalIgnoreCase)))
        {
            var dir = Path.GetDirectoryName(package)!;
            foreach (var script in Scripts(package))
            {
                var isWatch = script.Contains("watch", StringComparison.OrdinalIgnoreCase);
                if (script is not ("dev" or "start") && !isWatch) continue;
                var target = new TargetDef
                {
                    Name = Unique(targets, Slug(script)),
                    Kind = "node",
                    Cwd = Path.GetRelativePath(root, dir),
                    Script = script,
                    Requires = ExampleKeys(dir, root),
                };
                targets.Add(target);
                // A watcher beside a .NET project (Tailwind next to Blazor) runs with it.
                if (isWatch && byDir.TryGetValue(dir, out var host)) host.Companions.Add(target.Name);
            }
        }

        var fallback = targets.FirstOrDefault(t => t.Kind == "dotnet") ?? targets.FirstOrDefault(t => t.Script == "dev") ?? targets.FirstOrDefault();
        return new Result(targets, firstWeb ?? fallback?.Name);
    }

    private static void Walk(string dir, int depth, List<string> files)
    {
        if (depth > 6) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                    files.Add(file);
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (!SkipDirs.Contains(Path.GetFileName(sub)))
                    Walk(sub, depth + 1, files);
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static string? LaunchUrl(string projectDir)
    {
        var path = Path.Combine(projectDir, "Properties", "launchSettings.json");
        if (!File.Exists(path)) return null;
        try
        {
            return LaunchSettings.Read(path)
                .FirstOrDefault(p => p.CommandName == "Project" && !string.IsNullOrWhiteSpace(p.ApplicationUrl))?.ApplicationUrl;
        }
        catch (JsonException) { return null; }
    }

    private static List<string> ExampleKeys(string dir, string root)
    {
        foreach (var candidate in new[] { Path.Combine(dir, ".env.example"), Path.Combine(root, ".env.example") })
            if (File.Exists(candidate))
                return DotEnv.Keys(candidate);
        return [];
    }

    private static List<string> Scripts(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object
                ? scripts.EnumerateObject().Select(s => s.Name).ToList()
                : [];
        }
        catch (JsonException) { return []; }
    }

    private static string Slug(string name)
    {
        var slug = SlugRegex().Replace(name, "-").Trim('-').ToLowerInvariant();
        return slug.Length == 0 ? "target" : slug;
    }

    private static string Unique(List<TargetDef> existing, string name)
    {
        var candidate = name;
        for (var i = 2; existing.Any(t => t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++)
            candidate = $"{name}-{i}";
        return candidate;
    }

    [GeneratedRegex(@"<OutputType>\s*(Win)?Exe\s*</OutputType>", RegexOptions.IgnoreCase)]
    private static partial Regex ExeRegex();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex SlugRegex();
}
