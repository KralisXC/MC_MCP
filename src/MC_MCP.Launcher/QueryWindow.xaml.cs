using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MC_MCP.Indexer;

namespace MC_MCP.Launcher;

public enum QueryTool
{
    Symbol,
    Code,
    Snippet,
    Mixin,
    MixinConflict,
    Semantic,
    Mod
}

/// <summary>
/// 独立查询窗口：主界面只保留“数据源树 + 构建控制 + 实时日志”，
/// 符号搜索 / 代码搜索 / Mixin 注入 / 语义搜索 / Mod 信息 各自弹出这个窗口。
/// </summary>
public partial class QueryWindow : Window
{
    private readonly QueryTool _tool;
    private readonly SymbolQueryService _queries;
    private readonly IndexDatabase _database;
    private readonly string _indexRoot;
    private readonly Action<string>? _log;
    private int _lastOffset;
    private int _lastCount;

    /// <summary>与 MCP 工具一致的 50KB 单次返回上限（UTF-8 字节数）。</summary>
    private const int MaxPayloadBytes = 50 * 1024;

    public QueryWindow(
        QueryTool tool,
        SymbolQueryService queries,
        IndexDatabase database,
        string indexRoot,
        Action<string>? log = null)
    {
        InitializeComponent();
        _tool = tool;
        _queries = queries;
        _database = database;
        _indexRoot = indexRoot;
        _log = log;

        ConfigureTool();
        Loaded += async (_, _) => await RunQueryAsync();
    }

    private void ConfigureTool()
    {
        switch (_tool)
        {
            case QueryTool.Symbol:
                Title = "符号搜索";
                TitleText.Text = "符号搜索（search_symbol）";
                HintText.Text = "输入方法名或 FQN 片段，例如 tick";
                QueryBox.Text = "tick";
                AddColumn("Kind", 0, 90);
                AddColumn("FQN", 1, new DataGridLength(1, DataGridLengthUnitType.Star));
                AddColumn("Origin", 2, 120);
                AddColumn("Signature", 3, 320);
                AddColumn("Comment", 4, 300);
                break;

            case QueryTool.Code:
                Title = "代码搜索";
                TitleText.Text = "代码全文搜索（search_code / FTS5）";
                HintText.Text = "输入代码关键字，例如 player.isCreative()";
                QueryBox.Text = "player.isCreative()";
                AddColumn("File", 0, 340);
                AddColumn("Start", 1, 60);
                AddColumn("End", 2, 60);
                AddColumn("Snippet", 3, new DataGridLength(1, DataGridLengthUnitType.Star));
                break;

            case QueryTool.Mixin:
                Title = "Mixin 注入";
                TitleText.Text = "Mixin 注入查询（search_mixin）";
                HintText.Text = "输入目标类名，例如 net.minecraft.world.entity.player.Player";
                QueryBox.Text = "MappedRegistry";
                AddColumn("MixinClass", 0, 340);
                AddColumn("Annotation", 1, 110);
                AddColumn("TargetMethod", 2, 150);
                AddColumn("AtValue", 3, 100);
                AddColumn("Arguments", 4, new DataGridLength(1, DataGridLengthUnitType.Star));
                break;

            case QueryTool.Snippet:
                Title = "代码片段";
                TitleText.Text = "代码片段提取（get_code_snippet）";
                HintText.Text = "输入类或方法 FQN（方法形如 pkg.Class#method(...)），默认最多 80 行";
                QueryBox.Text =
                    "net.minecraft.world.level.block.entity.HopperBlockEntity#pushItemsTick(Level, BlockPos, BlockState, HopperBlockEntity)";
                AddColumn("Line", 0, 60);
                AddColumn("Code", 1, new DataGridLength(1, DataGridLengthUnitType.Star));
                break;

            case QueryTool.MixinConflict:
                Title = "Mixin 冲突检测";
                TitleText.Text = "Mixin 冲突检测（check_mixin_conflict）";
                HintText.Text = "输入目标类 FQN；可用 类FQN::方法名 只查某个方法";
                QueryBox.Text = "net.minecraft.client.model.geom.ModelPart";
                AddColumn("MixinClass", 0, 320);
                AddColumn("Mod", 1, 140);
                AddColumn("Annotation", 2, 100);
                AddColumn("TargetMethod", 3, 170);
                AddColumn("AtValue", 4, 90);
                AddColumn("Line", 5, 60);
                break;

            case QueryTool.Semantic:
                Title = "语义搜索";
                TitleText.Text = "语义搜索（semantic_search / e5-base-v2）";
                HintText.Text = "输入自然语言，例如 player model animation";
                QueryBox.Text = "player model animation";
                TopKLabel.Visibility = Visibility.Visible;
                TopKBox.Visibility = Visibility.Visible;
                AddColumn("Score", 0, 70);
                AddColumn("Kind", 1, 90);
                AddColumn("Origin", 2, 110);
                AddColumn("FQN", 3, new DataGridLength(1, DataGridLengthUnitType.Star));
                AddColumn("File", 4, 320);
                AddColumn("Line", 5, 60);
                AddColumn("Comment", 6, 300);
                break;

            case QueryTool.Mod:
                Title = "Mod 信息";
                TitleText.Text = "Mod 元数据（get_mod_metadata）";
                HintText.Text = "输入 mod_id / 名称 / 目录片段；留空列出全部";
                QueryBox.Text = string.Empty;
                AddColumn("ModId", 0, 140);
                AddColumn("Name", 1, 170);
                AddColumn("Version", 2, 90);
                AddColumn("Loader", 3, 90);
                AddColumn("依赖", 4, 220);
                AddColumn("被依赖", 5, 140);
                AddColumn("Mixin 配置", 6, 180);
                AddColumn("Mod 目录", 7, new DataGridLength(1, DataGridLengthUnitType.Star));
                break;
        }
    }

