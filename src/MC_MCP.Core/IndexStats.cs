namespace MC_MCP.Core;

/// <summary>索引规模统计（数据来自 SQLite 中的 symbols / edges / injections 表）。</summary>
public sealed record IndexStats(
    string Version,
    string Status,
    int Entries,
    int Symbols,
    int Edges,
    int Injections,
    int Files,
    int Mods,
    int Embeddings);
