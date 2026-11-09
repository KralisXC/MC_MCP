using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MC_MCP.Core;
using MC_MCP.Indexer;

namespace MC_MCP.Launcher;

/// <summary>
/// Phase 9 控制台：左侧海量 Mod 树形数据源，右侧按需构建，底部彩色实时日志；
/// 查询工具改为独立窗口，主界面只保留“构建 + 日志”。
/// </summary>
public partial class MainWindow : Window
{
    private enum LogLevel
    {
        Info,
        Warn,
        Error,
        Success
    }

    private enum HintState
    {
        Neutral,
        Ok,
        Warn
    }

    private static readonly SolidColorBrush InfoBrush = CreateBrush(0x1F, 0x29, 0x37);
    private static readonly SolidColorBrush WarnBrush = CreateBrush(0xB4, 0x53, 0x09);
    private static readonly SolidColorBrush ErrorBrush = CreateBrush(0xB9, 0x1C, 0x1C);
    private static readonly SolidColorBrush SuccessBrush = CreateBrush(0x04, 0x78, 0x57);
    private static readonly SolidColorBrush TimeBrush = CreateBrush(0x9C, 0xA3, 0xAF);
    private static readonly SolidColorBrush OkBackground = CreateBrush(0xEC, 0xFD, 0xF5);
    private static readonly SolidColorBrush OkBorder = CreateBrush(0xA7, 0xF3, 0xD0);
    private static readonly SolidColorBrush WarnBackground = CreateBrush(0xFF, 0xFB, 0xEB);
    private static readonly SolidColorBrush WarnBarBorder = CreateBrush(0xFC, 0xD3, 0x4D);
    private static readonly SolidColorBrush NeutralBackground = CreateBrush(0xF9, 0xFA, 0xFB);
    private static readonly SolidColorBrush NeutralBorder = CreateBrush(0xE5, 0xE7, 0xEB);

    // 语义检索只索引类型级符号：与 Phase 6 首次生成向量时的 --vectors-kinds 保持一致，
    // 避免误点“全局增量向量”时把 6 万多个方法/字段也纳入（约 45 分钟）。
    private static readonly string[] VectorKinds = { "class", "interface", "enum", "record", "annotation" };
    private static readonly string VectorKindsText = string.Join(",", VectorKinds);

    private readonly IndexDatabase _database = IndexDatabase.CreateAtDefaultLocation();
    private readonly ObservableCollection<DataSourceNode> _roots = new();
    private readonly HashSet<string> _changedPaths = new(StringComparer.Ordinal);

    private SymbolQueryService _queries = null!;
    private EmbeddingService _embeddings = null!;
    private string _indexRoot = string.Empty;
    private Dictionary<string, int> _errorPaths = new(StringComparer.Ordinal);
    private CancellationTokenSource? _taskCts;
    private CancellationTokenSource? _scanCts;
    private bool _busy;
    private InMemoryIndex? _memoryIndex;
    private bool _loadModeInitializing;

    public MainWindow()
    {
        InitializeComponent();

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(workArea.Width - 40, 900));
        Height = Math.Min(Height, Math.Max(workArea.Height - 40, 620));

