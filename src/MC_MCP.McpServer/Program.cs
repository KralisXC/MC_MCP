using System.IO;
using System.Text;
using System.Text.Json;
using MC_MCP.Core;
using MC_MCP.Indexer;

namespace MC_MCP.McpServer;

/// <summary>基于 stdio 的极简 MCP 服务器，暴露索引查询工具。</summary>
internal static class Program
{
    private const string ServerName = "mc-mcp-server";
    private const string ProtocolVersion = "2024-11-05";
    private const string DatabasePathEnvironmentVariable = "MCMCP_DB_PATH";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static int Main(string[] args)
    {
        var version = AppInfo.ResolveVersion();
        // 优先级：--db 参数 > MCMCP_DB_PATH 环境变量 > 项目根 index/mcmcp_core.db（自动迁移旧 test.db）。
        var databasePath = GetOption(args, "--db")?.Trim();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Environment.GetEnvironmentVariable(DatabasePathEnvironmentVariable)?.Trim();
        }

        var database = string.IsNullOrWhiteSpace(databasePath)
            ? IndexDatabase.CreateAtDefaultLocation()
            : new IndexDatabase(Path.GetFullPath(databasePath));
        database.EnsureCreated();

        if (args.Contains("--init-db"))
        {
            Console.Error.WriteLine($"[mc-mcp] 索引库已就绪: {database.FilePath}");
            return 0;
        }

        var indexRoot = GetOption(args, "--index");
        if (indexRoot is not null)
        {
            var forceFull = args.Contains("--full");
            var summary = new JavaIndexer().Index(indexRoot, database, forceFull);
            Console.Error.WriteLine(
                $"[mc-mcp] 索引完成: mode={(summary.FullRebuild ? "full" : "incremental")}, " +
                $"duration={summary.DurationMs}ms, files={summary.Files}, changed={summary.ChangedFiles}, " +
                $"skipped={summary.SkippedFiles}, removed={summary.RemovedFiles}, " +
                $"symbols={summary.Symbols}, edges={summary.Edges}, injections={summary.Injections}, " +
                $"parseErrors={summary.ParseErrors}, db={database.FilePath}");
            Console.Error.WriteLine(
                $"[mc-mcp] 汇总: symbols={database.GetSymbolCount()}, edges={database.GetEdgeCount()}, " +
                $"injections={database.GetInjectionCount()}, files={database.GetFileCount()}, " +
                $"codeChunks={database.GetCodeChunkCount()}, mods={database.GetModCount()}, " +
                $"embeddings={database.GetEmbeddingCount()}");
            return 0;
        }

        if (args.Contains("--build-vectors"))
        {
            if (args.Contains("--vectors-reset"))
            {
                var cleared = database.ClearEmbeddings();
                Console.Error.WriteLine($"[mc-mcp] 已清空旧向量: {cleared} 条");
            }

            var modelDirectory = GetOption(args, "--model-dir");
            var kinds = GetOption(args, "--vectors-kinds");
            var limitText = GetOption(args, "--vectors-limit");
            var limit = int.TryParse(limitText, out var parsedLimit) ? parsedLimit : 0;
            try
            {
                var result = new EmbeddingService(database).BuildVectors(
                    kinds,
                    limit,
                    modelDirectory,
                    message => Console.Error.WriteLine($"[mc-mcp] {message}"));
                Console.Error.WriteLine(
                    $"[mc-mcp] 向量生成完成: symbols={result.Symbols}, dim={result.Dimension}, " +
                    $"duration={result.DurationMs}ms, modelDir={result.ModelDirectory}, db={database.FilePath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[mc-mcp] 向量生成失败: {ex.Message}");
                return 1;
            }

            return 0;
        }

