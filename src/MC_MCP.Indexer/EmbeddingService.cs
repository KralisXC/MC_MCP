using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace MC_MCP.Indexer;

/// <summary>
/// e5-base-v2 向量化：离线用 --build-vectors 生成 symbol_embeddings，
/// 在线用 semantic_search 把 query 编码后做余弦相似度检索。模型加载为进程级静态缓存。
/// </summary>
public sealed class EmbeddingService
{
    public const string ModelDirectoryEnvironmentVariable = "MCMCP_MODEL_DIR";
    public const int Dimension = 768;

    /// <summary>库内单条向量占用的字节数：Float16 半精度，768 × 2 = 1536 字节（Float32 时是 3072）。</summary>
    public const int StoredVectorBytes = Dimension * sizeof(short);

    /// <summary>语义检索每批从 SQLite 读取的向量条数：决定扫描时的峰值内存，与库内总量无关。</summary>
    private const int ScanBatchSize = 10_000;

    private const int MaxTokens = 128;
    private const int BatchSize = 8;
    private const int DefaultTopK = 10;

    /// <summary>向量文本格式版本：Phase 10 起拼接注释；版本不一致时清空旧向量重建。</summary>
    private const string VectorTextRevisionKey = "embedding_text_revision";
    private const string VectorTextRevision = "10";

    private static readonly object ModelLock = new();
    private static readonly object RunLock = new();
    private static string? _cachedModelDirectory;
    private static InferenceSession? _cachedSession;
    private static BertTokenizer? _cachedTokenizer;

    private static readonly Regex CamelCasePattern = new(
        "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])",
        RegexOptions.Compiled);

    private readonly IndexDatabase _database;

    public EmbeddingService(IndexDatabase database)
    {
        _database = database;
    }

    public static string ResolveModelDirectory(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(ModelDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment.Trim());
        }

