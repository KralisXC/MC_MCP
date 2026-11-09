# Minecraft MCP (MC_MCP)

面向 Minecraft Java 版源码（含 Mod）的 MCP 索引服务。它用 Tree-sitter 解析 `.java` 文件，把符号、调用边、Mixin 注入和源码全文写入 SQLite，并通过 stdio 向 Cursor / Claude Desktop 等 MCP 客户端提供查询工具。

## 目录结构

```
MC_MCP/
├─ build.bat                  # 发布 Launcher + McpServer，并执行一次增量索引（绝不触碰 index\）
├─ build_clean.bat            # 只删除 app\ 后重新发布（索引与向量保留，需输入 YES 确认）
├─ app/                       # 发布产物（可整体删除/分发，不含索引）
│  ├─ MC_MCP.Launcher.exe
│  └─ MC_MCP.McpServer.exe
├─ index/mcmcp_core.db        # SQLite 索引（WAL 模式），位于项目根，删除 app\ 不会丢数据
├─ code/                      # 待索引的 Java 源码（只处理 .java）
├─ mods/                      # 通用 mod（mods/<mcver>/<mod>）与加载器专属 mod（mods/neoforge/<mcver>/...）
├─ LLM/e5-base-v2-onnx/       # ONNX 语义模型（model.onnx + vocab.txt）
├─ docs/                      # 各阶段报告
└─ src/
```

## 构建与索引

```powershell
# 编译整个解决方案
dotnet build .\MC_MCP.sln -c Release

# 发布到 app\ 并执行增量索引；只覆盖 exe / dll，绝不动 index\mcmcp_core.db
.\build.bat

# 只删除 app\ 后重新发布（index\mcmcp_core.db 与所有向量保留，输入 YES 确认）
.\build_clean.bat
```

`build.bat` 只创建缺失的 `app\` / `index\` 目录、用 `dotnet publish -o app` 覆盖可执行文件与依赖，然后跑一次增量索引。它不会删除、清空或重命名 `index\`，因此反复构建不会丢掉已经生成好的向量。

索引库位置在项目根 `index\mcmcp_core.db`，由 `IndexDatabase.ResolveProjectRoot()` 从可执行目录向上智能查找包含 `code\` / `mods\` / `index\` 的目录得到；程序从 `app\` 启动时即定位到项目根，因此整个工程可以整体迁移到任意盘符。需要自定义位置时设置环境变量 `MCMCP_DB_PATH`（或 `--db` 参数），优先级为 `--db` > `MCMCP_DB_PATH` > 项目根 `index\mcmcp_core.db`。

> 升级兼容：Phase 13 起数据库统一命名为 `index\mcmcp_core.db`。如果检测到旧版 `index\test.db`（含 `-wal` / `-shm`）且新库不存在，程序会自动就地重命名，并在 Launcher 日志中输出“检测到旧版 test.db，已自动重命名为 mcmcp_core.db”；更老的 `app\index\` 下的旧库也会自动迁移到项目根 `index\`。

也可以直接调用 CLI：

```powershell
# 增量索引：只重新解析 SHA256 发生变化的 .java 文件
.\app\MC_MCP.McpServer.exe --index E:\KMODS\MC_MCP

# 强制全量重建：清空所有表后重新解析
.\app\MC_MCP.McpServer.exe --index E:\KMODS\MC_MCP --full

# 指定数据库路径（优先级：--db > MCMCP_DB_PATH > 项目根 index\mcmcp_core.db）
.\app\MC_MCP.McpServer.exe --db D:\mcp\mcmcp_core.db
```

扫描目录时只接受 `.java` 后缀，`.json`、`.png`、`.class`、`.ogg` 等文件不会进入解析器。索引时会同时扫描 `neoforge.mods.toml`、`fabric.mod.json` 与 `*.mixins.json`，把 Mod 元数据和依赖写入 `mod_metadata` / `mod_dependencies`。

### 打包到另一台电脑做全量构建（U 盘）

```powershell
.\pack_for_full_build.ps1
# 产物：MC_MCP_FullBuild.zip
```

打包内容：`app\`（可直接运行的 Launcher / McpServer）、`LLM\`（e5 模型）、`code\`、`mods\`、`build.bat`、`build_clean.bat`、`README.md`，以及一个空的 `index\`。
绝不包含：`src\`、`docs\`、旧 `index\mcmcp_core.db`（含 `-wal` / `-shm`）、`bin\` / `obj\`、`.git\`、`.screenshots\`。
也可以直接在 Launcher 的“工具 → 打包全量构建包（U盘用）”中执行。

对方电脑解压后：双击 `app\MC_MCP.Launcher.exe` → 点击【全量重建】，再在命令行执行：

```powershell
.\app\MC_MCP.McpServer.exe --build-vectors --vectors-kinds class,interface,enum,record,annotation,method,field
```

> 包内不含 `src\`，因此对方电脑不要运行 `build.bat` / `build_clean.bat`（这两个脚本会重新编译源码）；直接运行 `app\` 下已发布的 exe 即可。

### 在 Surface 上常规索引，拷到 4050 生成向量

ONNX 推理比较吃 CPU/GPU，推荐把“解析源码”和“生成向量”分开跑：

```powershell
# 1) Surface：构建索引（增量很快；首次全量）
.\app\MC_MCP.McpServer.exe --index E:\KMODS\MC_MCP
# 产物：index\mcmcp_core.db（连同同目录的 mcmcp_core.db-wal / mcmcp_core.db-shm 一起拷走更稳妥）

