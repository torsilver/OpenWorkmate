# 脚本式 Skill VM：调试器使用说明（CLI + 模式 B）

本文说明如何用同仓 **SkillDebugger CLI** 与宿主 **调试 HTTP API** 排查分段跳转与注入内容。设计背景见 [skill-script-debugger-plan.md](skill-script-debugger-plan.md)；协议与 VM 主设计见 [skills-scripted-vm-plan.md](skills-scripted-vm-plan.md)。

## 模式 A：CLI（无模型、确定性回放）

独立项目在 `tools/SkillDebugger.Cli/`，引用宿主程序集，与 `SkillVmPlugin` / `SkillVmGotoPolicy` 行为一致。

### 构建

```bash
dotnet build tools/SkillDebugger.Cli/SkillDebugger.Cli.csproj
```

### 命令摘要

| 命令 | 作用 |
| --- | --- |
| `validate <技能目录> [--extra <其他技能目录> ...]` | 校验 manifest、段可读、`allowedGotoTargets` 与 goto 白名单 |
| `step <技能目录> --action next\|goto\|finish\|return\|pause ...` | 单步推进并打印 PC / 栈 / JSON 状态 |
| `replay <技能目录> <trace.json> [--verify]` | 按 [skill-debugger-trace-schema.json](skill-debugger-trace-schema.json) 回放 |
| `run <技能目录>` | 交互式输入 `next` / `goto` / `finish` 等 |

示例：

```bash
dotnet run --project tools/SkillDebugger.Cli -- validate backend/Skills/skill-vm-demo
dotnet run --project tools/SkillDebugger.Cli -- step backend/Skills/skill-vm-demo --action next
```

跨技能 `goto` 校验时，用 `--extra` 加载目标技能目录。

## 模式 B：宿主调试 API（注入预览与暂停标志）

仅允许 **本机 loopback** 调用（与 `/api/debug/agent-stats` 等一致）。

### `GET /api/debug/skill-vm/{sessionId}`

返回 JSON（camelCase）：

- `ok`：是否成功找到会话状态
- `state`：当前 `SkillVmState`（PC、栈、变量等）
- `injectionPreview`：与对话注入一致的 **内存·注入预览** 文本块
- `estimatedTokens`：粗略估计（字符/4）
- `flags`：当前会话调试标志（见下）

无状态则 **404**，body 含 `message`。

### `POST /api/debug/skill-vm/{sessionId}/flags`

请求体：

```json
{
  "pauseBeforeInject": false,
  "pauseAfterSkillStep": true
}
```

- **`pauseAfterSkillStep`**：为 `true` 时，每次 `skill_step` 成功执行后，宿主将 `SkillVmState.paused` 置为 `true` 并持久化，便于在下一回合观察。
- **`pauseBeforeInject`**：保留标志位，供面板/流程在注入前配合轮询使用（当前不改变聊天管线）。

成功时返回 `{ "ok": true, "flags": { ... } }`。

## 可选：本地 Web 骨架

`tools/SkillDebugger.Web/index.html` 为静态页，可在浏览器中打开，填写后端基址与 `sessionId` 后点击「拉取状态」，映射 **CPU / 脚本·段 / 栈 / Memory·注入预览 / Watch·标志** 面板占位。

开发环境下宿主 CORS 通常允许 `localhost`，若打开文件协议受限，请将 HTML 置于本地 HTTP 服务下访问。

## 轨迹格式

录制与回放用的 JSON Schema 见 [skill-debugger-trace-schema.json](skill-debugger-trace-schema.json)，附录说明见 [skill-script-debugger-plan.md](skill-script-debugger-plan.md) 附录 A。
