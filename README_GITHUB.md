# MC_MCP · Minecraft AI Mod 开发智能检索引擎

面向 Minecraft Java 版源码（原版 + Mod）的 MCP 索引服务：用 AST 解析 `.java` 文件，把符号、调用边、Mixin 注入点与源码全文写入 SQLite，再通过 12 个 MCP 工具，让 Cursor / Claude Desktop 里的 AI 像读自己的代码一样读整个 Mod 生态。

> 目标场景：写 Mixin 前先查冲突、改原版方法前先看调用方、让 AI 直接读真实源码而不是“猜”。

---

## 核心特性

- **12 个 MCP 工具**：符号搜索、源码定位、代码片段、继承链、调用边、Mixin 注入、Mixin 冲突检测、全文检索、Mod 元数据、语义检索、索引统计一次到位。
- **全内存毫秒级语义检索**：e5-base-v2 ONNX 向量常驻内存，9,000+ 条向量内部检索约 **20–30 ms**（`MCMCP_LOAD_MODE=full`）。
- **Mixin 冲突检测**：`check_mixin_conflict` 汇总目标类/方法上的所有注入点，按 Mixin 类分组并归属到具体 Mod，写 Mixin 前即可发现“这个类已经被别人注入过了”。
- **AST 级解析**：Tree-sitter 解析 Java，支持嵌套类（JVM 二进制名 `Outer$Inner`）、record、注解、`@Mixin(targets=...)` 映射。
- **增量索引**：按文件 SHA256 跳过未变化文件，只重解析变更部分；Mod 增删后重跑一次即可。
- **跨层符号合并**：同一 FQN 的 vanilla / NeoForge / Mod 实现自动合并，返回 `origin` 与各层源码位置。
- **AI 上下文防爆**：所有列表工具支持 `offset`，单次响应超过 50 KB 自动截断并提示翻页。
- **源码全文检索**：FTS5 + bm25 排序，40 行切片，毫秒级定位 `setDeltaMovement` 这类关键字。
- **可视化控制台**：WPF Launcher 提供数据源树、增量/全量构建、向量生成与彩色实时日志。

## 12 个 MCP 工具

| 工具 | 说明 |
| --- | --- |
| `search_symbol` | 按名称 / FQN 片段搜索符号，自动合并跨层来源 |
| `get_symbol_source` | 返回符号所在文件与起止行号 |
| `get_code_snippet` | 直接读取源码并返回代码片段，超长自动截断 |
| `get_symbol_used_by` | 谁引用 / 继承 / 实现了该符号 |
| `get_inheritors` | 查询全部子类 / 实现类 |
| `get_symbol_uses` | 查询出边（call / instantiation / extends / implements） |
| `search_mixin` | 查询目标类上的 Mixin 注入点与 `@At` 参数 |
| `check_mixin_conflict` | 检测目标类 / 方法上的注入冲突并归属 Mod |
| `search_code` | FTS5 全文检索源码片段 |
| `get_mod_metadata` | Mod 加载器、Mixin 配置、依赖关系 |
| `semantic_search` | e5-base-v2 语义检索，返回最相关 FQN |
| `get_index_stats` | 版本、文件 / 符号 / 边 / 注入 / Mod / 向量统计 |

## 快速开始

### 环境要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10/11（Launcher 为 WPF） |
| .NET | .NET 10 SDK |
| 磁盘 | 源码 + 索引 + 模型约 10 GB 起 |
| 模型 | e5-base-v2 ONNX（`model.onnx` + BERT `vocab.txt`） |

### 编译

```powershell
git clone <your-repo-url> MC_MCP
cd MC_MCP
dotnet build .\MC_MCP.sln -c Release
```

