using System.Text.Json;

namespace RepoRunner.Core;

public static class DotEnv
{
    /// <summary>Parses KEY=VALUE lines. Blank lines and # comments are skipped; a leading "export " and matching quotes are removed.</summary>
    public static List<KeyValuePair<string, string>> Parse(string text)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            result.Add(new(key, value));
        }
        return result;
    }

    public static List<string> Keys(string path) => Parse(File.ReadAllText(path)).Select(p => p.Key).ToList();
}

/// <summary>Finds files in a repo that hold variables in clear: .env* files and launch profiles with more than one variable.</summary>
public static class PlaintextFiles
{
    public static List<string> Find(string root)
    {
        var found = new List<string>();
        Walk(root, 0, found);
        return found;
    }

    private static void Walk(string dir, int depth, List<string> found)
    {
        try
        {
            found.AddRange(Directory.EnumerateFiles(dir, ".env*").Where(f => !f.EndsWith(".example", StringComparison.OrdinalIgnoreCase)));
            var launch = Path.Combine(dir, "Properties", "launchSettings.json");
            if (File.Exists(launch) && LaunchSettings.Read(launch).Any(p => p.Variables.Count > 1)) found.Add(launch);
            if (depth >= 3) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (Path.GetFileName(sub) is not ("bin" or "obj" or "node_modules" or ".git" or ".vs" or ".idea" or ".venv"))
                    Walk(sub, depth + 1, found);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
}

public static class LaunchSettings
{
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public sealed record Profile(string Name, string? CommandName, string? ApplicationUrl, List<KeyValuePair<string, string>> Variables);

    public static List<Profile> Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), Lenient);
        var profiles = new List<Profile>();
        if (!doc.RootElement.TryGetProperty("profiles", out var node) || node.ValueKind != JsonValueKind.Object) return profiles;
        foreach (var p in node.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            var vars = new List<KeyValuePair<string, string>>();
            if (p.Value.TryGetProperty("environmentVariables", out var ev) && ev.ValueKind == JsonValueKind.Object)
                foreach (var v in ev.EnumerateObject())
                    vars.Add(new(v.Name, v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString()! : v.Value.GetRawText()));
            profiles.Add(new Profile(p.Name, Text(p.Value, "commandName"), Text(p.Value, "applicationUrl"), vars));
        }
        return profiles;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
