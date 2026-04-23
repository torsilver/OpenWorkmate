/**
 * 与后端 VendorModelDefaults / connectionKind 一致；vendorId 与 ConfigService 持久化字段对齐。
 *
 * 产品仅开放：通义百炼（Qwen3.6）、月之暗面 Kimi（K2.6）。chat 项无 providerChoices 时主对接类型行由 JS 收在「高级」。
 * 语音识别仅百炼实时 ASR，无独立 stt 预设表。
 */
window.OFFICE_COPILOT_VENDOR_PRESETS = {
  chat: [
    { id: 'aliyun_bailian', label: '阿里巴巴 通义百炼（Qwen3.6）', provider: 'OpenAI', defaultEndpoint: 'https://dashscope.aliyuncs.com/compatible-mode/v1', defaultModelId: 'qwen3.6-plus' },
    { id: 'moonshot', label: '月之暗面 Kimi（K2.6）', provider: 'OpenAI', defaultEndpoint: 'https://api.moonshot.cn/v1', defaultModelId: 'kimi-k2-6' }
  ],
  ocr: [
    { id: 'other_auto', label: '其他（按网址自动识别）', connectionKind: '', defaultEndpoint: '', defaultModelId: '' },
    { id: 'aliyun_bailian', label: '阿里巴巴 通义百炼', connectionKind: 'dashscope_openai_chat_image', defaultEndpoint: 'https://dashscope.aliyuncs.com/compatible-mode/v1', defaultModelId: 'qwen-vl-ocr-latest' }
  ]
};