发布到 `app\` 并执行一次增量索引（索引库固定放在项目根 `index\`，不会被清空）：

```powershell
.\build.bat
# 产物：app\MC_MCP.Launcher.exe、app\MC_MCP.McpServer.exe、index\mcmcp_core.db
```

只想重新发布、保留已有索引与向量：

```powershell
.\build_clean.bat
```

## 准备源码

仓库**不包含**任何 Minecraft 源码或 Mod 源码（体积大且版权各异），需要自行放入：

```
code/vanilla/<版本>/        # 反编译后的原版 .java，例如 code/vanilla/26.1.2/net/minecraft/...
code/neoforge/<版本>/       # NeoForge 补丁层（可选）
mods/<版本>/<Mod 名>/       # 通用 Mod 源码
mods/neoforge/<版本>/<Mod>  # 加载器专属 Mod 源码
```

只处理 `.java` 文件；同时会扫描 `neoforge.mods.toml`、`fabric.mod.json`、`*.mixins.json` 写入 Mod 元数据。

构建索引：

```powershell
# 增量（按 SHA256 跳过未变化文件）
.\app\MC_MCP.McpServer.exe --index .

# 全量重建
.\app\MC_MCP.McpServer.exe --index . --full
```

## 准备模型与向量

把 e5-base-v2 的 ONNX 模型放到：

```
LLM/e5-base-v2-onnx/
├─ model.onnx
└─ vocab.txt
```

生成语义向量（首次全量，之后自动增量补齐）：

```powershell
.\app\MC_MCP.McpServer.exe --build-vectors `
  --vectors-kinds class,interface,enum,record,annotation
```

> 没有模型时其余 11 个工具仍可用，只有 `semantic_search` 需要它。

## 在 Cursor 中配置

写入 `%USERPROFILE%\.cursor\mcp.json`（全局）或 `<项目根>\.cursor\mcp.json`（项目级）：

```json
{
  "mcpServers": {
    "mc-mcp": {
      "command": "E:\\KMODS\\MC_MCP\\app\\MC_MCP.McpServer.exe",
      "args": ["--db", "E:\\KMODS\\MC_MCP\\index\\mcmcp_core.db"],
      "env": {
        "MCMCP_VERSION": "26.1.2",
        "MCMCP_DB_PATH": "E:\\KMODS\\MC_MCP\\index\\mcmcp_core.db",
        "MCMCP_MODEL_DIR": "E:\\KMODS\\MC_MCP\\LLM\\e5-base-v2-onnx"
      }
    }
  }
}
```

- `--db` 与 `MCMCP_DB_PATH` 指向同一个库即可，实际以 `--db` 优先。
- 模型就在 `app` 上级的 `LLM\e5-base-v2-onnx` 时可省略 `MCMCP_MODEL_DIR`。
- 重启 Cursor 后，MCP 面板应出现 `mc-mcp` 的 12 个工具。

## 目录结构

```
MC_MCP/
├─ build.bat / build_clean.bat   # 发布 + 增量索引
├─ app/                          # 发布产物（不入库）
├─ index/mcmcp_core.db           # SQLite 索引（WAL，不入库）
├─ code/                         # 原版 / 补丁层源码（不入库）
├─ mods/                         # Mod 源码（不入库）
├─ LLM/                          # ONNX 模型（不入库）
├─ docs/                         # 内部阶段报告（不入库）
└─ src/
   ├─ MC_MCP.Core/               # 版本探测等公共逻辑
   ├─ MC_MCP.Indexer/            # Tree-sitter 解析、SQLite、向量
   ├─ MC_MCP.McpServer/          # MCP stdio 服务（12 个工具）
   └─ MC_MCP.Launcher/           # WPF 控制台
```

`.gitignore` 已排除 `app/`、`bin/`、`obj/`、`index/`、`LLM/`、`code/`、`mods/`、`docs/` 与临时打包目录的内容，并用 `.gitkeep` 保留这些空目录结构，仓库里只有工具本身。

## 数据库说明

- 索引库默认位于项目根 `index\mcmcp_core.db`，WAL 模式；
- 从旧版本升级时，若检测到 `index\test.db` 且新库不存在，会自动重命名（含 `-wal` / `-shm`）；
- 也可用 `--db <路径>` 或环境变量 `MCMCP_DB_PATH` 指定任意位置。

## 说明

本仓库仅开源检索工具与 MCP 服务本身，不附带 Minecraft / Mod 源码与模型权重。使用前请自行准备对应版本的源码与模型，并遵守其各自的许可协议。
