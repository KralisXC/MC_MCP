using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MC_MCP.Indexer;

/// <summary>面向 MCP 工具与 Launcher 的只读索引查询服务。</summary>
public sealed class SymbolQueryService
{
    // internal_call：同一类内部方法之间的调用（Phase 9 由 call 降级而来），默认不返回，避免刷屏。
    private static readonly string[] KnownEdgeTypes =
        { "call", "instantiation", "extends", "implements", "internal_call" };

    private readonly IndexDatabase _database;
    private readonly InMemoryIndex? _memory;
    private readonly string _sourceRoot;

    public SymbolQueryService(IndexDatabase database, InMemoryIndex? memory = null, string? sourceRoot = null)
    {
        _database = database;
        _memory = memory;
        // 索引里的 file_path 是相对工程根的路径，读取源码时需要在同一根目录下拼接。
        _sourceRoot = string.IsNullOrWhiteSpace(sourceRoot)
            ? IndexDatabase.ResolveProjectRoot()
            : sourceRoot;
    }

    /// <summary>
    /// 跨层合并：同一 FQN 在 vanilla 与 NeoForge 中都存在时，合并为一条记录并保留所有来源。
    /// 这让 AI 既能看到原版签名，也能看到 NeoForge 补丁层新增/改写的版本。
    /// offset 按“合并后的 FQN”分页：先用 GROUP BY 取一页 FQN，再回查这些 FQN 的所有来源。
    /// </summary>
    public IReadOnlyList<MergedSymbol> SearchSymbols(
        string query,
        int topK = 20,
        int offset = 0,
        string? kind = null)
    {
        var limit = Math.Clamp(topK, 1, 100);
        var skip = Math.Max(0, offset);
        var kindFilter = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant();
        using var connection = _database.OpenConnection();

        var fqns = new List<string>(limit);
        using (var pageCommand = connection.CreateCommand())
        {
            pageCommand.CommandText =
                """
                SELECT fqn
                FROM symbols
                WHERE (name LIKE '%' || $query || '%' OR fqn LIKE '%' || $query || '%')
                  AND ($kind IS NULL OR kind = $kind)
                GROUP BY fqn
                ORDER BY
                    MIN(CASE
                        WHEN name = $query THEN 0
                        WHEN name LIKE $query || '%' THEN 1
                        ELSE 2
                    END),
                    fqn
                LIMIT $limit OFFSET $offset;
                """;
            pageCommand.Parameters.AddWithValue("$query", query);
            pageCommand.Parameters.AddWithValue("$kind", kindFilter ?? (object)DBNull.Value);
            pageCommand.Parameters.AddWithValue("$limit", limit);
            pageCommand.Parameters.AddWithValue("$offset", skip);
            using var pageReader = pageCommand.ExecuteReader();
            while (pageReader.Read())
            {
                fqns.Add(pageReader.GetString(0));
            }
        }

        if (fqns.Count == 0)
        {
            return Array.Empty<MergedSymbol>();
        }

        using var command = connection.CreateCommand();
        var placeholders = new List<string>(fqns.Count);
        for (var index = 0; index < fqns.Count; index++)
        {
            var name = $"$fqn{index}";
            placeholders.Add(name);
            command.Parameters.AddWithValue(name, fqns[index]);
        }

        command.CommandText =
            $"""
            SELECT fqn, kind, signature, origin, file_path, start_line, end_line, comment
            FROM symbols
            WHERE fqn IN ({string.Join(", ", placeholders)});
            """;

        var grouped = new Dictionary<string, List<SymbolRow>>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var row = new SymbolRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    (int)reader.GetInt64(5),
                    (int)reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7));
                if (!grouped.TryGetValue(row.Fqn, out var list))
                {
                    list = new List<SymbolRow>();
                    grouped[row.Fqn] = list;
                }

                list.Add(row);
            }
        }

        var results = new List<MergedSymbol>(fqns.Count);
        foreach (var fqn in fqns)
        {
            if (grouped.TryGetValue(fqn, out var rows))
            {
                results.Add(Merge(fqn, rows));
            }
        }

        return results;
    }

    private static MergedSymbol Merge(string fqn, List<SymbolRow> rows)
    {
        var ordered = rows
            .OrderBy(row => OriginRank(row.Origin))
            .ThenBy(row => row.FilePath, StringComparer.Ordinal)
            .ToList();
        var origins = ordered
            .Select(row => row.Origin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var primary = ordered[0];
        var signature = primary.Signature ?? ordered.FirstOrDefault(row => row.Signature is not null)?.Signature;
        var comment = primary.Comment ?? ordered.FirstOrDefault(row => row.Comment is not null)?.Comment;
        return new MergedSymbol(
            fqn,
            primary.Kind,
            signature,
            comment,
            DisplayOrigin(primary.Origin, origins),
            origins,
            ordered
                .Select(row => new SymbolOriginSource(row.Origin, row.FilePath, row.StartLine, row.EndLine))
                .ToList());
    }

    /// <summary>NeoForge 独有符号在对外 JSON 中标记为 neoforge_added，便于 AI 区分“新增”与“覆盖”。</summary>
    private static string DisplayOrigin(string origin, IReadOnlyList<string> origins)
        => origin.Equals("neoforge", StringComparison.OrdinalIgnoreCase) &&
           !origins.Any(item => item.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
            ? "neoforge_added"
            : origin;

    private static int OriginRank(string origin) => origin.ToLowerInvariant() switch
    {
        "vanilla" => 0,
        "neoforge" => 1,
        "fabric" => 2,
        "mod" => 3,
        _ => 4
    };

    public SymbolSource? GetSymbolSource(string fqn)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT fqn, kind, signature, origin, file_path, start_line, end_line, comment
            FROM symbols
            WHERE fqn = $fqn
            ORDER BY
                CASE lower(origin)
                    WHEN 'vanilla' THEN 0
                    WHEN 'neoforge' THEN 1
                    WHEN 'fabric' THEN 2
                    ELSE 3
                END,
                file_path
            LIMIT 50;
            """;
        command.Parameters.AddWithValue("$fqn", fqn);

        var rows = new List<SymbolRow>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(new SymbolRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    (int)reader.GetInt64(5),
                    (int)reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        if (rows.Count == 0)
        {
            return null;
        }

        var primary = rows[0];
        var origins = rows
            .Select(row => row.Origin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SymbolSource(
            primary.Fqn,
            primary.Kind,
            primary.Signature ?? rows.FirstOrDefault(row => row.Signature is not null)?.Signature,
            primary.Comment ?? rows.FirstOrDefault(row => row.Comment is not null)?.Comment,
            DisplayOrigin(primary.Origin, origins),
            primary.FilePath,
            primary.StartLine,
            primary.EndLine,
            rows
                .Select(row => new SymbolOriginSource(row.Origin, row.FilePath, row.StartLine, row.EndLine))
                .ToList());
    }

    public IReadOnlyList<EdgeSummary> GetUsedBy(string fqn, int limit = 50, int offset = 0)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT src_fqn, src_method, dst_fqn, dst_member, edge_type, file_path, line
            FROM edges
            WHERE dst_fqn = $fqn
               OR dst_member = $fqn
               OR (src_fqn = $fqn AND edge_type IN ('extends', 'implements'))
            ORDER BY edge_type, src_fqn, line
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$fqn", fqn);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        var results = new List<EdgeSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new EdgeSummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                (int)reader.GetInt64(6)));
        }

        return results;
    }

    /// <summary>查询继承/实现了指定类的所有子类（edges 中的 extends/implements 入边）。</summary>
    public IReadOnlyList<InheritorSummary> GetInheritors(string fqn, int limit = 100, int offset = 0)
    {
        var results = QueryInheritors(fqn, limit, offset, suffixMatch: false);
        if (results.Count == 0 && offset == 0 && !fqn.Contains('.'))
        {
            results = QueryInheritors(fqn, limit, offset, suffixMatch: true);
        }

        return results;
    }

    private IReadOnlyList<InheritorSummary> QueryInheritors(string fqn, int limit, int offset, bool suffixMatch)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        var condition = suffixMatch
            ? "(dst_fqn LIKE '%.' || $fqn OR dst_fqn LIKE '%$' || $fqn)"
            : "dst_fqn = $fqn";
        command.CommandText =
            $"""
            SELECT src_fqn, edge_type, file_path, line
            FROM edges
            WHERE edge_type IN ('extends', 'implements')
              AND {condition}
            ORDER BY src_fqn, edge_type
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$fqn", fqn);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        var results = new List<InheritorSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new InheritorSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                (int)reader.GetInt64(3)));
        }

        return results;
    }

    /// <summary>
    /// 查询指定类的全部出边：调用了谁、实例化了谁、继承了谁。
    /// edgeType 可选 call / instantiation / extends / implements / internal_call（支持逗号分隔）；
    /// 不指定时默认排除 internal_call（同类内部调用），并对 call 边做启发式过滤，
    /// 丢掉 player.foo() 这类以小写局部变量/字段为接收者的噪声。
    /// </summary>
    public IReadOnlyList<EdgeSummary> GetSymbolUses(
        string fqn,
        int limit = 200,
        string? edgeType = null,
        int offset = 0,
        string? dstMember = null)
    {
        var types = ParseEdgeTypes(edgeType);
        var results = QuerySymbolUses(fqn, limit, offset, suffixMatch: false, types, dstMember);
        if (results.Count == 0 && offset == 0 && !fqn.Contains('.'))
        {
            results = QuerySymbolUses(fqn, limit, offset, suffixMatch: true, types, dstMember);
        }

        return results;
    }

    private static IReadOnlyList<string> ParseEdgeTypes(string? edgeType)
    {
        if (string.IsNullOrWhiteSpace(edgeType))
        {
            return Array.Empty<string>();
        }

        var requested = edgeType
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToLowerInvariant())
            .Where(item => KnownEdgeTypes.Contains(item))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return requested;
    }

    private IReadOnlyList<EdgeSummary> QuerySymbolUses(
        string fqn,
        int limit,
        int offset,
        bool suffixMatch,
        IReadOnlyList<string> edgeTypes,
        string? dstMember)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        var condition = suffixMatch
            ? "(src_fqn LIKE '%.' || $fqn OR src_fqn LIKE '%$' || $fqn)"
            : "src_fqn = $fqn";

        // 默认不返回同类内部调用：图剪枝后它们数量最多，但信息价值最低。
        var typeCondition = " AND edge_type <> 'internal_call'";
        if (edgeTypes.Count > 0)
        {
            var placeholders = new List<string>();
            for (var index = 0; index < edgeTypes.Count; index++)
            {
                var name = $"$edgeType{index}";
                placeholders.Add(name);
                command.Parameters.AddWithValue(name, edgeTypes[index]);
            }

            typeCondition = $" AND edge_type IN ({string.Join(", ", placeholders)})";
        }

        // call 边的接收者只有在 symbols 中能找到同名类（FQN 或简单名）时才保留；
        // player.foo()、stack.push()、animLineDataList.add() 这类局部变量/字段链会被过滤掉。
        var noiseFilter =
            """
            edge_type <> 'call'
            OR dst_fqn IN ('this', 'super')
            OR dst_fqn IN (SELECT fqn FROM symbols WHERE kind NOT IN ('method', 'constructor'))
            OR dst_fqn IN (SELECT name FROM symbols WHERE kind NOT IN ('method', 'constructor'))
            """;

        command.CommandText =
            $"""
            SELECT src_fqn, src_method, dst_fqn, dst_member, edge_type, file_path, line
            FROM edges
            WHERE {condition}
              AND ({noiseFilter})
              {typeCondition}
              AND ($dstMember IS NULL OR dst_member LIKE '%' || $dstMember || '%')
            ORDER BY edge_type, dst_fqn, line
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$fqn", fqn);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        command.Parameters.AddWithValue(
            "$dstMember",
            string.IsNullOrWhiteSpace(dstMember) ? (object)DBNull.Value : dstMember.Trim());

        var results = new List<EdgeSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new EdgeSummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                (int)reader.GetInt64(6)));
        }

        return results;
    }

    /// <summary>基于 FTS5 的源码片段搜索；多关键字按 AND 组合。</summary>
    public IReadOnlyList<CodeSearchResult> SearchCode(string keyword, int topK = 10, int offset = 0)
    {
        var match = BuildFtsQuery(keyword);
        if (match is null)
        {
            return Array.Empty<CodeSearchResult>();
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.file_path, c.start_line, c.end_line,
                   snippet(code_fts, 0, '[[', ']]', ' ... ', 40) AS snippet
            FROM code_fts
            JOIN code_chunks c ON c.id = code_fts.rowid
            WHERE code_fts MATCH $match
            ORDER BY bm25(code_fts)
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", Math.Clamp(topK, 1, 50));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        var results = new List<CodeSearchResult>();
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new CodeSearchResult(
                    reader.GetString(0),
                    (int)reader.GetInt64(1),
                    (int)reader.GetInt64(2),
                    reader.GetString(3)));
            }
        }
        catch (SqliteException)
        {
            return Array.Empty<CodeSearchResult>();
        }

        return results;
    }

    private static string? BuildFtsQuery(string keyword)
    {
        var tokens = Regex.Matches(keyword, @"[\p{L}\p{N}]+")
            .Select(match => match.Value)
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        if (tokens.Count == 0)
        {
            return null;
        }

        // 每个 token 加双引号，避免用户输入被当作 FTS5 运算符解析。
        return string.Join(" AND ", tokens.Select(token => $"\"{token}\""));
    }

    public IReadOnlyList<InjectionSummary> SearchMixins(string targetClass, int limit = 50, int offset = 0)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT mixin_class, annotation, target_method, at_value, arguments
            FROM injections
            WHERE target_class = $targetClass
               OR target_class LIKE '%' || $targetClass || '%'
            ORDER BY id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$targetClass", targetClass);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        var results = new List<InjectionSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new InjectionSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return results;
    }

    /// <summary>
    /// 按 FQN 直接读取源码文件中的符号行区间：类 FQN 返回声明起始处，方法/构造器 FQN
    /// （形如 pkg.Class#method(...)）返回方法体。超过 maxLines 时只返回前 maxLines 行，
    /// 并在末尾追加“// ... (代码已截断)”。
    /// </summary>
    public CodeSnippetResult? GetCodeSnippet(string fqn, int maxLines = 80)
    {
        var source = GetSymbolSource(fqn);
        if (source is null)
        {
            return null;
        }

        var limit = Math.Clamp(maxLines, 1, 500);
        var totalLines = Math.Max(0, source.EndLine - source.StartLine + 1);
        var absolutePath = Path.IsPathRooted(source.FilePath)
            ? source.FilePath
            : Path.Combine(_sourceRoot, source.FilePath.Replace('/', Path.DirectorySeparatorChar));

        var lines = new List<string>(Math.Min(limit, totalLines));
        var fileExists = File.Exists(absolutePath);
        if (fileExists)
        {
            var lineNumber = 0;
            using var reader = new StreamReader(absolutePath);
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (lineNumber < source.StartLine)
                {
                    continue;
                }

                if (lineNumber > source.EndLine)
                {
                    break;
                }

                lines.Add(line);
                if (lines.Count >= limit)
                {
                    break;
                }
            }
        }

        var truncated = totalLines > lines.Count;
        var code = string.Join('\n', lines);
        if (truncated)
        {
            code += "\n// ... (代码已截断)";
        }

        return new CodeSnippetResult(
            source.Fqn,
            source.Kind,
            source.Signature,
            source.Comment,
            source.FilePath,
            source.StartLine,
            source.EndLine,
            totalLines,
            lines.Count,
            truncated,
            fileExists,
            code);
    }

    /// <summary>
    /// Mixin 冲突检测：列出落在目标类（可再限定目标方法）上的所有注入点，按 mixin 类分组，
    /// 并用 mod_metadata.mod_dir 前缀把 mixin 归属到具体 Mod。类级 @Mixin 标记不算注入点。
    /// </summary>
    public MixinConflictResult CheckMixinConflict(
        string targetClass,
        string? targetMethod = null,
        int limit = 100,
        int offset = 0)
    {
        using var connection = _database.OpenConnection();

        var modDirs = new List<(string ModDir, string ModId)>();
        using (var modCommand = connection.CreateCommand())
        {
            modCommand.CommandText =
                """
                SELECT mod_id, mod_dir
                FROM mod_metadata
                WHERE mod_dir IS NOT NULL AND mod_dir <> ''
                ORDER BY length(mod_dir) DESC;
                """;
            using var modReader = modCommand.ExecuteReader();
            while (modReader.Read())
            {
                modDirs.Add((modReader.GetString(1).Replace('\\', '/'), modReader.GetString(0)));
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT mixin_class, annotation, target_method, at_value, arguments, file_path, line
            FROM injections
            WHERE target_class = $targetClass
              AND annotation <> 'Mixin'
              AND ($targetMethod IS NULL
                   OR target_method = $targetMethod
                   OR target_method LIKE $targetMethod || '(%')
            ORDER BY mixin_class, target_method, line;
            """;
        command.Parameters.AddWithValue("$targetClass", targetClass);
        command.Parameters.AddWithValue(
            "$targetMethod",
            string.IsNullOrWhiteSpace(targetMethod) ? (object)DBNull.Value : targetMethod.Trim());

        var rows = new List<RawInjectionRow>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(new RawInjectionRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    (int)reader.GetInt64(6)));
            }
        }

        var groups = rows
            .GroupBy(row => row.MixinClass, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new MixinConflictGroup(
                    group.Key,
                    ResolveModId(first.FilePath, modDirs),
                    group.Count(),
                    group
                        .Select(row => new MixinInjectionPoint(
                            row.Annotation,
                            row.TargetMethod,
                            row.AtValue,
                            row.Arguments,
                            row.FilePath,
                            row.Line))
                        .ToList());
            })
            .OrderBy(group => group.MixinClass, StringComparer.Ordinal)
            .ToList();

        var mods = groups
            .Select(group => group.ModId)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var page = groups
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();

        string? warning = null;
        string? note = null;
        if (rows.Count == 0)
        {
            note = string.IsNullOrWhiteSpace(targetMethod)
                ? "未检测到任何 Mod 对该类进行注入，可以安全编写新的 Mixin。"
                : $"未检测到任何 Mod 对方法 {targetMethod} 的注入。";
        }
        else
        {
            var modCount = mods.Count > 0 ? mods.Count : groups.Count;
            warning = $"检测到 {modCount} 个 Mod 对该目标进行了注入，请注意 @Inject 的执行顺序及 CallbackInfo 冲突";
        }

        return new MixinConflictResult(
            targetClass,
            string.IsNullOrWhiteSpace(targetMethod) ? null : targetMethod.Trim(),
            rows.Count,
            groups.Count,
            mods.Count,
            mods,
            page,
            warning,
            note);
    }

    private static string? ResolveModId(string filePath, List<(string ModDir, string ModId)> modDirs)
    {
        var normalized = filePath.Replace('\\', '/');
        foreach (var (modDir, modId) in modDirs)
        {
            if (normalized.Equals(modDir, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(modDir + "/", StringComparison.OrdinalIgnoreCase))
            {
                return modId;
            }
        }

        return null;
    }

    /// <summary>查询 Mod 元数据、依赖与被依赖关系；modId 支持 id / 名称 / 路径片段。</summary>
    public IReadOnlyList<ModMetadataResult> GetModMetadata(string query, int limit = 10, int offset = 0)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT mod_id, name, version, loader, mod_dir, toml_path, mixin_configs, description, indexed_at
            FROM mod_metadata
            WHERE mod_id = $query
               OR mod_id LIKE '%' || $query || '%'
               OR name LIKE '%' || $query || '%'
               OR mod_dir LIKE '%' || $query || '%'
            ORDER BY
                CASE
                    WHEN mod_id = $query THEN 0
                    WHEN mod_id LIKE $query || '%' THEN 1
                    ELSE 2
                END,
                mod_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));

        var rawRows = new List<RawModRow>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rawRows.Add(new RawModRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8)));
            }
        }

        var results = new List<ModMetadataResult>(rawRows.Count);
        foreach (var raw in rawRows)
        {
            using var dependencyCommand = connection.CreateCommand();
            dependencyCommand.CommandText =
                """
                SELECT dep_mod_id, mandatory, version_range, side
                FROM mod_dependencies
                WHERE mod_id = $modId
                ORDER BY dep_mod_id;
                """;
            dependencyCommand.Parameters.AddWithValue("$modId", raw.ModId);
            var dependencies = new List<ModDependencyInfo>();
            using (var dependencyReader = dependencyCommand.ExecuteReader())
            {
                while (dependencyReader.Read())
                {
                    dependencies.Add(new ModDependencyInfo(
                        dependencyReader.GetString(0),
                        dependencyReader.GetInt64(1) != 0,
                        dependencyReader.IsDBNull(2) ? null : dependencyReader.GetString(2),
                        dependencyReader.IsDBNull(3) ? null : dependencyReader.GetString(3)));
                }
            }

            using var dependentCommand = connection.CreateCommand();
            dependentCommand.CommandText =
                """
                SELECT mod_id
                FROM mod_dependencies
                WHERE dep_mod_id = $modId
                ORDER BY mod_id;
                """;
            dependentCommand.Parameters.AddWithValue("$modId", raw.ModId);
            var dependents = new List<string>();
            using (var dependentReader = dependentCommand.ExecuteReader())
            {
                while (dependentReader.Read())
                {
                    dependents.Add(dependentReader.GetString(0));
                }
            }

            var mixins = new List<string>();
            if (raw.MixinConfigs is not null)
            {
                try
                {
                    mixins.AddRange(JsonSerializer.Deserialize<string[]>(raw.MixinConfigs) ?? Array.Empty<string>());
                }
                catch (JsonException)
                {
                    // 元数据损坏时保持空列表，不影响其余字段返回。
                }
            }

            results.Add(new ModMetadataResult(
                raw.ModId,
                raw.Name,
                raw.Version,
                raw.Loader,
                raw.ModDir,
                raw.TomlPath,
                mixins,
                raw.Description,
                dependencies,
                dependents,
                raw.IndexedAt));
        }

        return results;
    }

    /// <summary>
    /// ONNX 语义检索：full 模式下遍历内存向量，lazy 模式下分批扫 SQLite；offset 用于翻页。
    /// </summary>
    public IReadOnlyList<SemanticMatch> SemanticSearch(string query, int topK = 10, int offset = 0)
        => new EmbeddingService(_database).Search(query, topK, _memory, offset);

    private sealed record SymbolRow(
        string Fqn,
        string Kind,
        string? Signature,
        string Origin,
        string FilePath,
        int StartLine,
        int EndLine,
        string? Comment);

    private sealed record RawModRow(
        string ModId,
        string? Name,
        string? Version,
        string Loader,
        string ModDir,
        string? TomlPath,
        string? MixinConfigs,
        string? Description,
        string IndexedAt);

    private sealed record RawInjectionRow(
        string MixinClass,
        string Annotation,
        string? TargetMethod,
        string? AtValue,
        string? Arguments,
        string FilePath,
        int Line);
}