# 2) 把 mcmcp_core.db 与 LLM\e5-base-v2-onnx\ 一起拷到 4050 电脑

# 3) 4050：只生成向量，不动源码解析（--vectors-kinds 可只跑类/接口，显著缩短时间）
.\app\MC_MCP.McpServer.exe --build-vectors --db D:\mcp\mcmcp_core.db `
  --model-dir D:\mcp\e5-base-v2-onnx --vectors-kinds class,interface,enum,record,annotation

# 4) 把生成好向量的 mcmcp_core.db 拷回 Surface，Cursor 即可调用 semantic_search
```

**默认是增量生成**：不加任何额外参数时，`--build-vectors` 会通过
`LEFT JOIN symbol_embeddings` 只挑出 `embeddings.symbol_id IS NULL` 的符号。第一次是全量，之后新增 Mod / 新增类再跑只会补齐新增的那部分；没有新符号时直接退出（甚至不加载 ONNX 模型，秒完成）。

```powershell
# 日常：增量补齐（推荐，重复执行安全）
.\app\MC_MCP.McpServer.exe --build-vectors --db E:\KMODS\MC_MCP\index\mcmcp_core.db

# 只想补某几类符号（类/接口/枚举/记录/注解）
.\app\MC_MCP.McpServer.exe --build-vectors --vectors-kinds class,interface,enum,record,annotation

# 首次验证只跑前 200 条
.\app\MC_MCP.McpServer.exe --build-vectors --vectors-limit 200

# 全量重算：显式清空 symbol_embeddings 后再生成（模型升级 / 更换维度时使用）
.\app\MC_MCP.McpServer.exe --build-vectors --vectors-reset
```

向量写入 `symbol_embeddings`，主键为 `symbol_id` 并外键引用 `symbols(id) ON DELETE CASCADE`；删除或重命名源码文件时，旧符号的向量会被 SQLite 自动级联清理，不会留下孤儿数据。

## 环境变量

| 变量 | 作用 | 默认值 |
| --- | --- | --- |
| `MCMCP_VERSION` | 覆盖 MCP `serverInfo.version` 与 `get_index_stats` 返回的版本号 | 自动探测 `code/vanilla/<版本>`；否则 `0.0.0-dev` |
| `MCMCP_DB_PATH` | SQLite 索引路径，等价于 `--db`（命令行参数优先级更高） | `<项目根>\index\mcmcp_core.db` |
| `MCMCP_MODEL_DIR` | e5-base-v2 ONNX 目录，等价于 `--model-dir` | 从可执行文件目录向上查找 `LLM\e5-base-v2-onnx` |
| `MCMCP_LOAD_MODE` | 语义检索载入模式：`lazy`（默认，实时走 SQLite）/ `full`（启动时把向量与符号元数据全部载入内存） | `lazy`，也可在 Launcher 顶部下拉框选择并写入 `index_meta.load_mode` |

```powershell
$env:MCMCP_VERSION = "26.1.2"
$env:MCMCP_DB_PATH = "E:\KMODS\MC_MCP\index\mcmcp_core.db"
$env:MCMCP_MODEL_DIR = "E:\KMODS\MC_MCP\LLM\e5-base-v2-onnx"
```

## Cursor 配置

把下面内容写入：

- 全局：`%USERPROFILE%\.cursor\mcp.json`
- 或项目级：`<项目根>\.cursor\mcp.json`

