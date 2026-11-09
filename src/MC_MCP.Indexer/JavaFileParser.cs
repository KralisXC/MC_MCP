using System.Text.RegularExpressions;
using TreeSitter;

namespace MC_MCP.Indexer;

/// <summary>单个 Java 文件的解析结果，写入队列后由单线程消费者落库。</summary>
internal sealed record FileParseResult(
    string FilePath,
    string Sha256,
    bool Succeeded,
    string? PackageName,
    IReadOnlyList<string> Imports,
    IReadOnlyList<string> StaticImports,
    IReadOnlyList<SymbolRecord> Symbols,
    IReadOnlyList<EdgeRecord> Edges,
    IReadOnlyList<InjectionRecord> Injections,
    IReadOnlyList<ParseErrorRecord> Errors,
    IReadOnlyList<CodeChunkRecord> Chunks)
{
    public static FileParseResult Failed(string filePath, string sha256, string message)
        => new(filePath, sha256, false, null, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<SymbolRecord>(), Array.Empty<EdgeRecord>(), Array.Empty<InjectionRecord>(),
            new[] { new ParseErrorRecord(filePath, 0, "exception", null, message) },
            Array.Empty<CodeChunkRecord>());
}

/// <summary>写入 code_fts 的源码切片（默认 40 行一段）。</summary>
internal sealed record CodeChunkRecord(
    string FilePath,
    int StartLine,
    int EndLine,
    string Content);

internal sealed record SymbolRecord(
    string Kind,
    string Name,
    string Fqn,
    string? PackageName,
    string? Signature,
    string? ReturnType,
    string FilePath,
    int StartLine,
    int EndLine,
    string Origin,
    string? Comment);

internal sealed record EdgeRecord(
    string SrcFqn,
    string? SrcMethod,
    string DstFqn,
    string? DstMember,
    string EdgeType,
    string FilePath,
    int Line);

internal sealed record InjectionRecord(
    string MixinClass,
    string? TargetClass,
    string Annotation,
    string? TargetMethod,
    string? AtValue,
    string? Arguments,
    string FilePath,
    int Line);

internal sealed record ParseErrorRecord(
    string FilePath,
    int Line,
    string NodeType,
    string? Snippet,
    string Message);

/// <summary>
/// 每个线程持有独立实例：独立 Language / Parser / Query，避免 Tree-sitter 的线程安全问题。
/// </summary>
internal sealed class JavaFileParser : IDisposable
{
    private const int MaxErrorsPerFile = 50;
    private const int ChunkLineCount = 40;
    private const int MaxCommentLength = 400;

    private static readonly HashSet<string> TypeDeclarationTypes = new(StringComparer.Ordinal)
    {
        "class_declaration", "interface_declaration", "enum_declaration",
        "record_declaration", "annotation_type_declaration"
    };

    private const string QuerySource =
        """
        (package_declaration) @package
        (import_declaration) @import
        (class_declaration name: (identifier) @type.name) @type.decl
        (interface_declaration name: (identifier) @type.name) @type.decl
        (enum_declaration name: (identifier) @type.name) @type.decl
        (method_declaration name: (identifier) @method.name parameters: (formal_parameters) @method.params) @method.decl
        (constructor_declaration name: (identifier) @ctor.name parameters: (formal_parameters) @ctor.params) @ctor.decl
        (superclass (type_identifier) @edge.extends)
        (super_interfaces (type_list (type_identifier) @edge.implements))
        (extends_interfaces (type_list (type_identifier) @edge.interface_extends))
        (method_invocation) @call.invocation
        (object_creation_expression) @call.new
        (annotation) @annotation
        (marker_annotation) @annotation
        """;

