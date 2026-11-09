using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MC_MCP.Indexer;

/// <summary>
/// 增量索引引擎：文件哈希对比 → 多线程解析（生产者）→ 单线程批量写库（消费者）→ FQN 回写。
/// </summary>
public sealed class JavaIndexer
{
    private const int WriterBatchSize = 64;
    private const int QueueCapacity = 128;

    /// <summary>边剪枝算法版本号：存进 index_meta，用于升级后自动补跑一次剪枝。</summary>
    private const string EdgePruneRevisionKey = "edge_prune_revision";
    private const string EdgePruneRevision = "9";

    /// <summary>
    /// 索引入口。scopePaths 非空时只处理这些相对路径前缀（数据源树里勾选的 Mod / 目录），
    /// reparseScope 为 true 时忽略这些文件的既有哈希，强制重新解析。
    /// </summary>
    public IndexSummary Index(
        string rootDirectory,
        IndexDatabase database,
        bool forceFullRebuild = false,
        IReadOnlyCollection<string>? scopePaths = null,
        bool reparseScope = false,
        CancellationToken cancellationToken = default,
        Action<string>? log = null,
        Action<int, int>? progress = null)
    {
        var write = log ?? (message => Console.Error.WriteLine($"[mc-mcp] {message}"));
        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var root = Path.GetFullPath(rootDirectory);
        var prefixes = NormalizeScope(scopePaths);
        var scoped = prefixes.Length > 0;
        var allFiles = EnumerateJavaFiles(root);
        var files = scoped
            ? allFiles.Where(file => MatchesScope(file.RelativePath, prefixes)).ToList()
            : allFiles;

        // Phase 10 给 symbols 增加 comment 字段：旧库的 file_hashes 会让增量构建跳过所有文件，
        // 用解析器版本号强制一次全量重建，才能把已有源码的注释回填进库。
        const string parserRevisionKey = "parser_revision";
        const string parserRevision = "10";
        if (!forceFullRebuild && !scoped &&
            !string.Equals(database.GetMeta(parserRevisionKey), parserRevision, StringComparison.Ordinal))
        {
            write("检测到解析器版本升级（Phase 10 注释提取），本次自动转为全量重建");
            forceFullRebuild = true;
        }

        if (forceFullRebuild)
        {
            ClearIndex(database);
        }

        // 1) 并行计算 SHA256，决定哪些文件需要重新解析。
        var knownHashes = forceFullRebuild
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : database.LoadFileHashes();
        if (reparseScope && scoped)
        {
            foreach (var path in knownHashes.Keys.Where(path => MatchesScope(path, prefixes)).ToArray())
            {
                knownHashes.Remove(path);
            }
        }

        var changed = new ConcurrentBag<FileWorkItem>();
        var skipped = 0;
        var scannedPaths = new HashSet<string>(files.Select(file => file.RelativePath), StringComparer.Ordinal);
        var hashed = 0;
        var cancelled = false;

        try
        {
            Parallel.ForEach(
                files,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                    CancellationToken = cancellationToken
                },
                file =>
                {
                    try
                    {
                        var hash = ComputeSha256(file.AbsolutePath);
                        if (!forceFullRebuild &&
                            knownHashes.TryGetValue(file.RelativePath, out var previous) &&
                            string.Equals(previous, hash, StringComparison.OrdinalIgnoreCase))
                        {
                            Interlocked.Increment(ref skipped);
                            return;
                        }

                        changed.Add(new FileWorkItem(file.AbsolutePath, file.RelativePath, hash));
                    }
                    catch (Exception ex)
                    {
                        write($"跳过不可读文件 {file.RelativePath}: {ex.Message}");
                    }

                    var done = Interlocked.Increment(ref hashed);
                    if (done % 2000 == 0)
                    {
                        write($"已比对 {done}/{files.Count} 个文件哈希");
                    }
                });
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            write("构建已取消（哈希比对阶段）");
        }

        // 2) 删除磁盘上已不存在的文件对应记录（增量模式下）。
        var removed = forceFullRebuild || cancelled
            ? Array.Empty<string>()
            : knownHashes.Keys
                .Where(path => !scannedPaths.Contains(path) && (!scoped || MatchesScope(path, prefixes)))
                .ToArray();
        if (removed.Length > 0)
        {
            DeleteFiles(database, removed);
            write($"已删除 {removed.Length} 个已消失文件的索引记录");
        }

