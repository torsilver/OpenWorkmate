# 销售数据库 MCP (Sales Database MCP)

面向销售用 SQL Server 的只读 MCP 服务，供 AI（如 Cursor/Copilot）通过工具查询销售库的表、结构和数据。

## 工具列表

| 工具名 | 说明 |
|--------|------|
| `sales_db_health` | 检查与销售库的连接是否正常 |
| `sales_db_query` | 执行只读 SELECT 查询，返回表格化文本结果 |
| `sales_db_list_tables` | 列出库中的表和视图，可按 schema 过滤 |
| `sales_db_get_schema` | 获取指定表/视图的列名、类型、可空与主键信息 |

## 快速开始

1. **配置连接字符串**：复制 `appsettings.Example.json` 为 `appsettings.json` 并填入你的 SQL Server 连接串，或设置环境变量 `SALES_DB_CONNECTION_STRING`。
2. **在 Cursor 中使用**：用 Cursor 打开本仓库根目录（`_Taskly`），根目录下已有 `.vscode/mcp.json`，会加载销售数据库 MCP。在对话中即可让 AI 使用 `sales_db_*` 工具查询销售库。
3. **验证**：在 Cursor 中问「检查一下销售数据库连接是否正常」，AI 会调用 `sales_db_health` 进行检测。

## 连接字符串配置（重要）

**连接字符串只放在 MCP 端，不要写在 skills 或代码里。**

任选其一即可：

1. **环境变量**（推荐）：设置 `SALES_DB_CONNECTION_STRING`，例如：
   ```bash
   set SALES_DB_CONNECTION_STRING=Server=.;Database=Sales;User Id=sa;Password=xxx;TrustServerCertificate=True
   ```
   或在 Cursor/VS Code 的 MCP 配置里为该 MCP 的 `env` 中设置（注意不要提交含密码的配置文件）。

2. **配置文件**：在运行目录下的 `appsettings.json` 中配置：
   ```json
   {
     "SalesDb": {
       "ConnectionString": "Server=.;Database=Sales;..."
     }
   }
   ```
   建议将含真实连接串的 `appsettings.json` 加入 `.gitignore`，仅提交示例文件。

环境变量会覆盖 `appsettings.json` 中的 `SalesDb:ConnectionString`。

## 在 Cursor / VS Code 中注册

本仓库已在根目录提供 `.vscode/mcp.json`，使用相对路径指向本 MCP 项目。在项目根目录打开工作区即可使用。

若需单独配置（例如全局或其它工作区），在对应位置创建或编辑 MCP 配置文件：

- **Cursor / VS Code**：工作区下 `.vscode/mcp.json` 或用户级 MCP 配置
- **Visual Studio**：解决方案目录下的 `.mcp.json`

示例（从项目根目录 `dotnet run` 本 MCP）：

```json
{
  "servers": {
    "SalesDbMcp": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "sales-db-mcp/SalesDbMcp.csproj"
      ],
      "env": {}
    }
  }
}
```

连接字符串建议通过系统环境变量 `SALES_DB_CONNECTION_STRING` 设置，不要写在 `env` 里再提交到仓库。

## 本地运行与测试

```bash
cd sales-db-mcp
# 设置连接字符串后再运行
set SALES_DB_CONNECTION_STRING=Server=.;Database=Sales;...
dotnet run
```

运行后 MCP 通过 stdio 与 IDE 通信；在 Cursor/VS Code 中启用该 MCP 后，可用自然语言让 AI 调用上述工具查询销售库。

## 技术栈

- .NET 10
- Microsoft.Data.SqlClient（SQL Server）
- Model Context Protocol (stdio transport)

## 安全说明

- 仅支持只读：`sales_db_query` 仅接受 SELECT，拒绝其他语句。
- 连接字符串、密码不要写入 skills、不要提交到版本库；通过环境变量或本地配置文件提供。