    private static readonly Regex AtValuePattern = new(
        "@At\\s*\\(\\s*(?:value\\s*=\\s*)?\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled);

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 解析阶段就能确认的 JDK 标准库前缀：这些调用/实例化边与 Minecraft 源码分析无关，
    /// 直接丢弃可减少写库量与后续图遍历噪声（简单类名如 String 交给落库后的 knownFqns 过滤）。
    /// </summary>
    private static readonly string[] StandardLibraryPrefixes =
    {
        "java.", "javax.", "sun.", "jdk.", "com.sun.", "com.google.", "org.slf4j.", "org.apache."
    };

    private static bool IsStandardLibraryName(string name)
    {
        foreach (var prefix in StandardLibraryPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private readonly Language _language;
    private readonly Parser _parser;
    private readonly Query _query;

    public JavaFileParser()
    {
        _language = new Language("Java");
        _parser = new Parser(_language);
        _query = new Query(_language, QuerySource);
    }

    public FileParseResult Parse(string relativePath, string source, string sha256)
    {
        using var tree = _parser.Parse(source);
        if (tree is null)
        {
            return FileParseResult.Failed(relativePath, sha256, "Tree-sitter 返回空语法树");
        }

        var root = tree.RootNode;
        using var cursor = _query.Execute(root);
        var captures = cursor.Captures.ToList();

        var packageName = ResolvePackage(captures);
        var imports = ResolveImports(captures);
        var origin = ResolveOrigin(relativePath);
        var mixinTargets = BuildMixinTargets(captures, packageName);
        var symbols = new List<SymbolRecord>();
        var edges = new List<EdgeRecord>();
        var injections = new List<InjectionRecord>();

        foreach (var capture in captures)
        {
            var node = capture.Node;
            switch (capture.Name)
            {
                case "type.decl":
                {
                    var kind = node.Type switch
                    {
                        "class_declaration" => "class",
                        "interface_declaration" => "interface",
                        "enum_declaration" => "enum",
                        _ => null
                    };
                    var name = node.GetChildForField("name")?.Text;
                    if (kind is null || name is null)
                    {
                        break;
                    }

                    symbols.Add(new SymbolRecord(
                        kind,
                        name,
                        BuildDeclaredTypeFqn(node, packageName)!,
                        packageName,
                        null,
                        null,
                        relativePath,
                        node.StartPosition.Row + 1,
                        node.EndPosition.Row + 1,
                        origin,
                        ExtractLeadingComment(node)));
                    break;
                }

                case "method.decl":
                case "ctor.decl":
                {
                    var name = node.GetChildForField("name")?.Text;
                    if (name is null)
                    {
                        break;
                    }

                    var signature = BuildMethodSignature(node);
                    var enclosingFqn = FindEnclosingTypeFqn(node, packageName);
                    var returnType = node.GetChildForField("type")?.Text;
                    symbols.Add(new SymbolRecord(
                        capture.Name == "ctor.decl" ? "constructor" : "method",
                        name,
                        enclosingFqn is null ? signature : $"{enclosingFqn}#{signature}",
                        packageName,
                        signature,
                        returnType is null ? null : Compact(returnType),
                        relativePath,
                        node.StartPosition.Row + 1,
                        node.EndPosition.Row + 1,
                        origin,
                        ExtractLeadingComment(node)));
                    break;
                }

                case "edge.extends":
                case "edge.implements":
                case "edge.interface_extends":
                {
                    var owningType = FindEnclosingType(node);
                    var sourceFqn = BuildDeclaredTypeFqn(owningType, packageName);
                    if (sourceFqn is null)
                    {
                        break;
                    }

                    edges.Add(new EdgeRecord(
                        sourceFqn,
                        null,
                        Compact(node.Text),
                        null,
                        capture.Name == "edge.implements" ? "implements" : "extends",
                        relativePath,
                        node.StartPosition.Row + 1));
                    break;
                }

                case "call.invocation":
                {
                    var member = node.GetChildForField("name")?.Text;
                    if (member is null)
                    {
                        break;
                    }

                    var receiver = node.GetChildForField("object")?.Text;
                    var receiverName = receiver is null ? null : Compact(receiver);
                    if (receiverName is not null && IsStandardLibraryName(receiverName))
                    {
                        break;
                    }

                    var context = ResolveCallContext(node, packageName);
                    edges.Add(new EdgeRecord(
                        context.SourceFqn,
                        context.SourceMethod,
                        receiverName ?? context.SourceFqn,
                        member,
                        "call",
                        relativePath,
                        node.StartPosition.Row + 1));
                    break;
                }

                case "call.new":
                {
                    var typeName = node.GetChildForField("type")?.Text;
                    if (typeName is null)
                    {
                        break;
                    }

                    var compactType = Compact(typeName);
                    if (IsStandardLibraryName(compactType))
                    {
                        break;
                    }

                    var context = ResolveCallContext(node, packageName);
                    edges.Add(new EdgeRecord(
                        context.SourceFqn,
                        context.SourceMethod,
                        compactType,
                        "<init>",
                        "instantiation",
                        relativePath,
                        node.StartPosition.Row + 1));
                    break;
                }
            }
        }

        foreach (var capture in captures)
        {
            if (capture.Name != "annotation" || !IsDirectAnnotation(capture.Node))
            {
                continue;
            }

            var annotation = capture.Node;
            var annotationName = annotation.GetChildForField("name")?.Text;
            if (annotationName is not ("Mixin" or "Inject" or "Redirect"))
            {
                continue;
            }

            var arguments = ParseAnnotationArguments(annotation);
            var argumentsNode = annotation.GetChildForField("arguments");
            var mixinClass = FindEnclosingTypeFqn(annotation, packageName) ?? "<unknown>";
            var targetClass = annotationName == "Mixin"
                ? NormalizeMixinTarget(arguments.GetValueOrDefault("targets") ?? arguments.GetValueOrDefault("value"))
                : mixinTargets.GetValueOrDefault(mixinClass);
            var targetMethod = annotationName == "Mixin"
                ? null
                : Unquote(arguments.GetValueOrDefault("method"));
            var atValue = annotationName == "Mixin"
                ? null
                : ExtractAtValue(arguments.GetValueOrDefault("at"));

            injections.Add(new InjectionRecord(
                mixinClass,
                targetClass,
                annotationName,
                targetMethod,
                atValue,
                argumentsNode?.Text,
                relativePath,
                annotation.StartPosition.Row + 1));
        }

        var errors = new List<ParseErrorRecord>();
        if (root.HasError)
        {
            CollectErrors(root, relativePath, errors);
        }

        return new FileParseResult(
            relativePath,
            sha256,
            true,
            packageName,
            imports.Imports,
            imports.StaticImports,
            symbols,
            edges,
            injections,
            errors,
            BuildChunks(relativePath, source));
    }

    public void Dispose()
    {
        _query.Dispose();
        _parser.Dispose();
        _language.Dispose();
    }

    private static void CollectErrors(Node root, string filePath, List<ParseErrorRecord> errors)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0 && errors.Count < MaxErrorsPerFile)
        {
            var node = stack.Pop();
            if (node.IsError || node.IsMissing)
            {
                errors.Add(new ParseErrorRecord(
                    filePath,
                    node.StartPosition.Row + 1,
                    node.Type,
                    Truncate(Compact(node.Text), 160),
                    node.IsMissing ? "missing node" : "syntax error"));
                continue;
            }

            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }
    }

    private static ImportDeclarations ResolveImports(IReadOnlyList<QueryCapture> captures)
    {
        var imports = new List<string>();
        var staticImports = new List<string>();
        foreach (var capture in captures)
        {
            if (capture.Name != "import")
            {
                continue;
            }

            var text = capture.Node.Text.Trim();
            if (!text.StartsWith("import ", StringComparison.Ordinal))
            {
                continue;
            }

            var body = text["import ".Length..].Trim().TrimEnd(';').Trim();
            if (body.StartsWith("static ", StringComparison.Ordinal))
            {
                staticImports.Add(body["static ".Length..].Trim());
            }
            else
            {
                imports.Add(body);
            }
        }

        return new ImportDeclarations(imports, staticImports);
    }

    private static string? ResolvePackage(IReadOnlyList<QueryCapture> captures)
    {
        foreach (var capture in captures)
        {
            if (capture.Name != "package")
            {
                continue;
            }

            var identifier = capture.Node.NamedChildren
                .FirstOrDefault(child => child.Type is "scoped_identifier" or "identifier");
            var name = identifier?.Text.Trim();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        return null;
    }

    private static Dictionary<string, string> BuildMixinTargets(
        IReadOnlyList<QueryCapture> captures,
        string? packageName)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var capture in captures)
        {
            if (capture.Name != "annotation" || !IsDirectAnnotation(capture.Node))
            {
                continue;
            }

            var annotation = capture.Node;
            if (annotation.GetChildForField("name")?.Text != "Mixin")
            {
                continue;
            }

            var mixinClass = FindEnclosingTypeFqn(annotation, packageName);
            var arguments = ParseAnnotationArguments(annotation);
            var target = NormalizeMixinTarget(
                arguments.GetValueOrDefault("targets") ?? arguments.GetValueOrDefault("value"));
            if (mixinClass is not null && target is not null)
            {
                result[mixinClass] = target;
            }
        }

        return result;
    }