        var stats = new WriterStats();
        var changedCount = changed.Count;
        if (!cancelled && changedCount > 0)
        {
            try
            {
                ParseAndWrite(database, changed, stats, cancellationToken, progress, write);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                write("构建已取消（解析阶段），已写入的批次已提交");
            }

            if (!cancelled)
            {
                var updatedEdges = ResolveEdgeTargets(database);
                write($"FQN 回写完成: 更新 {updatedEdges} 条边");
            }
        }

        // 剪枝是幂等的维护动作：有新写入的边，或库还是 Phase 9 之前的剪枝版本时执行一次。
        // 跳过无变化构建（changed=0）可省掉整表扫描，避免每次增量构建固定多花几十秒。
        if (!cancelled &&
            (changedCount > 0 || !string.Equals(database.GetMeta(EdgePruneRevisionKey), EdgePruneRevision, StringComparison.Ordinal)))
        {
            var prune = PruneEdges(database);
            database.SetMeta(EdgePruneRevisionKey, EdgePruneRevision);
            write(
                $"边剪枝: internal_call={prune.InternalCalls:N0}, 标准库删除={prune.StandardLibrary:N0}, " +
                $"未知目标删除={prune.UnknownTargets:N0}, 剩余 edges={prune.Remaining:N0}（{prune.DurationMs}ms）");
        }

        var durationMs = stopwatch.ElapsedMilliseconds;
        var summary = new IndexSummary(
            files.Count,
            changedCount,
            skipped,
            removed.Length,
            stats.Symbols,
            stats.Edges,
            stats.Injections,
            stats.ParseErrors,
            durationMs,
            forceFullRebuild,
            cancelled);

        if (!cancelled)
        {
            var modScan = ModMetadataScanner.Scan(root, database);
            write($"Mod 元数据: mods={modScan.Mods}, dependencies={modScan.Dependencies}, " +
                  $"mixinConfigs={modScan.MixinConfigs}");
            if (!scoped)
            {
                database.SetMeta(parserRevisionKey, parserRevision);
            }
        }