        // Phase 10：载入模式（lazy / full）。--load-mode > MCMCP_LOAD_MODE > Launcher 写入的 index_meta。
        var loadMode = InMemoryIndex.ResolveLoadMode(GetOption(args, "--load-mode"), database);
        InMemoryIndex? memory = null;
        try
        {
            memory = InMemoryIndex.TryLoad(loadMode, database, message => Console.Error.WriteLine($"[mc-mcp] {message}"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mc-mcp] 全内存模式加载失败，已回退 lazy：{ex.Message}");
        }

        // stdio 模式下 stdout 只能输出 JSON-RPC 报文，日志一律走 stderr。
        Console.Error.WriteLine(
            $"[mc-mcp] started, version={version}, loadMode={(memory is null ? "lazy" : "full")}, db={database.FilePath}");

        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            HandleLine(line, output, database, version, memory);
        }

        return 0;
    }

    private static void HandleLine(
        string line,
        TextWriter output,
        IndexDatabase database,
        string version,
        InMemoryIndex? memory)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            output.WriteLine(ErrorResponse(null, -32700, "Parse error"));
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                output.WriteLine(ErrorResponse(null, -32600, "Invalid Request"));
                return;
            }

            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetRawText() : null;
            var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
            var parameters = root.TryGetProperty("params", out var paramsElement) &&
                             paramsElement.ValueKind == JsonValueKind.Object
                ? paramsElement
                : (JsonElement?)null;

            if (method is null)
            {
                if (id is not null)
                {
                    output.WriteLine(ErrorResponse(id, -32600, "Invalid Request"));
                }

                return;
            }

            switch (method)
            {
                case "initialize":
                    if (id is not null)
                    {
                        output.WriteLine(ResultResponse(id, InitializeResult(version)));
                    }

                    return;

                case "notifications/initialized":
                case "notifications/cancelled":
                    return;

                case "ping":
                    if (id is not null)
                    {
                        output.WriteLine(ResultResponse(id, "{}"));
                    }

                    return;

                case "tools/list":
                    if (id is not null)
                    {
                        output.WriteLine(ResultResponse(id, ToolsListResult()));
                    }

                    return;

                case "tools/call":
                    if (id is not null)
                    {
                        output.WriteLine(ToolsCallResponse(id, parameters, database, version, memory));
                    }

                    return;

                default:
                    if (id is not null)
                    {
                        output.WriteLine(ErrorResponse(id, -32601, $"Method not found: {method}"));
                    }

                    return;
            }
        }
    }

    private static string InitializeResult(string version)
    {
        var serverVersion = JsonSerializer.Serialize(version);
        return $"{{\"protocolVersion\":\"{ProtocolVersion}\",\"capabilities\":{{\"tools\":{{\"listChanged\":false}}}}," +
               $"\"serverInfo\":{{\"name\":\"{ServerName}\",\"version\":{serverVersion}}}}}";
    }

    // MCP stdio 传输要求每条消息单行；工具清单在源码里为可读的多行 JSON，输出前压平。
    private static string ToolsListResult() => McpTools.CatalogJson.ReplaceLineEndings("");

    private static string ToolsCallResponse(
        string id,
        JsonElement? parameters,
        IndexDatabase database,
        string version,
        InMemoryIndex? memory)
    {
        var toolName = parameters is not null && parameters.Value.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;

        if (toolName is null)
        {
            return ErrorResponse(id, -32602, "tools/call 缺少 name");
        }

        var arguments = parameters is not null &&
                        parameters.Value.TryGetProperty("arguments", out var argumentsElement) &&
                        argumentsElement.ValueKind == JsonValueKind.Object
            ? argumentsElement
            : (JsonElement?)null;

        var outcome = McpTools.Invoke(toolName, arguments, database, version, memory);

        if (outcome is null)
        {
            return ErrorResponse(id, -32602, $"Unknown tool: {toolName}");
        }

        var content = $"{{\"content\":[{{\"type\":\"text\",\"text\":{JsonSerializer.Serialize(outcome.Text, JsonOptions)}}}]," +
                      $"\"isError\":{(outcome.IsError ? "true" : "false")}}}";
        return ResultResponse(id, content);
    }

    private static string ResultResponse(string id, string result)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}";

    private static string ErrorResponse(string? id, int code, string message)
    {
        var idText = id ?? "null";
        var messageText = JsonSerializer.Serialize(message);
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{idText},\"error\":{{\"code\":{code},\"message\":{messageText}}}}}";
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