    /// <summary>
    /// Phase 10：向上收集紧贴声明的 block_comment / line_comment（Javadoc 在 tree-sitter-java 里也是 block_comment）。
    /// 两条注释之间或注释与声明之间允许相邻行，出现空行即视为无关注释，停止向上找。
    /// </summary>
    private static string? ExtractLeadingComment(Node declaration)
    {
        var nextStartRow = declaration.StartPosition.Row;
        var sibling = declaration.PreviousNamedSibling;
        var parts = new List<string>();
        while (sibling is not null && sibling.Type is "block_comment" or "line_comment")
        {
            if (sibling.EndPosition.Row < nextStartRow - 1)
            {
                break;
            }

            parts.Add(sibling.Text);
            nextStartRow = sibling.StartPosition.Row;
            sibling = sibling.PreviousNamedSibling;
        }

        if (parts.Count == 0)
        {
            return null;
        }

        parts.Reverse();
        return CleanComment(string.Join('\n', parts));
    }

    /// <summary>去掉 /**、*、// 等装饰，压成一行自然语言；超过 400 字符截断，避免污染向量文本。</summary>
    private static string? CleanComment(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("/**", StringComparison.Ordinal))
        {
            text = text[3..];
        }
        else if (text.StartsWith("/*", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        if (text.EndsWith("*/", StringComparison.Ordinal))
        {
            text = text[..^2];
        }

        var builder = new System.Text.StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var cleaned = line.Trim();
            if (cleaned.StartsWith("//", StringComparison.Ordinal))
            {
                cleaned = cleaned[2..].Trim();
            }

            cleaned = cleaned.TrimStart('*').Trim();
            if (cleaned.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(cleaned);
        }

        var result = WhitespacePattern.Replace(builder.ToString(), " ").Trim();
        return result.Length == 0 ? null : Truncate(result, MaxCommentLength);
    }

    private static string BuildMethodSignature(Node declaration)
    {
        var name = declaration.GetChildForField("name")?.Text ?? "?";
        var parameters = declaration.GetChildForField("parameters");
        var types = new List<string>();
        if (parameters is not null)
        {
            foreach (var child in parameters.NamedChildren)
            {
                if (child.Type is not ("formal_parameter" or "spread_parameter" or "receiver_parameter"))
                {
                    continue;
                }

                types.Add(GetParameterType(child));
            }
        }

        return $"{name}({string.Join(", ", types)})";
    }

    private static string GetParameterType(Node parameter)
    {
        var type = parameter.GetChildForField("type");
        if (type is not null)
        {
            return Compact(type.Text);
        }

        var text = Compact(parameter.Text);
        var separator = text.LastIndexOf(' ');
        return separator > 0 ? text[..separator] : text;
    }

    private static CallContext ResolveCallContext(Node node, string? packageName)
    {
        var enclosingType = FindEnclosingType(node);
        var sourceFqn = BuildDeclaredTypeFqn(enclosingType, packageName) ?? "<unknown>";
        var enclosingMethod = FindEnclosingMethod(node);
        var sourceMethod = enclosingMethod is null ? null : BuildMethodSignature(enclosingMethod);
        return new CallContext(sourceFqn, sourceMethod);
    }

    private static Node? FindEnclosingType(Node node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (TypeDeclarationTypes.Contains(current.Type))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static Node? FindEnclosingMethod(Node node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current.Type is "method_declaration" or "constructor_declaration")
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? FindEnclosingTypeFqn(Node node, string? packageName)
        => BuildDeclaredTypeFqn(FindEnclosingType(node), packageName);

    /// <summary>
    /// 拼出类型 FQN：顶层为 package.Outer，嵌套类为 package.Outer$Inner（与 JVM 二进制名一致）。
    /// </summary>
    private static string? BuildDeclaredTypeFqn(Node? typeDeclaration, string? packageName)
    {
        if (typeDeclaration is null)
        {
            return null;
        }

        var names = new List<string>();
        var current = typeDeclaration;
        while (current is not null)
        {
            if (TypeDeclarationTypes.Contains(current.Type))
            {
                var name = current.GetChildForField("name")?.Text;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Insert(0, name);
                }
            }

            current = current.Parent;
        }

        if (names.Count == 0)
        {
            return null;
        }

        var typeName = string.Join('$', names);
        return string.IsNullOrWhiteSpace(packageName) ? typeName : $"{packageName}.{typeName}";
    }

    /// <summary>
    /// 按相对路径推断符号层来源：code/ 为原版；mods/neoforge 或 versions/*-neoforge 为 NeoForge；
    /// mods/fabric 或 *-fabric 为 Fabric；其余 mods 归为普通 mod。
    /// </summary>
    private static string ResolveOrigin(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        if (path.StartsWith("code/", StringComparison.OrdinalIgnoreCase))
        {
            return "vanilla";
        }

        if (path.StartsWith("mods/neoforge/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/neoforge/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("-neoforge/", StringComparison.OrdinalIgnoreCase))
        {
            return "neoforge";
        }

        if (path.StartsWith("mods/fabric/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/fabric/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("-fabric/", StringComparison.OrdinalIgnoreCase))
        {
            return "fabric";
        }

        return "mod";
    }

    private static bool IsDirectAnnotation(Node annotation)
    {
        var parent = annotation.Parent;
        return parent is not null && parent.Type == "modifiers";
    }

    private static Dictionary<string, string> ParseAnnotationArguments(Node annotation)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var arguments = annotation.GetChildForField("arguments");
        if (arguments is null)
        {
            return result;
        }

        foreach (var child in arguments.NamedChildren)
        {
            if (child.Type != "element_value_pair")
            {
                result["value"] = child.Text;
                continue;
            }

            var key = child.GetChildForField("key")?.Text;
            var value = child.GetChildForField("value");
            if (key is not null && value is not null)
            {
                result[key] = value.Text;
            }
        }

        return result;
    }

    private static string? ExtractAtValue(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var match = AtValuePattern.Match(raw);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string? Unquote(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return value;
    }

    /// <summary>去掉 Mixin targets 里的 .class 后缀，数组写法只取第一个目标。</summary>
    private static string? NormalizeMixinTarget(string? raw)
    {
        var value = Unquote(raw)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith('{') && value.EndsWith('}'))
        {
            value = value[1..^1].Split(',').FirstOrDefault()?.Trim() ?? string.Empty;
        }

        if (value.EndsWith(".class", StringComparison.Ordinal))
        {
            value = value[..^".class".Length].Trim();
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>把源码切成 40 行一段，供 code_fts 全文检索。</summary>
    private static List<CodeChunkRecord> BuildChunks(string filePath, string source)
    {
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var chunks = new List<CodeChunkRecord>();
        for (var start = 0; start < lines.Length; start += ChunkLineCount)
        {
            var end = Math.Min(start + ChunkLineCount, lines.Length);
            var content = string.Join("\n", lines, start, end - start);
            if (content.Trim().Length == 0)
            {
                continue;
            }

            chunks.Add(new CodeChunkRecord(filePath, start + 1, end, content));
        }

        return chunks;
    }

    private static string Compact(string text) => WhitespacePattern.Replace(text, " ").Trim();

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength];

    private sealed record CallContext(string SourceFqn, string? SourceMethod);

    private sealed record ImportDeclarations(IReadOnlyList<string> Imports, IReadOnlyList<string> StaticImports);
}
