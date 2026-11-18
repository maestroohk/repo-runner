using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RepoRunner.Core;

/// <summary>One step on a feature's path through the code.</summary>
public sealed class CodeNode
{
    /// <summary>method | env | config | http | db | throw | catch</summary>
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public string? File { get; init; }
    public int Line { get; init; }
    /// <summary>Set when the method is already on the path above (a cycle) or the trace was cut for size.</summary>
    public string? Note { get; set; }
    public List<CodeNode> Children { get; } = [];

    public IEnumerable<CodeNode> All() => new[] { this }.Concat(Children.SelectMany(c => c.All()));
}

/// <summary>
/// A map of a repo's own code, built by parsing its source: which types exist, what they hold, and what each
/// method calls. It follows calls by name and declared type rather than by compiling, so it is fast and needs
/// no build, at the cost of missing calls it cannot resolve that way (reflection, delegates, dynamic dispatch).
/// </summary>
public sealed partial class CodeIndex
{
    private sealed class TypeInfo
    {
        /// <summary>Unique key. A class is known by its name; a Razor component by its file, since many share a name (Index).</summary>
        public required string Name { get; init; }
        public required string Display { get; init; }
        public HashSet<string> Bases { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Members { get; } = new(StringComparer.Ordinal);
        public List<MethodInfo> Methods { get; } = [];
    }

    private sealed record MethodInfo(string Name, int Parameters, SyntaxNode Body, string File, int LineOffset, Dictionary<string, string> ParameterTypes);

    private static readonly HashSet<string> HttpCalls = new(StringComparer.Ordinal)
    {
        "GetAsync", "PostAsync", "PutAsync", "DeleteAsync", "PatchAsync", "SendAsync", "GetStringAsync", "GetFromJsonAsync",
        "PostAsJsonAsync", "PutAsJsonAsync", "GetByteArrayAsync", "GetStreamAsync",
    };

    private static readonly HashSet<string> DbCalls = new(StringComparer.Ordinal)
    {
        "Query", "QueryAsync", "QueryFirst", "QueryFirstAsync", "QueryFirstOrDefault", "QueryFirstOrDefaultAsync", "QuerySingle",
        "QuerySingleAsync", "QuerySingleOrDefaultAsync", "QueryMultipleAsync", "Execute", "ExecuteAsync", "ExecuteScalar",
        "ExecuteScalarAsync", "ExecuteReader", "ExecuteReaderAsync", "ExecuteNonQuery", "ExecuteNonQueryAsync", "SaveChanges", "SaveChangesAsync",
    };

    private static readonly HashSet<string> ConfigCalls = new(StringComparer.Ordinal) { "GetValue", "GetSection", "GetConnectionString" };

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", "packages", "publish", "dist", "wwwroot", "TestResults", "Migrations" };

    private readonly Dictionary<string, TypeInfo> _types = new(StringComparer.Ordinal);

    public string Root { get; }
    public int Files { get; private set; }
    public int Types => _types.Count;
    public int Methods => _types.Values.Sum(t => t.Methods.Count);

    private CodeIndex(string root) => Root = root;

    public static CodeIndex Build(string root)
    {
        var index = new CodeIndex(root);
        foreach (var file in SourceFiles(root, 0))
        {
            string text;
            try
            {
                if (new FileInfo(file).Length > 1_500_000) continue;
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            index.Files++;
            var relative = Path.GetRelativePath(root, file);
            if (file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) index.AddRazor(relative, text);
            else index.AddSource(relative, CSharpSyntaxTree.ParseText(text).GetRoot(), 0);
        }
        return index;
    }

    public bool HasType(string name) => _types.ContainsKey(name);

    /// <summary>The key of the component a .razor file (or its .razor.cs code-behind) defines.</summary>
    public static string ComponentKey(string relativeFile) =>
        relativeFile.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase) ? relativeFile[..^9]
        : relativeFile.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ? relativeFile[..^6]
        : relativeFile;

