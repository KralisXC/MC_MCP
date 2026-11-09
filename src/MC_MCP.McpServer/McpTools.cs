using System.Text;
using System.Text.Json;
using MC_MCP.Indexer;

namespace MC_MCP.McpServer;

/// <summary>
/// MCP 工具层：把 SQLite 查询结果裁剪为精简 JSON。
/// Phase 10 起所有列表型工具统一支持 offset 翻页，并在序列化后超过 50KB 时强制截断，
/// 同时在 JSON 顶层追加 warning 告诉 AI 下一页的 offset。
/// </summary>
internal static class McpTools
{
    /// <summary>单次返回给 AI 的 JSON 体积上限（UTF-8 编码后的字节数）。</summary>
    private const int MaxPayloadBytes = 50 * 1024;

    /// <summary>为 warning / truncated 字段预留的空间，避免截断后加字段反而超限。</summary>
    private const int WarningReserveBytes = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public const string CatalogJson =
        """
        {"tools":[
          {"name":"search_symbol","description":"按 name 或 fqn 模糊搜索符号，返回 fqn、kind、signature、comment（源码注释）。支持 kind 精确过滤与 offset 翻页。","inputSchema":{"type":"object","properties":{"query":{"type":"string","description":"名称或 FQN 片段"},"kind":{"type":"string","description":"可选，只搜某一类符号：class / interface / enum / record / annotation / method / constructor / field"},"top_k":{"type":"integer","description":"最多返回条数，默认 20","minimum":1,"maximum":100,"default":20},"offset":{"type":"integer","description":"跳过前 offset 条，用于翻页，默认 0","minimum":0,"default":0}},"required":["query"],"additionalProperties":false}},
          {"name":"get_symbol_source","description":"按 FQN 返回符号的 file_path、start_line、end_line、signature、comment。","inputSchema":{"type":"object","properties":{"fqn":{"type":"string"}},"required":["fqn"],"additionalProperties":false}},
          {"name":"get_code_snippet","description":"按 FQN 直接读取源码文件并返回对应行号范围的代码片段（类 FQN 或方法 FQN，方法形如 pkg.Class#method(...)）。超过 max_lines 时截取前 max_lines 行并在末尾追加 // ... (代码已截断)。适合查到符号后立即查看实现，省去手动翻源码。","inputSchema":{"type":"object","properties":{"fqn":{"type":"string","description":"类 FQN 或方法/构造器 FQN"},"max_lines":{"type":"integer","description":"最多返回行数，默认 80","minimum":1,"maximum":500,"default":80}},"required":["fqn"],"additionalProperties":false}},
          {"name":"get_symbol_used_by","description":"查询以该 FQN 为目标的边，以及该类型的 extends/implements 出边。","inputSchema":{"type":"object","properties":{"fqn":{"type":"string"},"top_k":{"type":"integer","description":"最多返回条数，默认 50","minimum":1,"maximum":500,"default":50},"offset":{"type":"integer","description":"跳过前 offset 条，用于翻页，默认 0","minimum":0,"default":0}},"required":["fqn"],"additionalProperties":false}},
          {"name":"search_mixin","description":"按目标类查询 Mixin 注入记录。","inputSchema":{"type":"object","properties":{"target_class":{"type":"string"},"top_k":{"type":"integer","description":"最多返回条数，默认 50","minimum":1,"maximum":500,"default":50},"offset":{"type":"integer","description":"跳过前 offset 条，用于翻页，默认 0","minimum":0,"default":0}},"required":["target_class"],"additionalProperties":false}},
          {"name":"check_mixin_conflict","description":"写 Mixin 前先查冲突：列出目标类（可再限定目标方法）上已有的全部注入点，按 mixin 类分组并通过 mod_metadata 归属到具体 Mod；命中时返回冲突警告。","inputSchema":{"type":"object","properties":{"target_class_fqn":{"type":"string","description":"目标类 FQN，例如 net.minecraft.client.model.geom.ModelPart"},"target_method_name":{"type":"string","description":"可选，只查该目标方法上的注入，支持 render 或完整描述符"},"top_k":{"type":"integer","description":"最多返回多少个 mixin 分组，默认 100","minimum":1,"maximum":200,"default":100},"offset":{"type":"integer","description":"跳过前 offset 个 mixin 分组，用于翻页，默认 0","minimum":0,"default":0}},"required":["target_class_fqn"],"additionalProperties":false}},
          {"name":"get_inheritors","description":"查询继承或实现了指定类的所有子类 FQN（edges 的 extends/implements 入边）。","inputSchema":{"type":"object","properties":{"class_fqn":{"type":"string","description":"父类/接口 FQN"},"top_k":{"type":"integer","description":"最多返回条数，默认 100","minimum":1,"maximum":500,"default":100},"offset":{"type":"integer","description":"跳过前 offset 条，用于翻页，默认 0","minimum":0,"default":0}},"required":["class_fqn"],"additionalProperties":false}},
          {"name":"get_symbol_uses","description":"查询指定类的全部出边：调用了哪些类/方法、实例化了哪些对象、继承/实现了谁。默认过滤局部变量接收者与同类内部调用噪声；可用 dst_member 只保留目标方法名包含指定片段的边（如 tick）。","inputSchema":{"type":"object","properties":{"fqn":{"type":"string","description":"类 FQN"},"edge_type":{"type":"string","description":"可选，按边类型过滤：call / instantiation / extends / implements / internal_call，可用逗号组合"},"dst_member":{"type":"string","description":"可选，目标方法名模糊匹配，例如 tick 只返回调用 xxxTick/onTick 的边"},"top_k":{"type":"integer","description":"最多返回条数，默认 200","minimum":1,"maximum":500,"default":200},"offset":{"type":"integer","description":"跳过前 offset 条，用于翻页，默认 0","minimum":0,"default":0}},"required":["fqn"],"additionalProperties":false}},
          {"name":"search_code","description":"基于 FTS5 全文索引搜索 Java 源码片段（支持 player.isCreative() 这类代码搜索）。","inputSchema":{"type":"object","properties":{"keyword":{"type":"string","description":"代码关键字，多个词按 AND 组合"},"top_k":{"type":"integer","description":"最多返回条数，默认 10","minimum":1,"maximum":50,"default":10},"offset":{"type":"integer","description":"跳过前 offset 条，用于翻页，默认 0","minimum":0,"default":0}},"required":["keyword"],"additionalProperties":false}},
          {"name":"get_mod_metadata","description":"按 mod_id / 名称 / 路径片段查询 Mod 元数据、加载器类型、Mixin 配置、依赖与被依赖关系。","inputSchema":{"type":"object","properties":{"mod_id":{"type":"string","description":"Mod ID、显示名称或目录片段"},"top_k":{"type":"integer","description":"最多返回条数，默认 10","minimum":1,"maximum":50,"default":10},"offset":{"type":"integer","description":"跳过前 offset 条，用于翻页，默认 0","minimum":0,"default":0}},"required":["mod_id"],"additionalProperties":false}},
          {"name":"semantic_search","description":"基于 e5-base-v2 ONNX 向量的语义搜索，用自然语言描述（如 player model animation）召回最相关的类/方法 FQN。full 模式（MCMCP_LOAD_MODE=full）下直接遍历内存向量，响应在毫秒级。","inputSchema":{"type":"object","properties":{"query":{"type":"string","description":"自然语言查询"},"top_k":{"type":"integer","description":"最多返回条数，默认 10","minimum":1,"maximum":50,"default":10},"offset":{"type":"integer","description":"跳过前 offset 条，用于翻页，默认 0","minimum":0,"default":0}},"required":["query"],"additionalProperties":false}},
          {"name":"get_index_stats","description":"返回 files 以及 symbols、edges、injections、mods、embeddings 数量，以及当前载入模式（lazy / full）。","inputSchema":{"type":"object","properties":{},"additionalProperties":false}}
        ]}
        """;