public sealed record MergedSymbol(
    string Fqn,
    string Kind,
    string? Signature,
    string? Comment,
    string Origin,
    IReadOnlyList<string> Origins,
    IReadOnlyList<SymbolOriginSource> Sources);

public sealed record SymbolOriginSource(
    string Origin,
    string FilePath,
    int StartLine,
    int EndLine);

public sealed record SymbolSource(
    string Fqn,
    string Kind,
    string? Signature,
    string? Comment,
    string Origin,
    string FilePath,
    int StartLine,
    int EndLine,
    IReadOnlyList<SymbolOriginSource> Sources);

public sealed record EdgeSummary(
    string SrcFqn,
    string? SrcMethod,
    string DstFqn,
    string? DstMember,
    string EdgeType,
    string FilePath,
    int Line);

public sealed record InjectionSummary(
    string MixinClass,
    string Annotation,
    string? TargetMethod,
    string? AtValue,
    string? Arguments);

public sealed record InheritorSummary(
    string Subclass,
    string Relation,
    string FilePath,
    int Line);

public sealed record CodeSearchResult(
    string FilePath,
    int StartLine,
    int EndLine,
    string Snippet);

public sealed record ModMetadataResult(
    string ModId,
    string? Name,
    string? Version,
    string Loader,
    string ModDir,
    string? TomlPath,
    IReadOnlyList<string> MixinConfigs,
    string? Description,
    IReadOnlyList<ModDependencyInfo> Dependencies,
    IReadOnlyList<string> Dependents,
    string IndexedAt);

