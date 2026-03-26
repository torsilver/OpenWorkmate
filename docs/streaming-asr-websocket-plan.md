# 会议 / 语音输入：百炼实时 ASR（v1/inference）落地说明

> 本仓库已采用 **阿里云百炼实时语音识别 WebSocket**（`wss://dashscope.aliyuncs.com/api-ws/v1/inference` 或新加坡 `wss://dashscope-intl.aliyuncs.com/...`），与 **通义千问 `/api-ws/v1/realtime`** 不是同一路径、不是同一协议。

## 架构

- **Chrome 扩展**（侧栏）：麦克风 → 重采样 16 kHz PCM s16le → `WebSocket` → **本机后端** `/api/stt-stream?token=&mode=inline|meeting&meetingSessionId=…`
- **本机后端**：`ClientWebSocket` 连 DashScope，发送 `run-task` / 二进制音频 / `finish-task`；解析 `result-generated`（`sentence_end` 区分 partial / final）
- **会议模式**：句末 **final** 由后端直接写入 `MeetingTranscriptStore`（JSONL），扩展侧仅展示与下载 HTML
- **语音输入模式**：仅 **final** 文本拼入输入框

## 配置

- 设置页 **「百炼实时语音识别」** → `AppConfig.realtimeAsr`（`apiKey`、`webSocketBaseUrl`、`modelId`、`languageHints` 等）
- 默认模型 **`fun-asr-realtime`**；可选用 **`paraformer-realtime-v2`** 等（见[官方说明](https://help.aliyun.com/zh/model-studio/real-time-speech-recognition)）
- **语音识别仅此一路径**：侧栏语音、会议、`POST /api/transcribe`、**MCP_STT** 均依赖 `realtimeAsr.apiKey`；已移除 Whisper 兼容 HTTP 与设置页 STT 多模型列表

## 测试

- `POST /api/config/test-realtime-asr`：短静音 WAV 走完整 WS 闭环（需有效 Key 与网络）
- 单元：`DashScopeInferenceAsrProtocolTests`（事件解析、run-task JSON）

## 参考

- [实时语音识别-Fun-ASR/Gummy/Paraformer](https://help.aliyun.com/zh/model-studio/real-time-speech-recognition)
- [Paraformer 实时语音识别 WebSocket API](https://help.aliyun.com/zh/model-studio/developer-reference/websocket-for-paraformer-real-time-service)
