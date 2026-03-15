# 记忆管理：选 Sqlite 仍不创建数据库 — 修复计划

## 问题现象

- 用户在对话中通过 AI 保存记忆（如「请新增一条记忆 我是一个程序员」），AI 回复已保存。
- 在设置页「记忆与 RAG」→「记忆管理」中选「全部」并刷新，列表仍为空。
- 用户已确认：向量存储选的是 **Sqlite**（非内存）、未重启后台；怀疑 **根本没有创建 Sqlite 数据库**。

## 根因

**后端**（[backend/Program.cs](backend/Program.cs) 第 32–45 行）：

- `IVectorStore` 工厂中：仅当 `RagStorageType == "Sqlite"` **且** `RagStoragePath` **非空** 时才 `new SqliteVectorStore(...)`。
- 若路径为空或未配置，直接走 `return new InMemoryVectorStore()`，不会创建任何 SQLite 文件。

**前端**（[chrome-extension/options.js](chrome-extension/options.js) 第 819 行）：

- 选 Sqlite 时若路径输入框留空，保存时发送 `ragStoragePath: undefined`（或空字符串）。

**设置页**（[chrome-extension/options.html](chrome-extension/options.html) 第 385 行）：

- placeholder 写的是「留空则用 %LocalAppData%/OfficeCopilot/rag.db」，但后端未实现「留空用默认路径」。

因此：用户选 Sqlite 且路径留空 → 后端实际使用内存存储 → 不创建 DB → 记忆在进程内，且与「以为在用 Sqlite」的预期不符；设置页列表与对话共用同一内存存储，若存在其他因素（如首次解析时机等）也可能表现为列表为空。

## 修复方案

### 1. 后端：Sqlite 且路径为空时使用默认路径（核心）

**文件**：[backend/Program.cs](backend/Program.cs)

**位置**：`AddSingleton<IVectorStore>` 的工厂 lambda 内，在 `var path = (config.RagStoragePath ?? "").Trim();` 之后。

**逻辑**：

- 当 `string.Equals(t, "Sqlite", StringComparison.OrdinalIgnoreCase)` 时：
  - 若 `string.IsNullOrEmpty(path)`，则令 `path = "rag.db"`。
  - 再按现有逻辑：`path = Environment.ExpandEnvironmentVariables(path);`，若 `!Path.IsPathRooted(path)` 则 `path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfficeCopilot", path);`。
  - 最后 `return new SqliteVectorStore("Data Source=" + path);`。
- 这样选 Sqlite 且路径留空时，实际使用 `%LocalAppData%/OfficeCopilot/rag.db`，与设置页 placeholder 一致，并会创建该 SQLite 文件。

**要点**：不再要求「Sqlite 且 path 非空」才建 Sqlite；改为「Sqlite 且 path 为空则用默认路径再建 Sqlite」。

### 2. 可选：后端 GET /api/memory 的 scope 容错

- 对 `scope` 做 Trim + 大小写不敏感，空或 `"all"` 均视为查全部，避免参数不一致导致列表行为异常。

### 3. 可选：设置页记忆管理 UX

- 进入「记忆与 RAG」时默认范围选「全部」；在「仅共享记忆」且列表为空时提示可切到「全部」查看。属体验优化，非本次根因修复。

## 涉及文件

| 文件 | 变更 |
|------|------|
| [backend/Program.cs](backend/Program.cs) | Sqlite 分支内：path 为空时赋默认 `"rag.db"`，再做路径解析并 `new SqliteVectorStore(...)` |

## 验证建议

1. 设置页选「Sqlite」、路径留空并保存；重启后端。
2. 在对话中让 AI 保存一条记忆。
3. 设置页「记忆管理」选「全部」并刷新，应能看到该条。
4. 确认 `%LocalAppData%/OfficeCopilot/rag.db`（或当前环境对应路径）已生成且可打开。
