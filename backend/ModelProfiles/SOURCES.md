# 模型能力摘录来源

`vendor/model_prices_excerpt.json` 中的条目剪枝自 **LiteLLM** 仓库的公开文件：

- 上游文件：<https://github.com/BerriAI/litellm/blob/main/model_prices_and_context_window.json>
- 固定参考提交（pin，便于 diff / bump）：`e9e86ed956ba`（`main` 上某次 `HEAD`，更新时请重新 `curl` 上游并核对键名）

## 本仓库收录的 LiteLLM 键

| profileKey（与 LiteLLM 顶层键一致） | 说明 |
|--------------------------------------|------|
| `moonshot/kimi-k2.6` | Kimi K2.6（Moonshot OpenAI 兼容） |
| `dashscope/qwen3-max` | 阿里百炼 DashScope Qwen3-Max；LiteLLM 无独立 `qwen3.6` 键时以此为代表，实际 `AiModelEntry.modelId` 仍以控制台为准 |

## 如何 bump 上游摘录

1. 打开 LiteLLM 仓库上述 JSON，搜索对应键，复制整段对象到 `vendor/model_prices_excerpt.json`。
2. 更新本文件中的 commit pin。
3. 运行 `dotnet test --filter ModelProfileRegistry` 与相关单测。