public sealed record ModDependencyInfo(
    string ModId,
    bool Mandatory,
    string? VersionRange,
    string? Side);

public sealed record SemanticMatch(
    string Fqn,
    string Kind,
    string Origin,
    double Score,
    string? Signature,
    string FilePath,
    int StartLine,
    int EndLine,
    string? Comment);

/// <summary>代码片段提取结果：Code 为直接可读的源码文本，行号用于回溯。</summary>
public sealed record CodeSnippetResult(
    string Fqn,
    string Kind,
    string? Signature,
    string? Comment,
    string FilePath,
    int StartLine,
    int EndLine,
    int TotalLines,
    int ReturnedLines,
    bool Truncated,
    bool FileExists,
    string Code);

/// <summary>单个 Mixin 注入点（@Inject / @Redirect / @ModifyVariable 等）。</summary>
public sealed record MixinInjectionPoint(
    string Annotation,
    string? TargetMethod,
    string? AtValue,
    string? Arguments,
    string FilePath,
    int Line);

/// <summary>按 mixin 类聚合的注入记录，ModId 由 mod_metadata.mod_dir 前缀推断。</summary>
public sealed record MixinConflictGroup(
    string MixinClass,
    string? ModId,
    int Count,
    IReadOnlyList<MixinInjectionPoint> Injections);

/// <summary>Mixin 冲突检测结果：Mods 为去重后的 Mod 列表，Warning 为冲突提示。</summary>
public sealed record MixinConflictResult(
    string TargetClass,
    string? TargetMethod,
    int InjectionCount,
    int MixinCount,
    int ModCount,
    IReadOnlyList<string> Mods,
    IReadOnlyList<MixinConflictGroup> Groups,
    string? Warning,
    string? Note);
