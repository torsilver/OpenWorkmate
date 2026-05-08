# AI Gateway（本机网关 + 观测）

本机进程 **AI Gateway**（`ai-gateway/`，默认 `http://127.0.0.1:8777`）同时扮演三个角色：

1. **LLM 代理**：按运维 + 用户策略把 OpenWorkmate 后台的聊天请求转发给上游供应商（`POST /llm/v1/chat/completions`），或由后台 **直连**（见 `routeMode`）。
2. **结构化观测落盘**：后台通过 `POST /ingest/spans`（JSON 批量）把 trace/span 写入 Gateway；Gateway 以 **每会话一行 JSONL** 形式存到 `DataRoot/sessions/<sid>.jsonl`，大负载写入 `DataRoot/blobs/`。
3. **策略与管理 UI**：
   - 聚合策略：`GET /api/policy/aggregated[?profileId=…]`（Bearer / `X-Telemetry-Key` = `AiGateway:ApiKey`），返回 `{effective, ops, user, userOverlayViolations, eTag, …}`。OpenWorkmate 后台进程启动时拉一次，之后约每 1 小时拉一次（不因 user-config 保存而额外拉取）。
   - 运维面板：`/admin.html`（写 `DataRoot/policy.ops.json`）。
   - 本机我的数据：`/my.html`（loopback 专属，展开 trace 树、删除、导出 .md、反馈评分、切换 `routeMode`）。

## 配置真源

| 维度 | 文件 | 说明 |
| ---- | ---- | ---- |
| 运维策略 | `DataRoot/policy.ops.json` | 管理员可编辑：允许的 `routeMode`、各类 profile、retention、MaxEventPayloadChars 等 |
| 用户策略 | `DataRoot/policy.user.json` | 仅 loopback PUT；当前包含 `routeMode` (`gateway` \| `direct`) |
| 运行期选项 | `AiGateway:*` in `appsettings.json` | `ApiKey`、`AdminApiKey`、`DataRoot`、`RetentionDays`、`MaxEventPayloadChars` |
| OpenWorkmate 后台 | `user-config.json` 顶层 | `telemetryEnabled`、`aiGatewayBaseUrl`、`aiGatewayApiKey`、`opsPolicyProfileId` |

## 健康与 fail-closed（后台侧）

- `TelemetryTransmissionPolicyBackgroundService` 拉取聚合策略并缓存；拉取失败或 JSON 不可解析时 `IsTelemetryPolicyHealthy == false`。
- 不健康时：`TelemetryRelayDispatchService` **跳过** `/ingest/spans` POST；`LlmGatewayHeadersHandler` **回退直连**（即便 `routeMode=gateway` 也不会卡死请求）。
- `telemetryEnabled=false` 或未配置 Gateway URL/Key：不拉取、不入队、直连上游 LLM。
- 用户要彻底关闭客户端出站观测：`user-config.telemetryUserObservabilityEnabled = false`，后台强制过滤（与侧栏是否携带 `deviceId` 无关）。

## routeMode 切换

- 合法值 `gateway` | `direct`。默认 `gateway`。
- 运维策略里声明"允许的集合"；用户策略选择当前值。若用户值不在允许集合内，聚合策略将其回落并记入 `userOverlayViolations`。
- 用户可通过 `/my.html` 保存 `routeMode`；后台在下一轮定时拉取聚合策略（最长约 1 小时）后才会用上新策略。修改 `user-config` 中的遥测相关项同理，重启进程可立刻重新拉取。

## 反馈（`/ingest/scores`）

- `POST /ingest/scores`，payload 示例：
  ```json
  { "sessionId": "...", "traceId": "...", "name": "user_thumb", "value": 1, "source": "user", "comment": "..." }
  ```
- 认证：支持 Bearer Api Key 或 loopback 直连。
- 写入：在该 session 的 JSONL 附加一行 `{"kind":"score", …}`。

## 数据目录约定

```
<DataRoot>/
  sessions/<sessionId>.jsonl         # 按会话 append，每行一个 span 或 score
  blobs/<sha256>[:0-2]/<sha256>...   # 大负载（请求/响应体）按内容寻址
  index/sessions.idx.json            # 会话索引：firstAt/lastAt/traceCount/sizeBytes/shards
  policy.ops.json                    # 运维策略
  policy.user.json                   # 用户策略
```

## 端到端启动

- 使用 `start-ai-and-gateway.cmd` 同时启动 AI 后台与 Gateway；`aiGatewayBaseUrl=http://127.0.0.1:8777` 为默认约定。
- Serilog 现在只对 Console + File 落盘（见 `backend/Program.cs`）；已移除 Seq 依赖（`Serilog.Sinks.Seq` 不再引用）。