    /// <summary>Method names of a type, in source order; for a page these are its lifecycle methods and handlers.</summary>
    public List<string> MethodsOf(string type) =>
        _types.TryGetValue(type, out var info) ? info.Methods.Select(m => m.Name).Distinct().ToList() : [];

    /// <summary>The path from one method down through the repo's own code.</summary>
    public CodeNode? Trace(string type, string method, int maxDepth = 7)
    {
        if (!_types.TryGetValue(type, out var info) || FindMethod(info, method, -1) is not { } found) return null;
        var budget = 400;
        return TraceMethod(found.Owner, found.Method, [], maxDepth, ref budget);
    }

    // ---------- reading source ----------

    private void AddRazor(string file, string text)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var info = TypeFor(ComponentKey(file), name);
        foreach (Match m in InjectRegex().Matches(text)) info.Members[m.Groups[2].Value] = Simple(m.Groups[1].Value);
        foreach (Match m in CodeBlockRegex().Matches(text))
        {
            // Find the matching brace of the @code block, then parse its inside as the members of the component class.
            var open = m.Index + m.Length - 1;
            var depth = 0;
            var close = -1;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) { close = i; break; }
            }
            if (close < 0) continue;
            var body = text[(open + 1)..close];
            var lineOffset = text.AsSpan(0, open).Count('\n');
            AddSource(file, CSharpSyntaxTree.ParseText("class " + name + " {" + body + "}").GetRoot(), lineOffset);
        }
    }

    private void AddSource(string file, SyntaxNode root, int lineOffset)
    {
        // Index.razor and Index.razor.cs are two halves of one component.
        var component = file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(ComponentKey(file))
            : null;
        foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var typeName = declaration.Identifier.Text;
            var info = typeName == component ? TypeFor(ComponentKey(file), typeName) : TypeFor(typeName);
            foreach (var baseType in declaration.BaseList?.Types ?? default) info.Bases.Add(Simple(baseType.Type));
            foreach (var parameter in declaration.ParameterList?.Parameters ?? default)
                if (parameter.Type is not null) info.Members[parameter.Identifier.Text] = Simple(parameter.Type);

            foreach (var member in declaration.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                        foreach (var variable in field.Declaration.Variables) info.Members[variable.Identifier.Text] = Simple(field.Declaration.Type);
                        break;
                    case PropertyDeclarationSyntax property:
                        info.Members[property.Identifier.Text] = Simple(property.Type);
                        break;
                    case ConstructorDeclarationSyntax constructor:
                        foreach (var parameter in constructor.ParameterList.Parameters)
                            if (parameter.Type is not null) info.Members.TryAdd(parameter.Identifier.Text, Simple(parameter.Type));
                        break;
                    case MethodDeclarationSyntax method when ((SyntaxNode?)method.Body ?? method.ExpressionBody) is { } body:
                        info.Methods.Add(new MethodInfo(method.Identifier.Text, method.ParameterList.Parameters.Count, body, file, lineOffset,
                            method.ParameterList.Parameters.Where(p => p.Type is not null)
                                .ToDictionary(p => p.Identifier.Text, p => Simple(p.Type!), StringComparer.Ordinal)));
                        break;
                }
            }
        }
    }

    private TypeInfo TypeFor(string key, string? display = null)
    {
        if (!_types.TryGetValue(key, out var info)) _types[key] = info = new TypeInfo { Name = key, Display = display ?? key };
        return info;
    }

    private static string Simple(TypeSyntax type) => type switch
    {
        NullableTypeSyntax nullable => Simple(nullable.ElementType),
        QualifiedNameSyntax qualified => Simple(qualified.Right),
        GenericNameSyntax generic => generic.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        _ => type.ToString(),
    };

    private static string Simple(string type)
    {
        var cut = type.IndexOf('<');
        var name = (cut < 0 ? type : type[..cut]).TrimEnd('?');
        return name[(name.LastIndexOf('.') + 1)..];
    }

    // ---------- following calls ----------

    private (TypeInfo Owner, MethodInfo Method)? FindMethod(TypeInfo type, string name, int arguments, int hops = 0)
    {
        var candidates = type.Methods.Where(m => m.Name == name).ToList();
        if (candidates.Count > 0)
            return (type, candidates.FirstOrDefault(m => m.Parameters == arguments) ?? candidates[0]);
        if (hops > 4) return null;
        foreach (var baseName in type.Bases)
            if (_types.TryGetValue(baseName, out var baseType) && FindMethod(baseType, name, arguments, hops + 1) is { } inherited)
                return inherited;
        return null;
    }

    /// <summary>The concrete type behind a declared one: an interface resolves to the class in this repo that implements it.</summary>
    private TypeInfo? Concrete(string declared)
    {
        _types.TryGetValue(declared, out var known);
        if (known is { Methods.Count: > 0 } && !(declared.Length > 1 && declared[0] == 'I' && char.IsUpper(declared[1]))) return known;
        var implementers = _types.Values.Where(t => t.Bases.Contains(declared) && t.Methods.Count > 0).ToList();
        if (implementers.Count > 0)
            return implementers.FirstOrDefault(t => "I" + t.Name == declared) ?? implementers[0];
        return known is { Methods.Count: > 0 } ? known : null;
    }

    private CodeNode TraceMethod(TypeInfo owner, MethodInfo method, HashSet<string> path, int depthLeft, ref int budget)
    {
        var key = owner.Name + "." + method.Name;
        var node = new CodeNode
        {
            Kind = "method",
            Title = owner.Display + "." + method.Name,
            File = method.File,
            Line = method.LineOffset + method.Body.SyntaxTree.GetLineSpan(method.Body.Parent?.Span ?? method.Body.Span).StartLinePosition.Line + 1,
        };
        if (path.Contains(key)) { node.Note = "already on this path"; return node; }
        if (depthLeft <= 0 || budget <= 0) { node.Note = "not followed further"; return node; }
        path.Add(key);

        var locals = new Dictionary<string, string>(method.ParameterTypes, StringComparer.Ordinal);
        foreach (var local in method.Body.DescendantNodes().OfType<VariableDeclarationSyntax>())
            foreach (var variable in local.Variables)
            {
                var declared = local.Type.IsVar
                    ? variable.Initializer?.Value is ObjectCreationExpressionSyntax created ? Simple(created.Type) : null
                    : Simple(local.Type);
                if (declared is not null) locals.TryAdd(variable.Identifier.Text, declared);
            }

        foreach (var item in method.Body.DescendantNodes().OrderBy(n => n.SpanStart))
        {
            if (budget <= 0) break;
            CodeNode? child = item switch
            {
                InvocationExpressionSyntax invocation => Call(owner, method, invocation, locals, path, depthLeft, ref budget),
                ThrowStatementSyntax { Expression: ObjectCreationExpressionSyntax thrown } => Leaf("throw", Simple(thrown.Type), First(thrown.ArgumentList), method, thrown),
                ThrowExpressionSyntax { Expression: ObjectCreationExpressionSyntax thrown } => Leaf("throw", Simple(thrown.Type), First(thrown.ArgumentList), method, thrown),
                CatchClauseSyntax handler when !handler.Block.DescendantNodes().Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax) =>
                    Leaf("catch", handler.Declaration is { } caught ? Simple(caught.Type) : "everything",
                        handler.Block.Statements.Count == 0 ? "empty catch: the error disappears" : "caught here and not rethrown", method, handler),
                ElementAccessExpressionSyntax { Expression: var target } access
                    when target.ToString().Contains("onfig", StringComparison.Ordinal) && Literal(access.ArgumentList.Arguments.FirstOrDefault()?.Expression) is { } setting =>
                    Leaf("config", setting, null, method, access),
                _ => null,
            };
            if (child is null) continue;
            // The same leaf twice in a row (a retry, a guard clause repeated) reads better once.
            if (node.Children.Count > 0 && node.Children[^1] is var last && last.Kind == child.Kind && last.Title == child.Title && child.Children.Count == 0 && last.Children.Count == 0)
                continue;
            node.Children.Add(child);
            budget--;
        }
        path.Remove(key);
        return node;
    }

    private CodeNode? Call(TypeInfo owner, MethodInfo method, InvocationExpressionSyntax invocation, Dictionary<string, string> locals,
        HashSet<string> path, int depthLeft, ref int budget)
    {
        ExpressionSyntax? receiver;
        string name;
        switch (invocation.Expression)
        {
            case MemberAccessExpressionSyntax access:
                (receiver, name) = (access.Expression, access.Name.Identifier.Text);
                break;
            case MemberBindingExpressionSyntax binding:
                (receiver, name) = ((invocation.Parent as ConditionalAccessExpressionSyntax)?.Expression, binding.Name.Identifier.Text);
                break;
            case SimpleNameSyntax simple:
                (receiver, name) = (null, simple.Identifier.Text);
                break;
            default:
                return null;
        }
        var firstArgument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;

        if (name == "GetEnvironmentVariable" && Literal(firstArgument) is { } variable) return Leaf("env", variable, null, method, invocation);
        if (ConfigCalls.Contains(name) && Literal(firstArgument) is { } setting) return Leaf("config", setting, null, method, invocation);

        // A call into this repo's own code is followed; anything else is kept only if it leaves the process.
        var target = receiver switch
        {
            null or ThisExpressionSyntax or BaseExpressionSyntax => owner,
            IdentifierNameSyntax id => DeclaredType(owner, locals, id.Identifier.Text) is { } declared ? Concrete(declared) : null,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var member } =>
                owner.Members.TryGetValue(member.Identifier.Text, out var declared) ? Concrete(declared) : null,
            _ => null,
        };
        if (target is not null && FindMethod(target, name, invocation.ArgumentList.Arguments.Count) is { } found)
            return TraceMethod(found.Owner, found.Method, path, depthLeft - 1, ref budget);

        if (HttpCalls.Contains(name)) return Leaf("http", name, Describe(firstArgument), method, invocation);
        if (DbCalls.Contains(name)) return Leaf("db", name, Describe(firstArgument), method, invocation);
        return null;
    }

    private string? DeclaredType(TypeInfo owner, Dictionary<string, string> locals, string identifier) =>
        locals.TryGetValue(identifier, out var local) ? local
        : owner.Members.TryGetValue(identifier, out var member) ? member
        : _types.ContainsKey(identifier) ? identifier // a static call on one of the repo's own types
        : null;

    private static CodeNode Leaf(string kind, string title, string? detail, MethodInfo method, SyntaxNode at) => new()
    {
        Kind = kind,
        Title = title,
        Detail = detail,
        File = method.File,
        Line = method.LineOffset + at.SyntaxTree.GetLineSpan(at.Span).StartLinePosition.Line + 1,
    };

    private static string? Literal(ExpressionSyntax? expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null;

    private static string? First(ArgumentListSyntax? arguments) => Describe(arguments?.Arguments.FirstOrDefault()?.Expression);

    /// <summary>A short, readable form of an argument: the text of a literal, or the expression itself.</summary>
    private static string? Describe(ExpressionSyntax? expression)
    {
        if (expression is null) return null;
        var text = Literal(expression) ?? expression.ToString();
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= 90 ? text : text[..89] + "…";
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
            if (name.StartsWith('.') || SkipDirs.Contains(name) || name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Test", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in SourceFiles(sub, depth + 1)) yield return file;
        }
    }

    [GeneratedRegex(@"^\s*@inject\s+([\w.<>,\[\]? ]+?)\s+(\w+)\s*$", RegexOptions.Multiline)]
    private static partial Regex InjectRegex();

    [GeneratedRegex(@"^\s*@(?:code|functions)\s*\{", RegexOptions.Multiline)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