```json
{
  "mcpServers": {
    "mc-mcp": {
      "command": "E:\\KMODS\\MC_MCP\\app\\MC_MCP.McpServer.exe",
      "args": [
        "--db",
        "E:\\KMODS\\MC_MCP\\index\\mcmcp_core.db"
      ],
      "env": {
        "MCMCP_VERSION": "26.1.2",
        "MCMCP_DB_PATH": "E:\\KMODS\\MC_MCP\\index\\mcmcp_core.db",
        "MCMCP_MODEL_DIR": "E:\\KMODS\\MC_MCP\\LLM\\e5-base-v2-onnx"
      }
    }
  }
}
```

`--db` 与 `MCMCP_DB_PATH` 同时写出是为了让配置自解释；实际解析时 `--db` 优先，二者指向同一个库即可。`MCMCP_MODEL_DIR` 只在 `semantic_search` 需要，若模型就在 `app` 上级的 `LLM\e5-base-v2-onnx` 可省略。配置完成后重启 Cursor，在 MCP 面板中应能看到 `mc-mcp` 的 12 个工具。

### 在 Cursor 里验收 12 个工具

新建一个对话，把下面这段一次性发给 AI：

```text
请只用 mc-mcp 这个 MCP 服务回答，不要猜测：
1) 用 get_index_stats 告诉我当前索引的版本、符号数、边数、Mod 数和向量数。
2) 用 search_symbol 找 net.minecraft.world.entity.player.Player，并给出它的 origin。
3) 用 get_inheritors 查 net.minecraft.world.entity.Entity 的直接子类（top_k=5）。
4) 用 get_symbol_uses 查 net.minecraft.world.entity.player.Player，edge_type 设为 call，列出前 5 条。
5) 用 search_mixin 查 net.minecraft.client.gui.screens.Screen，列出 3 个注入点。
6) 用 get_mod_metadata 查 mod_id=emf，说明它的加载器与依赖。
7) 用 semantic_search 搜“player model animation”，列出最相关的 5 个 FQN 和分数。
8) 用 search_code 搜 keyword=setDeltaMovement。
9) 用 get_code_snippet 读取 net.minecraft.world.level.block.entity.HopperBlockEntity#pushItemsTick(Level, BlockPos, BlockState, HopperBlockEntity)，确认返回真实源码行。
10) 用 check_mixin_conflict 查 net.minecraft.client.model.geom.ModelPart，列出注入的 Mod 和冲突警告。
最后按工具名逐条汇报是否成功、返回了多少条结果。
```

如果 10 项都有结果（`semantic_search` 需要先跑过 `--build-vectors`），说明继承、调用边、Mixin、Mod 元数据、全文与语义检索、片段与冲突检测全部打通。任何一项返回“未找到”，先看该工具的错误文本：通常是 `--db` 路径不对、索引未建，或向量尚未生成。

> 使用 `semantic_search` 时，MCP 进程需要能读到 `LLM\e5-base-v2-onnx`（或设置 `MCMCP_MODEL_DIR`）。如果只把 `mcmcp_core.db` 拷给其他电脑，请把模型目录一并拷贝，且在 app 目录下运行时模型目录需要位于 `app` 的上级 `LLM\e5-base-v2-onnx`。

## Claude Desktop 配置

编辑 `%APPDATA%\Claude\claude_desktop_config.json`，内容与上面相同：

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

## MCP 工具

| 工具 | 参数 | 说明 |
| --- | --- | --- |
| `search_symbol` | `query`, `top_k` | 按名称或 FQN 片段搜索符号；同一 FQN 的 vanilla/NeoForge 来源自动合并，并带 `origin` / `origins` |
| `get_symbol_source` | `fqn` | 返回符号的文件与起止行号，附全部层来源 |
| `get_code_snippet` | `fqn`, `max_lines` | 按 FQN 直接读取源码文件并返回对应行号范围的代码片段；超过 `max_lines`（默认 80）时截断并追加 `// ... (代码已截断)` |
| `get_symbol_used_by` | `fqn` | 查询谁引用/继承/实现了该符号 |
| `get_inheritors` | `class_fqn`, `top_k` | 查询继承或实现了该类的全部子类 |
| `get_symbol_uses` | `fqn`, `edge_type`, `top_k` | 查询该类的出边；`edge_type` 可为 `call` / `instantiation` / `extends` / `implements`，并过滤局部变量接收者噪声 |
| `search_mixin` | `target_class` | 查询 Mixin 注入点与 `@At` 参数 |
| `check_mixin_conflict` | `target_class_fqn`, `target_method_name` | 列出目标类（可限定方法）上已有的注入点，按 mixin 类分组并归属到 Mod，命中时返回冲突警告 |
| `search_code` | `keyword`, `top_k` | FTS5 全文搜索源码片段 |
| `get_mod_metadata` | `mod_id`, `top_k` | 查询 Mod 的加载器、Mixin 配置、依赖与被依赖方 |
| `semantic_search` | `query`, `top_k` | e5-base-v2 ONNX 语义检索，返回最相关的类/方法 FQN |
| `get_index_stats` | — | 返回版本、文件数、符号/边/注入/Mod/向量数量 |

