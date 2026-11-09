using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using MC_MCP.Indexer;

namespace MC_MCP.Launcher;

public enum DataSourceKind
{
    Group,
    Vanilla,
    Patch,
    Mod
}

/// <summary>
/// 数据源树节点。支持三态勾选（父节点全选/半选/全不选）、统计徽标与过滤显隐。
/// RelativePath 为空表示纯分组（如 Vanilla / Mods），非空表示可参与构建的作用域前缀。
/// </summary>
public sealed class DataSourceNode : INotifyPropertyChanged
{
    private static readonly SolidColorBrush VanillaBrush = CreateBrush(0x3B, 0x82, 0xF6);
    private static readonly SolidColorBrush PatchBrush = CreateBrush(0x8B, 0x5C, 0xF6);
    private static readonly SolidColorBrush ModBrush = CreateBrush(0x10, 0xB9, 0x81);
    private static readonly SolidColorBrush GroupBrush = CreateBrush(0x6B, 0x72, 0x80);
    private static readonly SolidColorBrush WarnBackground = CreateBrush(0xFE, 0xF3, 0xC7);
    private static readonly SolidColorBrush WarnForeground = CreateBrush(0xB4, 0x53, 0x09);
    private static readonly SolidColorBrush ErrorBackground = CreateBrush(0xFE, 0xE2, 0xE2);
    private static readonly SolidColorBrush ErrorForeground = CreateBrush(0xB9, 0x1C, 0x1C);

    private bool? _isChecked = false;
    private bool _isExpanded = true;
    private Visibility _visibility = Visibility.Visible;
    private bool _updating;

    public DataSourceNode(string title, string? relativePath, DataSourceKind kind, DataSourceNode? parent = null)
    {
        Title = title;
        RelativePath = relativePath;
        Kind = kind;
        Parent = parent;
        Children = new ObservableCollection<DataSourceNode>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }

    public string? RelativePath { get; }

    public DataSourceKind Kind { get; }

    public DataSourceNode? Parent { get; }

    public ObservableCollection<DataSourceNode> Children { get; }

    public bool IsScope => RelativePath is not null;

    public int FileCount { get; set; }

    public int ChangedCount { get; set; }

    public int ErrorCount { get; set; }

    public string ToolTipText => RelativePath ?? Title;

    public FontWeight TitleWeight => Kind == DataSourceKind.Mod ? FontWeights.Normal : FontWeights.SemiBold;

    public Brush IconBrush => Kind switch
    {
        DataSourceKind.Vanilla => VanillaBrush,
        DataSourceKind.Patch => PatchBrush,
        DataSourceKind.Mod => ModBrush,
        _ => GroupBrush
    };

    public string Badge => ErrorCount > 0
        ? $"{ErrorCount:N0} 报错"
        : ChangedCount > 0
            ? $"{ChangedCount:N0} 变更"
            : string.Empty;

    public Visibility BadgeVisibility => Badge.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Brush BadgeBackground => ErrorCount > 0 ? ErrorBackground : WarnBackground;

    public Brush BadgeForeground => ErrorCount > 0 ? ErrorForeground : WarnForeground;

    public string Detail => IsScope ? $"{FileCount:N0} 文件" : $"{Children.Count} 组";

    /// <summary>三态勾选：父节点变化同步子节点，子节点变化反向聚合父节点。</summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            OnPropertyChanged();

            if (_updating || value is null)
            {
                return;
            }

            _updating = true;
            foreach (var child in Children)
            {
                child.IsChecked = value;
            }

            _updating = false;
            Parent?.RefreshFromChildren();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    /// <summary>过滤后的行可见性；绑定到 TreeViewItem.Visibility。</summary>
    public Visibility Visibility
    {
        get => _visibility;
        set
        {
            if (_visibility == value)
            {
                return;
            }

            _visibility = value;
            OnPropertyChanged();
        }
    }

    public void AddChild(DataSourceNode child) => Children.Add(child);

    /// <summary>收集勾选的作用域；父节点全选时直接使用父作用域，避免重复。</summary>
    public void CollectScopes(ICollection<string> scopes)
    {
        if (IsChecked == false)
        {
            return;
        }

        if (IsScope && IsChecked == true)
        {
            scopes.Add(RelativePath!);
            return;
        }

        foreach (var child in Children)
        {
            child.CollectScopes(scopes);
        }
    }

