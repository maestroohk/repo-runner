using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RepoRunner.Core;

/// <summary>
/// Made-up answers for a sandbox: what a call expects back, read from the code that makes it, filled with values
/// that look right (borrowed from what the app is known to use where possible).
/// </summary>
public sealed partial class CodeIndex
{
    /// <summary>String constants by name, e.g. SystemCode = "ACC": a source of believable values.</summary>
    public IEnumerable<(string Name, string Value)> Constants =>
        _constants.Select(c => (Name: c.Key[(c.Key.LastIndexOf('.') + 1)..], c.Value))
            .Where(c => !c.Value.Equals(c.Name, StringComparison.OrdinalIgnoreCase) && c.Value.Length is > 0 and <= 60);

    private readonly Dictionary<string, string?> _shapes = new(StringComparer.Ordinal);

    /// <summary>
    /// The type the code expects back from the call that asks for <paramref name="command"/> (a stored procedure, a
    /// GetItem target, an API action), as C# source text, e.g. "GenericOpsApiResponse&lt;List&lt;PortalSystem&gt;&gt;".
    /// Null when no call in the code names it.
    /// </summary>
    public string? ResponseTypeFor(string command)
    {
        lock (_shapes)
            if (_shapes.TryGetValue(command, out var known)) return known;
        string? found = null;
        foreach (var owner in _types.Values)
        {
            foreach (var method in owner.Methods)
            {
                foreach (var node in method.Body.DescendantNodes())
                {
                    InvocationExpressionSyntax? call = node switch
                    {
                        InvocationExpressionSyntax invocation when invocation.ArgumentList.Arguments.Any(a => Constant(owner, a.Expression) == command) => invocation,
                        // targetCommand = "X" in a request object: the call that sends it comes next.
                        AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.Text: var field }, Right: var value } assignment
                            when assignment.Parent is InitializerExpressionSyntax && CommandField().IsMatch(field) && Constant(owner, value) == command =>
                            method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                                .Where(i => i.SpanStart > assignment.Span.End && GenericOf(i) is { TypeArgumentList.Arguments.Count: > 0 })
                                .MinBy(i => i.SpanStart),
                        _ => null,
                    };
                    if (call is not null && ExpectedOf(call) is { } type) { found = type; break; }
                }
                if (found is not null) break;
            }
            if (found is not null) break;
        }
        lock (_shapes) _shapes[command] = found;
        return found;
    }

    private static GenericNameSyntax? GenericOf(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g,
        MemberBindingExpressionSyntax { Name: GenericNameSyntax g } => g,
        GenericNameSyntax g => g,
        _ => null,
    };

    /// <summary>
    /// The answer type of a generic call such as GetItem&lt;List&lt;PortalSystem&gt;&gt;(...). When the method is the repo's
    /// own and unwraps an envelope (it deserializes GenericOpsApiResponse&lt;T&gt;), the envelope is what comes over the wire.
    /// </summary>
    private string? ExpectedOf(InvocationExpressionSyntax call)
    {
        if (GenericOf(call) is not { } generic) return null;
        var arguments = generic.TypeArgumentList.Arguments.Select(a => a.ToString()).ToList();
        var expected = arguments[^1];
        // The repo's own method of that name that unwraps what comes over the wire (an interface has no body to read).
        var unwrapping = _types.Values.SelectMany(t => t.Methods)
            .Where(m => m.Name == generic.Identifier.Text && m.TypeParameters.Count == arguments.Count)
            .Select(m => (Method: m, Read: m.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(GenericOf)
                .FirstOrDefault(g => g is not null && Deserializer().IsMatch(g.Identifier.Text))))
            .FirstOrDefault(x => x.Read is not null);
        if (unwrapping.Read is not { } read) return expected;
        var own = unwrapping.Method;
        var wire = read.TypeArgumentList.Arguments[^1].ToString();
        for (var i = 0; i < own.TypeParameters.Count; i++)
            wire = Regex.Replace(wire, $@"\b{Regex.Escape(own.TypeParameters[i])}\b", arguments[i]);
        return wire;
    }

    /// <summary>A JSON value of the given C# type, made up. <paramref name="values"/> gives known values for a field name.</summary>
    public JsonNode? Sample(string type, Func<string, IReadOnlyList<string>> values)
    {
        try { return SampleOf(SyntaxFactory.ParseTypeName(type), [], null, 0, 0, values); }
        catch (Exception) { return null; }
    }

    private static readonly HashSet<string> Lists = new(StringComparer.Ordinal)
        { "List", "IList", "IEnumerable", "ICollection", "IReadOnlyList", "IReadOnlyCollection", "HashSet", "Collection", "ObservableCollection" };

    private JsonNode? SampleOf(TypeSyntax type, Dictionary<string, TypeSyntax> given, string? field, int depth, int index, Func<string, IReadOnlyList<string>> values)
    {
        if (depth > 6) return null;
        switch (type)
        {
            case NullableTypeSyntax nullable:
                return SampleOf(nullable.ElementType, given, field, depth, index, values);
            case ArrayTypeSyntax array:
                return ListOf(array.ElementType, given, depth, values);
            case PredefinedTypeSyntax predefined:
                return predefined.Keyword.Kind() switch
                {
                    SyntaxKind.StringKeyword => Text(field, index, values),
                    SyntaxKind.BoolKeyword => true,
                    SyntaxKind.ObjectKeyword => new JsonObject(),
                    SyntaxKind.DecimalKeyword or SyntaxKind.DoubleKeyword or SyntaxKind.FloatKeyword => Number(field, index, values) + 0.5m,
                    _ => Number(field, index, values),
                };
            case QualifiedNameSyntax qualified:
                return SampleOf(qualified.Right, given, field, depth, index, values);
        }
        var name = type switch { GenericNameSyntax g => g.Identifier.Text, IdentifierNameSyntax i => i.Identifier.Text, _ => type.ToString() };
        var arguments = type is GenericNameSyntax gn ? gn.TypeArgumentList.Arguments.ToList() : [];
        if (arguments.Count == 0 && given.TryGetValue(name, out var bound)) return SampleOf(bound, [], field, depth, index, values);
        switch (name)
        {
            case "String": return Text(field, index, values);
            case "Boolean": return true;
            case "Int32" or "Int64" or "Int16": return Number(field, index, values);
            case "Decimal" or "Double": return Number(field, index, values) + 0.5m;
            case "DateTime" or "DateTimeOffset": return DateTime.Today.AddDays(-index).ToString("yyyy-MM-ddTHH:mm:ss");
            case "DateOnly": return DateTime.Today.AddDays(-index).ToString("yyyy-MM-dd");
            case "Guid": return Guid.NewGuid().ToString();
            case "JObject" or "JToken" or "JsonElement" or "JsonNode" or "ExpandoObject" or "Dictionary" or "IDictionary" or "IReadOnlyDictionary": return new JsonObject();
            case "Task" or "ValueTask" when arguments.Count == 1: return SampleOf(arguments[0], given, field, depth, index, values);
        }
        if (Lists.Contains(name) && arguments.Count == 1) return ListOf(arguments[0], given, depth, values);
        if (!_types.TryGetValue(name, out var info)) return field is null ? new JsonObject() : Text(field, index, values);

        var inner = new Dictionary<string, TypeSyntax>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(info.TypeParameters.Count, arguments.Count); i++) inner[info.TypeParameters[i]] = Resolve(arguments[i], given);
        var result = new JsonObject();
        foreach (var (property, propertyType, jsonName, owner) in PropertiesOf(info))
        {
            var key = jsonName ?? char.ToLowerInvariant(property[0]) + property[1..];
            if (result.ContainsKey(key)) continue;
            result[key] = SampleOf(propertyType, owner == info ? inner : [], property, depth + 1, index, values);
        }
        // A made-up name reads better as the code it belongs to: "ACC (sandbox)" rather than "Sample system name 4".
        var code = result.Where(p => IdentifyingField().IsMatch(p.Key) && p.Value is JsonValue v && v.TryGetValue<string>(out var t) && !t.StartsWith("Sample "))
            .Select(p => (string)p.Value!).FirstOrDefault();
        if (code is not null)
            foreach (var p in result.ToList())
                if (p.Key.EndsWith("name", StringComparison.OrdinalIgnoreCase) && p.Value is JsonValue v && v.TryGetValue<string>(out var t) && t.StartsWith("Sample "))
                    result[p.Key] = $"{code} (sandbox)";
        return result;
    }

    /// <summary>A type with what it was given filled in, so List&lt;T&gt; inside an envelope becomes the list of the real thing.</summary>
    private static TypeSyntax Resolve(TypeSyntax type, Dictionary<string, TypeSyntax> given) =>
        type is IdentifierNameSyntax id && given.TryGetValue(id.Identifier.Text, out var bound) ? bound : type;

    private IEnumerable<(string Name, TypeSyntax Type, string? JsonName, TypeInfo Owner)> PropertiesOf(TypeInfo info, int hops = 0)
    {
        foreach (var p in info.Properties) yield return (p.Name, p.Type, p.JsonName, info);
        if (hops > 4) yield break;
        foreach (var baseName in info.Bases)
            if (_types.TryGetValue(baseName, out var baseType))
                foreach (var p in PropertiesOf(baseType, hops + 1)) yield return p;
    }

    /// <summary>
    /// A list of made-up items. When the item has an identifying field (SystemCode, MemberId...) with known values,
    /// there is one item per value, so made-up lists line up with real ones (a made-up catalogue of the systems your
    /// access names).
    /// </summary>
    private JsonArray ListOf(TypeSyntax item, Dictionary<string, TypeSyntax> given, int depth, Func<string, IReadOnlyList<string>> values)
    {
        var resolved = Resolve(item, given);
        var name = resolved switch { GenericNameSyntax g => g.Identifier.Text, IdentifierNameSyntax i => i.Identifier.Text, _ => "" };
        var count = 3;
        if (_types.TryGetValue(name, out var info))
        {
            var identifying = PropertiesOf(info).FirstOrDefault(p => IdentifyingField().IsMatch(p.Name) && values(p.Name).Count > 0);
            if (identifying.Name is not null) count = Math.Clamp(values(identifying.Name).Count, 1, 12);
        }
        var list = new JsonArray();
        for (var i = 0; i < count; i++) list.Add(SampleOf(resolved, given, null, depth + 1, i, values));
        return list;
    }

    private static JsonNode Text(string? field, int index, Func<string, IReadOnlyList<string>> values)
    {
        var name = field ?? "value";
        var known = values(name);
        if (known.Count > 0) return known[index % known.Count];
        var lower = name.ToLowerInvariant();
        var last = Discovery.Words(name).Split(' ')[^1].ToLowerInvariant();
        return lower switch
        {
            "errorcode" or "statuscode" or "responsecode" => "0",
            _ when lower.Contains("timeout") || lower.Contains("logouttime") || lower.Contains("expiry") => "30",
            _ when lower.Contains("errordescription") || lower.Contains("message") || lower.Contains("description") && lower.Contains("status") =>
                "Made up by the RepoRunner sandbox",
            _ when lower.Contains("email") => $"sandbox.user{index + 1}@example.invalid",
            _ when lower.Contains("phone") || lower.Contains("mobile") || lower.Contains("msisdn") => $"25670000000{index}",
            _ when lower.Contains("date") || last is "time" or "at" or "on" or "timestamp" or "created" or "updated" =>
                DateTime.Today.AddDays(-index).ToString("yyyy-MM-ddTHH:mm:ss"),
            _ when lower.Contains("url") || lower.Contains("link") => "https://sandbox.invalid/",
            _ when lower.Contains("icon") => "",
            _ when lower is "status" or "state" => "Active",
            _ => $"Sample {Discovery.Words(name).ToLowerInvariant()} {index + 1}",
        };
    }

    private static decimal Number(string? field, int index, Func<string, IReadOnlyList<string>> values) =>
        field is not null && values(field) is { Count: > 0 } known && decimal.TryParse(known[index % known.Count], out var n) ? n : index + 1;

    private static string? JsonName(SyntaxList<AttributeListSyntax> attributes) =>
        attributes.SelectMany(a => a.Attributes)
            .Where(a => a.Name.ToString() is "JsonProperty" or "JsonPropertyName" or "JsonPropertyAttribute" or "JsonPropertyNameAttribute")
            .Select(a => a.ArgumentList?.Arguments.FirstOrDefault()?.Expression)
            .OfType<LiteralExpressionSyntax>().Select(l => l.Token.ValueText).FirstOrDefault();

    [GeneratedRegex(@"Deserialize|ReadFromJson|GetFromJson|ReadAs")]
    private static partial Regex Deserializer();

    [GeneratedRegex(@"(Code|Id|Key|No|Number)$")]
    private static partial Regex IdentifyingField();
}
