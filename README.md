# MC_MCP · Minecraft AI Mod 开发智能检索引擎

[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Minecraft](https://img.shields.io/badge/Minecraft-26.1.2-green)](https://minecraft.net/)
[![NeoForge](https://img.shields.io/badge/NeoForge-Supported-orange)](https://neoforged.net/)
[![MCP](https://img.shields.io/badge/MCP-Supported-blue)](https://modelcontextprotocol.io/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

把整个 Minecraft Java 源码与 Mod 生态变成 AI 可检索的知识库：Tree-sitter 解析 `.java`，SQLite 存符号 / 调用边 / Mixin 注入 / 全文索引，再通过 12 个 MCP 工具接入 Cursor、Claude Desktop 等客户端。

## 核心特性

- **12 个 MCP 工具**：符号搜索、源码片段、继承链、调用边、Mixin 注入与冲突检测、全文检索、Mod 元数据、语义检索、索引统计。
- **全内存毫秒级语义检索**：e5-base-v2 ONNX 向量常驻内存，9,000+ 条向量内部检索约 20–30 ms。
- **Mixin 冲突检测**：按目标类 / 方法汇总所有注入点，并按 Mod 分组，写 Mixin 前就能发现冲突。
- **AST 级解析**：支持嵌套类、record、注解与 `@Mixin(targets=...)` 映射，FQN 与 JVM 二进制名一致。
- **增量索引**：按文件 SHA256 只重解析变更文件；Mod 增删后重跑一次即可。
- **AI 上下文防爆**：列表工具支持 `offset`，单次响应超过 50 KB 自动截断并提示翻页。
- **源码全文检索**：FTS5 + bm25 排序，毫秒级定位关键字。

## 快速开始

环境要求：Windows 10/11、.NET 10 SDK、约 10 GB 磁盘空间、e5-base-v2 ONNX 模型（可选）。

```powershell
git clone https://github.com/KralisXC/MC_MCP.git
cd MC_MCP

# 1) 放入源码：code/vanilla/<版本>/、mods/<版本>/<Mod>/
# 2) 放入模型（可选）：LLM/e5-base-v2-onnx/model.onnx + vocab.txt
# 3) 编译、发布并建立索引（生成 index/mcmcp_core.db）
.\build.bat
```

更完整的构建、增量索引、Launcher、U 盘打包与排查说明见 [DEVELOPMENT.md](DEVELOPMENT.md)。

## 在 Cursor 中配置

> 请将下方路径替换为你实际的项目存放路径。

写入 `%USERPROFILE%\.cursor\mcp.json`（全局）或 `<项目根>\.cursor\mcp.json`（项目级）：

```json
{
  "mcpServers": {
    "mc-mcp": {
      "command": "C:\\path\\to\\MC_MCP\\app\\MC_MCP.McpServer.exe",
      "args": ["--db", "C:\\path\\to\\MC_MCP\\index\\mcmcp_core.db"],
      "env": {
        "MCMCP_VERSION": "26.1.2",
        "MCMCP_DB_PATH": "C:\\path\\to\\MC_MCP\\index\\mcmcp_core.db",
        "MCMCP_MODEL_DIR": "C:\\path\\to\\MC_MCP\\LLM\\e5-base-v2-onnx"
      }
    }
  }
}
```

重启 Cursor 后，MCP 面板中应出现 `mc-mcp` 的 12 个工具。模型位于 `app` 上级的 `LLM\e5-base-v2-onnx` 时可省略 `MCMCP_MODEL_DIR`。

## 文档

| 文档 | 内容 |
| --- | --- |
| [DEVELOPMENT.md](DEVELOPMENT.md) | 构建、增量索引、向量生成、环境变量、Launcher、排查 |
| `src/` | Indexer、McpServer、WPF Launcher 源码 |

## License

本项目基于 [MIT License](LICENSE) 开源，Copyright (c) 2026 KralisXC。
