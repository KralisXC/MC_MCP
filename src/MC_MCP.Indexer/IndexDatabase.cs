using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MC_MCP.Indexer;

/// <summary>SQLite 索引库：负责建表、设置 WAL、增量哈希以及规模统计。</summary>
public sealed class IndexDatabase
{
    private readonly string _filePath;

    public IndexDatabase(string filePath)
    {
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    /// <summary>
    /// 项目根目录：从可执行文件目录向上找包含 code/ 或 mods/ 的目录。
    /// 发布后程序位于 app/，若 app/ 下恰好有旧版 index/ 目录，绝不能把它当成工程根，
    /// 因此第一轮只认 code/ 与 mods/；找不到时再退回“含 index/ 的目录”。
    /// </summary>
    public static string ResolveProjectRoot()
    {
        var current = Path.GetFullPath(AppContext.BaseDirectory);
        return FindUpwards(current, requireSourceDirectories: true)
               ?? FindUpwards(current, requireSourceDirectories: false)
               ?? Directory.GetCurrentDirectory();
    }

    private static string? FindUpwards(string start, bool requireSourceDirectories)
    {
        var current = start;
        for (var depth = 0; depth < 8; depth++)
        {
            var found = requireSourceDirectories
                ? Directory.Exists(Path.Combine(current, "code")) ||
                  Directory.Exists(Path.Combine(current, "mods"))
                : Directory.Exists(Path.Combine(current, "index"));
            if (found)
            {
                return current;
            }

            var parent = Directory.GetParent(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        return null;
    }

    /// <summary>当前数据库文件名（旧版本为 test.db，Phase 13 起统一为 mcmcp_core.db）。</summary>
    public const string DatabaseFileName = "mcmcp_core.db";

    /// <summary>旧版数据库文件名，仅用于自动迁移检测。</summary>
    public const string LegacyDatabaseFileName = "test.db";

    /// <summary>默认库位置：项目根 index/mcmcp_core.db（不是 app/index，避免删 app 或分发 app 时丢向量）。</summary>
    public static string DefaultDatabasePath => Path.Combine(ResolveProjectRoot(), "index", DatabaseFileName);

    /// <summary>最近一次旧库迁移的提示信息（供 Launcher 写日志）。</summary>
    public static string? LastMigrationMessage { get; private set; }

    /// <summary>最近一次向量精度迁移的提示信息（Float32 → Float16，供 Launcher 写日志）。</summary>
    public static string? LastEmbeddingFormatMessage { get; private set; }

    /// <summary>
    /// 默认库：先把项目根 index/test.db 就地改名为 mcmcp_core.db，
    /// 再尝试把更老的 app/index/test.db 迁移到项目根，最后返回实例。
    /// </summary>
    public static IndexDatabase CreateAtDefaultLocation()
    {
        var target = DefaultDatabasePath;
        // 先做同目录改名：项目根的 test.db 才是最新数据，不能被 app/index 里的老库抢占。
        LastMigrationMessage = MigrateRenamedDatabase(target) ?? MigrateLegacyIndex(target);
        return new IndexDatabase(target);
    }

    /// <summary>
    /// Phase 13 数据库改名迁移：index/ 下存在旧版 test.db（含 -wal / -shm）且 mcmcp_core.db
    /// 不存在时，就地重命名，避免升级后几十万条向量“消失”。
    /// </summary>
    public static string? MigrateRenamedDatabase(string targetPath)
    {
        var target = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var legacy = Path.GetFullPath(Path.Combine(directory, LegacyDatabaseFileName));
        if (string.Equals(legacy, target, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!File.Exists(legacy) || File.Exists(target))
        {
            return null;
        }

        try
        {
            // 连接池可能仍持有 test.db-wal / test.db-shm 句柄，不先清空无法重命名。
            SqliteConnection.ClearAllPools();
            File.Move(legacy, target);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(legacy + suffix))
                {
                    File.Move(legacy + suffix, target + suffix, true);
                }
            }

            const string message = "检测到旧版 test.db，已自动重命名为 mcmcp_core.db";
            Console.Error.WriteLine($"[mc-mcp] {message}");
            return message;
        }
        catch (Exception ex)
        {
            var message = $"旧版 test.db 重命名失败（{legacy} → {target}）：{ex.Message}";
            Console.Error.WriteLine($"[mc-mcp] {message}");
            return message;
        }
    }

    /// <summary>
    /// 旧版本把索引库放在 app/index/test.db。若目标库不存在而旧库存在，连同 -wal / -shm 一起移动，
    /// 避免升级后几十万条向量“消失”。迁移失败不阻断启动，只返回提示。
    /// </summary>
    public static string? MigrateLegacyIndex(string targetPath)
    {
        var legacy = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "index", LegacyDatabaseFileName));
        var target = Path.GetFullPath(targetPath);
        if (string.Equals(legacy, target, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!File.Exists(legacy) || File.Exists(target))
        {
            return null;
        }

        try
        {
            SqliteConnection.ClearAllPools();
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(legacy, target);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(legacy + suffix))
                {
                    File.Move(legacy + suffix, target + suffix, true);
                }
            }

            var message = $"检测到旧索引库 {legacy}，已迁移到 {target}（含 -wal/-shm）";
            Console.Error.WriteLine($"[mc-mcp] {message}");
            return message;
        }
        catch (Exception ex)
        {
            var message = $"旧索引库迁移失败（{legacy} → {target}）：{ex.Message}";
            Console.Error.WriteLine($"[mc-mcp] {message}");
            return message;
        }
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        using var connection = OpenConnection();
        using (var wal = connection.CreateCommand())
        {
            // WAL 是持久化设置，只需在建库时设置一次；后续连接自动继承。
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteScalar();
        }

