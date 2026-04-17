# 遥测：Seq 与中继（telemetry-relay）

## 数据流（当前实现）

- **观测与运行日志**：OfficeCopilot **后台**通过 **Serilog** 写入内网 **Seq**（配置键 **`Telemetry:SeqServerUrl`**、**`Telemetry:SeqApiKey`**，见 `backend/appsettings.json` 或环境变量）。队列中的遥测事件由 `TelemetryRelayDispatchService` 以结构化日志发出，与业务日志进入同一 Seq。
- **全进程 Serilog → Seq**：与「结构化遥测」独立；**fail-closed 仅作用于结构化遥测**（`Telemetry …` 模板），不默认关闭其它业务日志。
- **遥测中继（`telemetry-relay`，默认 `http://127.0.0.1:8777`）**：提供 **策略 JSON** 与 Admin；AI 后台 **`GET /policy/aggregated`**（Bearer / `X-Telemetry-Key` 与 `Telemetry:ApiKey` 一致）**定时拉取**（约 30s + 配置变更触发）。**已不再提供 `POST /ingest/batch`**。
- **聚合体字段（与 AI 端对齐）**：`transmission`、`availableLogKinds`、`telemetryEmissionAllowed`（默认 true）、`maxEventPayloadChars` 等。中继侧 `availableLogKinds` 来自 **`DataRoot/telemetry-relay-policy.json`** 内当前选中 profile 的 `logKinds`（与内置种类合并；仓库示例见 `telemetry-relay/telemetry-relay-policy.example.json`），见 `telemetry-relay`。

## 策略真源与 fail-closed（AI 后台）

- **健康条件**：拉取成功且 JSON 可解析，且 `telemetryEmissionAllowed != false`，且 **`availableLogKinds` 非空**。否则 `IsTelemetryPolicyHealthy == false`。
- **结构化遥测**：仅当策略健康时，`TelemetryRelaySessionExtensions.TryEnqueueFromSession` 才入队；`TelemetryRelayDispatchService` 亦在写出 Seq 前检查健康，避免竞态残留。
- **用户子集**：WebSocket `telemetryLogKinds`（逗号分隔）与远端允许集合求 **交集**；未传或空表示 **在远端允许范围内全选**（不再表示无限制）。
- **关闭遥测**：`user-config` 中 `telemetryEnabled: false` 或未配置中继 URL/Key 时，不拉取中继，策略视为不健康，结构化遥测不发。

## Chrome 选项页

- 中继 URL/API Key 写入 `user-config.json`，与后台拉取策略一致；选项页拉取聚合策略用于展示 `availableLogKinds` 多选；**浏览器能访问中继不代表 AI 后台一定健康**，以后台定时拉取为准。

## 运维

- **Seq**：单独部署在内网一台机器；在 Seq 管理界面为日志/信号配置**统一保留期**（遥测事件与错误日志可同一策略）。
- **网络**：运行后台的主机必须能访问 Seq 的 **ingestion** 端点（防火墙/HTTPS 按需）；须能访问遥测中继以拉取策略。
- **中继 8777** 与 **Seq 默认端口**不是同一服务；`start-ai-and-telemetry.cmd` 仅启动后台与中继，不包含 Seq 进程。

## 管理员全停

- 将聚合中的 **`availableLogKinds` 置为空**，或在中继响应中置 **`telemetryEmissionAllowed`: false**（若中继实现该字段），AI 端将 fail-closed，不向 Seq 写结构化遥测。