    public static McpToolResult? Invoke(
        string toolName,
        JsonElement? arguments,
        IndexDatabase database,
        string version,
        InMemoryIndex? memory = null)
    {
        var queries = new SymbolQueryService(database, memory);
        switch (toolName)
        {
            case "search_symbol":
            {
                var query = GetString(arguments, "query");
                if (query is null)
                {
                    return Error("search_symbol 需要非空的 query 参数");
                }

                var topK = GetInt(arguments, "top_k") ?? 20;
                var offset = GetOffset(arguments);
                var kind = GetString(arguments, "kind");
                var symbols = queries.SearchSymbols(query, topK, offset, kind);
                return OkList(symbols, list => new Dictionary<string, object?>
                {
                    ["query"] = query,
                    ["kind"] = kind,
                    ["count"] = list.Count,
                    ["offset"] = offset,
                    ["symbols"] = list
                }, offset);
            }

            case "get_symbol_source":
            {
                var fqn = GetString(arguments, "fqn");
                if (fqn is null)
                {
                    return Error("get_symbol_source 需要非空的 fqn 参数");
                }

                var source = queries.GetSymbolSource(fqn);
                return source is null ? Error($"未找到符号: {fqn}") : Ok(source);
            }

            case "get_code_snippet":
            {
                var fqn = GetString(arguments, "fqn");
                if (fqn is null)
                {
                    return Error("get_code_snippet 需要非空的 fqn 参数");
                }

                var maxLines = GetInt(arguments, "max_lines") ?? 80;
                var snippet = queries.GetCodeSnippet(fqn, maxLines);
                if (snippet is null)
                {
                    return Error($"未找到符号: {fqn}");
                }

                if (!snippet.FileExists)
                {
                    return Error(
                        $"索引命中符号 {snippet.Fqn}，但源码文件不存在: {snippet.FilePath}" +
                        "（请确认索引库与 code/、mods/ 位于同一工程根目录）");
                }

                return OkSnippet(snippet);
            }

            case "get_symbol_used_by":
            {
                var fqn = GetString(arguments, "fqn");
                if (fqn is null)
                {
                    return Error("get_symbol_used_by 需要非空的 fqn 参数");
                }

                var limit = GetInt(arguments, "top_k") ?? 50;
                var offset = GetOffset(arguments);
                var edges = queries.GetUsedBy(fqn, limit, offset);
                return OkList(edges, list => new Dictionary<string, object?>
                {
                    ["fqn"] = fqn,
                    ["count"] = list.Count,
                    ["offset"] = offset,
                    ["edges"] = list
                }, offset);
            }

            case "search_mixin":
            {
                var targetClass = GetString(arguments, "target_class");
                if (targetClass is null)
                {
                    return Error("search_mixin 需要非空的 target_class 参数");
                }

                var limit = GetInt(arguments, "top_k") ?? 50;
                var offset = GetOffset(arguments);
                var injections = queries.SearchMixins(targetClass, limit, offset);
                return OkList(injections, list => new Dictionary<string, object?>
                {
                    ["targetClass"] = targetClass,
                    ["count"] = list.Count,
                    ["offset"] = offset,
                    ["injections"] = list
                }, offset);
            }

            case "check_mixin_conflict":
            {
                var targetClass = GetString(arguments, "target_class_fqn");
                if (targetClass is null)
                {
                    return Error("check_mixin_conflict 需要非空的 target_class_fqn 参数");
                }

                var targetMethod = GetString(arguments, "target_method_name");
                var limit = GetInt(arguments, "top_k") ?? 100;
                var offset = GetOffset(arguments);
                var conflict = queries.CheckMixinConflict(targetClass, targetMethod, limit, offset);
                return OkList(conflict.Groups, list => new Dictionary<string, object?>
                {
                    ["targetClass"] = conflict.TargetClass,
                    ["targetMethod"] = conflict.TargetMethod,
                    ["injectionCount"] = conflict.InjectionCount,
                    ["mixinCount"] = conflict.MixinCount,
                    ["modCount"] = conflict.ModCount,
                    ["mods"] = conflict.Mods,
                    ["count"] = list.Count,
                    ["offset"] = offset,
                    ["groups"] = list,
                    ["warning"] = conflict.Warning,
                    ["note"] = conflict.Note
                }, offset);
            }

            case "get_inheritors":
            {
                var classFqn = GetString(arguments, "class_fqn");
                if (classFqn is null)
                {
                    return Error("get_inheritors 需要非空的 class_fqn 参数");
                }

                var limit = GetInt(arguments, "top_k") ?? 100;
                var offset = GetOffset(arguments);
                var inheritors = queries.GetInheritors(classFqn, limit, offset);
                return OkList(inheritors, list => new Dictionary<string, object?>
                {
                    ["classFqn"] = classFqn,
                    ["count"] = list.Count,
                    ["offset"] = offset,
                    ["inheritors"] = list
                }, offset);
            }

            case "get_symbol_uses":
            {
                var fqn = GetString(arguments, "fqn");
                if (fqn is null)
                {
                    return Error("get_symbol_uses 需要非空的 fqn 参数");
                }

                var limit = GetInt(arguments, "top_k") ?? 200;
                var edgeType = GetString(arguments, "edge_type");
                var dstMember = GetString(arguments, "dst_member");
                var offset = GetOffset(arguments);
                var uses = queries.GetSymbolUses(fqn, limit, edgeType, offset, dstMember);
                return OkList(uses, list => new Dictionary<string, object?>
                {
                    ["fqn"] = fqn,
                    ["edgeType"] = edgeType,
                    ["dstMember"] = dstMember,
                    ["count"] = list.Count,
                    ["offset"] = offset,
                    ["edges"] = list
                }, offset);
            }

            case "search_code":
            {
                var keyword = GetString(arguments, "keyword");
                if (keyword is null)
                {
                    return Error("search_code 需要非空的 keyword 参数");
                }

                var topK = GetInt(arguments, "top_k") ?? 10;
                var offset = GetOffset(arguments);
                var matches = queries.SearchCode(keyword, topK, offset);
                return OkList(matches, list => new Dictionary<string, object?>
                {
                    ["query"] = keyword,
                    ["count"] = list.Count,
                    ["offset"] = offset,
                    ["matches"] = list
                }, offset);
            }

            case "get_index_stats":
            {
                var symbols = database.GetSymbolCount();
                var edges = database.GetEdgeCount();
                var injections = database.GetInjectionCount();
                var files = database.GetFileCount();
                // 保持 Phase 9 的扁平字段，同时补充载入模式，便于 AI 判断语义检索是否需要预热。
                return Ok(new Dictionary<string, object?>
                {
                    ["version"] = version,
                    ["status"] = files > 0 ? "ready" : "empty",
                    ["entries"] = symbols + edges + injections,
                    ["symbols"] = symbols,
                    ["edges"] = edges,
                    ["injections"] = injections,
                    ["files"] = files,
                    ["mods"] = database.GetModCount(),
                    ["embeddings"] = database.GetEmbeddingCount(),
                    ["loadMode"] = memory is null ? "lazy" : "full",
                    ["memoryVectors"] = memory?.Vectors.Count ?? 0,
                    ["memoryMegabytes"] = memory is null ? 0 : Math.Round(memory.MemoryBytes / 1024d / 1024, 1)
                });
            }

            case "get_mod_metadata":
            {
                var modId = GetString(arguments, "mod_id");
                if (modId is null)
                {
                    return Error("get_mod_metadata 需要非空的 mod_id 参数");
                }

                var limit = GetInt(arguments, "top_k") ?? 10;
                var offset = GetOffset(arguments);
                var matches = queries.GetModMetadata(modId, limit, offset);
                return OkList(matches, list => new Dictionary<string, object?>
                {
                    ["query"] = modId,
                    ["count"] = list.Count,
                    ["offset"] = offset,
                    ["matches"] = list
                }, offset);
            }

            case "semantic_search":
            {
                var query = GetString(arguments, "query");
                if (query is null)
                {
                    return Error("semantic_search 需要非空的 query 参数");
                }

                var topK = GetInt(arguments, "top_k") ?? 10;
                var offset = GetOffset(arguments);
                try
                {
                    var matches = queries.SemanticSearch(query, topK, offset);
                    return OkList(matches, list => new Dictionary<string, object?>
                    {
                        ["query"] = query,
                        ["count"] = list.Count,
                        ["offset"] = offset,
                        ["loadMode"] = memory is null ? "lazy" : "full",
                        ["matches"] = list
                    }, offset);
                }
                catch (Exception ex)
                {
                    return Error($"semantic_search 失败：{ex.Message}（请先执行 --build-vectors 并确认模型目录）");
                }
            }

            default:
                return null;
        }
    }

