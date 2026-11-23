using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RepoRunner.Core;

/// <summary>One step on a feature's path through the code.</summary>
public sealed class CodeNode
{
    /// <summary>method | env | config | http | db | sp | throw | catch</summary>
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    /// <summary>For a call to another system: GET, POST, PUT or DELETE, when the code says.</summary>
    public string? Verb { get; init; }
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
        /// <summary>For a Razor component: the components its markup places, by tag name.</summary>
        public HashSet<string> Uses { get; } = new(StringComparer.Ordinal);
        /// <summary>Public properties with their full declared type and the name they have in JSON, for making sample data.</summary>
        public List<(string Name, TypeSyntax Type, string? JsonName)> Properties { get; } = [];
        public List<string> TypeParameters { get; } = [];
    }

    private sealed record MethodInfo(string Name, int Parameters, SyntaxNode Body, string File, int LineOffset, Dictionary<string, string> ParameterTypes)
    {
        public List<string> TypeParameters { get; init; } = [];
    }

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
    /// <summary>String constants by "Type.Name": settings names are often kept in a constants class.</summary>
    private readonly Dictionary<string, string> _constants = new(StringComparer.Ordinal);

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

    /// <summary>
    /// The repo's own components a component's markup places, nearest first when several share a name
    /// (the one in the same folder, then the closest folder up).
    /// </summary>
    public List<string> ComponentsUsedBy(string type)
    {
        if (!_types.TryGetValue(type, out var info)) return [];
        var folder = Path.GetDirectoryName(type) ?? "";
        var used = new List<string>();
        foreach (var tag in info.Uses)
        {
            var best = _types.Values
                .Where(t => t.Display == tag && t.Name != type && t.Name.Contains(Path.DirectorySeparatorChar) | t.Name.Contains('/'))
                .OrderByDescending(t => CommonPrefix(Path.GetDirectoryName(t.Name) ?? "", folder))
                .FirstOrDefault();
            if (best is not null) used.Add(best.Name);
        }
        return used;
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && char.ToLowerInvariant(a[n]) == char.ToLowerInvariant(b[n])) n++;
        return n;
    }

    /// <summary>The name people see for a type: a component's file name, a class's name.</summary>
    public string DisplayOf(string type) => _types.TryGetValue(type, out var info) ? info.Display : type;

    /// <summary>Names of the methods a type calls on itself. The rest are where its work starts (buttons, lifecycle, actions).</summary>
    public HashSet<string> CalledFromWithin(string type)
    {
        var called = new HashSet<string>(StringComparer.Ordinal);
        if (!_types.TryGetValue(type, out var info)) return called;
        var own = info.Methods.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var method in info.Methods)
            foreach (var invocation in method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = invocation.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    GenericNameSyntax generic => generic.Identifier.Text,
                    MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access => access.Name.Identifier.Text,
                    _ => null,
                };
                if (name is not null && name != method.Name && own.Contains(name)) called.Add(name);
            }
        return called;
    }

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
        foreach (Match m in TagRegex().Matches(text)) info.Uses.Add(m.Groups[1].Value);
        if (InheritsRegex().Match(text) is { Success: true } inherits) info.Bases.Add(Simple(inherits.Groups[1].Value));
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
            // A nested class (OpsLoginRespone.Result) is known by its full name: many classes nest a "Result".
            var outer = string.Join(".", declaration.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text));
            var info = typeName == component ? TypeFor(ComponentKey(file), typeName)
                : outer.Length > 0 ? TypeFor(outer + "." + typeName, typeName)
                : TypeFor(typeName);
            foreach (var baseType in declaration.BaseList?.Types ?? default) info.Bases.Add(Simple(baseType.Type));
            if (info.TypeParameters.Count == 0 && declaration.TypeParameterList is { } typeParameters)
                info.TypeParameters.AddRange(typeParameters.Parameters.Select(t => t.Identifier.Text));
            foreach (var parameter in declaration.ParameterList?.Parameters ?? default)
                if (parameter.Type is not null) info.Members[parameter.Identifier.Text] = Simple(parameter.Type);

            foreach (var member in declaration.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                        foreach (var variable in field.Declaration.Variables)
                        {
                            info.Members[variable.Identifier.Text] = Simple(field.Declaration.Type);
                            if (Literal(variable.Initializer?.Value) is { } constant
                                && field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword) || m.IsKind(SyntaxKind.StaticKeyword)))
                                _constants[typeName + "." + variable.Identifier.Text] = constant;
                        }
                        break;
                    case PropertyDeclarationSyntax property:
                        info.Members[property.Identifier.Text] = Simple(property.Type);
                        if (property.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) && !property.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                            && !info.Properties.Any(p => p.Name == property.Identifier.Text))
                            info.Properties.Add((property.Identifier.Text, property.Type, JsonName(property.AttributeLists)));
                        if (Literal(property.Initializer?.Value ?? property.ExpressionBody?.Expression) is { } fixedValue
                            && property.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                            _constants[typeName + "." + property.Identifier.Text] = fixedValue;
                        break;
                    case ConstructorDeclarationSyntax constructor:
                        foreach (var parameter in constructor.ParameterList.Parameters)
                            if (parameter.Type is not null) info.Members.TryAdd(parameter.Identifier.Text, Simple(parameter.Type));
                        break;
                    case MethodDeclarationSyntax method when ((SyntaxNode?)method.Body ?? method.ExpressionBody) is { } body:
                        info.Methods.Add(new MethodInfo(method.Identifier.Text, method.ParameterList.Parameters.Count, body, file, lineOffset,
                            method.ParameterList.Parameters.Where(p => p.Type is not null)
                                .ToDictionary(p => p.Identifier.Text, p => Simple(p.Type!), StringComparer.Ordinal))
                        {
                            TypeParameters = method.TypeParameterList?.Parameters.Select(t => t.Identifier.Text).ToList() ?? [],
                        });
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
        var key = owner.Name + "." + method.Name + "/" + method.Parameters; // overloads are different methods
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
        var values = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
        foreach (var local in method.Body.DescendantNodes().OfType<VariableDeclarationSyntax>())
            foreach (var variable in local.Variables)
            {
                if (variable.Initializer?.Value is { } initial) values.TryAdd(variable.Identifier.Text, initial);
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
                InvocationExpressionSyntax invocation => Call(owner, method, invocation, locals, values, path, depthLeft, ref budget),
                AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.Text: var field }, Right: var value } assignment
                    when assignment.Parent is InitializerExpressionSyntax && CommandField().IsMatch(field) && Constant(owner, value) is { } procedure
                         && ProcedureName().IsMatch(procedure) =>
                    Leaf("sp", procedure, null, method, assignment),
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
        Dictionary<string, ExpressionSyntax> values, HashSet<string> path, int depthLeft, ref int budget)
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

        if (name == "GetEnvironmentVariable" && Constant(owner, firstArgument) is { } variable) return Leaf("env", variable, null, method, invocation);
        if (ConfigCalls.Contains(name) && Constant(owner, firstArgument) is { } setting) return Leaf("config", setting, null, method, invocation);

        var arguments = invocation.ArgumentList.Arguments.Select(a => a.Expression).ToList();
        // A stored procedure: called by name with CommandType.StoredProcedure, or by name through a generic runner.
        if (Constant(owner, firstArgument) is { } procedure && ProcedureName().IsMatch(procedure)
            && (arguments.Any(a => a.ToString().EndsWith("StoredProcedure", StringComparison.Ordinal))
                || DbCalls.Contains(name) || ProcedureCall().IsMatch(name)))
            return Leaf("sp", procedure, null, method, invocation);

        // A call to another system named by an endpoint enum, e.g. Http.GetData(path, HttpEndPoints.OPSAPI).
        if (arguments.FirstOrDefault(a => a is MemberAccessExpressionSyntax m && EndpointEnum().IsMatch(m.Expression.ToString())) is MemberAccessExpressionSyntax system
            // The address is the argument that is written out (a literal or built string); a variable only if none is.
            && (arguments.FirstOrDefault(a => a is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } or InterpolatedStringExpressionSyntax or BinaryExpressionSyntax)
                ?? arguments.FirstOrDefault(a => a is IdentifierNameSyntax id && values.ContainsKey(id.Identifier.Text))) is { } address)
            return Leaf("http", system.Name.Identifier.Text, Address(owner, address, values), method, invocation, VerbOf(name, arguments));

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

        if (HttpCalls.Contains(name)) return Leaf("http", name, firstArgument is null ? null : Address(owner, firstArgument, values), method, invocation, VerbOf(name, arguments));
        if (DbCalls.Contains(name) || ProcedureCall().IsMatch(name) && invocation.ArgumentList.Arguments.Any(a => a.ToString().EndsWith("StoredProcedure", StringComparison.Ordinal)))
            return Leaf("db", name, Describe(firstArgument), method, invocation);
        return null;
    }

    private string? DeclaredType(TypeInfo owner, Dictionary<string, string> locals, string identifier) =>
        locals.TryGetValue(identifier, out var local) ? local
        : owner.Members.TryGetValue(identifier, out var member) ? member
        : _types.ContainsKey(identifier) ? identifier // a static call on one of the repo's own types
        : null;

    /// <summary>The HTTP verb of a call: from an argument such as HttpRequestType.POST, else from the method's name.</summary>
    private static string? VerbOf(string name, List<ExpressionSyntax> arguments)
    {
        foreach (var argument in arguments)
            if (argument is MemberAccessExpressionSyntax access && access.Name.Identifier.Text.ToUpperInvariant() is var verb
                && verb is "GET" or "POST" or "PUT" or "DELETE" or "PATCH")
                return verb;
        return name switch
        {
            _ when name.StartsWith("Get", StringComparison.Ordinal) => "GET",
            _ when name.StartsWith("Delete", StringComparison.Ordinal) => "DELETE",
            _ when name.StartsWith("Put", StringComparison.Ordinal) || name.StartsWith("Update", StringComparison.Ordinal) => "PUT",
            _ when name.StartsWith("Patch", StringComparison.Ordinal) => "PATCH",
            _ when name.StartsWith("Post", StringComparison.Ordinal) => "POST",
            _ => null,
        };
    }

    private static CodeNode Leaf(string kind, string title, string? detail, MethodInfo method, SyntaxNode at, string? verb = null) => new()
    {
        Kind = kind,
        Title = title,
        Detail = detail,
        Verb = verb,
        File = method.File,
        Line = method.LineOffset + at.SyntaxTree.GetLineSpan(at.Span).StartLinePosition.Line + 1,
    };

    /// <summary>A string known before the program runs: a literal, or a constant of this repo.</summary>
    private string? Constant(TypeInfo owner, ExpressionSyntax? expression) => expression switch
    {
        LiteralExpressionSyntax => Literal(expression),
        MemberAccessExpressionSyntax access when _constants.TryGetValue(Simple(access.Expression.ToString()) + "." + access.Name.Identifier.Text, out var value) => value,
        IdentifierNameSyntax id when _constants.TryGetValue(owner.Display + "." + id.Identifier.Text, out var value) => value,
        _ => null,
    };

    /// <summary>
    /// Where a call goes, as far as the source says: literal parts kept, settings named in braces
    /// ({EsbUrl}/members?id=…), anything else left as an ellipsis.
    /// </summary>
    private string? Address(TypeInfo owner, ExpressionSyntax expression, Dictionary<string, ExpressionSyntax> values, int depth = 0)
    {
        string Part(ExpressionSyntax part) => Address(owner, part, values, depth + 1) ?? "…";
        if (depth > 4) return null;
        var text = expression switch
        {
            LiteralExpressionSyntax => Literal(expression),
            InterpolatedStringExpressionSyntax interpolated => string.Concat(interpolated.Contents.Select(c => c switch
            {
                InterpolatedStringTextSyntax t => t.TextToken.ValueText,
                InterpolationSyntax i => i.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax
                    ? Part(i.Expression) : "…",
                _ => "",
            })),
            BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } sum => Part(sum.Left) + Part(sum.Right),
            IdentifierNameSyntax id when values.TryGetValue(id.Identifier.Text, out var initial) => Address(owner, initial, values, depth + 1) ?? "{" + id.Identifier.Text + "}",
            InvocationExpressionSyntax call when call.Expression.ToString().EndsWith("GetEnvironmentVariable", StringComparison.Ordinal)
                && Constant(owner, call.ArgumentList.Arguments.FirstOrDefault()?.Expression) is { } setting => "{" + setting + "}",
            ElementAccessExpressionSyntax access when access.Expression.ToString().Contains("onfig", StringComparison.Ordinal)
                && Constant(owner, access.ArgumentList.Arguments.FirstOrDefault()?.Expression) is { } setting => "{" + setting + "}",
            MemberAccessExpressionSyntax or IdentifierNameSyntax => Constant(owner, expression) ?? "{" + expression.ToString().Split('.').Last() + "}",
            _ => null,
        };
        if (text is null) return depth == 0 ? Describe(expression) : null;
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= 140 ? text : text[..139] + "…";
    }

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

    /// <summary>A component tag in markup: &lt;PaymentDetails ...&gt;.</summary>
    [GeneratedRegex(@"<([A-Z][A-Za-z0-9]+)[\s/>]")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^\s*@inherits\s+([\w.<>]+)", RegexOptions.Multiline)]
    private static partial Regex InheritsRegex();

    [GeneratedRegex(@"^\s*@(?:code|functions)\s*\{", RegexOptions.Multiline)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Fields that carry the name of a stored procedure to a generic runner: targetCommand, CommandText, ProcedureName...</summary>
    [GeneratedRegex(@"^(target)?command(name|text)?$|procedure|storedproc|^sp(name)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CommandField();

    /// <summary>A database object name: one word, no spaces, usually with an underscore or a schema dot (Member_Save, dbo.GetX).</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*([_.][A-Za-z0-9_]+)+$")]
    private static partial Regex ProcedureName();

    [GeneratedRegex(@"Execute|Query|Command|Proc|^Get(All)?(Async)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ProcedureCall();

    [GeneratedRegex(@"End[Pp]oints?$|Apis?$|Services?$|Systems?$")]
    private static partial Regex EndpointEnum();
}
