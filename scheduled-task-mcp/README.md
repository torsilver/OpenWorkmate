# 定时任务 MCP (Scheduled Task MCP)

供 AI 创建与管理定时任务：任务内容为 MD 文件（自然语言描述「到点后 AI 要做的事」），调度信息存于同名 .meta.json。到点后由**后端**将 MD 发给 AI 执行。

## 工具

| 工具 | 说明 |
|------|------|
| `scheduled_task_create` | 创建任务（id、title、content、scheduleType、cronExpression 或 intervalMinutes 等） |
| `scheduled_task_list` | 列举任务（可选 enabledOnly） |
| `scheduled_task_read` | 按 id 读取任务内容与 meta |
| `scheduled_task_update` | 更新任务（标题、内容、调度、enabled） |
| `scheduled_task_delete` | 删除任务 |

## 配置

- 目录：默认 `%LocalAppData%/OfficeCopilot/ScheduledTasks`，可通过 `ScheduledTasks:Directory` 或环境变量 `SCHEDULED_TASKS_DIRECTORY` 覆盖。
- 与后端共用同一目录时，后端需传入相同路径（如通过 env），设置页与 MCP 操作同一批文件。

## 技术栈

- .NET 10
- Cronos（cron 解析与下次执行时间）
- Model Context Protocol (stdio)