    private void AddColumn(string header, int index, double width)
        => AddColumn(header, index, new DataGridLength(width));

    private void AddColumn(string header, int index, DataGridLength width)
    {
        ResultGrid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding($"Values[{index}]"),
            Width = width
        });
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e) => await RunQueryAsync();

    private async void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        // 与 MCP 返回的 warning 一致：下一页 offset = 当前 offset + 本页返回条数。
        OffsetBox.Text = (_lastOffset + _lastCount).ToString();
        await RunQueryAsync();
    }

    private async void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await RunQueryAsync();
        }
    }

    private int ReadOffset()
        => int.TryParse(OffsetBox.Text.Trim(), out var parsed) ? Math.Max(0, parsed) : 0;

    private async Task RunQueryAsync()
    {
        var query = QueryBox.Text.Trim();
        var offset = ReadOffset();
        StatusText.Text = "查询中……";

        try
        {
            switch (_tool)
            {
                case QueryTool.Symbol:
                {
                    if (query.Length == 0)
                    {
                        StatusText.Text = "请输入关键字后再搜索。";
                        return;
                    }

                    var results = _queries.SearchSymbols(query, 20, offset);
                    var rows = results.Select(symbol =>
                    {
                        var location = ResolveSymbolLocation(symbol.Fqn);
                        return new QueryRow(
                            new[]
                            {
                                symbol.Kind,
                                symbol.Fqn,
                                string.Join(" / ", symbol.Origins),
                                symbol.Signature ?? "—",
                                symbol.Comment ?? "—"
                            },
                            location.FilePath,
                            location.Line);
                    }).ToList();
                    Show(rows, offset, $"search_symbol(\"{query}\", offset={offset}) 命中 {rows.Count} 条");
                    break;
                }

                case QueryTool.Code:
                {
                    if (query.Length == 0)
                    {
                        StatusText.Text = "请输入代码关键字后再搜索。";
                        return;
                    }

                    var results = _queries.SearchCode(query, 20, offset);
                    var rows = results
                        .Select(chunk => new QueryRow(
                            new[]
                            {
                                chunk.FilePath,
                                chunk.StartLine.ToString(),
                                chunk.EndLine.ToString(),
                                chunk.Snippet.Replace('\n', ' ')
                            },
                            chunk.FilePath,
                            chunk.StartLine))
                        .ToList();
                    Show(rows, offset, $"search_code(\"{query}\", offset={offset}) 命中 {rows.Count} 条");
                    break;
                }

                case QueryTool.Snippet:
                {
                    if (query.Length == 0)
                    {
                        StatusText.Text = "请输入类或方法 FQN 后再提取。";
                        return;
                    }

                    var snippet = _queries.GetCodeSnippet(query, 80);
                    if (snippet is null)
                    {
                        ResultGrid.ItemsSource = null;
                        StatusText.Text = $"未找到符号：{query}";
                        return;
                    }

                    if (!snippet.FileExists)
                    {
                        ResultGrid.ItemsSource = null;
                        StatusText.Text = $"源码文件不存在：{snippet.FilePath}（请确认索引库与 code/、mods/ 同属一个工程根）";
                        return;
                    }

                    const string marker = "// ... (代码已截断)";
                    var rows = snippet.Code
                        .Split('\n')
                        .Select((line, index) => new QueryRow(
                            new[]
                            {
                                line == marker ? "…" : (snippet.StartLine + index).ToString(),
                                line
                            },
                            snippet.FilePath,
                            line == marker ? snippet.EndLine : snippet.StartLine + index))
                        .ToList();
                    var status = $"get_code_snippet(\"{query}\") → {snippet.FilePath}:{snippet.StartLine}-{snippet.EndLine}，" +
                                 $"返回 {snippet.ReturnedLines}/{snippet.TotalLines} 行";
                    if (snippet.Truncated)
                    {
                        status += "（已截断，可减小 max_lines 或改用方法级 FQN）";
                    }

                    Show(rows, offset, status);
                    break;
                }

                case QueryTool.Mixin:
                {
                    if (query.Length == 0)
                    {
                        StatusText.Text = "请输入目标类名后再查询。";
                        return;
                    }

                    var results = _queries.SearchMixins(query, 100, offset);
                    var rows = results
                        .Select(item => new QueryRow(
                            new[]
                            {
                                item.MixinClass,
                                item.Annotation,
                                item.TargetMethod ?? "—",
                                item.AtValue ?? "—",
                                item.Arguments ?? "—"
                            }))
                        .ToList();
                    Show(rows, offset, $"search_mixin(\"{query}\", offset={offset}) 命中 {rows.Count} 条");
                    break;
                }

                case QueryTool.MixinConflict:
                {
                    if (query.Length == 0)
                    {
                        StatusText.Text = "请输入目标类 FQN 后再检测。";
                        return;
                    }

                    // 支持“类FQN::方法名”只查某个方法。
                    var parts = query.Split("::", 2, StringSplitOptions.TrimEntries);
                    var targetClass = parts[0];
                    var targetMethod = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
                    var conflict = _queries.CheckMixinConflict(targetClass, targetMethod, 100, offset);
                    var rows = conflict.Groups
                        .SelectMany(group => group.Injections.Select(point => new QueryRow(
                            new[]
                            {
                                group.MixinClass,
                                group.ModId ?? "—",
                                point.Annotation,
                                point.TargetMethod ?? "—",
                                point.AtValue ?? "—",
                                point.Line.ToString()
                            })))
                        .ToList();
                    var status = $"check_mixin_conflict(\"{targetClass}\"" +
                                 (targetMethod is null ? string.Empty : $", method={targetMethod}") +
                                 $") 命中 {conflict.InjectionCount} 个注入点 / " +
                                 $"{conflict.MixinCount} 个 Mixin / {conflict.ModCount} 个 Mod";
                    Show(rows, offset, status);
                    var extra = conflict.Warning ?? conflict.Note;
                    if (!string.IsNullOrEmpty(extra))
                    {
                        StatusText.Text += $"｜{extra}";
                    }

                    break;
                }

                case QueryTool.Semantic:
                {
                    if (query.Length == 0)
                    {
                        StatusText.Text = "请输入自然语言查询。";
                        return;
                    }

                    var topK = int.TryParse(TopKBox.Text.Trim(), out var parsed) ? Math.Clamp(parsed, 1, 50) : 10;
                    StatusText.Text = "语义检索中，首次加载 ONNX 模型约需数秒……";
                    // 走 SymbolQueryService：full 模式下自动切换为内存向量，lazy 模式分批扫库。
                    var results = await Task.Run(() => _queries.SemanticSearch(query, topK, offset));
                    var rows = results
                        .Select(match => new QueryRow(
                            new[]
                            {
                                match.Score.ToString("F4"),
                                match.Kind,
                                match.Origin,
                                match.Fqn,
                                match.FilePath,
                                match.StartLine.ToString(),
                                match.Comment ?? "—"
                            },
                            match.FilePath,
                            match.StartLine))
                        .ToList();
                    Show(rows, offset, rows.Count == 0
                        ? $"semantic_search(\"{query}\", offset={offset}) 无结果：请先在主界面执行“全局增量向量”。"
                        : $"semantic_search(\"{query}\", top_k={topK}, offset={offset}) 命中 {rows.Count} 条");
                    break;
                }

                case QueryTool.Mod:
                {
                    var results = _queries.GetModMetadata(query, 100, offset);
                    var rows = results
                        .Select(mod => new QueryRow(
                            new[]
                            {
                                mod.ModId,
                                mod.Name ?? "—",
                                mod.Version ?? "—",
                                mod.Loader,
                                FormatDependencies(mod),
                                mod.Dependents.Count == 0 ? "—" : string.Join(", ", mod.Dependents),
                                mod.MixinConfigs.Count == 0
                                    ? "—"
                                    : string.Join(", ", mod.MixinConfigs.Select(Path.GetFileName)),
                                mod.ModDir
                            }))
                        .ToList();
                    Show(rows, offset, query.Length == 0
                        ? $"get_mod_metadata(offset={offset}) 列出 {rows.Count} 个 Mod"
                        : $"get_mod_metadata(\"{query}\", offset={offset}) 命中 {rows.Count} 个 Mod");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            ResultGrid.ItemsSource = null;
            StatusText.Text = $"查询失败：{ex.Message}";
            _log?.Invoke($"[{Title}] 查询失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 与 McpTools 相同的体积控制：序列化超过 50KB 时只保留前面的行，
    /// 并在状态栏给出下一页 offset，保证查询窗口与 MCP 工具行为一致。
    /// </summary>
    private void Show(IReadOnlyList<QueryRow> rows, int offset, string status)
    {
        _lastOffset = offset;
        var visible = ApplyBudget(rows, out var truncated);
        _lastCount = visible.Count;
        ResultGrid.ItemsSource = visible;

        var message = visible.Count == 0
            ? offset > 0 ? $"offset={offset} 处已无更多结果，可减小 offset 返回上一页。" : "无结果。"
            : $"{status}（双击结果行跳转源码）";
        if (truncated)
        {
            message += $"｜结果超过 50KB 已截断，下一页请设置 offset={offset + visible.Count}";
        }

        StatusText.Text = message;
    }

    private static IReadOnlyList<QueryRow> ApplyBudget(IReadOnlyList<QueryRow> rows, out bool truncated)
    {
        truncated = false;
        if (rows.Count == 0)
        {
            return rows;
        }

        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(rows)) <= MaxPayloadBytes)
        {
            return rows;
        }

        var kept = rows.Count - 1;
        while (kept > 0 &&
               Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(rows.Take(kept))) > MaxPayloadBytes)
        {
            kept--;
        }

        truncated = true;
        return rows.Take(kept).ToList();
    }

    private static string FormatDependencies(ModMetadataResult mod)
        => mod.Dependencies.Count == 0
            ? "—"
            : string.Join(", ", mod.Dependencies.Select(dep => dep.Mandatory ? dep.ModId : $"{dep.ModId}(可选)"));

    /// <summary>符号结果没有直接行号，回溯 get_symbol_source；同名时优先 vanilla。</summary>
    private (string? FilePath, int Line) ResolveSymbolLocation(string fqn)
    {
        try
        {
            var source = _queries.GetSymbolSource(fqn);
            if (source is null)
            {
                return (null, 1);
            }

            var preferred = source.Sources
                .OrderBy(entry => entry.Origin.Equals("vanilla", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            return preferred is not null && !string.IsNullOrEmpty(preferred.FilePath)
                ? (preferred.FilePath, preferred.StartLine)
                : (source.FilePath, source.StartLine);
        }
        catch
        {
            return (null, 1);
        }
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (source is null || ItemsControl.ContainerFromElement(ResultGrid, source) is not DataGridRow)
        {
            return;
        }

        if (ResultGrid.SelectedItem is not QueryRow row || row.FilePath is not { Length: > 0 } filePath)
        {
            return;
        }

        SourceNavigator.Open(_indexRoot, filePath, row.Line, message => _log?.Invoke(message));
        e.Handled = true;
    }

    private sealed record QueryRow(string[] Values, string? FilePath = null, int Line = 1);
}
