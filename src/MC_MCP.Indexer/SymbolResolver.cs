namespace MC_MCP.Indexer;

/// <summary>单个 Java 文件的解析上下文，用于短名到 FQN 的解析。</summary>
public sealed record JavaFileContext(
    string FilePath,
    string? PackageName,
    IReadOnlyList<string> Imports,
    IReadOnlyList<string> StaticImports);

/// <summary>
/// 已知类型索引：FQN 集合 + 简单名反查表。
/// 简单名同时支持 Java 源码写法（Outer.Inner）与数据库中的二进制写法（Outer$Inner）。
/// </summary>
public sealed class KnownTypeIndex
{
    private readonly HashSet<string> _fqns;
    private readonly Dictionary<string, List<string>> _bySimpleName = new(StringComparer.Ordinal);

    public KnownTypeIndex(IEnumerable<string> fqns)
    {
        _fqns = new HashSet<string>(fqns, StringComparer.Ordinal);
        foreach (var fqn in _fqns)
        {
            var simpleName = GetSimpleName(fqn);
            if (!_bySimpleName.TryGetValue(simpleName, out var list))
            {
                list = new List<string>();
                _bySimpleName[simpleName] = list;
            }

            list.Add(fqn);
        }
    }

    public IReadOnlySet<string> Fqns => _fqns;

    public bool Contains(string fqn) => _fqns.Contains(fqn);

    public IReadOnlyList<string> FindBySimpleName(string simpleName)
        => _bySimpleName.GetValueOrDefault(simpleName) ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>把 a.b.Outer.Inner 逐段尝试转成 a.b.Outer$Inner，只返回真实存在的 FQN。</summary>
    public string NormalizeNested(string candidate)
    {
        if (_fqns.Contains(candidate))
        {
            return candidate;
        }

        var segments = candidate.Split('.');
        for (var split = segments.Length - 1; split >= 1; split--)
        {
            var prefix = string.Join('.', segments.Take(split));
            var nested = string.Join('$', segments.Skip(split));
            var normalized = $"{prefix}${nested}";
            if (_fqns.Contains(normalized))
            {
                return normalized;
            }
        }

        return candidate;
    }

    /// <summary>在指定命名空间下按简单名查找唯一类型（用于同包/通配 import 的嵌套类）。</summary>
    public string? FindUniqueUnderNamespace(string namespacePrefix, string simpleName)
    {
        var matches = new List<string>();
        foreach (var fqn in FindBySimpleName(simpleName))
        {
            if (namespacePrefix.Length == 0 ||
                fqn.StartsWith($"{namespacePrefix}.", StringComparison.Ordinal))
            {
                matches.Add(fqn);
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    public static string GetSimpleName(string fqn)
    {
        var separator = fqn.LastIndexOfAny(new[] { '.', '$' });
        return separator >= 0 ? fqn[(separator + 1)..] : fqn;
    }
}

/// <summary>基于 package / import / 已知类型表将 Java 短名解析为 FQN。</summary>
public static class SymbolResolver
{
    private static readonly HashSet<string> JavaLangTypes = new(StringComparer.Ordinal)
    {
        "Object", "String", "Integer", "Long", "Short", "Byte", "Double", "Float", "Boolean",
        "Character", "Void", "Number", "Math", "System", "Thread", "Runnable", "Exception",
        "RuntimeException", "Error", "Throwable", "StringBuilder", "StringBuffer", "Iterable",
        "Comparable", "CharSequence", "Class", "Enum", "Record", "Runtime", "Process",
        "StackTraceElement", "AutoCloseable", "Override", "Deprecated", "SuppressWarnings"
    };

    /// <summary>
    /// 解析顺序：已限定名（含嵌套类）→ 精确 import → 同包已知符号 → 通配 import 中的已知符号 →
    /// java.lang 常用类型 → 保留原样。
    /// </summary>
    public static string ResolveType(string rawName, JavaFileContext context, KnownTypeIndex knownTypes)
    {
        var name = rawName.Trim();
        if (name.Length == 0)
        {
            return rawName;
        }

        if (name.Contains('.'))
        {
            return ResolveQualified(name, context, knownTypes);
        }

        return ResolveSimple(name, context, knownTypes);
    }

    private static string ResolveQualified(string name, JavaFileContext context, KnownTypeIndex knownTypes)
    {
        // 完整 FQN 或已写入 $ 的嵌套类。
        var normalized = knownTypes.NormalizeNested(name);
        if (knownTypes.Contains(normalized))
        {
            return normalized;
        }

        // 形如 Outer.Inner 的引用：先解析 Outer，再拼回 Inner 查表。
        var separator = name.IndexOf('.');
        var head = name[..separator];
        var tail = name[(separator + 1)..];
        if (IsSimpleIdentifier(head))
        {
            var resolvedHead = ResolveSimple(head, context, knownTypes);
            if (resolvedHead.Contains('.'))
            {
                var candidate = knownTypes.NormalizeNested($"{resolvedHead}.{tail}");
                if (knownTypes.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        return name;
    }

    private static string ResolveSimple(string name, JavaFileContext context, KnownTypeIndex knownTypes)
    {
        if (name is "byte" or "short" or "int" or "long" or "float" or "double" or "boolean" or "char" or "void")
        {
            return name;
        }

        // 1) 精确 import：如 import a.b.Outer.Inner; 引用 Inner。
        foreach (var importName in context.Imports)
        {
            if (importName.EndsWith($".{name}", StringComparison.Ordinal))
            {
                var normalized = knownTypes.NormalizeNested(importName);
                return knownTypes.Contains(normalized) ? normalized : importName;
            }
        }

        // 2) 同包：优先生成 package.Name，再尝试 package.Outer$Name 的嵌套类。
        if (context.PackageName is not null)
        {
            var samePackage = $"{context.PackageName}.{name}";
            var normalized = knownTypes.NormalizeNested(samePackage);
            if (knownTypes.Contains(normalized))
            {
                return normalized;
            }

            var nested = knownTypes.FindUniqueUnderNamespace(context.PackageName, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        // 3) 通配 import：a.b.* → a.b.Name 或 a.b.Outer$Name。
        foreach (var importName in context.Imports)
        {
            if (!importName.EndsWith(".*", StringComparison.Ordinal))
            {
                continue;
            }

            var namespacePrefix = importName[..^2];
            var candidate = $"{namespacePrefix}.{name}";
            if (knownTypes.Contains(candidate))
            {
                return candidate;
            }

            var nested = knownTypes.FindUniqueUnderNamespace(namespacePrefix, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        // 4) java.lang 常用类型。
        if (JavaLangTypes.Contains(name))
        {
            return $"java.lang.{name}";
        }

        return name;
    }

    private static bool IsSimpleIdentifier(string value) => value.Length > 0 &&
        (char.IsLetter(value[0]) || value[0] == '_' || value[0] == '$') &&
        value.All(character => char.IsLetterOrDigit(character) || character is '_' or '$');
}
