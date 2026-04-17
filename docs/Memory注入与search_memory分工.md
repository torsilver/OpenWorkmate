# Memory：注入与 `search_memory` 分工

## 现状（两条路径）

1. **自动注入（RAG 式）**  
   - 实现：`MemoryContextProvider`（MAF `MessageAIContextProvider`）  
   - 行为：按当前用户句做向量检索，将会话记忆 + 共享记忆片段注入为 **额外 system 消息**（受 `MemorySessionTopK` / `MemorySharedTopK`、`MemoryInjectionMaxChars` 等限制）。  
   - 适用：轻量、与本轮问题可能相关的背景，减少模型「忘记去查」的摩擦。

2. **工具调用**  
   - 实现：`MemoryPlugin` 的 `search_memory`、`save_memory`  
   - 行为：模型主动按 query 检索或写入；结果以工具返回进入对话轮次。  
   - 适用：需要明确检索词、或写入新记忆时。

## 建议用法

- **默认**：保持注入 + 工具并存；避免在提示词中重复堆叠同一批记忆全文。  
- **强依赖某条记忆时**：优先让模型使用 `search_memory` 精确拉取，而不是仅依赖注入摘要。  
- **调优**：若上下文拥挤，可降低 `MemorySessionTopK` / `MemorySharedTopK` 或 `MemoryInjectionMaxChars`（见 `ContextWindowConfig`），而非关闭 Embedding 能力。

## 代码位置

- 注入：`backend/Services/ContextProviders/MemoryContextProvider.cs`  
- 工具：`backend/Plugins/MemoryPlugin.cs`
