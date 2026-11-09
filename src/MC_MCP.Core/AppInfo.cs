namespace MC_MCP.Core;

/// <summary>MVP 阶段共享的常量与环境变量读取逻辑。</summary>
public static class AppInfo
{
    public const string VersionEnvironmentVariable = "MCMCP_VERSION";

    public const string FallbackVersion = "26.1.2";

    /// <summary>读取 MCMCP_VERSION 环境变量，未设置时回退到默认版本号。</summary>
    public static string ResolveVersion()
    {
        var raw = Environment.GetEnvironmentVariable(VersionEnvironmentVariable);
        return string.IsNullOrWhiteSpace(raw) ? FallbackVersion : raw.Trim();
    }
}
