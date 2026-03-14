# 准确数据 MCP (Accurate Data MCP)

面向 AI 的「准确数据」持久化 MCP 服务：将需要保证准确性、可复用的数据以文件形式存于指定目录，供后续按 id 精确读取或按需列举。

## 工具列表

| 工具名 | 说明 |
|--------|------|
| `accurate_data_write` | 写入或覆盖一条准确数据（id、content、format：md/json） |
| `accurate_data_read` | 按 id 读取一条准确数据 |
| `accurate_data_list` | 列举条目，可选 prefix、limit |
| `accurate_data_delete` | 按 id 删除一条准确数据 |

## 配置

- **目录**：默认 `%LocalAppData%/OfficeCopilot/AccurateData`。可通过 `AccurateData:Directory` 或环境变量 `ACCURATE_DATA_DIRECTORY` 覆盖。
- **存储**：每条数据一个文件（`{id}.md` 或 `{id}.json`），并附带 `{id}.meta.json` 记录更新时间。

## 快速开始

1. **配置目录（可选）**：复制 `appsettings.Example.json` 为 `appsettings.json` 并设置 `AccurateData:Directory`，或设置环境变量 `ACCURATE_DATA_DIRECTORY`。
2. **在 Cursor / 后端中使用**：在 MCP 配置中增加本服务（见下方示例），AI 即可在需要「先存下来再复用」的数据时调用上述工具。

## 在 Cursor / VS Code 中注册

从项目根目录运行示例：

```json
{
  "servers": {
    "AccurateDataMcp": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "accurate-data-mcp/AccurateDataMcp.csproj"],
      "env": {}
    }
  }
}
```

若需指定目录，可在 `env` 中设置 `ACCURATE_DATA_DIRECTORY`（绝对路径）。

## 与后端 / RAG 整合

- 本 MCP 仅负责**文件级**读写与列举；数据目录可与后端配置一致（例如后端传入相同路径），便于后端在写入时同步入 RAG 知识库 `accurate_data` 做语义检索。
- 搜索需求可与现有记忆/RAG 整合：由后端提供「准确数据」检索 API，内部调用 `SearchKnowledgeBaseAsync("accurate_data", ...)`，无需在本 MCP 内再建搜索。

## 技术栈

- .NET 10
- Model Context Protocol (stdio transport)

## 安全说明

- 所有读写限制在配置的根目录内，禁止路径穿越；id 仅允许字母、数字、下划线、连字符。
