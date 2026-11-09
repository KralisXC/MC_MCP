using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MC_MCP.Indexer;

/// <summary>
/// 扫描 mods/ 下的 neoforge.mods.toml、fabric.mod.json 与 *.mixins.json，
/// 推断加载器类型并把 Mod 元数据与依赖写入 mod_metadata / mod_dependencies。
/// </summary>
public static class ModMetadataScanner
{
    private static readonly Regex KeyValuePattern = new(
        @"^([A-Za-z0-9_\-]+)\s*=\s*(.+)$",
        RegexOptions.Compiled);

    public sealed record ScanResult(int Mods, int Dependencies, int MixinConfigs);

    public static ScanResult Scan(string rootDirectory, IndexDatabase database)
    {
        var root = Path.GetFullPath(rootDirectory);
        var modsRoot = Path.Combine(root, "mods");
        if (!Directory.Exists(modsRoot))
        {
            Save(database, Array.Empty<ModEntry>(), ModsRootDirectory(root));
            return new ScanResult(0, 0, 0);
        }

        var entries = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var tomlPath in Directory.EnumerateFiles(modsRoot, "neoforge.mods.toml", SearchOption.AllDirectories))
        {
            var parsed = ParseToml(tomlPath);
            var modDirectory = ResolveModDirectory(root, tomlPath);
            var resourcesDirectory = ResolveResourcesDirectory(tomlPath, modDirectory);
            foreach (var parsedMod in parsed.Mods)
            {
                var entry = GetOrCreate(entries, parsedMod.ModId, root, modDirectory);
                entry.Name ??= parsedMod.Name;
                entry.Version ??= parsedMod.Version;
                entry.Description ??= parsedMod.Description;
                entry.TomlPath = Path.GetRelativePath(root, tomlPath).Replace('\\', '/');
                entry.Loaders.Add(InferLoader(root, modDirectory, hasToml: true, hasFabric: false));
                foreach (var config in parsed.MixinConfigs)
                {
                    entry.Mixins.Add(config);
                    FindMixinFile(resourcesDirectory, config, entry);
                }

                foreach (var dependency in parsed.Dependencies)
                {
                    AddDependency(entry, dependency);
                }
            }
        }

        foreach (var fabricPath in Directory.EnumerateFiles(modsRoot, "fabric.mod.json", SearchOption.AllDirectories))
        {
            var parsed = ParseFabricMetadata(fabricPath);
            if (parsed is null)
            {
                continue;
            }

            var modDirectory = ResolveModDirectory(root, fabricPath);
            var entry = GetOrCreate(entries, parsed.ModId, root, modDirectory);
            entry.Name ??= parsed.Name;
            entry.Version ??= parsed.Version;
            entry.Description ??= parsed.Description;
            entry.Loaders.Add(InferLoader(root, modDirectory, hasToml: false, hasFabric: true));
            foreach (var config in parsed.MixinConfigs)
            {
                entry.Mixins.Add(config);
                FindMixinFile(ResolveResourcesDirectory(fabricPath, modDirectory), config, entry);
            }
        }

        // 兜底：没有被 toml/fabric 引用的 mixins.json，按所在目录归到最近的 Mod 条目。
        var mixinJsonFiles = Directory
            .EnumerateFiles(modsRoot, "*.mixins.json", SearchOption.AllDirectories)
            .Select(path => Path.GetFullPath(path))
            .ToList();
        foreach (var mixinPath in mixinJsonFiles)
        {
            var directory = Path.GetDirectoryName(mixinPath)!;
            var owner = FindOwner(entries, root, directory);
            if (owner is null)
            {
                var syntheticId = Path.GetFileName(mixinPath).Replace(".mixins.json", string.Empty);
                owner = GetOrCreate(entries, syntheticId, root, directory);
                var loader = InferLoader(root, directory, hasToml: false, hasFabric: false);
                // 没有 toml/fabric.mod.json 时，退化为按 mixins.json 文件名推断。
                if (loader == "unknown")
                {
                    var fileName = Path.GetFileName(mixinPath).ToLowerInvariant();
                    loader = fileName.Contains("fabric") ? "fabric"
                        : fileName.Contains("neoforge") ? "neoforge"
                        : "unknown";
                }

                owner.Loaders.Add(loader);
            }

            owner.Mixins.Add(Path.GetFileName(mixinPath));
        }