        var candidates = new List<string>();
        var current = Path.GetFullPath(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && current is not null; depth++)
        {
            candidates.Add(Path.Combine(current, "LLM", "e5-base-v2-onnx"));
            candidates.Add(Path.Combine(current, "e5-base-v2-onnx"));
            current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "LLM", "e5-base-v2-onnx"));
        var match = candidates.FirstOrDefault(Directory.Exists);
        return match is null
            ? Path.Combine(AppContext.BaseDirectory, "LLM", "e5-base-v2-onnx")
            : Path.GetFullPath(match);
    }

    /// <summary>
    /// Phase 10 把注释拼进向量文本：旧向量与新文本不一致，增量也不会补，必须整表清空重建。
    /// 只在版本号变化时执行一次，日常增量不受影响。
    /// </summary>
    private void InvalidateLegacyVectorText(string directory, Action<string>? log)
    {
        if (string.Equals(_database.GetMeta(VectorTextRevisionKey), VectorTextRevision, StringComparison.Ordinal))
        {
            return;
        }

        var cleared = _database.ClearEmbeddings();
        _database.SetMeta(VectorTextRevisionKey, VectorTextRevision);
        log?.Invoke($"[vectors] 向量文本已升级为“FQN + 签名 + 注释”，清空旧向量 {cleared:N0} 条后重建（模型目录 {directory}）");
    }

    /// <summary>
    /// 离线批量生成向量。默认增量：LEFT JOIN symbol_embeddings，只处理还没有向量的 symbols。
    /// kinds 为空表示全部符号类型；limit 为 0 表示不限制。
    /// </summary>
    public EmbeddingBuildResult BuildVectors(
        string? kinds = null,
        int limit = 0,
        string? modelDirectory = null,
        Action<string>? log = null,
        IReadOnlyCollection<string>? scopePaths = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var directory = ResolveModelDirectory(modelDirectory);
        InvalidateLegacyVectorText(directory, log);

        // 先查待生成符号，再加载模型：增量二次运行时 0 条可以秒完成，不必加载 ONNX。
        var rows = LoadSymbolsForVector(kinds, limit, scopePaths);
        if (rows.Count == 0)
        {
            log?.Invoke("[vectors] 没有缺失向量的符号，当前已是最新（增量模式）");
            return new EmbeddingBuildResult(0, 0, Dimension, stopwatch.ElapsedMilliseconds, directory);
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureModelFiles(directory);
        var model = EnsureModel(directory);
        log?.Invoke(
            $"[vectors] 待生成 {rows.Count} 条符号向量（Float16 压缩，{Dimension} 维 × 2 字节 = {StoredVectorBytes} 字节/条），模型目录 {directory}");

        var now = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO symbol_embeddings (symbol_id, dim, vector, format, source_text, model, created_at)
            VALUES ($symbolId, $dim, $vector, $format, $sourceText, $model, $createdAt)
            ON CONFLICT(symbol_id) DO UPDATE SET
                dim = excluded.dim,
                vector = excluded.vector,
                format = excluded.format,
                source_text = excluded.source_text,
                model = excluded.model,
                created_at = excluded.created_at;
            """;
        command.Parameters.Add("$symbolId", SqliteType.Integer);
        command.Parameters.Add("$dim", SqliteType.Integer);
        command.Parameters.Add("$vector", SqliteType.Blob);
        command.Parameters.Add("$format", SqliteType.Text);
        command.Parameters.Add("$sourceText", SqliteType.Text);
        command.Parameters.Add("$model", SqliteType.Text);
        command.Parameters.Add("$createdAt", SqliteType.Text);

        var processed = 0;
        try
        {
            for (var offset = 0; offset < rows.Count; offset += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = rows.Skip(offset).Take(BatchSize).ToList();
                var vectors = EmbedTexts(model, batch.Select(row => BuildPassageText(row)).ToList());
                for (var index = 0; index < batch.Count; index++)
                {
                    var row = batch[index];
                    var vector = vectors[index];

                    // Float32 → Float16 再落库：768 维由 3072 字节压到 1536 字节，体积直接减半。
                    var bytes = ToHalfBytes(vector);

                    command.Parameters["$symbolId"].Value = row.Id;
                    command.Parameters["$dim"].Value = vector.Length;
                    command.Parameters["$vector"].Value = bytes;
                    command.Parameters["$format"].Value = "float16";
                    command.Parameters["$sourceText"].Value = BuildPassageText(row);
                    command.Parameters["$model"].Value = "e5-base-v2-onnx";
                    command.Parameters["$createdAt"].Value = now;
                    command.ExecuteNonQuery();
                }

                processed += batch.Count;
                if (processed % 500 == 0 || processed == rows.Count)
                {
                    log?.Invoke($"[vectors] 已生成 {processed}/{rows.Count}（{stopwatch.ElapsedMilliseconds}ms）");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消时提交已完成的部分，避免几十秒的推理全部丢弃。
            transaction.Commit();
            log?.Invoke($"[vectors] 已取消，保留已生成的 {processed}/{rows.Count} 条向量");
            throw;
        }

        transaction.Commit();
        return new EmbeddingBuildResult(processed, processed, Dimension, stopwatch.ElapsedMilliseconds, directory);
    }

    /// <summary>
    /// 在线语义检索：query 向量与库内向量做余弦相似度，返回分数最高的 FQN。
    /// memory 非空（full 模式）时直接在内存字典里遍历，9 千条量级 20-30 ms；
    /// 否则采用“分批读取 + Top-K 最小堆”，峰值内存恒定（与库内 10 万 / 100 万条无关）。
    /// offset 用于截断后翻页：跳过前 offset 条，再返回 topK 条。
    /// </summary>
    public IReadOnlyList<SemanticMatch> Search(
        string query,
        int topK = DefaultTopK,
        InMemoryIndex? memory = null,
        int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SemanticMatch>();
        }

        var limit = Math.Clamp(topK, 1, 50);
        var skip = Math.Max(0, offset);
        var directory = ResolveModelDirectory();
        EnsureModelFiles(directory);
        var model = EnsureModel(directory);

        var queryVector = EmbedTexts(model, new List<string> { $"query: {query.Trim()}" })[0];
        if (memory is not null)
        {
            return SearchInMemory(memory, queryVector, limit, skip);
        }

        // 多留一些候选：同一 FQN 在 vanilla / NeoForge 各有向量时仍能取到最优的一条。
        var capacity = Math.Min(Math.Max((limit + skip) * 10, 64), 500);
        var heap = new PriorityQueue<(long Id, float Score), float>();
        var buffer = ArrayPool<byte>.Shared.Rent(Dimension * sizeof(float));
        var scanned = 0;
        var skipped = 0;
        long lastId = 0;

        using var connection = _database.OpenConnection();
        try
        {
            while (true)
            {
                using var command = connection.CreateCommand();
                // 只扫 symbol_embeddings，不 JOIN symbols：元数据留到 Top-K 确定后一次性回查，
                // 否则几十万行每行都要做一次 b-tree 查找 + 字符串物化。
                command.CommandText =
                    """
                    SELECT symbol_id, vector
                    FROM symbol_embeddings
                    WHERE symbol_id > $lastId AND dim = $dim
                    ORDER BY symbol_id
                    LIMIT $batch;
                    """;
                command.Parameters.AddWithValue("$lastId", lastId);
                command.Parameters.AddWithValue("$dim", Dimension);
                command.Parameters.AddWithValue("$batch", ScanBatchSize);

                var rows = 0;
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows++;
                        lastId = reader.GetInt64(0);
                        scanned++;
                        if (reader.IsDBNull(1))
                        {
                            skipped++;
                            continue;
                        }

                        // GetBytes 复用同一个缓冲区，避免每行 new byte[1536]：30 万行时那会是 450MB 垃圾。
                        var length = (int)reader.GetBytes(1, 0, buffer, 0, buffer.Length);
                        var score = DotSpan(buffer.AsSpan(0, length), queryVector, Dimension);
                        if (float.IsNegativeInfinity(score))
                        {
                            skipped++;
                            continue;
                        }

                        // 堆已满且当前分数不高于堆顶（候选里最低分）时直接跳过，连对象都不创建。
                        if (heap.Count >= capacity && score <= heap.Peek().Score)
                        {
                            continue;
                        }

                        heap.Enqueue((lastId, score), score);
                        if (heap.Count > capacity)
                        {
                            heap.Dequeue();
                        }
                    }
                }

                if (rows < ScanBatchSize)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Console.Error.WriteLine(
            $"[mc-mcp] semantic_search 扫描 {scanned} 条向量（跳过 {skipped} 条异常），TopK={limit}，批次={ScanBatchSize}");

        if (heap.Count == 0)
        {
            return Array.Empty<SemanticMatch>();
        }

        var candidates = new List<(long Id, float Score)>(heap.Count);
        while (heap.TryDequeue(out var item, out _))
        {
            candidates.Add(item);
        }

        var metadata = LoadSymbolMetadata(connection, candidates.Select(item => item.Id).Distinct().ToArray());

        // 堆按分数升序弹出，这里按“分数降序 + vanilla 优先”重排后再按 FQN 去重。
        candidates.Sort((left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var leftOrigin = metadata.TryGetValue(left.Id, out var leftMeta) ? leftMeta.Origin : string.Empty;
            var rightOrigin = metadata.TryGetValue(right.Id, out var rightMeta) ? rightMeta.Origin : string.Empty;
            return OriginPriority(leftOrigin).CompareTo(OriginPriority(rightOrigin));
        });

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<SemanticMatch>(limit + skip);
        foreach (var (id, score) in candidates)
        {
            if (!metadata.TryGetValue(id, out var candidate) || !seen.Add(candidate.Fqn))
            {
                continue;
            }

            results.Add(new SemanticMatch(
                candidate.Fqn,
                candidate.Kind,
                DisplayOrigin(candidate.Origin),
                Math.Round(score, 4),
                candidate.Signature,
                candidate.FilePath,
                candidate.StartLine,
                candidate.EndLine,
                candidate.Comment));

            if (results.Count >= limit + skip)
            {
                break;
            }
        }

        return skip >= results.Count ? Array.Empty<SemanticMatch>() : results.Skip(skip).ToList();
    }

    /// <summary>全内存模式检索：直接遍历 Dictionary&lt;long, float[]&gt; 算点积，再按 FQN 去重。</summary>
    private static IReadOnlyList<SemanticMatch> SearchInMemory(
        InMemoryIndex memory,
        float[] queryVector,
        int limit,
        int skip)
    {
        var stopwatch = Stopwatch.StartNew();
        var capacity = Math.Min(Math.Max((limit + skip) * 10, 64), 500);
        var candidates = memory.TopK(queryVector, capacity).ToList();
        candidates.Sort((left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var leftOrigin = memory.Symbols.TryGetValue(left.Id, out var leftMeta) ? leftMeta.Origin : string.Empty;
            var rightOrigin = memory.Symbols.TryGetValue(right.Id, out var rightMeta) ? rightMeta.Origin : string.Empty;
            return OriginPriority(leftOrigin).CompareTo(OriginPriority(rightOrigin));
        });

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<SemanticMatch>(limit + skip);
        foreach (var (id, score) in candidates)
        {
            if (!memory.Symbols.TryGetValue(id, out var candidate) || !seen.Add(candidate.Fqn))
            {
                continue;
            }

            results.Add(new SemanticMatch(
                candidate.Fqn,
                candidate.Kind,
                DisplayOrigin(candidate.Origin),
                Math.Round(score, 4),
                candidate.Signature,
                candidate.FilePath,
                candidate.StartLine,
                candidate.EndLine,
                candidate.Comment));

            if (results.Count >= limit + skip)
            {
                break;
            }
        }

        Console.Error.WriteLine(
            $"[mc-mcp] semantic_search 全内存模式：遍历 {memory.Vectors.Count:N0} 条向量，" +
            $"TopK={limit}，offset={skip}，耗时 {stopwatch.ElapsedMilliseconds} ms");
        return skip >= results.Count ? Array.Empty<SemanticMatch>() : results.Skip(skip).ToList();
    }

    /// <summary>按 id 批量回查 Top-K 候选的符号元数据（一次 IN 查询，最多 500 行）。</summary>
    private static Dictionary<long, SymbolMetadata> LoadSymbolMetadata(
        SqliteConnection connection,
        IReadOnlyList<long> ids)
    {
        var result = new Dictionary<long, SymbolMetadata>(ids.Count);
        if (ids.Count == 0)
        {
            return result;
        }

        using var command = connection.CreateCommand();
        var placeholders = new List<string>(ids.Count);
        for (var index = 0; index < ids.Count; index++)
        {
            var name = $"$id{index}";
            placeholders.Add(name);
            command.Parameters.AddWithValue(name, ids[index]);
        }

        command.CommandText =
            $"""
            SELECT id, fqn, kind, signature, origin, file_path, start_line, end_line, comment
            FROM symbols
            WHERE id IN ({string.Join(", ", placeholders)});
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetInt64(0)] = new SymbolMetadata(
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                (int)reader.GetInt64(6),
                (int)reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8));
        }

        return result;
    }

    /// <summary>查询还没有向量的符号：LEFT JOIN symbol_embeddings，只保留 e.symbol_id IS NULL 的行。</summary>
    private List<SymbolVectorRow> LoadSymbolsForVector(
        string? kinds,
        int limit,
        IReadOnlyCollection<string>? scopePaths = null)
    {
        var kindsList = string.IsNullOrWhiteSpace(kinds)
            ? Array.Empty<string>()
            : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        var conditions = new List<string> { "e.symbol_id IS NULL", "s.fqn <> ''" };
        if (kindsList.Length > 0)
        {
            var placeholders = new List<string>();
            for (var index = 0; index < kindsList.Length; index++)
            {
                var name = $"$kind{index}";
                placeholders.Add(name);
                command.Parameters.AddWithValue(name, kindsList[index]);
            }

            conditions.Add($"s.kind IN ({string.Join(", ", placeholders)})");
        }

        IndexDatabase.AppendScopeConditions(command, conditions, scopePaths);

        command.CommandText =
            $"""
            SELECT s.id, s.fqn, s.kind, s.name, s.signature, s.origin, s.comment
            FROM symbols s
            LEFT JOIN symbol_embeddings e ON e.symbol_id = s.id
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY s.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit > 0 ? limit : -1);

        var rows = new List<SymbolVectorRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SymbolVectorRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    private static int OriginPriority(string origin)
        => origin.ToLowerInvariant() switch
        {
            "vanilla" => 0,
            "neoforge" => 1,
            "fabric" => 2,
            _ => 3
        };

    private static string DisplayOrigin(string origin)
        => origin.Equals("neoforge", StringComparison.OrdinalIgnoreCase) ? "neoforge_added" : origin;

    private static string BuildPassageText(SymbolVectorRow row)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("passage: ").Append(Humanize(row.Fqn));
        if (row.Signature is not null)
        {
            builder.Append(' ').Append(Humanize(row.Signature));
        }

        builder.Append(' ').Append(row.Kind);
        if (row.Origin is not "vanilla")
        {
            builder.Append(' ').Append(row.Origin);
        }

        // Phase 10：注释是反编译源码里唯一的自然语言说明，拼进向量文本可显著提升语义召回。
        if (!string.IsNullOrWhiteSpace(row.Comment))
        {
            builder.Append(' ').Append(row.Comment);
        }

        return builder.ToString();
    }

    /// <summary>把 FQN / 签名拆成自然语言可匹配的词：EMFAnimation → EMF Animation。</summary>
    private static string Humanize(string text)
    {
        var normalized = text
            .Replace('.', ' ')
            .Replace('$', ' ')
            .Replace('#', ' ')
            .Replace('(', ' ')
            .Replace(')', ' ')
            .Replace(',', ' ')
            .Replace('<', ' ')
            .Replace('>', ' ')
            .Replace('/', ' ')
            .Replace('_', ' ');
        return CamelCasePattern.Replace(normalized, " ");
    }

    /// <summary>
    /// 直接对库内 BLOB 计算点积：Float16（768 × 2 = 1536 字节）按 System.Half 读取，
    /// 同时兼容旧库遗留的 Float32（3072 字节）；长度不符的脏数据返回 -∞，由调用方跳过。
    /// </summary>
    private static float DotSpan(ReadOnlySpan<byte> bytes, float[] query, int dimension)
    {
        if (bytes.Length == dimension * sizeof(short))
        {
            var stored = MemoryMarshal.Cast<byte, Half>(bytes);
            var sum = 0.0f;
            for (var index = 0; index < dimension && index < stored.Length; index++)
            {
                sum += (float)stored[index] * query[index];
            }

            return sum;
        }

        if (bytes.Length == dimension * sizeof(float))
        {
            var stored = MemoryMarshal.Cast<byte, float>(bytes);
            var sum = 0.0f;
            for (var index = 0; index < dimension && index < stored.Length; index++)
            {
                sum += stored[index] * query[index];
            }

            return sum;
        }

        return float.NegativeInfinity;
    }

    /// <summary>float 向量 → Float16 字节：768 维固定 1536 字节，存储体积是 Float32 的一半。</summary>
    private static byte[] ToHalfBytes(float[] vector)
    {
        var halves = new Half[vector.Length];
        for (var index = 0; index < vector.Length; index++)
        {
            halves[index] = (Half)vector[index];
        }

        // Half 在 CLR 里不是 primitive，不能用 Buffer.BlockCopy；按 2 字节/元素直接取原生内存。
        return MemoryMarshal.AsBytes(halves.AsSpan()).ToArray();
    }

    /// <summary>批量编码 + 推理 + 注意力掩码均值池化 + L2 归一化。</summary>
    private static List<float[]> EmbedTexts(ModelState model, IReadOnlyList<string> texts)
    {
        var results = new List<float[]>(texts.Count);
        lock (RunLock)
        {
            for (var offset = 0; offset < texts.Count; offset += BatchSize)
            {
                var batchTexts = texts.Skip(offset).Take(BatchSize).ToList();
                var tokenized = batchTexts
                    .Select(text => Encode(model.Tokenizer, text))
                    .ToList();
                var sequenceLength = Math.Max(2, tokenized.Max(ids => ids.Count));
                var batch = tokenized.Count;

                var inputIds = new DenseTensor<long>(new[] { batch, sequenceLength });
                var attentionMask = new DenseTensor<long>(new[] { batch, sequenceLength });
                var tokenTypeIds = new DenseTensor<long>(new[] { batch, sequenceLength });
                for (var row = 0; row < batch; row++)
                {
                    var ids = tokenized[row];
                    for (var column = 0; column < sequenceLength; column++)
                    {
                        var id = column < ids.Count ? ids[column] : 0;
                        inputIds[row, column] = id;
                        attentionMask[row, column] = column < ids.Count ? 1 : 0;
                        tokenTypeIds[row, column] = 0;
                    }
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                    NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
                };
                if (model.HasTokenTypeInput)
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
                }

                using var outputs = model.Session.Run(inputs);
                var output = outputs.FirstOrDefault(item => item.Name == "last_hidden_state") ?? outputs.First();
                var tensor = output.AsTensor<float>();

                for (var row = 0; row < batch; row++)
                {
                    var vector = new float[Dimension];
                    var tokenCount = 0;
                    for (var column = 0; column < sequenceLength; column++)
                    {
                        if (attentionMask[row, column] == 0)
                        {
                            continue;
                        }

                        tokenCount++;
                        for (var feature = 0; feature < Dimension; feature++)
                        {
                            vector[feature] += tensor[row, column, feature];
                        }
                    }

                    if (tokenCount > 0)
                    {
                        for (var feature = 0; feature < Dimension; feature++)
                        {
                            vector[feature] /= tokenCount;
                        }
                    }

                    Normalize(vector);
                    results.Add(vector);
                }
            }
        }

        return results;
    }

    private static List<int> Encode(BertTokenizer tokenizer, string text)
    {
        // e5 使用 BERT WordPiece：先按普通文本编码，再手动补 [CLS] / [SEP] 并截断。
        var ids = tokenizer
            .EncodeToIds(text, addSpecialTokens: false, considerPreTokenization: true, considerNormalization: true)
            .Take(MaxTokens - 2)
            .ToList();
        var result = new List<int>(ids.Count + 2) { tokenizer.ClassificationTokenId };
        result.AddRange(ids);
        result.Add(tokenizer.SeparatorTokenId);
        return result;
    }

    private static void Normalize(float[] vector)
    {
        var sum = 0.0d;
        foreach (var value in vector)
        {
            sum += value * value;
        }

        var norm = Math.Sqrt(sum);
        if (norm <= 0)
        {
            return;
        }

        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = (float)(vector[index] / norm);
        }
    }

    private static void EnsureModelFiles(string directory)
    {
        var modelPath = Path.Combine(directory, "model.onnx");
        var vocabPath = Path.Combine(directory, "vocab.txt");
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"未找到 ONNX 模型：{modelPath}。请通过 --model-dir 或 MCMCP_MODEL_DIR 指定 e5-base-v2-onnx 目录。");
        }

        if (!File.Exists(vocabPath))
        {
            throw new FileNotFoundException(
                $"未找到 BERT 词表：{vocabPath}。Microsoft.ML.Tokenizers 需要 vocab.txt（无法直接读取 HF tokenizer.json）。");
        }
    }

    /// <summary>
    /// Microsoft.ML.Tokenizers 2.x 只提供 BertTokenizer（WordPiece + vocab.txt），
    /// 没有 HuggingFace tokenizer.json 加载器；这里显式使用 vocab.txt 并记录该限制。
    /// </summary>
    private static ModelState EnsureModel(string directory)
    {
        lock (ModelLock)
        {
            if (_cachedSession is not null &&
                _cachedTokenizer is not null &&
                string.Equals(_cachedModelDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelState(_cachedSession, _cachedTokenizer, _cachedSession.InputMetadata.ContainsKey("token_type_ids"));
            }

            var options = new BertOptions
            {
                LowerCaseBeforeTokenization = true,
                ApplyBasicTokenization = true,
                RemoveNonSpacingMarks = true,
                IndividuallyTokenizeCjk = false
            };
            var tokenizer = BertTokenizer.Create(Path.Combine(directory, "vocab.txt"), options);
            var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount)
            };
            var session = new InferenceSession(Path.Combine(directory, "model.onnx"), sessionOptions);

            _cachedSession = session;
            _cachedTokenizer = tokenizer;
            _cachedModelDirectory = directory;
            Console.Error.WriteLine($"[mc-mcp] 已加载 e5-base-v2 ONNX: {directory}");
            return new ModelState(session, tokenizer, session.InputMetadata.ContainsKey("token_type_ids"));
        }
    }

    private sealed record ModelState(InferenceSession Session, BertTokenizer Tokenizer, bool HasTokenTypeInput);

    private sealed record SymbolVectorRow(
        long Id,
        string Fqn,
        string Kind,
        string Name,
        string? Signature,
        string Origin,
        string? Comment);

    private sealed record SymbolMetadata(
        string Fqn,
        string Kind,
        string? Signature,
        string Origin,
        string FilePath,
        int StartLine,
        int EndLine,
        string? Comment);
}

public sealed record EmbeddingBuildResult(
    int Symbols,
    int Stored,
    int Dimension,
    long DurationMs,
    string ModelDirectory);
