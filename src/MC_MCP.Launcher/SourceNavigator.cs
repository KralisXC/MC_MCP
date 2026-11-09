using System.Diagnostics;
using System.IO;

namespace MC_MCP.Launcher;

/// <summary>
/// 双击结果行时定位源码：优先用 Cursor / VS Code 的 "-g 文件:行号" 跳转，
/// 都不在 PATH 中时回退到系统默认程序打开文件。
/// </summary>
internal static class SourceNavigator
{
    private static bool _editorResolved;
    private static string? _editorPath;

    public static void Open(string indexRoot, string filePath, int line, Action<string>? log = null)
    {
        var absolute = ResolvePath(indexRoot, filePath);
        if (!File.Exists(absolute))
        {
            log?.Invoke($"[open] 文件不存在：{absolute}");
            return;
        }

        var lineNumber = Math.Max(line, 1);
        var editor = ResolveEditor();
        if (editor is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = editor,
                    Arguments = $"-g \"{absolute}:{lineNumber}\"",
                    UseShellExecute = true
                });
                log?.Invoke($"[open] {Path.GetFileName(editor)} -g \"{absolute}:{lineNumber}\"");
                return;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[open] 调用 {Path.GetFileName(editor)} 失败：{ex.Message}，回退到默认编辑器");
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = absolute, UseShellExecute = true });
            log?.Invoke($"[open] 未找到 cursor/code 命令，已用系统默认程序打开：{absolute}:{lineNumber}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[open] 打开失败：{ex.Message}");
        }
    }

    /// <summary>索引库中的路径是相对工程根的，例如 code/vanilla/26.1.2/...；绝对路径原样返回。</summary>
    private static string ResolvePath(string indexRoot, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(filePath))
        {
            return Path.GetFullPath(filePath);
        }

        return Path.GetFullPath(Path.Combine(indexRoot, filePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>在 PATH 中依次查找 cursor / code 的命令行入口（.cmd / .exe / .bat）。</summary>
    private static string? ResolveEditor()
    {
        if (_editorResolved)
        {
            return _editorPath;
        }

        _editorResolved = true;
        var searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in new[] { "cursor", "code" })
        {
            foreach (var directory in directories)
            {
                foreach (var extension in new[] { ".cmd", ".exe", ".bat" })
                {
                    var candidate = Path.Combine(directory.Trim('"'), name + extension);
                    if (File.Exists(candidate))
                    {
                        _editorPath = candidate;
                        return candidate;
                    }
                }
            }
        }

        return null;
    }
}