## Launcher（海量 Mod 控制台）

`app\MC_MCP.Launcher.exe` 在 Phase 8 重构为“面向海量 Mod 生态”的三段式控制台：顶部环境状态与智能提示、中部数据源树 + 构建控制、底部彩色实时日志。

- **数据源树**：一级为 `Vanilla（原版）` / `NeoForge（补丁层）` / `Mods（第三方 Mod）`；`Mods` 下按加载器再分组（`Multi-Loader`、`NeoForge`、`Fabric` …），三级为具体 Mod。每一级都带勾选框、文件数、变更徽标与解析报错徽标，支持搜索框与“全部 / 仅显示有变化的 / 仅显示解析报错的”过滤。
- **构建控制**：`增量构建`（按哈希跳过未变化文件）、`全量重建`、`仅构建选中项`（只强制重解析勾选的数据源）、`为选中项生成向量`、`全局增量向量`、`取消构建`；所有任务共用一条进度条与取消令牌。
- **智能提示**：启动时后台并行比对文件 SHA256。无变化时显示绿色“索引一致，无需构建”；有变化时显示黄色“检测到 X 个文件变更，Y 条符号缺失向量”，并给出“立即增量构建”入口。
- **实时日志**：构建输出按 Info（黑）/ Warn（黄）/ Error（红）/ Success（绿）着色，右下角可切换“自动滚动到底部”。
- **查询工具**：符号搜索、代码搜索、代码片段、Mixin 注入、Mixin 冲突检测、语义搜索、Mod 信息全部收进顶部“查询”菜单，各自弹出独立窗口，主界面只剩构建与日志。
- **工具菜单**：运行自检、替换当前数据库（覆盖 `index\mcmcp_core.db`）、导出索引为 ZIP。

查询结果双击跳转规则：先查 PATH 中的 `cursor`，再查 `code`，用 `-g "绝对路径:行号"` 打开；两者都没有时回退到系统默认程序打开文件。索引里存的是相对工程根的路径（如 `code/vanilla/26.1.2/...`），Launcher 会自动拼成绝对路径。

## 运行机制