        _database.EnsureCreated();
        _queries = new SymbolQueryService(_database);
        _embeddings = new EmbeddingService(_database);
        _indexRoot = IndexDatabase.ResolveProjectRoot();
        SourceTree.ItemsSource = _roots;
        SourceTree.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnTreeCheckChanged));
        SourceTree.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnTreeCheckChanged));
        SourceTree.AddHandler(ToggleButton.IndeterminateEvent, new RoutedEventHandler(OnTreeCheckChanged));

        Loaded += OnLoaded;
        Closing += OnWindowClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnvVersionText.Text = AppInfo.ResolveVersion();
        EnvDeviceText.Text = "CPU（ONNX Runtime）";
        EnvModelText.Text = EmbeddingService.ResolveModelDirectory();
        EnvDbText.Text = _database.FilePath;

        if (IndexDatabase.LastMigrationMessage is { } migration)
        {
            AppendLog(migration, LogLevel.Success);
        }

        if (IndexDatabase.LastEmbeddingFormatMessage is { } formatMessage)
        {
            AppendLog(formatMessage, LogLevel.Warn);
        }

        AppendLog($"索引库：{_database.FilePath}");
        AppendLog($"源码根：{_indexRoot}");
        if (!File.Exists(_database.FilePath))
        {
            AppendLog("索引库尚不存在，首次构建会自动创建。", LogLevel.Warn);
        }

        _errorPaths = _database.GetParseErrorPaths();
        RebuildTree();
        RefreshStats();

        // Load Mode 持久化在 index_meta：McpServer 启动时读取（环境变量 MCMCP_LOAD_MODE 优先级更高）。
        _loadModeInitializing = true;
        var savedMode = InMemoryIndex.ResolveLoadMode(null, _database);
        LoadModeCombo.SelectedIndex = savedMode == "full" ? 1 : 0;
        _loadModeInitializing = false;
        AppendLog($"McpServer 载入模式：{savedMode}（环境变量 MCMCP_LOAD_MODE 可覆盖）");
        if (savedMode == "full")
        {
            await LoadMemoryIndexAsync();
        }

        AppendLog("界面已就绪：左侧勾选数据源，右侧执行构建；查询工具在顶部“查询”菜单。", LogLevel.Success);
        await StartBackgroundScanAsync();
    }

    private async void OnLoadModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadModeInitializing || LoadModeCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var mode = item.Tag?.ToString() ?? "lazy";
        _database.SetMeta(InMemoryIndex.LoadModeMetaKey, mode);
        AppendLog($"Load Mode 已保存为 {mode}：McpServer 下次启动时生效（MCMCP_LOAD_MODE 环境变量优先）。");
        if (mode == "full")
        {
            await LoadMemoryIndexAsync();
        }
        else
        {
            _memoryIndex = null;
            _queries = new SymbolQueryService(_database);
            AppendLog("已切回 lazy：语义检索实时走 SQLite（分批扫描，峰值内存恒定）。");
        }
    }

    private async Task LoadMemoryIndexAsync()
    {
        if (_memoryIndex is not null)
        {
            AppendLog(_memoryIndex.Summary, LogLevel.Success);
            return;
        }

        try
        {
            SetHint("正在把向量与符号元数据载入内存……", HintState.Neutral, false);
            var index = await Task.Run(() => InMemoryIndex.Load(_database));
            _memoryIndex = index;
            _queries = new SymbolQueryService(_database, index);
            AppendLog(index.Summary, LogLevel.Success);
            SetHint("全内存模式已就绪：语义检索直接遍历内存向量", HintState.Ok, false);
        }
        catch (Exception ex)
        {
            AppendLog($"全内存模式加载失败，已回退 lazy：{ex.Message}", LogLevel.Error);
        }
    }

    // ---------- 智能提示 + 后台 SHA256 扫描 ----------

    private async Task StartBackgroundScanAsync()
    {
        // 不 Dispose 旧 CTS：旧扫描可能仍在 Parallel.ForEach 内注册回调。
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        SetHint("正在后台比对文件 SHA256……", HintState.Neutral, showAction: false);
        try
        {
            var result = await Task.Run(
                () => ChangeScanner.Scan(
                    _indexRoot,
                    _database,
                    token,
                    (done, total) => Dispatcher.BeginInvoke(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            SetHint($"正在后台比对文件 SHA256…… {done:N0} / {total:N0}", HintState.Neutral, false);
                        }
                    })),
                token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            _changedPaths.Clear();
            foreach (var path in result.ChangedPaths)
            {
                _changedPaths.Add(path);
            }

            var missing = await Task.Run(() => _database.GetMissingEmbeddingCount(VectorKinds), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (result.ChangedFiles == 0 && result.RemovedFiles == 0 && missing == 0)
            {
                SetHint($"索引一致，无需构建（已比对 {result.TotalFiles:N0} 个 Java 文件）", HintState.Ok, false);
                AppendLog($"快速扫描完成：{result.TotalFiles:N0} 个文件无变化，索引一致。", LogLevel.Success);
            }
            else
            {
                SetHint($"检测到 {result.ChangedFiles} 个文件变更，{missing:N0} 条符号缺失向量", HintState.Warn, true);
                AppendLog(
                    $"快速扫描：新增 {result.AddedFiles:N0}、修改 {result.ModifiedFiles:N0}、删除 {result.RemovedFiles:N0}，" +
                    $"缺失向量 {missing:N0} 条。",
                    LogLevel.Warn);
            }

            RebuildTree();
            RefreshStats();
        }
        catch (OperationCanceledException)
        {
            // 新扫描覆盖旧扫描，忽略。
        }
        catch (Exception ex)
        {
            SetHint($"变更扫描失败：{ex.Message}", HintState.Warn, false);
            AppendLog($"变更扫描失败：{ex.Message}", LogLevel.Error);
        }
    }

    private void SetHint(string text, HintState state, bool showAction)
    {
        HintText.Text = text;
        HintActionButton.Visibility = showAction ? Visibility.Visible : Visibility.Collapsed;
        switch (state)
        {
            case HintState.Ok:
                HintBar.Background = OkBackground;
                HintBar.BorderBrush = OkBorder;
                HintText.Foreground = SuccessBrush;
                break;
            case HintState.Warn:
                HintBar.Background = WarnBackground;
                HintBar.BorderBrush = WarnBarBorder;
                HintText.Foreground = WarnBrush;
                break;
            default:
                HintBar.Background = NeutralBackground;
                HintBar.BorderBrush = NeutralBorder;
                HintText.Foreground = InfoBrush;
                break;
        }
    }

    // ---------- 数据源树 ----------

    private void RebuildTree()
    {
        var checkedScopes = CollectCheckedScopes();
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        CollectExpanded(_roots, expanded);

        _roots.Clear();
        foreach (var node in DataSourceTreeBuilder.Build(_indexRoot, _errorPaths, _changedPaths))
        {
            _roots.Add(node);
        }

        RestoreChecked(_roots, checkedScopes);
        RestoreExpanded(_roots, expanded);
        ApplyFilter();
        UpdateSelectionSummary();
    }

    private HashSet<string> CollectCheckedScopes()
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in _roots)
        {
            node.CollectScopes(scopes);
        }

        return scopes;
    }

    private static void CollectExpanded(IEnumerable<DataSourceNode> nodes, ISet<string> expanded)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded)
            {
                expanded.Add(NodeKey(node));
            }

            CollectExpanded(node.Children, expanded);
        }
    }

    private static void RestoreExpanded(IEnumerable<DataSourceNode> nodes, HashSet<string> expanded)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = expanded.Count == 0 ? node.Children.Count > 0 : expanded.Contains(NodeKey(node));
            RestoreExpanded(node.Children, expanded);
        }
    }

    private static void RestoreChecked(IEnumerable<DataSourceNode> nodes, HashSet<string> scopes)
    {
        if (scopes.Count == 0)
        {
            return;
        }

        foreach (var node in nodes)
        {
            if (node.IsScope && scopes.Contains(node.RelativePath!))
            {
                node.IsChecked = true;
                continue;
            }

            RestoreChecked(node.Children, scopes);
        }
    }

    private static string NodeKey(DataSourceNode node) => node.RelativePath ?? "group:" + node.Title;

    private void OnNodeCheckClick(object sender, RoutedEventArgs e) => UpdateSelectionSummary();

    /// <summary>UI 自动化 / 键盘切换勾选时不会触发 Click，但会触发 Checked/Unchecked 路由事件。</summary>
    private void OnTreeCheckChanged(object sender, RoutedEventArgs e)
        => Dispatcher.BeginInvoke(new Action(UpdateSelectionSummary));

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (SourceSearchBox is null || SourceFilterCombo is null)
        {
            return;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var keyword = SourceSearchBox.Text.Trim();
        var mode = SourceFilterCombo.SelectedIndex;
        foreach (var node in _roots)
        {
            FilterNode(node, keyword, mode);
        }
    }

    /// <summary>叶子按关键字 + 过滤条件匹配；分组只要任一子节点可见即可保留。</summary>
    private bool FilterNode(DataSourceNode node, string keyword, int mode)
    {
        var selfMatch = keyword.Length == 0 ||
                        node.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        (node.RelativePath?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);
        var modeMatch = mode switch
        {
            1 => node.ChangedCount > 0,
            2 => node.ErrorCount > 0,
            _ => true
        };

        var childVisible = false;
        foreach (var child in node.Children)
        {
            childVisible |= FilterNode(child, keyword, mode);
        }

        var visible = node.Children.Count == 0
            ? selfMatch && modeMatch
            : childVisible || (selfMatch && modeMatch);

        node.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (childVisible && keyword.Length > 0)
        {
            node.IsExpanded = true;
        }

        return visible;
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var node in _roots)
        {
            node.IsChecked = true;
        }

        UpdateSelectionSummary();
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var node in _roots)
        {
            node.IsChecked = false;
        }

        UpdateSelectionSummary();
    }

    private void OnExpandAllClick(object sender, RoutedEventArgs e) => SetExpanded(true);

    private void OnCollapseAllClick(object sender, RoutedEventArgs e) => SetExpanded(false);

    private void SetExpanded(bool expanded)
    {
        foreach (var node in _roots)
        {
            node.SetExpandedRecursive(expanded);
        }

        if (expanded)
        {
            ApplyFilter();
        }
    }

    private void UpdateSelectionSummary()
    {
        var scopes = CollectCheckedScopes();
        SelectionSummary.Text = scopes.Count == 0 ? "未勾选" : $"已勾选 {scopes.Count} 项";

        if (scopes.Count == 0)
        {
            BuildScopeText.Text = "当前作用域：全部数据源（未勾选时“增量构建 / 全量重建”作用于全部）";
            return;
        }

        var preview = string.Join("、", scopes.Take(4));
        BuildScopeText.Text = scopes.Count <= 4
            ? $"当前作用域：{preview}"
            : $"当前作用域：{preview} 等 {scopes.Count} 项";
    }

    private List<string>? RequireScopes()
    {
        var scopes = CollectCheckedScopes().ToList();
        if (scopes.Count > 0)
        {
            return scopes;
        }

        AppendLog("请先在左侧勾选至少一个数据源（可勾选 Mod、加载器或整个 Vanilla）。", LogLevel.Warn);
        MessageBox.Show("请先在左侧勾选至少一个数据源。", "未选择数据源", MessageBoxButton.OK,
            MessageBoxImage.Information);
        return null;
    }

    // ---------- 构建 ----------

    private async void OnIncrementalBuildClick(object sender, RoutedEventArgs e)
        => await RunIndexAsync(forceFull: false, scopes: null, reparseScope: false);

    private async void OnFullRebuildClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            $"将清空全部索引记录（符号 / 边 / 注入 / 文件哈希 / 向量），并重新解析 {_indexRoot} 下的所有 .java 文件。\n\n是否继续？",
            "全量重建",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await RunIndexAsync(forceFull: true, scopes: null, reparseScope: false);
    }

    private async void OnBuildSelectedClick(object sender, RoutedEventArgs e)
    {
        var scopes = RequireScopes();
        if (scopes is null)
        {
            return;
        }

        await RunIndexAsync(forceFull: false, scopes: scopes, reparseScope: true);
    }

    private async Task RunIndexAsync(bool forceFull, IReadOnlyCollection<string>? scopes, bool reparseScope)
    {
        if (_busy)
        {
            AppendLog("已有任务在执行中，请先取消或等待完成。", LogLevel.Warn);
            return;
        }

        SetBusy(true);
        _taskCts = new CancellationTokenSource();
        var token = _taskCts.Token;
        BuildProgress.IsIndeterminate = true;
        BuildProgress.Value = 0;
        BuildProgressText.Text = forceFull ? "全量重建中……" : "增量构建中……";

        var scopeText = scopes is { Count: > 0 } ? $"作用域 {scopes.Count} 项（强制重解析）" : "全部数据源";
        AppendLog($"开始{(forceFull ? "全量重建" : "增量构建")}：{scopeText}", LogLevel.Info);

        try
        {
            var indexer = new JavaIndexer();
            var summary = await Task.Run(
                () => indexer.Index(
                    _indexRoot,
                    _database,
                    forceFull,
                    scopes,
                    reparseScope,
                    token,
                    message => Dispatcher.BeginInvoke(() => AppendLog(message)),
                    (done, total) => Dispatcher.BeginInvoke(() => UpdateProgress(done, total))),
                token);

            BuildProgress.IsIndeterminate = false;
            BuildProgress.Maximum = Math.Max(1, BuildProgress.Maximum);
            BuildProgress.Value = BuildProgress.Maximum;

            if (summary.Cancelled)
            {
                BuildProgressText.Text = "已取消（已写入的批次已保留）";
                AppendLog(
                    $"构建已取消：{summary.ChangedFiles:N0} 个待处理文件中已解析的批次已提交，其余保留原索引。",
                    LogLevel.Warn);
            }
            else
            {
                BuildProgressText.Text = "构建完成";
                AppendLog(
                    $"构建完成：files={summary.Files:N0}, changed={summary.ChangedFiles:N0}, " +
                    $"skipped={summary.SkippedFiles:N0}, removed={summary.RemovedFiles:N0}, " +
                    $"symbols={summary.Symbols:N0}, edges={summary.Edges:N0}, " +
                    $"injections={summary.Injections:N0}, parseErrors={summary.ParseErrors:N0}, " +
                    $"{(summary.DurationMs / 1000.0):F2}s",
                    LogLevel.Success);
            }

            await RefreshAfterTaskAsync();
        }
        catch (OperationCanceledException)
        {
            BuildProgress.IsIndeterminate = false;
            BuildProgressText.Text = "已取消（已写入的批次已保留）";
            AppendLog("构建已取消：并行解析已停止，已提交的批次保留。", LogLevel.Warn);
            await RefreshAfterTaskAsync();
        }
        catch (Exception ex)
        {
            BuildProgress.IsIndeterminate = false;
            BuildProgressText.Text = "构建失败";
            AppendLog($"构建失败：{ex.Message}", LogLevel.Error);
        }
        finally
        {
            _taskCts?.Dispose();
            _taskCts = null;
            SetBusy(false);
        }
    }

    private async void OnVectorSelectedClick(object sender, RoutedEventArgs e)
    {
        var scopes = RequireScopes();
        if (scopes is null)
        {
            return;
        }

        await RunVectorsAsync(scopes);
    }

    private async void OnGlobalVectorClick(object sender, RoutedEventArgs e) => await RunVectorsAsync(null);

    private async Task RunVectorsAsync(IReadOnlyCollection<string>? scopes)
    {
        if (_busy)
        {
            AppendLog("已有任务在执行中，请先取消或等待完成。", LogLevel.Warn);
            return;
        }

        SetBusy(true);
        _taskCts = new CancellationTokenSource();
        var token = _taskCts.Token;
        BuildProgress.IsIndeterminate = true;
        BuildProgress.Value = 0;
        BuildProgressText.Text = scopes is null ? "全局增量向量：扫描缺失向量……" : "为选中项生成向量……";
        AppendLog(scopes is null
            ? $"开始全局增量向量（类型级 {VectorKindsText}，只补齐缺失项）"
            : $"开始为选中 {scopes.Count} 项生成向量（类型级 {VectorKindsText}）", LogLevel.Info);

        try
        {
            var result = await Task.Run(
                () => _embeddings.BuildVectors(
                    kinds: VectorKindsText,
                    limit: 0,
                    modelDirectory: null,
                    log: message => Dispatcher.BeginInvoke(() => ReportVectorLog(message)),
                    scopePaths: scopes,
                    cancellationToken: token),
                token);

            BuildProgress.IsIndeterminate = false;
            if (result.Symbols == 0)
            {
                BuildProgress.Maximum = 100;
                BuildProgress.Value = 100;
                BuildProgressText.Text = "无需生成：所有符号都已有向量";
                AppendLog($"向量索引已是最新，耗时 {(result.DurationMs / 1000.0):F2}s", LogLevel.Success);
            }
            else
            {
                BuildProgress.Maximum = result.Symbols;
                BuildProgress.Value = result.Symbols;
                BuildProgressText.Text =
                    $"完成：{result.Symbols:N0} 条 / {result.Dimension} 维 / {(result.DurationMs / 1000.0):F1}s";
                AppendLog(
                    $"向量生成完成：symbols={result.Symbols:N0}, dim={result.Dimension}, " +
                    $"duration={(result.DurationMs / 1000.0):F2}s",
                    LogLevel.Success);
            }

            await RefreshAfterTaskAsync();
        }
        catch (OperationCanceledException)
        {
            BuildProgress.IsIndeterminate = false;
            BuildProgressText.Text = "向量生成已取消（已完成的向量已提交）";
            AppendLog("向量生成已取消：事务已提交，保留已生成的向量。", LogLevel.Warn);
            await RefreshAfterTaskAsync();
        }
        catch (Exception ex)
        {
            BuildProgress.IsIndeterminate = false;
            BuildProgressText.Text = "向量生成失败";
            AppendLog($"向量生成失败：{ex.Message}", LogLevel.Error);
        }
        finally
        {
            _taskCts?.Dispose();
            _taskCts = null;
            SetBusy(false);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_taskCts is null)
        {
            return;
        }

        AppendLog("已请求取消：等待后台任务在安全检查点退出……", LogLevel.Warn);
        CancelButton.IsEnabled = false;
        _taskCts.Cancel();
    }

    private void UpdateProgress(int done, int total)
    {
        BuildProgress.IsIndeterminate = false;
        BuildProgress.Minimum = 0;
        BuildProgress.Maximum = Math.Max(1, total);
        BuildProgress.Value = done;
        BuildProgressText.Text = $"{done:N0} / {total:N0}（{done * 100.0 / Math.Max(1, total):F0}%）";
    }

    /// <summary>EmbeddingService 的日志回调：解析“待生成 / 已生成”更新进度条。</summary>
    private void ReportVectorLog(string message)
    {
        AppendLog(message);

        var total = Regex.Match(message, @"待生成 (\d+) 条");
        if (total.Success && int.TryParse(total.Groups[1].Value, out var pending) && pending > 0)
        {
            BuildProgress.IsIndeterminate = false;
            BuildProgress.Maximum = pending;
            BuildProgress.Value = 0;
            BuildProgressText.Text = $"0 / {pending:N0}（0%）";
        }

        var progress = Regex.Match(message, @"已生成 (\d+)/(\d+)");
        if (progress.Success &&
            double.TryParse(progress.Groups[1].Value, out var done) &&
            double.TryParse(progress.Groups[2].Value, out var all) && all > 0)
        {
            BuildProgress.IsIndeterminate = false;
            BuildProgress.Maximum = all;
            BuildProgress.Value = done;
            BuildProgressText.Text = $"{done:N0} / {all:N0}（{done / all:P0}）";
        }
    }

    private async Task RefreshAfterTaskAsync()
    {
        _errorPaths = _database.GetParseErrorPaths();
        RefreshStats();
        await StartBackgroundScanAsync();
    }

    // ---------- 统计 ----------

    private void RefreshStats()
    {
        try
        {
            SymbolCountText.Text = _database.GetSymbolCount().ToString("N0");
            EdgeCountText.Text = _database.GetEdgeCount().ToString("N0");
            InjectionCountText.Text = _database.GetInjectionCount().ToString("N0");
            FileCountText.Text = _database.GetFileCount().ToString("N0");
            ModCountText.Text = _database.GetModCount().ToString("N0");
            EmbeddingCountText.Text = _database.GetEmbeddingCount().ToString("N0");
            CodeChunkCountText.Text = _database.GetCodeChunkCount().ToString("N0");
            ParseErrorCountText.Text = _database.GetParseErrorCount().ToString("N0");

            var run = _database.GetLastRun();
            LastRunText.Text = run is null
                ? "尚无运行记录"
                : $"时间      : {run.StartedAt}\n" +
                  $"模式      : {run.Mode}\n" +
                  $"耗时      : {(run.DurationMs / 1000.0):F2}s\n" +
                  $"文件      : files={run.Files:N0}, changed={run.ChangedFiles:N0}, " +
                  $"skipped={run.SkippedFiles:N0}, removed={run.RemovedFiles:N0}\n" +
                  $"符号 / 边 : symbols={run.Symbols:N0}, edges={run.Edges:N0}\n" +
                  $"注入 / 错误: injections={run.Injections:N0}, parseErrors={run.ParseErrors:N0}";
        }
        catch (Exception ex)
        {
            LastRunText.Text = $"读取统计失败：{ex.Message}";
        }
    }

    // ---------- 菜单 ----------

    private async void OnRefreshScanClick(object sender, RoutedEventArgs e)
    {
        AppendLog("手动重新扫描变更……");
        await StartBackgroundScanAsync();
    }

    private void OnOpenQueryToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag })
        {
            return;
        }

        var tool = tag switch
        {
            "symbol" => QueryTool.Symbol,
            "code" => QueryTool.Code,
            "snippet" => QueryTool.Snippet,
            "mixin" => QueryTool.Mixin,
            "mixinconflict" => QueryTool.MixinConflict,
            "semantic" => QueryTool.Semantic,
            _ => QueryTool.Mod
        };

        try
        {
            var window = new QueryWindow(
                tool,
                _queries,
                _database,
                _indexRoot,
                message => Dispatcher.BeginInvoke(() => AppendLog(message)))
            {
                Owner = this
            };
            window.Show();
            AppendLog($"已打开查询窗口：{window.Title}");
        }
        catch (Exception ex)
        {
            AppendLog($"打开查询窗口失败：{ex}", LogLevel.Error);
        }
    }

    private void OnOpenIndexDirectoryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.GetDirectoryName(_database.FilePath)!;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"打开索引目录失败：{ex.Message}", LogLevel.Error);
        }
    }

    private async void OnSelfTestClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            AppendLog("已有任务在执行中，请稍后再运行自检。", LogLevel.Warn);
            return;
        }

        SetBusy(true);
        AppendLog("开始自检：完整性 / 外键 / 孤儿向量 / FTS / 向量精度（Float16）……");
        try
        {
            var checks = await Task.Run(() => _database.RunSelfTest());
            foreach (var check in checks)
            {
                AppendLog($"[{(check.Passed ? "通过" : "失败")}] {check.Name}：{check.Detail}",
                    check.Passed ? LogLevel.Success : LogLevel.Error);
            }

            var failed = checks.Count(check => !check.Passed);
            AppendLog(failed == 0
                ? $"自检完成：{checks.Count} 项全部通过。"
                : $"自检完成：{failed}/{checks.Count} 项未通过，请先处理。",
                failed == 0 ? LogLevel.Success : LogLevel.Warn);
        }
        catch (Exception ex)
        {
            AppendLog($"自检失败：{ex.Message}", LogLevel.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnReplaceDatabaseClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            AppendLog("已有任务在执行中，请稍后再替换数据库。", LogLevel.Warn);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要导入的索引库（例如另一台电脑生成的 mcmcp_core.db）",
            Filter = "SQLite 索引库 (*.db)|*.db|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SetBusy(true);
        AppendLog($"正在替换数据库：{dialog.FileName}");
        try
        {
            var result = await Task.Run(() => _database.ReplaceWith(dialog.FileName));
            AppendLog(result.Message, LogLevel.Success);

            _errorPaths = _database.GetParseErrorPaths();
            RebuildTree();
            RefreshStats();
            MessageBox.Show(result.Message, "替换数据库", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"替换数据库失败：{ex.Message}", LogLevel.Error);
            MessageBox.Show($"替换失败：\n{ex.Message}", "替换数据库", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnExportZipClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            AppendLog("已有任务在执行中，请稍后再导出。", LogLevel.Warn);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出当前索引为 ZIP",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = $"mc-mcp-index-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SetBusy(true);
        AppendLog("正在生成索引快照并打包……");
        try
        {
            var path = await Task.Run(() => _database.ExportZip(dialog.FileName));
            var sizeMb = new FileInfo(path).Length / 1024.0 / 1024.0;
            AppendLog($"已导出：{path}（{sizeMb:F1} MB）", LogLevel.Success);
            MessageBox.Show($"索引已导出到：\n{path}", "导出完成", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"导出失败：{ex.Message}", LogLevel.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnPackFullBuildClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            AppendLog("已有任务在执行中，请稍后再打包。", LogLevel.Warn);
            return;
        }

        var script = Path.Combine(_indexRoot, "pack_for_full_build.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show($"未找到打包脚本：\n{script}", "打包失败", MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        SetBusy(true);
        AppendLog("开始打包全量构建包（U 盘用）：app / LLM / code / mods / build 脚本 / README / 空 index……");
        try
        {
            var result = await Task.Run(() => RunPackScript(script));
            foreach (var line in result.Output.Split('\n',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AppendLog(line, result.ExitCode == 0 ? LogLevel.Info : LogLevel.Error);
            }

            if (result.ExitCode != 0)
            {
                AppendLog($"打包失败（exit={result.ExitCode}）：{result.Error}", LogLevel.Error);
                MessageBox.Show($"打包失败，详见日志。\n\n{result.Error}", "打包失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var zipPath = result.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.StartsWith("ZIP_PATH=", StringComparison.Ordinal))
                ?.Substring("ZIP_PATH=".Length) ?? Path.Combine(_indexRoot, "MC_MCP_FullBuild.zip");
            var sizeMb = File.Exists(zipPath) ? new FileInfo(zipPath).Length / 1024.0 / 1024.0 : 0;
            AppendLog($"全量构建包已生成：{zipPath}（{sizeMb:F1} MB）", LogLevel.Success);
            MessageBox.Show(
                $"U 盘全量构建包已生成：\n{zipPath}\n\n大小：{sizeMb:F1} MB\n\n" +
                "拷贝到另一台电脑后解压，双击 app\\MC_MCP.Launcher.exe → 全量重建，" +
                "再在命令行执行 --build-vectors 生成全部向量。",
                "打包完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"打包失败：{ex.Message}", LogLevel.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static (int ExitCode, string Output, string Error) RunPackScript(string script)
    {
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("无法启动 powershell.exe");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, outputTask.Result, errorTask.Result);
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Minecraft MCP Console · Phase 9\n\n" +
            "左侧：数据源树（Vanilla / NeoForge 补丁层 / Mods，支持海量 Mod 分组与勾选）\n" +
            "右侧：全量重建、增量构建、仅构建选中项、按需生成向量\n" +
            "底部：彩色实时日志；查询工具在“查询”菜单中独立弹窗。\n\n" +
            $"索引库：{_database.FilePath}",
            "关于",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    // ---------- 日志与状态 ----------

    private void AppendLog(string message) => AppendLog(message, InferLevel(message));

    private void AppendLog(string message, LogLevel level)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var brush = level switch
        {
            LogLevel.Warn => WarnBrush,
            LogLevel.Error => ErrorBrush,
            LogLevel.Success => SuccessBrush,
            _ => InfoBrush
        };

        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };
        paragraph.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = TimeBrush });
        paragraph.Inlines.Add(new Run(message) { Foreground = brush });
        LogDocument.Blocks.Add(paragraph);

        while (LogDocument.Blocks.Count > 3000)
        {
            LogDocument.Blocks.Remove(LogDocument.Blocks.FirstBlock);
        }

        if (AutoScrollCheck.IsChecked == true)
        {
            LogBox.ScrollToEnd();
        }
    }

    private static LogLevel InferLevel(string message)
    {
        if (message.Contains("失败", StringComparison.Ordinal) ||
            message.Contains("错误", StringComparison.Ordinal) ||
            message.Contains("异常", StringComparison.Ordinal) ||
            message.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Error;
        }

        if (message.Contains("取消", StringComparison.Ordinal) ||
            message.Contains("跳过", StringComparison.Ordinal) ||
            message.StartsWith("[warn]", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Warn;
        }

        if (message.Contains("完成", StringComparison.Ordinal) ||
            message.Contains("通过", StringComparison.Ordinal) ||
            message.Contains("已迁移", StringComparison.Ordinal) ||
            message.Contains("已就绪", StringComparison.Ordinal))
        {
            return LogLevel.Success;
        }

        return LogLevel.Info;
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => LogDocument.Blocks.Clear();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        IncrementalButton.IsEnabled = !busy;
        RebuildButton.IsEnabled = !busy;
        BuildSelectedButton.IsEnabled = !busy;
        VectorSelectedButton.IsEnabled = !busy;
        VectorGlobalButton.IsEnabled = !busy;
        HintActionButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _taskCts?.Cancel();
        _scanCts?.Cancel();
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
