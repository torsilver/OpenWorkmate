/**
 * 与后端 VendorModelDefaults / connectionKind 一致；vendorId 与 ConfigService 持久化字段对齐。
 *
 * chat 项可选 providerChoices：对接类型下拉显示的取值列表；省略时视为 [provider] 单一协议，设置页默认隐藏主对接类型行。
 * other_auto 须列出全部可选协议供用户自选。语音识别仅百炼实时 ASR，无独立 stt 预设表。
 */
window.OFFICE_COPILOT_VENDOR_PRESETS = {
  chat: [
    { id: 'aliyun_bailian', label: '阿里巴巴 通义百炼', provider: 'OpenAI', defaultEndpoint: 'https://dashscope.aliyuncs.com/compatible-mode/v1', defaultModelId: 'qwen-turbo' },
    { id: 'zhipu_glm', label: '智谱 GLM（BigModel）', provider: 'OpenAI', defaultEndpoint: 'https://open.bigmodel.cn/api/paas/v4', defaultModelId: 'glm-4-flash' },
    { id: 'deepseek', label: 'DeepSeek', provider: 'OpenAI', defaultEndpoint: 'https://api.deepseek.com/v1', defaultModelId: 'deepseek-chat' },
    { id: 'moonshot', label: '月之暗面 Moonshot（Kimi）', provider: 'OpenAI', defaultEndpoint: 'https://api.moonshot.cn/v1', defaultModelId: 'moonshot-v1-8k' },
    { id: 'volcengine_doubao', label: '字节跳动 豆包（火山方舟）', provider: 'OpenAI', defaultEndpoint: 'https://ark.cn-beijing.volces.com/api/v3', defaultModelId: '' },
    { id: 'tencent_hunyuan', label: '腾讯 混元', provider: 'OpenAI', defaultEndpoint: '', defaultModelId: '' },
    { id: 'baidu_qianfan', label: '百度 千帆 / 文心', provider: 'OpenAI', defaultEndpoint: '', defaultModelId: '' },
    { id: 'iflytek_spark', label: '科大讯飞 星火', provider: 'OpenAI', defaultEndpoint: '', defaultModelId: '' },
    { id: 'minimax', label: 'MiniMax', provider: 'OpenAI', defaultEndpoint: 'https://api.minimax.chat/v1', defaultModelId: '' },
    { id: 'stepfun', label: '阶跃星辰 StepFun', provider: 'OpenAI', defaultEndpoint: 'https://api.stepfun.com/v1', defaultModelId: '' },
    { id: 'lingyiwanwu_yi', label: '零一万物 Yi', provider: 'OpenAI', defaultEndpoint: 'https://api.lingyiwanwu.com/v1', defaultModelId: '' },
    { id: 'siliconflow', label: '硅基流动 SiliconFlow', provider: 'OpenAI', defaultEndpoint: 'https://api.siliconflow.cn/v1', defaultModelId: '' },
    { id: 'huawei_maas', label: '华为云 MaaS', provider: 'OpenAI', defaultEndpoint: '', defaultModelId: '' },
    { id: 'kunlun_skywork', label: '天工 / 昆仑万维 Skywork', provider: 'OpenAI', defaultEndpoint: '', defaultModelId: '' },
    { id: 'openai', label: 'OpenAI', provider: 'OpenAI', defaultEndpoint: 'https://api.openai.com/v1', defaultModelId: 'gpt-4o-mini' },
    { id: 'azure_openai', label: '微软 Azure OpenAI', provider: 'Azure', defaultEndpoint: 'https://YOUR_RESOURCE.openai.azure.com/', defaultModelId: 'gpt-4o' },
    { id: 'google_gemini', label: 'Google Gemini（OpenAI 适配地址）', provider: 'OpenAI', defaultEndpoint: '', defaultModelId: 'gemini-2.0-flash' },
    { id: 'anthropic', label: 'Anthropic Claude（OpenAI 形态网关）', provider: 'Anthropic', providerChoices: ['Anthropic', 'OpenAI'], defaultEndpoint: '', defaultModelId: '' },
    { id: 'aws_bedrock', label: 'Amazon Bedrock（后续专用适配）', provider: 'OpenAI', defaultEndpoint: '', defaultModelId: '' },
    { id: 'groq', label: 'Groq', provider: 'OpenAI', defaultEndpoint: 'https://api.groq.com/openai/v1', defaultModelId: 'llama-3.1-8b-instant' },
    { id: 'mistral', label: 'Mistral AI', provider: 'OpenAI', defaultEndpoint: 'https://api.mistral.ai/v1', defaultModelId: 'mistral-small-latest' },
    { id: 'cohere', label: 'Cohere', provider: 'OpenAI', defaultEndpoint: 'https://api.cohere.ai/v1', defaultModelId: '' },
    { id: 'together', label: 'Together AI', provider: 'OpenAI', defaultEndpoint: 'https://api.together.xyz/v1', defaultModelId: '' },
    { id: 'fireworks', label: 'Fireworks', provider: 'OpenAI', defaultEndpoint: 'https://api.fireworks.ai/inference/v1', defaultModelId: '' },
    { id: 'openrouter', label: 'OpenRouter', provider: 'OpenAI', defaultEndpoint: 'https://openrouter.ai/api/v1', defaultModelId: '' },
    { id: 'perplexity', label: 'Perplexity', provider: 'OpenAI', defaultEndpoint: 'https://api.perplexity.ai', defaultModelId: '' },
    { id: 'xai', label: 'xAI（Grok）', provider: 'OpenAI', defaultEndpoint: 'https://api.x.ai/v1', defaultModelId: '' },
    { id: 'huggingface', label: 'Hugging Face Inference', provider: 'OpenAI', defaultEndpoint: '', defaultModelId: '' },
    { id: 'nvidia_nim', label: 'NVIDIA NIM', provider: 'OpenAI', defaultEndpoint: '', defaultModelId: '' },
    { id: 'ollama', label: 'Ollama（本地）', provider: 'Ollama', defaultEndpoint: 'http://localhost:11434/v1', defaultModelId: 'llama3' },
    { id: 'lm_studio', label: 'LM Studio（本地）', provider: 'OpenAI', defaultEndpoint: 'http://localhost:1234/v1', defaultModelId: '' },
    { id: 'other_auto', label: '其他（自行填写地址）', provider: 'OpenAI', providerChoices: ['OpenAI', 'Azure', 'Ollama', 'Anthropic'], defaultEndpoint: '', defaultModelId: '' }
  ],
  ocr: [
    { id: 'other_auto', label: '其他（根据网址自动识别）', connectionKind: '', defaultEndpoint: '', defaultModelId: '' },
    { id: 'openai', label: 'OpenAI', connectionKind: 'openai_compatible_multipart', defaultEndpoint: 'https://api.openai.com/v1', defaultModelId: '' },
    { id: 'azure_openai', label: '微软 Azure OpenAI', connectionKind: 'openai_compatible_multipart', defaultEndpoint: '', defaultModelId: '' },
    { id: 'aliyun_bailian', label: '阿里巴巴 通义百炼', connectionKind: 'dashscope_openai_chat_image', defaultEndpoint: 'https://dashscope.aliyuncs.com/compatible-mode/v1', defaultModelId: 'qwen-vl-ocr-latest' }
  ]
};