- **增量**：`file_hashes` 保存每个 `.java` 的 SHA256；哈希一致直接跳过，变化文件先删除旧记录再重新解析，磁盘上消失的文件自动清理。
- **并发**：`Parallel.ForEach` 多线程解析（每线程独立 Tree-sitter Parser），结果经 `BlockingCollection` 交给唯一写线程，每 64 个文件一个事务写入 SQLite。
- **全文索引**：源码按 40 行切片写入 `code_chunks`，由 FTS5 external content 虚拟表 `code_fts` 维护倒排索引；`search_code` 用 `MATCH` + `bm25` 排序。
- **FQN**：嵌套类统一使用 JVM 二进制名 `package.Outer$Inner`；`@Mixin(targets = X.class)` 会自动去掉 `.class` 并映射到真实 FQN。
- **跨层合并**：symbols 增加 `origin`（`vanilla` / `neoforge` / `fabric` / `mod`），查询时按 FQN 合并同名的 vanilla 与 NeoForge 符号，返回 `origins` 与每个来源的 `sources`。
- **Mod 元数据**：索引时解析 `neoforge.mods.toml` / `fabric.mod.json` / `*.mixins.json`，按路径与元数据推断加载器，写入 `mod_metadata` 与 `mod_dependencies`。
- **语义向量（Float16）**：`--build-vectors` 用 `LLM/e5-base-v2-onnx` 生成 768 维向量，写入 `symbol_embeddings` 前转成 `System.Half` 半精度，每条固定 1536 字节（Float32 时是 3072），存储体积减半；该表以 `symbol_id` 为主键、外键 `symbols(id) ON DELETE CASCADE`，默认只补齐缺失向量、增量执行。旧版遗留的 Float32 向量会在启动时被自动识别并清空，重新跑一次 `--build-vectors` 即可以半精度重建。
- **语义检索（分批扫描）**：`semantic_search` 先在线编码 query，然后按 `symbol_id` 游标每批 10,000 条从 SQLite 流式读取向量，边读边算点积、维护 Top-K 最小堆，最后只回查候选的符号元数据。查询内存占用与库内向量总量无关（30 万条也不会一次性载入），代价是每次检索线性扫描一遍向量表。
- **双载入模式（Phase 10）**：`MCMCP_LOAD_MODE=full`（或 Launcher 顶部下拉框）时，McpServer 启动即把 `symbol_embeddings` 全部反序列化为 `Dictionary<long, float[]>`，并把 symbols 的 id/fqn/name/kind/signature/origin/file_path/start_line/end_line/comment 读入内存字典；`semantic_search` 直接遍历内存算点积，9 千条量级内部耗时约 20-30 ms。启动日志会打印“已启用全内存模式，加载了 X 条向量，占用内存约 Y MB”。`lazy` 保持原先的分批扫库行为。
- **AI 上下文防爆（Phase 10）**：所有列表型工具都支持 `offset`，单次返回 JSON 超过 50KB 时自动截断，并在顶层追加 `warning` 与 `truncated` 字段，提示下一页的 offset。`get_symbol_uses` 新增 `dst_member`（目标方法名模糊匹配），`search_symbol` 新增 `kind`（class / method / field 等）。
- **注释上下文（Phase 10）**：解析 `class_declaration` / `method_declaration` 时向上收集紧邻的 `block_comment` / `line_comment`，清理 `/**`、`*`、`//` 后写入 `symbols.comment`；生成向量时文本由“FQN + 签名 + 类型 + 注释”组成，`search_symbol` / `get_symbol_source` / `semantic_search` 都会返回该字段。
- **代码片段提取（Phase 11）**：`get_code_snippet` 根据 `symbols` 的 `file_path` / `start_line` / `end_line`，从 `code/` 或 `mods/` 直接按行读取源码；类 FQN 返回声明起始处，方法 FQN（`pkg.Class#method(...)`）返回方法体，超过 `max_lines` 截断并追加 `// ... (代码已截断)`，单次响应仍受 50KB 上限保护。
- **Mixin 冲突检测（Phase 11）**：`check_mixin_conflict` 查询 `injections` 表中 `target_class` 命中的真实注入（排除类级 `@Mixin` 标记），可按 `target_method_name` 过滤；结果按 mixin 类分组，并用 `mod_metadata.mod_dir` 前缀归属到具体 Mod，命中时返回“检测到 X 个 Mod 对该目标进行了注入”的警告。
- **图关系剪枝**：索引完成后会把 `edges` 中的无效调用剔除——同类内部调用降级为 `internal_call`（默认查询不返回），指向 `java.* / javax.* / sun.* / jdk.*` 等标准库的边直接删除，`call` 的目标不在已索引类型中的边（局部变量接收者、外部未知库）也直接删除。剪枝幂等，并通过 `index_meta.edge_prune_revision` 记录版本，升级后自动补跑一次。
- **日志**：所有日志写入 stderr，stdout 只输出 MCP JSON-RPC，避免污染协议流。

## 排查

- **工具列表为空**：确认 `command` 指向 `app\MC_MCP.McpServer.exe`，路径使用双反斜杠。
- **数据库找不到**：显式传 `--db` 或设置 `MCMCP_DB_PATH`；服务启动日志会打印实际使用的路径。
- **升级后 `search_code` 无结果**：旧版 `code_fts` 结构会被自动丢弃并清空 `file_hashes`；再执行一次 `--index` 即可重建。
- **`semantic_search` 报找不到模型 / vocab.txt**：Microsoft.ML.Tokenizers 不能直接读 HuggingFace 的 `tokenizer.json`，必须使用同目录的 BERT `vocab.txt`；并确认 `model.onnx` 与 `vocab.txt` 都在 `MCMCP_MODEL_DIR` 下。
- **`semantic_search` 返回空**：先执行 `--build-vectors` 生成 `symbol_embeddings`；`get_index_stats` 的 `embeddings` 字段可确认数量。
- **部分文件解析报错**：Java 21 record patterns、Java 25 flexible constructor bodies、类型使用注解 + varargs 仍会留下 `parse_errors` 记录，不影响其余符号索引。