        database.SaveIndexRun(startedAt, summary);
        return summary;
    }

    /// <summary>把 scope 前缀规范成 "a/b" 形式，便于与 file_path 做前缀比较。</summary>
    private static string[] NormalizeScope(IReadOnlyCollection<string>? scopePaths)
        => scopePaths is not { Count: > 0 }
            ? Array.Empty<string>()
            : scopePaths
                .Select(scope => scope.Replace('\\', '/').Trim().Trim('/'))
                .Where(scope => scope.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    private static bool MatchesScope(string relativePath, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (relativePath.StartsWith(prefix + "/", StringComparison.Ordinal) ||
                string.Equals(relativePath, prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void ParseAndWrite(
        IndexDatabase database,
        ConcurrentBag<FileWorkItem> items,
        WriterStats stats,
        CancellationToken cancellationToken,
        Action<int, int>? progress,
        Action<string> write)
    {
        using var queue = new BlockingCollection<FileParseResult>(QueueCapacity);
        using var backpressure = new CancellationTokenSource();
        Exception? parseError = null;
        var cancelled = false;
        var processed = 0;
        var total = items.Count;

        var writer = Task.Run(() => WriteLoop(database, queue, stats));

        var workers = new ThreadLocal<JavaFileParser>(() => new JavaFileParser(), trackAllValues: true);
        try
        {
            Parallel.ForEach(
                items,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                    CancellationToken = cancellationToken
                },
                item =>
                {
                    FileParseResult result;
                    try
                    {
                        result = workers.Value!.Parse(
                            item.RelativePath,
                            File.ReadAllText(item.AbsolutePath),
                            item.Sha256);
                    }
                    catch (Exception ex)
                    {
                        result = FileParseResult.Failed(item.RelativePath, item.Sha256, ex.Message);
                    }

                    while (!queue.TryAdd(result, 1000, backpressure.Token))
                    {
                        // 等待消费者腾出队列空间。
                    }

                    var count = Interlocked.Increment(ref processed);
                    if (count % 500 == 0 || count == total)
                    {
                        write($"已解析 {count}/{total} 个文件");
                        progress?.Invoke(count, total);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            parseError = ex;
        }
        finally
        {
            backpressure.Cancel();
            queue.CompleteAdding();
            foreach (var parser in workers.Values)
            {
                parser.Dispose();
            }

            workers.Dispose();
        }

        try
        {
            writer.GetAwaiter().GetResult();
        }
        catch (Exception) when (parseError is not null || cancelled)
        {
            // 写线程异常在取消/已有首错时作为次要信息忽略。
        }

        if (parseError is not null)
        {
            throw parseError;
        }

        if (cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static void WriteLoop(
        IndexDatabase database,
        BlockingCollection<FileParseResult> queue,
        WriterStats stats)
    {
        using var connection = database.OpenConnection();
        var batch = new List<FileParseResult>(WriterBatchSize);
        foreach (var result in queue.GetConsumingEnumerable())
        {
            batch.Add(result);
            if (batch.Count >= WriterBatchSize)
            {
                Flush(connection, batch, stats);
            }
        }

        if (batch.Count > 0)
        {
            Flush(connection, batch, stats);
        }
    }

    /// <summary>单个事务写入一批文件；SQLite 只允许这个线程写。</summary>
    private static void Flush(SqliteConnection connection, List<FileParseResult> batch, WriterStats stats)
    {
        using var transaction = connection.BeginTransaction();
        using var symbolCommand = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO symbols (kind, name, fqn, package, signature, return_type, file_path, start_line, end_line, origin, comment)
            VALUES ($kind, $name, $fqn, $package, $signature, $returnType, $filePath, $startLine, $endLine, $origin, $comment);
            """);
        using var edgeCommand = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO edges (src_fqn, src_method, dst_fqn, dst_member, edge_type, file_path, line)
            VALUES ($srcFqn, $srcMethod, $dstFqn, $dstMember, $edgeType, $filePath, $line);
            """);
        using var injectionCommand = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO injections (mixin_class, target_class, annotation, target_method, at_value, arguments, file_path, line)
            VALUES ($mixinClass, $targetClass, $annotation, $targetMethod, $atValue, $arguments, $filePath, $line);
            """);
        using var errorCommand = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO parse_errors (file_path, line, node_type, snippet, message)
            VALUES ($filePath, $line, $nodeType, $snippet, $message);
            """);
        using var chunkCommand = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO code_chunks (file_path, start_line, end_line, content)
            VALUES ($filePath, $startLine, $endLine, $content);
            """);
        using var fileCommand = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO indexed_files (file_path, symbol_count, edge_count, injection_count)
            VALUES ($filePath, $symbolCount, $edgeCount, $injectionCount)
            ON CONFLICT(file_path) DO UPDATE SET
                symbol_count = excluded.symbol_count,
                edge_count = excluded.edge_count,
                injection_count = excluded.injection_count;
            """);
        using var hashCommand = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO file_hashes (file_path, sha256, last_indexed_at)
            VALUES ($filePath, $sha256, $indexedAt)
            ON CONFLICT(file_path) DO UPDATE SET
                sha256 = excluded.sha256,
                last_indexed_at = excluded.last_indexed_at;
            """);
        using var contextCommand = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO file_contexts (file_path, package, imports, static_imports)
            VALUES ($filePath, $package, $imports, $staticImports)
            ON CONFLICT(file_path) DO UPDATE SET
                package = excluded.package,
                imports = excluded.imports,
                static_imports = excluded.static_imports;
            """);

        var now = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var result in batch)
        {
            if (result.Succeeded)
            {
                DeleteFileRecords(connection, transaction, result.FilePath);

                foreach (var symbol in result.Symbols)
                {
                    InsertSymbol(symbolCommand, symbol);
                }

                foreach (var edge in result.Edges)
                {
                    InsertEdge(edgeCommand, edge);
                }

                foreach (var injection in result.Injections)
                {
                    InsertInjection(injectionCommand, injection);
                }

                foreach (var error in result.Errors)
                {
                    InsertError(errorCommand, error);
                }

                foreach (var chunk in result.Chunks)
                {
                    chunkCommand.Parameters.Clear();
                    chunkCommand.Parameters.AddWithValue("$filePath", chunk.FilePath);
                    chunkCommand.Parameters.AddWithValue("$startLine", chunk.StartLine);
                    chunkCommand.Parameters.AddWithValue("$endLine", chunk.EndLine);
                    chunkCommand.Parameters.AddWithValue("$content", chunk.Content);
                    chunkCommand.ExecuteNonQuery();
                }

                hashCommand.Parameters.Clear();
                hashCommand.Parameters.AddWithValue("$filePath", result.FilePath);
                hashCommand.Parameters.AddWithValue("$sha256", result.Sha256);
                hashCommand.Parameters.AddWithValue("$indexedAt", now);
                hashCommand.ExecuteNonQuery();

                contextCommand.Parameters.Clear();
                contextCommand.Parameters.AddWithValue("$filePath", result.FilePath);
                contextCommand.Parameters.AddWithValue("$package", result.PackageName ?? (object)DBNull.Value);
                contextCommand.Parameters.AddWithValue("$imports", string.Join('\n', result.Imports));
                contextCommand.Parameters.AddWithValue("$staticImports", string.Join('\n', result.StaticImports));
                contextCommand.ExecuteNonQuery();

                fileCommand.Parameters.Clear();
                fileCommand.Parameters.AddWithValue("$filePath", result.FilePath);
                fileCommand.Parameters.AddWithValue("$symbolCount", result.Symbols.Count);
                fileCommand.Parameters.AddWithValue("$edgeCount", result.Edges.Count);
                fileCommand.Parameters.AddWithValue("$injectionCount", result.Injections.Count);
                fileCommand.ExecuteNonQuery();
            }
            else
            {
                // 解析失败的文件保留旧数据，不更新哈希，下次运行会自动重试。
                foreach (var error in result.Errors)
                {
                    InsertError(errorCommand, error);
                }
            }

            stats.Symbols += result.Symbols.Count;
            stats.Edges += result.Edges.Count;
            stats.Injections += result.Injections.Count;
            stats.ParseErrors += result.Errors.Count;
            stats.CodeChunks += result.Chunks.Count;
        }

        transaction.Commit();
        batch.Clear();
    }

    private static int ResolveEdgeTargetsCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyDictionary<string, JavaFileContext> contexts,
        KnownTypeIndex knownTypes)
    {
        var cache = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        string Resolve(string filePath, string rawName)
        {
            var context = contexts.GetValueOrDefault(filePath);
            if (context is null)
            {
                return rawName;
            }

            var fileCache = cache.GetValueOrDefault(filePath);
            if (fileCache is null)
            {
                fileCache = new Dictionary<string, string>(StringComparer.Ordinal);
                cache[filePath] = fileCache;
            }

            if (fileCache.TryGetValue(rawName, out var cached))
            {
                return cached;
            }

            var resolved = SymbolResolver.ResolveType(rawName, context, knownTypes);
            fileCache[rawName] = resolved;
            return resolved;
        }

        var superclasses = new Dictionary<string, string>(StringComparer.Ordinal);
        var updates = new List<EdgeUpdate>();

        using (var select = CreateCommand(
                   connection,
                   transaction,
                   "SELECT id, file_path, src_fqn, dst_fqn, edge_type FROM edges WHERE edge_type IN ('extends', 'implements');"))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var filePath = reader.GetString(1);
                var sourceFqn = reader.GetString(2);
                var rawName = reader.GetString(3);
                var edgeType = reader.GetString(4);
                var resolved = Resolve(filePath, rawName);
                if (edgeType == "extends")
                {
                    superclasses.TryAdd(sourceFqn, resolved);
                }

                if (!string.Equals(resolved, rawName, StringComparison.Ordinal))
                {
                    updates.Add(new EdgeUpdate(id, resolved));
                }
            }
        }

        var phaseWatch = Stopwatch.StartNew();
        var updated = ApplyEdgeUpdates(connection, transaction, updates);
        Console.Error.WriteLine(
            $"[mc-mcp] FQN 回写[extends/implements]: {phaseWatch.ElapsedMilliseconds}ms, updates={updates.Count}, rows={updated}");

        phaseWatch.Restart();
        updates.Clear();
        using (var select = CreateCommand(
                   connection,
                   transaction,
                   "SELECT id, file_path, src_fqn, dst_fqn, edge_type FROM edges WHERE edge_type IN ('call', 'instantiation');"))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var filePath = reader.GetString(1);
                var sourceFqn = reader.GetString(2);
                var rawName = reader.GetString(3);
                var edgeType = reader.GetString(4);

                string resolved;
                if (edgeType == "call" && rawName == "super")
                {
                    resolved = superclasses.GetValueOrDefault(sourceFqn) ?? rawName;
                }
                else if (edgeType == "call" && rawName == "this")
                {
                    resolved = sourceFqn;
                }
                else if (IsSimpleIdentifier(rawName))
                {
                    resolved = Resolve(filePath, rawName);
                }
                else
                {
                    resolved = rawName;
                }

                if (!string.Equals(resolved, rawName, StringComparison.Ordinal))
                {
                    updates.Add(new EdgeUpdate(id, resolved));
                }
            }
        }

        updated += ApplyEdgeUpdates(connection, transaction, updates);
        Console.Error.WriteLine(
            $"[mc-mcp] FQN 回写[call/instantiation]: {phaseWatch.ElapsedMilliseconds}ms, updates={updates.Count}, rows={updated}");
        return updated;
    }

    /// <summary>
    /// 按主键批量更新：把解析结果相同的行合并在一条 UPDATE ... WHERE id IN (...) 中，
    /// 避免按 (file, src, dst) 组合扫描整张表（真实数据下会退化为分钟级）。
    /// </summary>
    private static int ApplyEdgeUpdates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<EdgeUpdate> updates)
    {
        if (updates.Count == 0)
        {
            return 0;
        }

        var grouped = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var update in updates)
        {
            var ids = grouped.GetValueOrDefault(update.NewName);
            if (ids is null)
            {
                ids = new List<long>();
                grouped[update.NewName] = ids;
            }

            ids.Add(update.Id);
        }

        using var command = CreateCommand(connection, transaction, string.Empty);
        var total = 0;
        foreach (var pair in grouped)
        {
            var ids = pair.Value;
            for (var offset = 0; offset < ids.Count; offset += 200)
            {
                var count = Math.Min(200, ids.Count - offset);
                var parameters = new string[count];
                for (var index = 0; index < count; index++)
                {
                    parameters[index] = $"$id{index}";
                }

                command.Parameters.Clear();
                command.CommandText =
                    $"UPDATE edges SET dst_fqn = $newName WHERE id IN ({string.Join(", ", parameters)});";
                command.Parameters.AddWithValue("$newName", pair.Key);
                for (var index = 0; index < count; index++)
                {
                    command.Parameters.AddWithValue($"$id{index}", ids[offset + index]);
                }

                total += command.ExecuteNonQuery();
            }
        }

        return total;
    }

    /// <summary>
    /// 图关系剪枝：把“AST 原始调用点”压缩成“有分析价值的类关系”。
    /// 1) 同类自调用（dst_fqn = src_fqn）降级为 edge_type = 'internal_call'，默认查询不再返回；
    /// 2) 指向 JDK / 三方基础库（java.*、javax.*、sun.* …）的边直接删除；
    /// 3) call 的目标不在已索引类型符号中（局部变量接收者、外部未知库）直接删除。
    /// 该操作幂等，可在每次索引后重复执行。
    /// </summary>
    private static EdgePruneResult PruneEdges(IndexDatabase database)
    {
        var stopwatch = Stopwatch.StartNew();
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        int internalCalls;
        using (var command = CreateCommand(
                   connection,
                   transaction,
                   "UPDATE edges SET edge_type = 'internal_call' WHERE edge_type = 'call' AND dst_fqn = src_fqn;"))
        {
            internalCalls = command.ExecuteNonQuery();
        }

        int standardLibrary;
        using (var command = CreateCommand(
                   connection,
                   transaction,
                   """
                   DELETE FROM edges
                   WHERE edge_type IN ('call', 'internal_call', 'instantiation')
                     AND (dst_fqn LIKE 'java.%' OR dst_fqn LIKE 'javax.%' OR dst_fqn LIKE 'sun.%'
                          OR dst_fqn LIKE 'jdk.%' OR dst_fqn LIKE 'com.sun.%'
                          OR dst_fqn LIKE 'com.google.%' OR dst_fqn LIKE 'org.slf4j.%'
                          OR dst_fqn LIKE 'org.apache.%');
                   """))
        {
            standardLibrary = command.ExecuteNonQuery();
        }

        int unknownTargets;
        using (var command = CreateCommand(
                   connection,
                   transaction,
                   """
                   DELETE FROM edges
                   WHERE edge_type = 'call'
                     AND dst_fqn NOT IN (
                         SELECT fqn FROM symbols
                         WHERE kind IN ('class', 'interface', 'enum', 'record', 'annotation'));
                   """))
        {
            unknownTargets = command.ExecuteNonQuery();
        }

        int remaining;
        using (var command = CreateCommand(connection, transaction, "SELECT COUNT(*) FROM edges;"))
        {
            remaining = Convert.ToInt32(command.ExecuteScalar());
        }

        transaction.Commit();
        return new EdgePruneResult(
            internalCalls,
            standardLibrary,
            unknownTargets,
            remaining,
            stopwatch.ElapsedMilliseconds);
    }

    private static int ResolveEdgeTargets(IndexDatabase database)
    {
        var contexts = LoadContexts(database);
        var knownTypes = LoadKnownTypes(database);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var updated = ResolveEdgeTargetsCore(connection, transaction, contexts, knownTypes);
        var injections = ResolveInjectionTargets(connection, transaction, contexts, knownTypes);
        transaction.Commit();
        Console.Error.WriteLine($"[mc-mcp] Mixin target 回写: 更新 {injections} 条注入记录");
        return updated;
    }

    /// <summary>把 @Mixin(targets = X.class) 的目标（可能只是短名）解析成真实 FQN。</summary>
    private static int ResolveInjectionTargets(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyDictionary<string, JavaFileContext> contexts,
        KnownTypeIndex knownTypes)
    {
        var updates = new List<(long Id, string NewName)>();
        using (var select = CreateCommand(
                   connection,
                   transaction,
                   "SELECT id, file_path, target_class FROM injections WHERE target_class IS NOT NULL;"))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var filePath = reader.GetString(1);
                var rawName = reader.GetString(2).Trim();
                if (rawName.Length == 0 || rawName.StartsWith('{'))
                {
                    continue;
                }

                if (rawName.EndsWith(".class", StringComparison.Ordinal))
                {
                    rawName = rawName[..^".class".Length].Trim();
                }

                var context = contexts.GetValueOrDefault(filePath);
                if (context is null)
                {
                    continue;
                }

                var resolved = SymbolResolver.ResolveType(rawName, context, knownTypes);
                if (!string.Equals(resolved, rawName, StringComparison.Ordinal))
                {
                    updates.Add((id, resolved));
                }
            }
        }

        if (updates.Count == 0)
        {
            return 0;
        }

        using var update = CreateCommand(
            connection,
            transaction,
            "UPDATE injections SET target_class = $targetClass WHERE id = $id;");
        var changed = 0;
        foreach (var (id, newName) in updates)
        {
            update.Parameters.Clear();
            update.Parameters.AddWithValue("$targetClass", newName);
            update.Parameters.AddWithValue("$id", id);
            changed += update.ExecuteNonQuery();
        }

        return changed;
    }

    private static IReadOnlyDictionary<string, JavaFileContext> LoadContexts(IndexDatabase database)
    {
        var contexts = new Dictionary<string, JavaFileContext>(StringComparer.Ordinal);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path, package, imports FROM file_contexts;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var filePath = reader.GetString(0);
            var packageName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var importsText = reader.GetString(2);
            var imports = importsText.Length == 0
                ? Array.Empty<string>()
                : importsText.Split('\n');
            contexts[filePath] = new JavaFileContext(filePath, packageName, imports, Array.Empty<string>());
        }

        return contexts;
    }

    private static KnownTypeIndex LoadKnownTypes(IndexDatabase database)
    {
        var known = new List<string>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT fqn FROM symbols WHERE kind IN ('class', 'interface', 'enum');";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            known.Add(reader.GetString(0));
        }

        return new KnownTypeIndex(known);
    }

    private static void DeleteFiles(IndexDatabase database, IReadOnlyList<string> paths)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var path in paths)
        {
            DeleteFileRecords(connection, transaction, path);
        }

        transaction.Commit();
    }

    private static void DeleteFileRecords(SqliteConnection connection, SqliteTransaction transaction, string filePath)
    {
        foreach (var table in new[] { "symbols", "edges", "injections", "parse_errors", "code_chunks", "file_hashes", "file_contexts", "indexed_files" })
        {
            using var command = CreateCommand(connection, transaction, $"DELETE FROM {table} WHERE file_path = $filePath;");
            command.Parameters.AddWithValue("$filePath", filePath);
            command.ExecuteNonQuery();
        }
    }

    private static void ClearIndex(IndexDatabase database)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = CreateCommand(
            connection,
            transaction,
            """
            DELETE FROM symbols;
            DELETE FROM edges;
            DELETE FROM injections;
            DELETE FROM indexed_files;
            DELETE FROM file_hashes;
            DELETE FROM file_contexts;
            DELETE FROM parse_errors;
            DELETE FROM code_chunks;
            DELETE FROM sqlite_sequence WHERE name IN ('symbols', 'edges', 'injections', 'parse_errors', 'index_runs');
            """);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void InsertSymbol(SqliteCommand command, SymbolRecord symbol)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$kind", symbol.Kind);
        command.Parameters.AddWithValue("$name", symbol.Name);
        command.Parameters.AddWithValue("$fqn", symbol.Fqn);
        command.Parameters.AddWithValue("$package", symbol.PackageName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$signature", symbol.Signature ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$returnType", symbol.ReturnType ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$filePath", symbol.FilePath);
        command.Parameters.AddWithValue("$startLine", symbol.StartLine);
        command.Parameters.AddWithValue("$endLine", symbol.EndLine);
        command.Parameters.AddWithValue("$origin", symbol.Origin);
        command.Parameters.AddWithValue("$comment", symbol.Comment ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void InsertEdge(SqliteCommand command, EdgeRecord edge)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$srcFqn", edge.SrcFqn);
        command.Parameters.AddWithValue("$srcMethod", edge.SrcMethod ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$dstFqn", edge.DstFqn);
        command.Parameters.AddWithValue("$dstMember", edge.DstMember ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$edgeType", edge.EdgeType);
        command.Parameters.AddWithValue("$filePath", edge.FilePath);
        command.Parameters.AddWithValue("$line", edge.Line);
        command.ExecuteNonQuery();
    }

    private static void InsertInjection(SqliteCommand command, InjectionRecord injection)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$mixinClass", injection.MixinClass);
        command.Parameters.AddWithValue("$targetClass", injection.TargetClass ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$annotation", injection.Annotation);
        command.Parameters.AddWithValue("$targetMethod", injection.TargetMethod ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$atValue", injection.AtValue ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$arguments", injection.Arguments ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$filePath", injection.FilePath);
        command.Parameters.AddWithValue("$line", injection.Line);
        command.ExecuteNonQuery();
    }

    private static void InsertError(SqliteCommand command, ParseErrorRecord error)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$filePath", error.FilePath);
        command.Parameters.AddWithValue("$line", error.Line);
        command.Parameters.AddWithValue("$nodeType", error.NodeType);
        command.Parameters.AddWithValue("$snippet", error.Snippet ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$message", error.Message);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    /// <summary>
    /// 枚举 code/ 与 mods/ 下全部 .java：严格只接受 .java 后缀（assets/data 的 json/png/ogg 一律不进入解析器），
    /// 返回绝对路径与相对工程根的路径（正斜杠）。
    /// </summary>
    public static List<JavaFileInfo> EnumerateJavaFiles(string root)
    {
        var files = new List<JavaFileInfo>();
        foreach (var directoryName in new[] { "code", "mods" })
        {
            var directory = Path.Combine(root, directoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!path.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                files.Add(new JavaFileInfo(path, Path.GetRelativePath(root, path).Replace('\\', '/')));
            }
        }

        files.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return files;
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsSimpleIdentifier(string value) => value.Length > 0 &&
        (char.IsLetter(value[0]) || value[0] == '_' || value[0] == '$') &&
        value.All(character => char.IsLetterOrDigit(character) || character is '_' or '$');

    public sealed record JavaFileInfo(string AbsolutePath, string RelativePath);

    private sealed record FileWorkItem(string AbsolutePath, string RelativePath, string Sha256);

    private sealed record EdgeUpdate(long Id, string NewName);

    private sealed class WriterStats
    {
        public int Symbols;
        public int Edges;
        public int Injections;
        public int ParseErrors;
        public int CodeChunks;
    }
}

/// <summary>一次边剪枝的结果：降级为 internal_call 的数量、删除的标准库/未知目标边数、剩余边数。</summary>
internal sealed record EdgePruneResult(
    int InternalCalls,
    int StandardLibrary,
    int UnknownTargets,
    int Remaining,
    long DurationMs);

/// <summary>单个索引批次的统计结果。</summary>
public sealed record IndexSummary(
    int Files,
    int ChangedFiles,
    int SkippedFiles,
    int RemovedFiles,
    int Symbols,
    int Edges,
    int Injections,
    int ParseErrors,
    long DurationMs,
    bool FullRebuild,
    bool Cancelled = false);
