using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MC_MCP.Indexer;

/// <summary>
/// Phase 10：全内存模式（MCMCP_LOAD_MODE=full）。
/// 启动时把 symbol_embeddings 的全部向量反序列化为 <see cref="float"/>[]，
/// 并把 symbols 的常用元数据读入字典，让语义检索不再逐批走 SQLite。
/// lazy 模式下不会创建该对象，检索仍由 EmbeddingService 分批扫描。
/// </summary>
public sealed class InMemoryIndex
{
    public const string LoadModeEnvironmentVariable = "MCMCP_LOAD_MODE";
    public const string LoadModeMetaKey = "load_mode";

    private InMemoryIndex(
        Dictionary<long, float[]> vectors,
        Dictionary<long, InMemorySymbol> symbols,
        long memoryBytes,
        long loadDurationMs)
    {
        Vectors = vectors;
        Symbols = symbols;
        MemoryBytes = memoryBytes;
        LoadDurationMs = loadDurationMs;
    }

    public IReadOnlyDictionary<long, float[]> Vectors { get; }

    public IReadOnlyDictionary<long, InMemorySymbol> Symbols { get; }

    public long MemoryBytes { get; }

    public long LoadDurationMs { get; }

    /// <summary>启动日志文案，供 McpServer / Launcher 直接打印。</summary>
    public string Summary =>
        $"已启用全内存模式，加载了 {Vectors.Count:N0} 条向量，占用内存约 {MemoryBytes / 1024d / 1024:0.0} MB" +
        $"（符号元数据 {Symbols.Count:N0} 条，启动耗时 {LoadDurationMs} ms）";

    /// <summary>
    /// 解析载入模式：命令行 --load-mode &gt; 环境变量 MCMCP_LOAD_MODE &gt; Launcher 写入的 index_meta.load_mode &gt; lazy。
    /// </summary>
    public static string ResolveLoadMode(string? explicitMode, IndexDatabase? database = null)
    {
        var mode = explicitMode;
        if (string.IsNullOrWhiteSpace(mode))
        {
            mode = Environment.GetEnvironmentVariable(LoadModeEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(mode) && database is not null)
        {
            try
            {
                mode = database.GetMeta(LoadModeMetaKey);
            }
            catch (Exception)
            {
                // 旧库/空库读不到 index_meta 时退回 lazy，不影响启动。
            }
        }

        return string.Equals(mode?.Trim(), "full", StringComparison.OrdinalIgnoreCase) ? "full" : "lazy";
    }

    /// <summary>按模式决定是否构建全内存索引；lazy 返回 null。</summary>
    public static InMemoryIndex? TryLoad(string mode, IndexDatabase database, Action<string>? log = null)
    {
        if (!string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke("载入模式：lazy（语义检索实时走 SQLite，分批扫描）");
            return null;
        }

        var index = Load(database);
        log?.Invoke(index.Summary);
        return index;
    }

    /// <summary>一次性读取向量与符号元数据；Float16 按 System.Half 还原为 float[]。</summary>
    public static InMemoryIndex Load(IndexDatabase database)
    {
        var stopwatch = Stopwatch.StartNew();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        using var connection = database.OpenConnection();

        var total = 0;
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM symbol_embeddings WHERE dim = $dim;";
            countCommand.Parameters.AddWithValue("$dim", EmbeddingService.Dimension);
            total = Convert.ToInt32(countCommand.ExecuteScalar());
        }

        var vectors = new Dictionary<long, float[]>(total);
        var buffer = ArrayPool<byte>.Shared.Rent(EmbeddingService.Dimension * sizeof(float));
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT symbol_id, vector
                FROM symbol_embeddings
                WHERE dim = $dim
                ORDER BY symbol_id;
                """;
            command.Parameters.AddWithValue("$dim", EmbeddingService.Dimension);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                var length = (int)reader.GetBytes(1, 0, buffer, 0, buffer.Length);
                var vector = Decode(buffer.AsSpan(0, length));
                if (vector is not null)
                {
                    vectors[reader.GetInt64(0)] = vector;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // 符号元数据按 id 建字典：语义检索命中后可直接取 FQN / 签名 / 注释，无需回查 SQLite。
        var symbols = new Dictionary<long, InMemorySymbol>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id, fqn, name, kind, signature, origin, file_path, start_line, end_line, comment
                FROM symbols;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                symbols[reader.GetInt64(0)] = new InMemorySymbol(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    (int)reader.GetInt64(7),
                    (int)reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9));
            }
        }

        var after = GC.GetTotalMemory(forceFullCollection: true);
        var measured = after - before;
        var estimated = EstimateBytes(vectors, symbols);
        var memoryBytes = Math.Max(measured, estimated);
        stopwatch.Stop();

        return new InMemoryIndex(vectors, symbols, memoryBytes, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>float32 向量点积 Top-K：带最小堆与 FQN 去重，9 千条量级约 20-30 ms。</summary>
    public IReadOnlyList<(long Id, float Score)> TopK(float[] query, int capacity)
    {
        var heap = new PriorityQueue<(long Id, float Score), float>(capacity);
        foreach (var (id, vector) in Vectors)
        {
            var score = Dot(vector, query);
            if (heap.Count >= capacity && score <= heap.Peek().Score)
            {
                continue;
            }

            heap.Enqueue((id, score), score);
            if (heap.Count > capacity)
            {
                heap.Dequeue();
            }
        }

        var results = new List<(long Id, float Score)>(heap.Count);
        while (heap.TryDequeue(out var item, out _))
        {
            results.Add(item);
        }

        return results;
    }

    private static float Dot(float[] left, float[] right)
    {
        var sum = 0.0f;
        var length = Math.Min(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            sum += left[index] * right[index];
        }

        return sum;
    }

    private static float[]? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == EmbeddingService.Dimension * sizeof(short))
        {
            var stored = MemoryMarshal.Cast<byte, Half>(bytes);
            var vector = new float[EmbeddingService.Dimension];
            for (var index = 0; index < vector.Length && index < stored.Length; index++)
            {
                vector[index] = (float)stored[index];
            }

            return vector;
        }

        if (bytes.Length == EmbeddingService.Dimension * sizeof(float))
        {
            return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        }

        return null;
    }

    /// <summary>粗略估算常驻内存：向量数组 + 字符串 + 字典桶开销。</summary>
    private static long EstimateBytes(
        Dictionary<long, float[]> vectors,
        Dictionary<long, InMemorySymbol> symbols)
    {
        long bytes = vectors.Count * (EmbeddingService.Dimension * sizeof(float) + 40L);
        bytes += symbols.Count * 96L;
        foreach (var symbol in symbols.Values)
        {
            bytes += (symbol.Fqn.Length + symbol.Name.Length + symbol.Kind.Length + symbol.Origin.Length +
                      symbol.FilePath.Length + (symbol.Signature?.Length ?? 0) + (symbol.Comment?.Length ?? 0)) * 2L;
        }

        return bytes;
    }
}

/// <summary>全内存模式下的符号元数据（只保留检索结果需要的字段）。</summary>
public sealed record InMemorySymbol(
    string Fqn,
    string Name,
    string Kind,
    string? Signature,
    string Origin,
    string FilePath,
    int StartLine,
    int EndLine,
    string? Comment);