        var saved = Save(database, entries.Values, ModsRootDirectory(root));
        var mixinCount = entries.Values.Sum(entry => entry.Mixins.Count);
        return new ScanResult(saved.Mods, saved.Dependencies, mixinCount);
    }

    private static string ModsRootDirectory(string root) => Path.Combine(root, "mods");

    private static ModEntry GetOrCreate(
        IDictionary<string, ModEntry> entries,
        string modId,
        string root,
        string modDirectory)
    {
        var normalizedId = string.IsNullOrWhiteSpace(modId)
            ? Path.GetFileName(modDirectory.TrimEnd(Path.DirectorySeparatorChar))
            : modId.Trim();
        if (entries.TryGetValue(normalizedId, out var existing))
        {
            return existing;
        }

        var entry = new ModEntry(normalizedId, Path.GetRelativePath(root, modDirectory).Replace('\\', '/'));
        entries[normalizedId] = entry;
        return entry;
    }

    private static ModEntry? FindOwner(
        IDictionary<string, ModEntry> entries,
        string root,
        string directory)
    {
        var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
        ModEntry? best = null;
        var bestLength = -1;
        foreach (var entry in entries.Values)
        {
            if (relative.StartsWith(entry.ModDir, StringComparison.OrdinalIgnoreCase) &&
                entry.ModDir.Length > bestLength)
            {
                best = entry;
                bestLength = entry.ModDir.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// 加载器推断：优先看 mods/neoforge、mods/fabric 这类路径前缀；
    /// 否则根据元数据文件类型判断，同时存在 toml 与 fabric.mod.json 时视为多加载器。
    /// </summary>
    private static string InferLoader(string root, string modDirectory, bool hasToml, bool hasFabric)
    {
        var relative = Path.GetRelativePath(root, modDirectory).Replace('\\', '/');
        if (relative.StartsWith("mods/neoforge/", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("/neoforge/", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("-neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return "neoforge";
        }

        if (relative.StartsWith("mods/fabric/", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("/fabric/", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("-fabric", StringComparison.OrdinalIgnoreCase))
        {
            return "fabric";
        }

        if (hasToml)
        {
            return "neoforge";
        }

        return hasFabric ? "fabric" : "unknown";
    }

    private static string ResolveModDirectory(string root, string metadataPath)
    {
        var fullPath = Path.GetFullPath(metadataPath);
        var normalized = fullPath.Replace('\\', '/');
        var srcIndex = normalized.IndexOf("/src/", StringComparison.OrdinalIgnoreCase);
        if (srcIndex >= 0)
        {
            return fullPath[..srcIndex].Replace('/', Path.DirectorySeparatorChar);
        }

        // mods/neoforge/<版本>/META-INF/neoforge.mods.toml → mods/neoforge/<版本>
        return Path.GetDirectoryName(Path.GetDirectoryName(fullPath)) ?? Path.GetDirectoryName(fullPath) ?? root;
    }

    private static string ResolveResourcesDirectory(string metadataPath, string modDirectory)
    {
        var normalized = Path.GetFullPath(metadataPath).Replace('\\', '/');
        var resourcesIndex = normalized.IndexOf("/src/main/resources/", StringComparison.OrdinalIgnoreCase);
        if (resourcesIndex >= 0)
        {
            return normalized[..(resourcesIndex + "/src/main/resources/".Length)]
                .Replace('/', Path.DirectorySeparatorChar);
        }

        return modDirectory;
    }

    private static void FindMixinFile(string resourcesDirectory, string configName, ModEntry entry)
    {
        var candidate = Path.Combine(resourcesDirectory, configName);
        if (File.Exists(candidate))
        {
            entry.Mixins.Add(configName);
        }
    }

    private static void AddDependency(ModEntry entry, TomlDependency dependency)
    {
        if (string.IsNullOrWhiteSpace(dependency.ModId) ||
            string.Equals(dependency.ModId, entry.ModId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (entry.Dependencies.Any(item => string.Equals(item.ModId, dependency.ModId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        entry.Dependencies.Add(dependency);
    }

    private static TomlDocument ParseToml(string path)
    {
        var document = new TomlDocument();
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        TomlMod? currentMod = null;
        TomlDependency? currentDependency = null;
        var section = string.Empty;
        var dependencyOwner = string.Empty;

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                var end = line.IndexOf("]]", StringComparison.Ordinal);
                var name = end > 2 ? line[2..end].Trim() : string.Empty;
                currentDependency = null;
                if (name == "mods")
                {
                    currentMod = new TomlMod();
                    document.Mods.Add(currentMod);
                    section = "mods";
                }
                else if (name.StartsWith("dependencies.", StringComparison.OrdinalIgnoreCase))
                {
                    section = "dependencies";
                    dependencyOwner = name["dependencies.".Length..].Trim();
                    currentDependency = new TomlDependency();
                    document.Dependencies.Add(currentDependency);
                }
                else
                {
                    section = name.StartsWith("mixins", StringComparison.OrdinalIgnoreCase) ? "mixins" : "other";
                }

                continue;
            }

            var match = KeyValuePattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups[1].Value.Trim();
            var value = match.Groups[2].Value.Trim();
            if (value.StartsWith("'''", StringComparison.Ordinal))
            {
                var builder = new StringBuilder(value[3..]);
                var closed = value[3..].Contains("'''", StringComparison.Ordinal);
                while (!closed && index + 1 < lines.Length)
                {
                    index++;
                    var next = lines[index];
                    var closeIndex = next.IndexOf("'''", StringComparison.Ordinal);
                    if (closeIndex >= 0)
                    {
                        builder.Append('\n').Append(next[..closeIndex]);
                        closed = true;
                    }
                    else
                    {
                        builder.Append('\n').Append(next);
                    }
                }

                value = builder.ToString().Trim();
            }
            else if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
            {
                value = value[1..^1];
            }
            else if (value.StartsWith('\'') && value.EndsWith('\'') && value.Length >= 2)
            {
                value = value[1..^1];
            }

            switch (section)
            {
                case "mods" when currentMod is not null:
                    switch (key)
                    {
                        case "modId":
                            currentMod.ModId = value;
                            break;
                        case "displayName":
                            currentMod.Name = value;
                            break;
                        case "version":
                            currentMod.Version = value;
                            break;
                        case "description":
                            currentMod.Description = value;
                            break;
                    }

                    break;

                case "dependencies" when currentDependency is not null && !string.IsNullOrEmpty(dependencyOwner):
                    currentDependency.Owner = dependencyOwner;
                    switch (key)
                    {
                        case "modId":
                            currentDependency.ModId = value;
                            break;
                        case "mandatory":
                            currentDependency.Mandatory = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "versionRange":
                            currentDependency.VersionRange = value;
                            break;
                        case "side":
                            currentDependency.Side = value;
                            break;
                    }

                    break;

                case "mixins":
                    if (key == "config")
                    {
                        document.MixinConfigs.Add(value);
                    }

                    break;
            }
        }

        return document;
    }

    private static FabricDocument? ParseFabricMetadata(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            var modId = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(modId))
            {
                return null;
            }

            var result = new FabricDocument
            {
                ModId = modId,
                Name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null,
                Version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null,
                Description = root.TryGetProperty("description", out var descriptionElement)
                    ? descriptionElement.GetString()
                    : null
            };

            if (root.TryGetProperty("mixins", out var mixinsElement) && mixinsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in mixinsElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        result.MixinConfigs.Add(item.GetString()!);
                    }
                    else if (item.ValueKind == JsonValueKind.Object &&
                             item.TryGetProperty("config", out var configElement))
                    {
                        var config = configElement.GetString();
                        if (config is not null)
                        {
                            result.MixinConfigs.Add(config);
                        }
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mc-mcp] 解析 fabric.mod.json 失败 {path}: {ex.Message}");
            return null;
        }
    }

    private static (int Mods, int Dependencies) Save(
        IndexDatabase database,
        IEnumerable<ModEntry> entries,
        string modsRoot)
    {
        var materialized = entries.ToList();
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM mod_metadata; DELETE FROM mod_dependencies;";
            clear.ExecuteNonQuery();
        }

        using var metadataCommand = connection.CreateCommand();
        metadataCommand.Transaction = transaction;
        metadataCommand.CommandText =
            """
            INSERT INTO mod_metadata (mod_id, name, version, loader, mod_dir, toml_path, mixin_configs, description, indexed_at)
            VALUES ($modId, $name, $version, $loader, $modDir, $tomlPath, $mixins, $description, $indexedAt);
            """;
        using var dependencyCommand = connection.CreateCommand();
        dependencyCommand.Transaction = transaction;
        dependencyCommand.CommandText =
            """
            INSERT OR IGNORE INTO mod_dependencies (mod_id, dep_mod_id, mandatory, version_range, side)
            VALUES ($modId, $depModId, $mandatory, $versionRange, $side);
            """;

        var now = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var dependencyCount = 0;
        foreach (var entry in materialized)
        {
            metadataCommand.Parameters.Clear();
            metadataCommand.Parameters.AddWithValue("$modId", entry.ModId);
            metadataCommand.Parameters.AddWithValue("$name", entry.Name ?? (object)DBNull.Value);
            metadataCommand.Parameters.AddWithValue("$version", entry.Version ?? (object)DBNull.Value);
            metadataCommand.Parameters.AddWithValue("$loader", entry.LoaderText);
            metadataCommand.Parameters.AddWithValue("$modDir", entry.ModDir);
            metadataCommand.Parameters.AddWithValue("$tomlPath", entry.TomlPath ?? (object)DBNull.Value);
            metadataCommand.Parameters.AddWithValue("$mixins", JsonSerializer.Serialize(entry.Mixins));
            metadataCommand.Parameters.AddWithValue("$description", entry.Description ?? (object)DBNull.Value);
            metadataCommand.Parameters.AddWithValue("$indexedAt", now);
            metadataCommand.ExecuteNonQuery();

            foreach (var dependency in entry.Dependencies)
            {
                dependencyCommand.Parameters.Clear();
                dependencyCommand.Parameters.AddWithValue("$modId", entry.ModId);
                dependencyCommand.Parameters.AddWithValue("$depModId", dependency.ModId);
                dependencyCommand.Parameters.AddWithValue("$mandatory", dependency.Mandatory ? 1 : 0);
                dependencyCommand.Parameters.AddWithValue("$versionRange", dependency.VersionRange ?? (object)DBNull.Value);
                dependencyCommand.Parameters.AddWithValue("$side", dependency.Side ?? (object)DBNull.Value);
                dependencyCommand.ExecuteNonQuery();
                dependencyCount++;
            }
        }

        transaction.Commit();
        return (materialized.Count, dependencyCount);
    }

    private sealed class ModEntry
    {
        public ModEntry(string modId, string modDir)
        {
            ModId = modId;
            ModDir = modDir;
        }

        public string ModId { get; }

        public string ModDir { get; }

        public string? Name { get; set; }

        public string? Version { get; set; }

        public string? Description { get; set; }

        public string? TomlPath { get; set; }

        public HashSet<string> Loaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SortedSet<string> Mixins { get; } = new(StringComparer.Ordinal);

        public List<TomlDependency> Dependencies { get; } = new();

        public string LoaderText => Loaders.Count == 0 ? "unknown" : string.Join(",", Loaders.OrderBy(item => item));
    }

    private sealed class TomlMod
    {
        public string ModId { get; set; } = string.Empty;

        public string? Name { get; set; }

        public string? Version { get; set; }

        public string? Description { get; set; }
    }

    private sealed class TomlDependency
    {
        public string Owner { get; set; } = string.Empty;

        public string ModId { get; set; } = string.Empty;

        public bool Mandatory { get; set; } = true;

        public string? VersionRange { get; set; }

        public string? Side { get; set; }
    }

    private sealed class TomlDocument
    {
        public List<TomlMod> Mods { get; } = new();

        public List<TomlDependency> Dependencies { get; } = new();

        public List<string> MixinConfigs { get; } = new();
    }

    private sealed class FabricDocument
    {
        public string ModId { get; set; } = string.Empty;

        public string? Name { get; set; }

        public string? Version { get; set; }

        public string? Description { get; set; }

        public List<string> MixinConfigs { get; } = new();
    }
}
