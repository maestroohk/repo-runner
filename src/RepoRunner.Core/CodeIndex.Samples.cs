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
        _constants.SelectMany(c =>
        {
            var type = c.Key[..Math.Max(0, c.Key.LastIndexOf('.'))];
            var name = c.Key[(c.Key.LastIndexOf('.') + 1)..];
            var named = new List<(string, string)> { (name, c.Value) };
            // A class of codes (RcuResourceCodes.Dashboard = "RCU_DASH") gives values for a field named after it (ResourceCode).
            var words = Discovery.Words(type).Split(' ');
            if (words.Length >= 2 && words[^1].Equals("codes", StringComparison.OrdinalIgnoreCase))
                named.Add((words[^2] + "Code", c.Value));
            return named;
        })
        .Where(c => !c.Item2.Equals(c.Item1, StringComparison.OrdinalIgnoreCase) && c.Item2.Length is > 0 and <= 60)
        .Select(c => (Name: c.Item1, Value: c.Item2));

    private readonly Dictionary<string, string?> _shapes = new(StringComparer.Ordinal);

    /// <summary>Why the last made-up answer could not be made, for showing beside the call.</summary>
    public string? LastSampleError { get; private set; }

    /// <summary>
    /// The type the code expects back from the call that asks for <paramref name="command"/> (a stored procedure, a
    /// GetItem target, an API action), as C# source text, e.g. "GenericOpsApiResponse&lt;List&lt;PortalSystem&gt;&gt;".
    /// Null when no call in the code names it.
    /// </summary>
    public string? ResponseTypeFor(string command)
    {
        lock (_shapes)
            if (_shapes.TryGetValue(command, out var known)) return known;
        string? found;
        var shapes = new List<string>();
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
                    // OpsQuery.Create("X", ...) builds the request; the typed call that sends it comes next.
                    if (call is not null && GenericOf(call) is null)
                        call = method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                            .Where(i => i.SpanStart > call.Span.End && GenericOf(i) is { TypeArgumentList.Arguments.Count: > 0 })
                            .MinBy(i => i.SpanStart);
                    if (call is not null && ExpectedOf(call) is { } type) shapes.Add(type);
                }
            }
        }
        // Several places may name the same call; the fullest shape (the envelope, not just its rows) is what comes over the wire.
        found = shapes.OrderByDescending(t => t.Count(c => c == '<')).ThenByDescending(t => t.Length).FirstOrDefault();
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
                .Where(g => g is not null && Deserializer().IsMatch(g.Identifier.Text))
                // The one that reads the answer uses the answer's type parameter (R); one reading an error body does not.
                .OrderByDescending(g => m.TypeParameters.Count > 0 && Regex.IsMatch(g!.TypeArgumentList.ToString(), $@"(?<![A-Za-z0-9_]){Regex.Escape(m.TypeParameters[^1])}(?![A-Za-z0-9_])"))
                .ThenByDescending(g => m.TypeParameters.Any(t => Regex.IsMatch(g!.TypeArgumentList.ToString(), $@"(?<![A-Za-z0-9_]){Regex.Escape(t)}(?![A-Za-z0-9_])")))
                .FirstOrDefault()))
            .Where(x => x.Read is not null)
            // Of several methods with that name (another HttpService, an overload), the one reading the answer as its own type wins.
            .OrderByDescending(x => x.Method.TypeParameters.Count > 0 && Regex.IsMatch(x.Read!.TypeArgumentList.ToString(), $@"(?<![A-Za-z0-9_]){Regex.Escape(x.Method.TypeParameters[^1])}(?![A-Za-z0-9_])"))
            .FirstOrDefault();
        if (unwrapping.Read is not { } read) return expected;
        var own = unwrapping.Method;
        var wire = read.TypeArgumentList.Arguments[^1].ToString();
        for (var i = 0; i < own.TypeParameters.Count; i++)
            wire = Regex.Replace(wire, $@"\b{Regex.Escape(own.TypeParameters[i])}\b", arguments[i]);
        return wire;
    }

    /// <summary>
    /// A JSON value of the given C# type, made up. <paramref name="values"/> gives known values for a field name;
    /// <paramref name="rowOf"/>, given a field and a value, gives a whole recorded row holding it, so an item keeps
    /// values that belong together (a resource's code, name and route).
    /// </summary>
    public JsonNode? Sample(string type, Func<string, IReadOnlyList<string>> values, Func<string, string, IReadOnlyDictionary<string, string>?>? rowOf = null)
    {
        lock (_sampling)
        {
            _rowOf = rowOf;
            try { return SampleOf(SyntaxFactory.ParseTypeName(type), [], null, 0, 0, values, null); }
            catch (Exception ex) { LastSampleError = $"{type}: {ex.GetType().Name}: {ex.Message}"; return null; }
            finally { _rowOf = null; }
        }
    }

    private readonly object _sampling = new();
    private Func<string, string, IReadOnlyDictionary<string, string>?>? _rowOf;

    /// <summary>A field name as rows are indexed by: lower case, without a P_ prefix or underscores.</summary>
    public static string FieldKey(string field)
    {
        var f = field.ToLowerInvariant();
        if (f.StartsWith("p_")) f = f[2..];
        return f.Replace("_", "");
    }


    private static readonly HashSet<string> Lists = new(StringComparer.Ordinal)
        { "List", "IList", "IEnumerable", "ICollection", "IReadOnlyList", "IReadOnlyCollection", "HashSet", "Collection", "ObservableCollection" };

    private JsonNode? SampleOf(TypeSyntax type, Dictionary<string, TypeSyntax> given, string? field, int depth, int index, Func<string, IReadOnlyList<string>> values, string? within = null)
    {
        if (depth > 6) return null;
        switch (type)
        {
            case NullableTypeSyntax nullable:
                return SampleOf(nullable.ElementType, given, field, depth, index, values, within);
            case ArrayTypeSyntax array:
                return ListOf(array.ElementType, given, depth, values, within);
            case PredefinedTypeSyntax predefined:
                return predefined.Keyword.Kind() switch
                {
                    SyntaxKind.StringKeyword => Text(field, index, values),
                    SyntaxKind.BoolKeyword => true,
                    SyntaxKind.ObjectKeyword => new JsonObject(),
                    SyntaxKind.DecimalKeyword or SyntaxKind.DoubleKeyword or SyntaxKind.FloatKeyword => Number(field, index, values) + 0.5m,
                    _ => Number(field, index, values),
                };
            case QualifiedNameSyntax qualified when _types.ContainsKey(qualified.ToString()):
                return SampleOf(SyntaxFactory.IdentifierName(qualified.ToString()), given, field, depth, index, values, null);
            case QualifiedNameSyntax qualified:
                return SampleOf(qualified.Right, given, field, depth, index, values, within);
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
        if (Lists.Contains(name) && arguments.Count == 1) return ListOf(arguments[0], given, depth, values, within);
        if (Lookup(name, within) is not { } info) return field is null ? new JsonObject() : Text(field, index, values);

        values = InContext(info, values);
        var inner = new Dictionary<string, TypeSyntax>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(info.TypeParameters.Count, arguments.Count); i++) inner[info.TypeParameters[i]] = Resolve(arguments[i], given);
        var result = new JsonObject();
        foreach (var (property, propertyType, jsonName, owner) in PropertiesOf(info))
        {
            var key = jsonName ?? char.ToLowerInvariant(property[0]) + property[1..];
            if (result.ContainsKey(key)) continue;
            // A nested class uses its outer class's type parameters (Response<T>.Result has "T data"), so bindings carry inward.
            var scope = new Dictionary<string, TypeSyntax>(given, StringComparer.Ordinal);
            foreach (var (k, v) in inner) scope[k] = v;
            result[key] = SampleOf(propertyType, scope, property, depth + 1, index, values, owner.Name);
        }
        // A made-up name reads better as the code it belongs to: "ACC (sandbox)" rather than "Sample system name 4",
        // and a route follows a dotted code the way the app's own routes do (RCU.DASHBOARD → /rcu/dashboard).
        var named = result.Where(p => IdentifyingField().IsMatch(p.Key) && p.Value is JsonValue v && v.TryGetValue<string>(out var t) && !t.StartsWith("Sample "))
            .Select(p => (Key: p.Key, Code: (string)p.Value!)).ToList();
        var code = named.Select(n => n.Code).OrderByDescending(c => c.Count(ch => ch == '.')).FirstOrDefault();
        // The route belongs to the resource (or page) the row is about, not to the module it sits in.
        if (named.Where(n => n.Code.Contains('.'))
                .OrderByDescending(n => n.Key.Contains("resource", StringComparison.OrdinalIgnoreCase) || n.Key.Contains("page", StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Code).FirstOrDefault() is { } dotted)
        {
            var prefix = "/" + dotted[..dotted.IndexOf('.')].ToLowerInvariant() + "/";
            foreach (var p in result.ToList())
            {
                // A route that does not even start where its code says (RCU.DASHBOARD at /ops/...) was borrowed from another row.
                if ((p.Key.Contains("route", StringComparison.OrdinalIgnoreCase) || p.Key.Contains("path", StringComparison.OrdinalIgnoreCase))
                    && p.Value is JsonValue rv && rv.TryGetValue<string>(out var rt)
                    && (rt.StartsWith("Sample ") || rt == "/" || !rt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    result[p.Key] = "/" + dotted.ToLowerInvariant().Replace('.', '/');
                // A dashboard or home comes first, so an app that lands on its first page lands there.
                if (p.Key.Contains("order", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(dotted, @"\.(DASHBOARD|HOME)$", RegexOptions.IgnoreCase))
                    result[p.Key] = p.Value is JsonValue ov && ov.TryGetValue<string>(out _) ? "0" : 0;
            }
        }
        if (code is not null)
            foreach (var p in result.ToList())
                if (p.Key.EndsWith("name", StringComparison.OrdinalIgnoreCase) && p.Value is JsonValue v && v.TryGetValue<string>(out var t) && t.StartsWith("Sample "))
                    result[p.Key] = $"{code} (sandbox)";
        return result;
    }

    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
        { "row", "rows", "dto", "item", "items", "result", "entity", "model", "response", "request", "data", "info", "view", "record", "runtime", "query" };

    /// <summary>
    /// Values for a field as its class means it: Code in RuntimeRoleRow is a role code, so known role codes come first.
    /// </summary>
    private static Func<string, IReadOnlyList<string>> InContext(TypeInfo info, Func<string, IReadOnlyList<string>> values)
    {
        var words = Discovery.Words(info.Display.Split('.')[^1]).Split(' ').Select(w => w.ToLowerInvariant())
            .Where(w => w.Length > 1 && !NoiseWords.Contains(w)).ToList();
        if (words.Count == 0) return values;
        return field =>
        {
            var lower = field.ToLowerInvariant();
            foreach (var word in words)
            {
                if (!lower.StartsWith(word) && values(word + field) is { Count: > 0 } meant) return meant;
                // A bare Code (or Id, Key) in a Role class is the role itself.
                if (lower is "code" or "id" or "key" && values(word) is { Count: > 0 } itself) return itself;
            }
            return values(field);
        };
    }

    /// <summary>A type by name as seen from inside another: its own nested class first (Outer.Result), then any of that name.</summary>
    private TypeInfo? Lookup(string name, string? within)
    {
        for (var scope = within; !string.IsNullOrEmpty(scope); scope = scope.Contains('.') ? scope[..scope.LastIndexOf('.')] : null)
            if (_types.TryGetValue(scope + "." + name, out var nested)) return nested;
        return _types.TryGetValue(name, out var info) ? info : null;
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
    private JsonArray ListOf(TypeSyntax item, Dictionary<string, TypeSyntax> given, int depth, Func<string, IReadOnlyList<string>> values, string? within = null)
    {
        var resolved = Resolve(item, given);
        var name = resolved switch { GenericNameSyntax g => g.Identifier.Text, IdentifierNameSyntax i => i.Identifier.Text, QualifiedNameSyntax q => q.ToString(), _ => "" };
        var count = 3;
        string? identifyingField = null;
        if (Lookup(name, within) is { } info)
        {
            values = InContext(info, values);
            // The field with the most known values decides how many items there are (ResourceCode over SystemCode).
            // A code says what the item is (ResourceCode); a record id only numbers it. Then the field with most known values.
            var identifying = PropertiesOf(info).Where(p => IdentifyingField().IsMatch(p.Name) && values(p.Name).Count > 0)
                .OrderByDescending(p => p.Name.EndsWith("Code", StringComparison.OrdinalIgnoreCase) && !p.Name.StartsWith("System", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(p => values(p.Name).Count).FirstOrDefault();
            if (identifying.Name is not null)
            {
                count = Math.Clamp(values(identifying.Name).Count, 1, 400);
                identifyingField = identifying.Name;
            }
        }
        var list = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            var itemValues = values;
            if (_rowOf is { } rowOf && identifyingField is { } id && values(id) is { Count: > 0 } ids
                && rowOf(id, ids[i % ids.Count]) is { } row)
                itemValues = f => row.TryGetValue(FieldKey(f), out var kept) ? [kept] : values(f);
            list.Add(SampleOf(resolved, given, null, depth + 1, i, itemValues, within));
        }
        return list;
    }

    private static JsonNode Text(string? field, int index, Func<string, IReadOnlyList<string>> values)
    {
        var name = field ?? "value";
        var words = Discovery.Words(name).Split(' ');
        var first = words[0].ToLowerInvariant();
        var last = words[^1].ToLowerInvariant();
        // Yes/no fields kept as text ("1"/"0") say yes, whatever was recorded: in the sandbox everything is on and allowed.
        if (words.Length > 1 && first is "is" or "has" or "can" or "allow" or "allows" or "enabled" or "active") return "1";
        var known = values(name);
        if (known.Count > 0) return known[index % known.Count];
        var lower = name.ToLowerInvariant();
        if (last is "order" or "count" or "number" or "sequence" or "level" or "rank" or "position" or "index") return (index + 1).ToString();
        return lower switch
        {
            "errorcode" or "statuscode" or "responsecode" => "0",
            _ when lower.Contains("token") || lower.Contains("jwt") => SandboxToken(values),
            _ when lower.Contains("timeout") || lower.Contains("logouttime") || lower.Contains("expiry") => "30",
            _ when lower.Contains("errordescription") || lower.Contains("message") || lower.Contains("description") && lower.Contains("status") =>
                "Made up by the RepoRunner sandbox",
            _ when lower.Contains("email") => $"sandbox.user{index + 1}@example.invalid",
            _ when lower.Contains("phone") || lower.Contains("mobile") || lower.Contains("msisdn") => $"25670000000{index}",
            _ when lower.Contains("date") || last is "time" or "at" or "on" or "timestamp" or "created" or "updated" =>
                DateTime.Today.AddDays(-index).ToString("yyyy-MM-ddTHH:mm:ss"),
            _ when lower.Contains("url") || lower.Contains("link") => "/", // stays inside the app; a made-up host would lead nowhere
            _ when lower.Contains("icon") => "",
            _ when lower is "status" or "state" => "Active",
            _ => $"Sample {Discovery.Words(name).ToLowerInvariant()} {index + 1}",
        };
    }

    /// <summary>
    /// A token apps can read but no server would accept: an unsigned JWT (alg "none") with a name, a department and
    /// eight hours of life. Apps that only read their claims work; anything that checks a signature refuses it.
    /// </summary>
    public static string SandboxToken(Func<string, IReadOnlyList<string>> values)
    {
        static string Part(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string First(string field, string fallback) => values(field) is { Count: > 0 } known ? known[0] : fallback;
        var claims = new JsonObject
        {
            ["sub"] = First("username", "sandbox.user"),
            ["name"] = First("username", "sandbox.user"),
            ["email"] = First("email", "sandbox.user@example.invalid"),
            ["Dpt"] = First("department", "Sandbox"),
            ["iss"] = "RepoRunner sandbox",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds(),
        };
        return Part("""{"alg":"none","typ":"JWT"}""") + "." + Part(claims.ToJsonString()) + ".";
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