    private static McpToolResult Ok(object payload)
        => new(JsonSerializer.Serialize(payload, JsonOptions), false);

    /// <summary>
    /// 代码片段出口：正常情况直接返回；极端情况下（max_lines 很大且单行很长）继续按行裁剪，
    /// 保证单次响应仍不超过 50KB，并提示减小 max_lines 或改用方法级 FQN。
    /// </summary>
    private static McpToolResult OkSnippet(CodeSnippetResult snippet)
    {
        var payload = SnippetPayload(snippet);
        var text = JsonSerializer.Serialize(payload, JsonOptions);
        if (Encoding.UTF8.GetByteCount(text) <= MaxPayloadBytes)
        {
            return new McpToolResult(text, false);
        }

        var lines = snippet.Code.Split('\n');
        var kept = lines.Length;
        while (kept > 1)
        {
            kept--;
            payload = SnippetPayload(snippet with
            {
                Code = string.Join('\n', lines.Take(kept)),
                ReturnedLines = kept,
                Truncated = true
            });
            payload["warning"] = $"代码片段超过 {MaxPayloadBytes / 1024}KB，已进一步截断为前 {kept} 行；" +
                                 "请减小 max_lines 或改用方法级 FQN。";
            payload["truncated"] = true;
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload, JsonOptions)) <= MaxPayloadBytes)
            {
                return new McpToolResult(JsonSerializer.Serialize(payload, JsonOptions), false);
            }
        }

        return new McpToolResult(JsonSerializer.Serialize(payload, JsonOptions), false);
    }

    private static Dictionary<string, object?> SnippetPayload(CodeSnippetResult snippet) => new()
    {
        ["fqn"] = snippet.Fqn,
        ["kind"] = snippet.Kind,
        ["signature"] = snippet.Signature,
        ["comment"] = snippet.Comment,
        ["filePath"] = snippet.FilePath,
        ["startLine"] = snippet.StartLine,
        ["endLine"] = snippet.EndLine,
        ["totalLines"] = snippet.TotalLines,
        ["returnedLines"] = snippet.ReturnedLines,
        ["truncated"] = snippet.Truncated,
        ["code"] = snippet.Code
    };

    /// <summary>
    /// 列表型结果统一出口：先按正常结构序列化；超过 50KB 时二分查找能容纳的最大条数，
    /// 再补上顶层 warning / truncated 字段，保证 AI 拿到的是合法 JSON 且能继续翻页。
    /// </summary>
    private static McpToolResult OkList<T>(
        IReadOnlyList<T> items,
        Func<IReadOnlyList<T>, Dictionary<string, object?>> shape,
        int offset)
    {
        var text = JsonSerializer.Serialize(shape(items), JsonOptions);
        if (Encoding.UTF8.GetByteCount(text) <= MaxPayloadBytes)
        {
            return new McpToolResult(text, false);
        }

        var budget = MaxPayloadBytes - WarningReserveBytes;
        var low = 0;
        var high = items.Count;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var candidate = JsonSerializer.Serialize(shape(items.Take(mid).ToList()), JsonOptions);
            if (Encoding.UTF8.GetByteCount(candidate) <= budget)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        var kept = low;
        while (true)
        {
            var payload = shape(items.Take(kept).ToList());
            var truncation = BuildTruncationWarning(offset, kept);
            // 工具自身的业务警告（如 Mixin 冲突）优先保留，截断提示追加在后面。
            payload["warning"] = payload.TryGetValue("warning", out var existing)
                                 && existing is string existingText
                                 && existingText.Length > 0
                ? $"{existingText} {truncation}"
                : truncation;
            payload["truncated"] = true;
            var finalText = JsonSerializer.Serialize(payload, JsonOptions);
            if (Encoding.UTF8.GetByteCount(finalText) <= MaxPayloadBytes || kept <= 0)
            {
                return new McpToolResult(finalText, false);
            }

            kept--;
        }
    }

    private static string BuildTruncationWarning(int offset, int returned)
        => $"结果过多已截断（当前 offset={offset}，返回 {returned} 条）。" +
           $"请设置 offset={offset + returned} 获取下一页，或使用更精准的过滤参数。";

    private static McpToolResult Error(string message)
        => new(JsonSerializer.Serialize(new { error = message }, JsonOptions), true);

    private static int GetOffset(JsonElement? arguments)
        => Math.Clamp(GetInt(arguments, "offset") ?? 0, 0, 1_000_000);

    private static string? GetString(JsonElement? arguments, string name)
    {
        if (arguments is null || arguments.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!arguments.Value.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static int? GetInt(JsonElement? arguments, string name)
    {
        if (arguments is null || arguments.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return arguments.Value.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var result)
            ? result
            : null;
    }
}

internal sealed record McpToolResult(string Text, bool IsError);