        // 旧版向量表以 fqn 为主键、没有 symbol_id 外键，直接 DROP 后按新结构重建。
        MigrateLegacyEmbeddings(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS symbols (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                kind        TEXT NOT NULL,
                name        TEXT NOT NULL,
                fqn         TEXT NOT NULL,
                package     TEXT,
                signature   TEXT,
                return_type TEXT,
                file_path   TEXT NOT NULL,
                start_line  INTEGER NOT NULL,
                end_line    INTEGER NOT NULL,
                origin      TEXT NOT NULL DEFAULT 'vanilla',
                comment     TEXT
            );

            CREATE TABLE IF NOT EXISTS edges (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                src_fqn     TEXT NOT NULL,
                src_method  TEXT,
                dst_fqn     TEXT NOT NULL,
                dst_member  TEXT,
                edge_type   TEXT NOT NULL,
                file_path   TEXT NOT NULL,
                line        INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS injections (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                mixin_class   TEXT NOT NULL,
                target_class  TEXT,
                annotation    TEXT NOT NULL,
                target_method TEXT,
                at_value      TEXT,
                arguments     TEXT,
                file_path     TEXT NOT NULL,
                line          INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS indexed_files (
                file_path       TEXT PRIMARY KEY,
                symbol_count    INTEGER NOT NULL,
                edge_count      INTEGER NOT NULL,
                injection_count INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS file_hashes (
                file_path       TEXT PRIMARY KEY,
                sha256          TEXT NOT NULL,
                last_indexed_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS file_contexts (
                file_path      TEXT PRIMARY KEY,
                package        TEXT,
                imports        TEXT NOT NULL,
                static_imports TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS parse_errors (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path  TEXT NOT NULL,
                line       INTEGER NOT NULL,
                node_type  TEXT NOT NULL,
                snippet    TEXT,
                message    TEXT
            );

            CREATE TABLE IF NOT EXISTS index_runs (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at     TEXT NOT NULL,
                duration_ms    INTEGER NOT NULL,
                mode           TEXT NOT NULL,
                files          INTEGER NOT NULL,
                changed_files  INTEGER NOT NULL,
                skipped_files  INTEGER NOT NULL,
                removed_files  INTEGER NOT NULL,
                symbols        INTEGER NOT NULL,
                edges          INTEGER NOT NULL,
                injections     INTEGER NOT NULL,
                parse_errors   INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS index_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS mod_metadata (
                mod_id         TEXT PRIMARY KEY,
                name           TEXT,
                version        TEXT,
                loader         TEXT NOT NULL,
                mod_dir        TEXT NOT NULL,
                toml_path      TEXT,
                mixin_configs  TEXT NOT NULL,
                description    TEXT,
                indexed_at     TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS mod_dependencies (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                mod_id        TEXT NOT NULL,
                dep_mod_id    TEXT NOT NULL,
                mandatory     INTEGER NOT NULL,
                version_range TEXT,
                side          TEXT,
                UNIQUE(mod_id, dep_mod_id)
            );

            CREATE TABLE IF NOT EXISTS symbol_embeddings (
                symbol_id   INTEGER PRIMARY KEY,
                vector      BLOB NOT NULL,
                dim         INTEGER NOT NULL,
                format      TEXT NOT NULL DEFAULT 'float16',
                source_text TEXT,
                model       TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                FOREIGN KEY (symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_symbols_fqn ON symbols(fqn);
            CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
            CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_path);
            CREATE INDEX IF NOT EXISTS idx_edges_src ON edges(src_fqn);
            CREATE INDEX IF NOT EXISTS idx_edges_dst ON edges(dst_fqn);
            CREATE INDEX IF NOT EXISTS idx_edges_file ON edges(file_path);
            CREATE INDEX IF NOT EXISTS idx_edges_type ON edges(edge_type);
            CREATE INDEX IF NOT EXISTS idx_injections_target ON injections(target_class);
            CREATE INDEX IF NOT EXISTS idx_injections_file ON injections(file_path);
            CREATE INDEX IF NOT EXISTS idx_parse_errors_file ON parse_errors(file_path);
            """;
        command.ExecuteNonQuery();

        EnsureSymbolOriginColumn(connection);
        EnsureSymbolCommentColumn(connection);
        MigrateLegacyCodeFts(connection);
        EnsureCodeFts(connection);
        EnsureEmbeddingFormatColumn(connection);
        MigrateLegacyEmbeddingsToFloat16(connection);
    }

    /// <summary>为 symbol_embeddings 补充 format 列（旧库没有）：标记向量存储精度。</summary>
    private static void EnsureEmbeddingFormatColumn(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('symbol_embeddings') WHERE name = 'format';";
        if (Convert.ToInt64(check.ExecuteScalar()) > 0)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE symbol_embeddings ADD COLUMN format TEXT NOT NULL DEFAULT 'float16';";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Phase 9：向量由 Float32 改为 Float16（768 × 4 = 3072 字节 → 768 × 2 = 1536 字节）。
    /// 抽样前几行判断精度（O(1)，不在每次启动时全表扫描）：一旦发现 4 字节/维的旧向量，
    /// 整表清空，由下一次 --build-vectors 以半精度重建，避免新旧精度混存导致检索静默跳过。
    /// </summary>
    private static void MigrateLegacyEmbeddingsToFloat16(SqliteConnection connection)
    {
        LastEmbeddingFormatMessage = null;

        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT dim, length(vector) FROM symbol_embeddings LIMIT 5;";
        var legacy = false;
        var dimensions = new SortedSet<int>();
        using (var reader = probe.ExecuteReader())
        {
            while (reader.Read())
            {
                var dim = reader.GetInt64(0);
                var bytes = reader.GetInt64(1);
                if (dim <= 0)
                {
                    continue;
                }

                dimensions.Add((int)dim);
                if (bytes == dim * sizeof(float))
                {
                    legacy = true;
                }
            }
        }

        if (!legacy)
        {
            return;
        }

        using var clear = connection.CreateCommand();
        clear.CommandText = "DELETE FROM symbol_embeddings;";
        var deleted = clear.ExecuteNonQuery();
        var dimText = dimensions.Count > 0 ? dimensions.Max.ToString() : "768";
        LastEmbeddingFormatMessage =
            $"检测到 {deleted:N0} 条 Float32 向量（{dimText}×4 字节），已清空；请重新执行 --build-vectors 以 Float16（{dimText}×2 字节）重建";
        Console.Error.WriteLine($"[mc-mcp] {LastEmbeddingFormatMessage}");
    }

    /// <summary>旧版 symbol_embeddings 以 fqn 为主键，没有 symbol_id；检测到旧结构直接 DROP 重建。</summary>
    private static void MigrateLegacyEmbeddings(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText =
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'symbol_embeddings';";
        var schema = check.ExecuteScalar() as string;
        if (schema is null || schema.Contains("symbol_id", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TABLE symbol_embeddings;";
        drop.ExecuteNonQuery();
        Console.Error.WriteLine("[mc-mcp] 检测到旧版 symbol_embeddings（fqn 主键），已 DROP 并按 symbol_id + ON DELETE CASCADE 重建");
    }

    /// <summary>旧库没有 origin 列：补列并按既有 file_path 回填 vanilla / neoforge / fabric / mod。</summary>
    private static void EnsureSymbolOriginColumn(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('symbols') WHERE name = 'origin';";
        if (Convert.ToInt64(check.ExecuteScalar()) > 0)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText =
            """
            ALTER TABLE symbols ADD COLUMN origin TEXT NOT NULL DEFAULT 'vanilla';
            UPDATE symbols SET origin = CASE
                WHEN file_path LIKE 'code/%' THEN 'vanilla'
                WHEN file_path LIKE '%/neoforge/%' OR file_path LIKE 'mods/neoforge/%' THEN 'neoforge'
                WHEN file_path LIKE '%/fabric/%' OR file_path LIKE 'mods/fabric/%' THEN 'fabric'
                ELSE 'mod'
            END;
            """;
        alter.ExecuteNonQuery();
        Console.Error.WriteLine("[mc-mcp] 已为 symbols 表补充 origin 列并回填");
    }

    /// <summary>Phase 10：symbols 增加 comment 列，存放声明前紧邻的 Javadoc / 行注释（反编译源码的自然语言说明）。</summary>
    private static void EnsureSymbolCommentColumn(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('symbols') WHERE name = 'comment';";
        if (Convert.ToInt64(check.ExecuteScalar()) > 0)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE symbols ADD COLUMN comment TEXT;";
        alter.ExecuteNonQuery();
        Console.Error.WriteLine("[mc-mcp] 已为 symbols 表补充 comment 列（注释上下文，需重新索引回填）");
    }

    /// <summary>
    /// FTS5 使用 external content 模式：真实内容存 code_chunks，code_fts 只保存倒排索引。
    /// 这样按 file_path 删除切片时走普通表索引，避免对 FTS 做全表扫描（旧实现会导致全量索引分钟级卡顿）。
    /// </summary>
    private static void EnsureCodeFts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS code_chunks (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path  TEXT NOT NULL,
                start_line INTEGER NOT NULL,
                end_line   INTEGER NOT NULL,
                content    TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_code_chunks_file ON code_chunks(file_path);

            CREATE VIRTUAL TABLE IF NOT EXISTS code_fts USING fts5(
                content,
                content='code_chunks',
                content_rowid='id',
                tokenize='unicode61 remove_diacritics 2'
            );

            CREATE TRIGGER IF NOT EXISTS code_chunks_ai AFTER INSERT ON code_chunks BEGIN
                INSERT INTO code_fts(rowid, content) VALUES (new.id, new.content);
            END;

            CREATE TRIGGER IF NOT EXISTS code_chunks_ad AFTER DELETE ON code_chunks BEGIN
                INSERT INTO code_fts(code_fts, rowid, content) VALUES ('delete', old.id, old.content);
            END;

            CREATE TRIGGER IF NOT EXISTS code_chunks_au AFTER UPDATE ON code_chunks BEGIN
                INSERT INTO code_fts(code_fts, rowid, content) VALUES ('delete', old.id, old.content);
                INSERT INTO code_fts(rowid, content) VALUES (new.id, new.content);
            END;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>旧版本把 file_path 直接存在 FTS 表里；检测到旧结构则重建，并强制下一次全量索引。</summary>
    private static void MigrateLegacyCodeFts(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'code_fts';";
        var schema = check.ExecuteScalar() as string;
        if (schema is null || schema.Contains("content='code_chunks'", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var drop = connection.CreateCommand();
        drop.CommandText =
            """
            DROP TABLE code_fts;
            DELETE FROM file_hashes;
            """;
        drop.ExecuteNonQuery();
        Console.Error.WriteLine("[mc-mcp] 检测到旧版 code_fts，已重建并清空 file_hashes 以触发全量索引");
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_filePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        // WAL 下读写并发更友好；NORMAL 降低 fsync 次数，索引吞吐更高；foreign_keys 必须逐连接开启，否则级联删除不生效。
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    /// <summary>读取全部文件的 SHA256，用于增量对比。</summary>
    public Dictionary<string, string> LoadFileHashes()
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path, sha256 FROM file_hashes;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hashes[reader.GetString(0)] = reader.GetString(1);
        }

        return hashes;
    }

    public int GetSymbolCount() => Count("symbols");

    public int GetEdgeCount() => Count("edges");

    public int GetInjectionCount() => Count("injections");

    public int GetFileCount() => Count("indexed_files");

    public int GetFileHashCount() => Count("file_hashes");

    public int GetParseErrorCount() => Count("parse_errors");

    public int GetCodeChunkCount() => Count("code_chunks");

    public int GetModCount() => Count("mod_metadata");

    public int GetEmbeddingCount() => Count("symbol_embeddings");

    /// <summary>解析报错的文件 → 条数，用于数据源树的“仅显示解析报错”过滤。</summary>
    public Dictionary<string, int> GetParseErrorPaths()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path, COUNT(*) FROM parse_errors GROUP BY file_path;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = (int)reader.GetInt64(1);
        }

        return result;
    }

    /// <summary>尚无向量的符号数量（可按 kinds 与源码范围过滤）。</summary>
    public int GetMissingEmbeddingCount(
        IReadOnlyCollection<string>? kinds = null,
        IReadOnlyCollection<string>? scopePaths = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var conditions = new List<string> { "s.fqn <> ''" };

        if (kinds is { Count: > 0 })
        {
            var placeholders = new List<string>();
            var index = 0;
            foreach (var kind in kinds)
            {
                placeholders.Add($"$kind{index}");
                command.Parameters.AddWithValue($"$kind{index}", kind.ToLowerInvariant());
                index++;
            }

            conditions.Add($"s.kind IN ({string.Join(", ", placeholders)})");
        }

        AppendScopeConditions(command, conditions, scopePaths);

        command.CommandText =
            $"""
            SELECT COUNT(*)
            FROM symbols s
            LEFT JOIN symbol_embeddings e ON e.symbol_id = s.id
            WHERE e.symbol_id IS NULL AND {string.Join(" AND ", conditions)};
            """;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>把“数据源相对路径前缀”翻译成 file_path LIKE 条件（供范围构建/范围向量复用）。</summary>
    internal static void AppendScopeConditions(
        SqliteCommand command,
        List<string> conditions,
        IReadOnlyCollection<string>? scopePaths)
    {
        if (scopePaths is not { Count: > 0 })
        {
            return;
        }

        var placeholders = new List<string>();
        var index = 0;
        foreach (var scope in scopePaths)
        {
            var prefix = scope.Replace('\\', '/').Trim().Trim('/');
            if (prefix.Length == 0)
            {
                continue;
            }

            placeholders.Add($"$scope{index}");
            command.Parameters.AddWithValue($"$scope{index}", prefix + "/%");
            index++;
        }

        if (placeholders.Count > 0)
        {
            conditions.Add($"({string.Join(" OR ", placeholders.Select(p => $"s.file_path LIKE {p}"))})");
        }
    }

    /// <summary>读取索引库级元信息（例如边剪枝版本），不存在时返回 null。</summary>
    public string? GetMeta(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM index_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    /// <summary>写入索引库级元信息（UPSERT）。</summary>
    public void SetMeta(string key, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO index_meta (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>清空向量表（用于 --vectors-reset 后重建）。</summary>
    public int ClearEmbeddings()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM symbol_embeddings;";
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// 用 VACUUM INTO 生成一致性快照，再连同 manifest 打包为 ZIP，便于分享索引库。
    /// </summary>
    public string ExportZip(string targetPath)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var targetDirectory = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"mc-mcp-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var snapshotPath = Path.Combine(tempDirectory, DatabaseFileName);
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                var escaped = snapshotPath.Replace("'", "''");
                command.CommandText = $"VACUUM INTO '{escaped}';";
                command.ExecuteNonQuery();
            }

            var run = GetLastRun();
            var manifest = new StringBuilder();
            manifest.AppendLine("mc-mcp index export");
            manifest.AppendLine($"created_at : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            manifest.AppendLine($"source_db  : {_filePath}");
            manifest.AppendLine($"symbols    : {GetSymbolCount()}");
            manifest.AppendLine($"edges      : {GetEdgeCount()}");
            manifest.AppendLine($"injections : {GetInjectionCount()}");
            manifest.AppendLine($"files      : {GetFileCount()}");
            manifest.AppendLine($"code_chunks: {GetCodeChunkCount()}");
            manifest.AppendLine($"parse_errs : {GetParseErrorCount()}");
            if (run is not null)
            {
                manifest.AppendLine($"last_run   : {run.Mode} {run.DurationMs}ms at {run.StartedAt}");
            }

            File.WriteAllText(Path.Combine(tempDirectory, "manifest.txt"), manifest.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(fullTarget))
            {
                File.Delete(fullTarget);
            }

            ZipFile.CreateFromDirectory(tempDirectory, fullTarget, CompressionLevel.Optimal, false);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }

        return fullTarget;
    }

    /// <summary>记录一次索引运行的耗时与统计，供 Launcher 展示。</summary>
    public void SaveIndexRun(string startedAt, IndexSummary summary)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO index_runs (started_at, duration_ms, mode, files, changed_files, skipped_files,
                                    removed_files, symbols, edges, injections, parse_errors)
            VALUES ($startedAt, $durationMs, $mode, $files, $changedFiles, $skippedFiles,
                    $removedFiles, $symbols, $edges, $injections, $parseErrors);
            """;
        command.Parameters.AddWithValue("$startedAt", startedAt);
        command.Parameters.AddWithValue("$durationMs", summary.DurationMs);
        command.Parameters.AddWithValue("$mode", summary.FullRebuild ? "full" : "incremental");
        command.Parameters.AddWithValue("$files", summary.Files);
        command.Parameters.AddWithValue("$changedFiles", summary.ChangedFiles);
        command.Parameters.AddWithValue("$skippedFiles", summary.SkippedFiles);
        command.Parameters.AddWithValue("$removedFiles", summary.RemovedFiles);
        command.Parameters.AddWithValue("$symbols", summary.Symbols);
        command.Parameters.AddWithValue("$edges", summary.Edges);
        command.Parameters.AddWithValue("$injections", summary.Injections);
        command.Parameters.AddWithValue("$parseErrors", summary.ParseErrors);
        command.ExecuteNonQuery();
    }

    /// <summary>最近一次索引运行的元数据；没有运行记录时返回 null。</summary>
    public IndexRunRecord? GetLastRun()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT started_at, duration_ms, mode, files, changed_files, skipped_files, removed_files,
                   symbols, edges, injections, parse_errors
            FROM index_runs
            ORDER BY id DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new IndexRunRecord(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            (int)reader.GetInt64(3),
            (int)reader.GetInt64(4),
            (int)reader.GetInt64(5),
            (int)reader.GetInt64(6),
            (int)reader.GetInt64(7),
            (int)reader.GetInt64(8),
            (int)reader.GetInt64(9),
            (int)reader.GetInt64(10));
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024d / 1024 / 1024:0.00} GB"
            : bytes >= 1024 * 1024
                ? $"{bytes / 1024d / 1024:0.00} MB"
                : bytes >= 1024
                    ? $"{bytes / 1024d:0.0} KB"
                    : $"{bytes} B";

    private int Count(string table)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        var value = command.ExecuteScalar();
        return value is long count ? (int)count : 0;
    }

    /// <summary>
    /// 全量跑之前的快速自检：完整性、外键开关、外键违例、孤儿向量、FTS 一致性、向量维度。
    /// 全部检查都在同一连接上执行，不修改任何数据。
    /// </summary>
    public IReadOnlyList<SelfTestCheck> RunSelfTest()
    {
        var checks = new List<SelfTestCheck>();
        using var connection = OpenConnection();

        (bool Ok, string Detail) Scalar(string sql)
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                var value = command.ExecuteScalar();
                return (true, value?.ToString() ?? "(null)");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // 1. 核心表是否齐全
        var requiredTables = new[]
        {
            "symbols", "edges", "injections", "indexed_files", "file_hashes",
            "code_chunks", "code_fts", "mod_metadata", "symbol_embeddings"
        };
        var missing = requiredTables
            .Where(table => Scalar($"SELECT COUNT(*) FROM sqlite_master WHERE name = '{table}';").Detail == "0")
            .ToArray();
        checks.Add(new SelfTestCheck(
            "核心表结构",
            missing.Length == 0,
            missing.Length == 0 ? $"{requiredTables.Length} 张核心表齐全" : $"缺失：{string.Join(", ", missing)}"));

        // 2. 数据库完整性
        var integrity = Scalar("PRAGMA integrity_check;");
        checks.Add(new SelfTestCheck(
            "数据库完整性",
            integrity.Detail == "ok",
            integrity.Ok ? integrity.Detail : $"执行失败：{integrity.Detail}"));

        // 3. 外键开关（必须逐连接开启，否则级联删除不生效）
        var foreignKeys = Scalar("PRAGMA foreign_keys;");
        checks.Add(new SelfTestCheck(
            "外键开关",
            foreignKeys.Detail == "1",
            foreignKeys.Detail == "1" ? "PRAGMA foreign_keys = ON" : $"当前为 {foreignKeys.Detail}"));

        // 4. 外键违例
        var violations = Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;");
        checks.Add(new SelfTestCheck(
            "外键一致性",
            violations.Ok && violations.Detail == "0",
            violations.Ok ? $"{violations.Detail} 条违例" : $"执行失败：{violations.Detail}"));

        // 5. 向量孤儿数据
        var orphans = Scalar(
            "SELECT COUNT(*) FROM symbol_embeddings e LEFT JOIN symbols s ON s.id = e.symbol_id WHERE s.id IS NULL;");
        checks.Add(new SelfTestCheck(
            "向量孤儿数据",
            orphans.Ok && orphans.Detail == "0",
            orphans.Ok ? $"{orphans.Detail} 条孤儿向量" : $"执行失败：{orphans.Detail}"));

        // 6. 向量表是否仍带 ON DELETE CASCADE
        var embeddingSchema = Scalar(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'symbol_embeddings';");
        var hasCascade = embeddingSchema.Detail.Contains("ON DELETE CASCADE", StringComparison.OrdinalIgnoreCase);
        checks.Add(new SelfTestCheck(
            "向量表级联定义",
            hasCascade,
            hasCascade ? "symbol_embeddings.symbol_id → symbols(id) ON DELETE CASCADE" : "缺少 ON DELETE CASCADE"));

        // 7. 向量维度 + Float16 半精度压缩（768 × 2 = 1536 字节/条）
        var vectors = Scalar(
            """
            SELECT COUNT(*) || '|' || COALESCE(SUM(length(vector)), 0) || '|' ||
                   COALESCE(SUM(CASE WHEN dim <> 768 OR length(vector) <> dim * 2 OR format <> 'float16'
                                     THEN 1 ELSE 0 END), 0)
            FROM symbol_embeddings;
            """);
        var vectorParts = vectors.Detail.Split('|');
        var vectorOk = vectors.Ok && vectorParts.Length == 3 && vectorParts[2] == "0";
        var vectorDetail = vectors.Ok && vectorParts.Length == 3
            ? $"{Convert.ToInt64(vectorParts[0]):N0} 条，共 {FormatBytes(Convert.ToInt64(vectorParts[1]))}，" +
              $"异常 {vectorParts[2]} 条（期望 768 维 × 2 字节 = {EmbeddingService.StoredVectorBytes} 字节/条）"
            : $"执行失败：{vectors.Detail}";
        checks.Add(new SelfTestCheck("向量精度（Float16）", vectorOk, vectorDetail));

        // 8. FTS 与切片表行数一致
        var chunks = Scalar("SELECT COUNT(*) FROM code_chunks;");
        var fts = Scalar("SELECT COUNT(*) FROM code_fts;");
        var ftsOk = chunks.Ok && fts.Ok && chunks.Detail == fts.Detail;
        checks.Add(new SelfTestCheck(
            "全文索引一致性",
            ftsOk,
            ftsOk ? $"code_chunks = code_fts = {chunks.Detail}" : $"code_chunks={chunks.Detail}, code_fts={fts.Detail}"));

        // 9. 符号基本字段
        var emptyFqn = Scalar("SELECT COUNT(*) FROM symbols WHERE fqn IS NULL OR fqn = '' OR file_path IS NULL;");
        checks.Add(new SelfTestCheck(
            "符号字段完整性",
            emptyFqn.Ok && emptyFqn.Detail == "0",
            emptyFqn.Ok ? $"空 FQN / 路径 {emptyFqn.Detail} 条" : $"执行失败：{emptyFqn.Detail}"));

        return checks;
    }

    /// <summary>
    /// 用外部 .db 覆盖当前索引库（用于把 4050 生成好向量的库拷回本机）。
    /// 覆盖前先校验源库可读且包含 symbols 表，覆盖后清理旧 WAL 并重跑建表迁移。
    /// </summary>
    public DatabaseReplaceResult ReplaceWith(string sourcePath)
    {
        var source = Path.GetFullPath(sourcePath);
        var target = Path.GetFullPath(_filePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("所选数据库不存在。", source);
        }

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseReplaceResult(target, GetSymbolCount(), GetEmbeddingCount(), "源文件与当前索引库相同，未做任何修改。");
        }

        int sourceSymbols;
        using (var probe = new SqliteConnection($"Data Source={source};Mode=ReadOnly"))
        {
            probe.Open();
            using var command = probe.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'symbols';";
            if (Convert.ToInt64(command.ExecuteScalar()) == 0)
            {
                throw new InvalidOperationException("所选文件不是有效的 MC_MCP 索引库（缺少 symbols 表）。");
            }

            command.CommandText = "SELECT COUNT(*) FROM symbols;";
            sourceSymbols = Convert.ToInt32(command.ExecuteScalar());
        }

        // Microsoft.Data.Sqlite 默认连接池会一直持有 mcmcp_core.db-wal / mcmcp_core.db-shm 的文件句柄，
        // 不先清空连接池，覆盖和删除都会抛“文件被另一个进程占用”。
        SqliteConnection.ClearAllPools();

        // 旧库的 WAL 若残留，回放到新库会直接损坏数据，先删掉再覆盖。
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var stale = target + suffix;
            if (File.Exists(stale))
            {
                File.Delete(stale);
            }
        }

        var targetDirectory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        File.Copy(source, target, true);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(source + suffix))
            {
                File.Copy(source + suffix, target + suffix, true);
            }
        }

        // 若拷来的是旧版本 schema（例如缺 mod_metadata 或旧向量表），这里顺带完成迁移。
        EnsureCreated();

        return new DatabaseReplaceResult(
            target,
            sourceSymbols,
            GetEmbeddingCount(),
            $"已替换索引库：{source} → {target}（symbols={sourceSymbols}, embeddings={GetEmbeddingCount()}）");
    }
}

/// <summary>单项自检结果。</summary>
public sealed record SelfTestCheck(string Name, bool Passed, string Detail);

/// <summary>替换数据库后的概要。</summary>
public sealed record DatabaseReplaceResult(string TargetPath, int Symbols, int Embeddings, string Message);

/// <summary>一次索引运行的概要，供 Launcher 展示“上次索引耗时”。</summary>
public sealed record IndexRunRecord(
    string StartedAt,
    long DurationMs,
    string Mode,
    int Files,
    int ChangedFiles,
    int SkippedFiles,
    int RemovedFiles,
    int Symbols,
    int Edges,
    int Injections,
    int ParseErrors);
