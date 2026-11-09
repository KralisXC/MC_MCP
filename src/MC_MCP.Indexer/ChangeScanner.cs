using System.Collections.Concurrent;

namespace MC_MCP.Indexer;

/// <summary>快速变更扫描结果：不写库，只比对磁盘文件与 file_hashes。</summary>
public sealed record ChangeScanResult(
    int TotalFiles,
    int AddedFiles,
    int ModifiedFiles,
    int RemovedFiles,
    IReadOnlyList<string> ChangedPaths,
    bool Cancelled)
{
    public int ChangedFiles => AddedFiles + ModifiedFiles;
}

/// <summary>
/// Launcher 启动时的后台扫描：并行算 SHA256 与 file_hashes 比对，
/// 用于“索引一致 / 检测到 N 个文件变更”的提示条和数据源树的“仅显示有变化的”过滤。
/// </summary>
public static class ChangeScanner
{
    public static ChangeScanResult Scan(
        string rootDirectory,
        IndexDatabase database,
        CancellationToken cancellationToken = default,
        Action<int, int>? progress = null)
    {
        var root = Path.GetFullPath(rootDirectory);
        var files = JavaIndexer.EnumerateJavaFiles(root);
        var knownHashes = database.LoadFileHashes();
        var scannedPaths = new HashSet<string>(files.Select(file => file.RelativePath), StringComparer.Ordinal);
        var changedPaths = new ConcurrentBag<string>();
        var added = 0;
        var modified = 0;
        var done = 0;
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
                    string hash;
                    try
                    {
                        hash = JavaIndexer.ComputeSha256(file.AbsolutePath);
                    }
                    catch
                    {
                        return;
                    }

                    if (!knownHashes.TryGetValue(file.RelativePath, out var previous))
                    {
                        Interlocked.Increment(ref added);
                        changedPaths.Add(file.RelativePath);
                    }
                    else if (!string.Equals(previous, hash, StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref modified);
                        changedPaths.Add(file.RelativePath);
                    }

                    var count = Interlocked.Increment(ref done);
                    if (count % 1000 == 0)
                    {
                        progress?.Invoke(count, files.Count);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        var removed = cancelled
            ? 0
            : knownHashes.Keys.Count(path => !scannedPaths.Contains(path));

        return new ChangeScanResult(
            files.Count,
            added,
            modified,
            removed,
            changedPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            cancelled);
    }
}