    public void SetExpandedRecursive(bool expanded)
    {
        IsExpanded = expanded;
        foreach (var child in Children)
        {
            child.SetExpandedRecursive(expanded);
        }
    }

    public void RefreshComputed()
    {
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(BadgeVisibility));
        OnPropertyChanged(nameof(BadgeBackground));
        OnPropertyChanged(nameof(BadgeForeground));
        OnPropertyChanged(nameof(Detail));
    }

    private void RefreshFromChildren()
    {
        if (_updating || Children.Count == 0)
        {
            return;
        }

        var checkedCount = Children.Count(child => child.IsChecked == true);
        var uncheckedCount = Children.Count(child => child.IsChecked == false);
        bool? state = checkedCount == Children.Count
            ? true
            : uncheckedCount == Children.Count
                ? false
                : null;

        if (_isChecked == state)
        {
            return;
        }

        _updating = true;
        _isChecked = state;
        _updating = false;
        OnPropertyChanged(nameof(IsChecked));

        // 半选状态必须继续向上聚合，否则勾选单个 Mod 时根节点仍是 false，CollectScopes 会提前返回。
        Parent?.RefreshFromChildren();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>供无障碍/UI 自动化读取，默认的 ToString 只会输出类型名。</summary>
    public override string ToString() => Title;

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 扫描磁盘上的 code/ 与 mods/ 目录，生成“Vanilla / NeoForge 补丁层 / Mods”三级树：
/// 一级为数据层，二级为加载器（Multi-Loader / NeoForge / Fabric …），三级为具体 Mod。
/// </summary>
public static class DataSourceTreeBuilder
{
    private static readonly Regex VersionPattern = new(@"^\d+(\.\d+)+$", RegexOptions.Compiled);

    private static readonly HashSet<string> PackageRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "net", "com", "org", "io", "dev", "me", "it", "java", "javax",
        "assets", "data", "META-INF", "reports", "META-INF-versions"
    };

    public static IReadOnlyList<DataSourceNode> Build(
        string rootDirectory,
        IReadOnlyDictionary<string, int>? errorCounts = null,
        ISet<string>? changedPaths = null)
    {
        var root = Path.GetFullPath(rootDirectory);
        var files = JavaIndexer.EnumerateJavaFiles(root);
        var roots = new List<DataSourceNode>();

        roots.AddRange(BuildVanilla(root, files));
        roots.AddRange(BuildPatchLayer(root, files));
        roots.AddRange(BuildMods(root, files));

        ApplyCounts(roots, files, errorCounts, changedPaths);
        return roots;
    }

    private static IEnumerable<DataSourceNode> BuildVanilla(string root, List<JavaIndexer.JavaFileInfo> files)
    {
        var vanillaRoot = Path.Combine(root, "code", "vanilla");
        if (!Directory.Exists(vanillaRoot))
        {
            yield break;
        }

        var node = new DataSourceNode("Vanilla（原版）", null, DataSourceKind.Vanilla);
        foreach (var versionDirectory in EnumerateDirectories(vanillaRoot))
        {
            var relative = ToRelative(root, versionDirectory);
            if (!ContainsJava(files, relative))
            {
                continue;
            }

            node.AddChild(new DataSourceNode(Path.GetFileName(versionDirectory), relative, DataSourceKind.Vanilla, node));
        }

        if (node.Children.Count > 0)
        {
            yield return node;
        }
    }

    private static IEnumerable<DataSourceNode> BuildPatchLayer(string root, List<JavaIndexer.JavaFileInfo> files)
    {
        var patchRoot = Path.Combine(root, "mods", "neoforge");
        if (!Directory.Exists(patchRoot))
        {
            yield break;
        }

        var node = new DataSourceNode("NeoForge（补丁层）", null, DataSourceKind.Patch);
        foreach (var versionDirectory in EnumerateDirectories(patchRoot))
        {
            var relative = ToRelative(root, versionDirectory);
            if (!ContainsJava(files, relative))
            {
                continue;
            }

            node.AddChild(new DataSourceNode(Path.GetFileName(versionDirectory), relative, DataSourceKind.Patch, node));
        }

        if (node.Children.Count > 0)
        {
            yield return node;
        }
    }

    private static IEnumerable<DataSourceNode> BuildMods(string root, List<JavaIndexer.JavaFileInfo> files)
    {
        var modsRoot = Path.Combine(root, "mods");
        if (!Directory.Exists(modsRoot))
        {
            yield break;
        }

        var node = new DataSourceNode("Mods（第三方 Mod）", null, DataSourceKind.Group);
        foreach (var directory in EnumerateDirectories(modsRoot))
        {
            var name = Path.GetFileName(directory);
            if (name.Equals("neoforge", StringComparison.OrdinalIgnoreCase))
            {
                // NeoForge 已提升为一级“补丁层”，避免重复展示。
                continue;
            }

            if (VersionPattern.IsMatch(name))
            {
                AddLoaderGroup(node, "Multi-Loader", directory, root, files);
                continue;
            }

            foreach (var versionDirectory in EnumerateDirectories(directory))
            {
                AddLoaderGroup(node, PrettyLoaderName(name), versionDirectory, root, files);
            }
        }

        if (node.Children.Count > 0)
        {
            yield return node;
        }
    }

    /// <summary>加载器分组：版本目录下每个包含 .java 的子目录视为一个 Mod 节点。</summary>
    private static void AddLoaderGroup(
        DataSourceNode parent,
        string loaderName,
        string versionDirectory,
        string root,
        List<JavaIndexer.JavaFileInfo> files)
    {
        var relative = ToRelative(root, versionDirectory);
        if (!ContainsJava(files, relative))
        {
            return;
        }

        var title = $"{loaderName} · {Path.GetFileName(versionDirectory)}";
        var group = new DataSourceNode(title, null, DataSourceKind.Group, parent);
        foreach (var modDirectory in EnumerateDirectories(versionDirectory))
        {
            var name = Path.GetFileName(modDirectory);
            if (PackageRootNames.Contains(name))
            {
                continue;
            }

            var modRelative = ToRelative(root, modDirectory);
            if (!ContainsJava(files, modRelative))
            {
                continue;
            }

            group.AddChild(new DataSourceNode(name, modRelative, DataSourceKind.Mod, group));
        }

        if (group.Children.Count == 0)
        {
            // 版本目录本身就是补丁层/单模块（例如 mods/fabric/26.1.2 没有独立 Mod 子目录）。
            parent.AddChild(new DataSourceNode(title, relative, DataSourceKind.Mod, parent));
            return;
        }

        parent.AddChild(group);
    }

    private static void ApplyCounts(
        IReadOnlyList<DataSourceNode> roots,
        List<JavaIndexer.JavaFileInfo> files,
        IReadOnlyDictionary<string, int>? errorCounts,
        ISet<string>? changedPaths)
    {
        var nodes = new List<DataSourceNode>();
        Flatten(roots, nodes);
        var scopedNodes = nodes.Where(node => node.IsScope).ToList();

        foreach (var file in files)
        {
            foreach (var node in scopedNodes)
            {
                if (!IsUnder(file.RelativePath, node.RelativePath!))
                {
                    continue;
                }

                node.FileCount++;
                if (changedPaths is not null && changedPaths.Contains(file.RelativePath))
                {
                    node.ChangedCount++;
                }

                if (errorCounts is not null && errorCounts.TryGetValue(file.RelativePath, out var errors))
                {
                    node.ErrorCount += errors;
                }
            }
        }

        foreach (var node in nodes)
        {
            node.RefreshComputed();
        }
    }

    private static void Flatten(IEnumerable<DataSourceNode> nodes, List<DataSourceNode> result)
    {
        foreach (var node in nodes)
        {
            result.Add(node);
            Flatten(node.Children, result);
        }
    }

    private static bool IsUnder(string relativePath, string scope)
        => relativePath.Equals(scope, StringComparison.Ordinal) ||
           relativePath.StartsWith(scope + "/", StringComparison.Ordinal);

    private static bool ContainsJava(List<JavaIndexer.JavaFileInfo> files, string relativeDirectory)
        => files.Any(file => IsUnder(file.RelativePath, relativeDirectory));

    private static IEnumerable<string> EnumerateDirectories(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            : Array.Empty<string>();

    private static string ToRelative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string PrettyLoaderName(string name) => name switch
    {
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "forge" => "Forge",
        _ => char.ToUpperInvariant(name[0]) + name[1..]
    };
}
