using Microsoft.Extensions.AI;

namespace OpenWorkmate.Server.Services;

/// <summary>
/// 主会话本轮流式请求的 system 首条拼装：持久 system + 客户端身份/页上下文/动态工具说明（由调用方拼入 <paramref name="identitySuffix"/>）+
/// 可选联网抑制 + 固定指令块（意图/工具参数/工具结果复述）。
/// </summary>
public static class SystemPromptBuilder
{
    /// <summary>注入：最新用户意图优先；可验证事实须本轮用工具刷新（与 Memory/AccurateData 边界见文内）。</summary>
    public const string LatestIntentAndGroundedFactsInstruction =
        "[意图优先级] 当用户在后续消息中纠正、细化或推翻先前要求时，以最近一条用户消息中的意图为准；"
        + "若与早前 assistant 总结或旧结论冲突，以最新用户表述为准。"
        + "未冲突的上下文约束（如已约定的路径、端侧、任务目标）仍应保留。"
        + "\n\n[事实与可验证状态] 关于会变化或需实勘的信息"
        + "（本机文件/目录列表与是否存在、run_command 能反映的本机状态、当前网页或当前文档的实时内容等），"
        + "禁止仅凭对话历史或旧 assistant 回复断言；须在本轮通过相应工具"
        + "（如 run_command、File/浏览器/Office 侧读取工具等）获取结果后再作答。"
        + "用户明确要求「再看一下」「重新确认」「现在有什么」等时，必须先执行能反映真实状态的查询。"
        + "通过 Memory、AccurateData 等工具写入或检索的记忆与结构化数据仍可使用，但不可替代「当前目录列表」等须当场核实的状态。";

    /// <summary>注入：tool_calls 的 arguments 键名须与工具 JSON Schema 一致。</summary>
    public const string ToolCallArgumentsSchemaInstruction =
        "[工具调用参数] 发起 tool_calls 时，function.arguments 解析后的 JSON 键名必须与该工具 OpenAPI/JSON Schema 中 properties 所列字段名完全一致（含大小写）；"
        + "禁止使用 schema 中未声明的键名去替代已声明字段（例如 schema 要求 filePath 时不要写 path 或 target）。"
        + "schema 标为 required 的字段不得省略；各字段值类型须与 schema 一致。";

    /// <summary>注入：用户界面看不到工具原始返回全文，模型必须在最终回复中整理复述。</summary>
    public const string ToolResultEchoSystemInstruction =
        "[工具与回答方式] 用户对话界面中看不到工具的原始返回全文（执行过程里可能仅有简短摘要）。"
        + "在调用工具前，可用一句简短说明本轮目标（便于用户理解你的意图）。"
        + "凡你调用了工具并从工具结果中获得了对用户有用的文字或数据，在本轮最终回复里必须用自然语言完整整理并复述给用户；"
        + "禁止仅用「已读取」「已完成」等占位描述而不给出实质内容。";

    /// <summary>
    /// 仅当当前模型在设置中开启百炼 <c>enable_search</c> 时注入：抑制笨重「开浏览器抠 SERP」路径。
    /// </summary>
    public const string EnableSearchSuppressionInstruction =
        "[联网检索] 当前对话模型已在设置中开启百炼「联网搜索」（enable_search）。"
        + "用户只要网络资讯、新闻、实时事实或「去网上查/搜一下」类需求时，优先直接作答，由服务端检索能力提供时效信息；"
        + "不要轻易使用 run_command 打开默认浏览器、run_custom_javascript_in_page（如 window.open 搜索页）或依赖 page_agent / run_custom_javascript_in_page 反复抓取搜索结果页来替代检索。"
        + "例外：用户明确要求操作其正在浏览的**当前网页**（高亮、读可见内容、截图等）或内置检索明显不足以完成该任务时，再用 Browser 等工具。";

    /// <summary>
    /// 在已完成「持久 system ± identity」后的文本上，追加可选联网抑制与三段固定指令。
    /// </summary>
    public static string BuildAugmentedSystemTextForStreaming(
        string systemTextAfterIdentityStep,
        string? enableSearchSuppressionSuffix)
    {
        var augmented = systemTextAfterIdentityStep ?? "";
        if (!string.IsNullOrEmpty(enableSearchSuppressionSuffix))
            augmented += "\n\n" + enableSearchSuppressionSuffix;
        augmented += "\n\n" + LatestIntentAndGroundedFactsInstruction + "\n\n" + ToolCallArgumentsSchemaInstruction + "\n\n" + ToolResultEchoSystemInstruction;
        return augmented;
    }

    /// <summary>
    /// 构建本轮流式请求用的消息列表：可选追加 client 身份后缀；再追加意图/事实约束、工具参数名约束与工具结果复述约束。
    /// </summary>
    public static List<ChatMessage> BuildHistoryForStreamingTurn(
        List<ChatMessage> stateHistory,
        string? identitySuffix,
        string? enableSearchSuppressionSuffix = null)
    {
        var historyToUse = stateHistory;
        if (!string.IsNullOrEmpty(identitySuffix) && stateHistory.Count > 0 && stateHistory[0].Role == ChatRole.System)
        {
            var sysMsg = stateHistory[0];
            var newSystemText = (sysMsg.Text ?? "") + "\n\n" + identitySuffix;
            historyToUse = new List<ChatMessage>(stateHistory.Count) { new(ChatRole.System, newSystemText) };
            for (var i = 1; i < stateHistory.Count; i++)
                historyToUse.Add(stateHistory[i]);
        }

        if (historyToUse.Count > 0 && historyToUse[0].Role == ChatRole.System)
        {
            var sys = historyToUse[0].Text ?? "";
            var augmented = BuildAugmentedSystemTextForStreaming(sys, enableSearchSuppressionSuffix);
            var withEcho = new List<ChatMessage>(historyToUse.Count) { new(ChatRole.System, augmented) };
            for (var i = 1; i < historyToUse.Count; i++)
                withEcho.Add(historyToUse[i]);
            historyToUse = withEcho;
        }

        return historyToUse;
    }

    /// <summary>按 clientType 返回本端 Agent 身份说明，用于追加到 system 提示。</summary>
    public static string GetClientTypeIdentitySuffix(string? clientType)
    {
        var ct = (clientType ?? "").Trim();
        if (string.IsNullOrEmpty(ct)) return "";
        if (string.Equals(ct, "chrome", StringComparison.OrdinalIgnoreCase))
            return "你是浏览器侧边栏助手：可对「本机路径上的」Word/Excel/PPT 使用 Word/Excel/Ppt 插件读写（与任务窗格互补；本端无 CurrentDocument 当前文档 API，须用文件路径调用文档工具）。另支持网页整页截图、页内基座 page_agent、自定义页内脚本 run_custom_javascript_in_page、页内高亮与便签、附件与文件工具、命令行等。用户已提供 docx/xlsx 等路径时，应直接用相应文档工具完成编辑与批注，不得以「浏览器端不能改 Word」为由拒绝或要求用户必须切到任务窗格。"
                + " page_agent 用于操作当前活动标签页（observe 得可交互 ref，再 click/fill/waitFor/scrollIntoView）；observe 的短标签**不是**文章正文。要总结/摘录当前页长文正文须用 run_custom_javascript_in_page（短脚本 return），勿仅靠反复 observe。"
                + " 总结外站网页长对话等：需要正文时用 run_custom 抽取；若要先展开/滚到底再读，可先用 page_agent 点对应 ref 或 run_custom 内滚动后再 return。";
        if (string.Equals(ct, "office-word", StringComparison.OrdinalIgnoreCase))
            return "你是 Word 侧助手，负责当前打开的 Word 文档；网页相关操作请由用户在浏览器侧边栏端完成。你只负责本端能力；若需求属于另一客户端，请说明并建议用户切换。";
        if (string.Equals(ct, "office-excel", StringComparison.OrdinalIgnoreCase))
            return "你是 Excel 侧助手，负责当前打开的 Excel 工作簿；网页相关操作请由用户在浏览器侧边栏端完成。你只负责本端能力；若需求属于另一客户端，请说明并建议用户切换。";
        if (string.Equals(ct, "wps", StringComparison.OrdinalIgnoreCase))
            return "你是 WPS 侧助手：通过 CurrentDocument 工具操作**当前活动**的 WPS 文字/表格/演示（以用户前台打开的窗口为准）；本端**没有**浏览器页内脚本与活动标签页能力，读网页、高亮页内内容请用户在 Chrome 侧边栏完成。"
                + " 工具选择顺序（与浏览器端「先内置 scriptId、再兜底」同理）：① 能完成任务时**优先**使用 CurrentDocument **具名工具**——表格用 current_excel_get_used_range、current_excel_read_range、current_excel_write_range、current_excel_list_sheets、current_excel_read_formulas、current_excel_write_formulas；文字用 current_word_*；演示用 current_ppt_*。② 确需脚本时**先** current_run_document_script（白名单 scriptId，须在前端注册表存在）。③ **最后**才用 current_run_custom_document_script；禁止把任意脚本当作首选。"
                + " current_excel_write_range：参数 data 必须是 **JSON 二维数组**（如 [[\"姓名\",\"部门\"],[\"张三\",\"技术部\"]]），不要用单个长字符串代替整张表；address 为左上角时应与数组行列一致，或写完整区域如 A1:E5。"
                + " WPS 宿主**不是** Office.js：不要使用 Excel 等 Office Add-in 全局；应使用 WPS 注入的 Application/文档对象模型（与任务窗格 RPC 实现一致）。"
                + " 若需求属于另一客户端，请说明并建议用户切换。";
        if (string.Equals(ct, "office-powerpoint", StringComparison.OrdinalIgnoreCase))
            return "你是 PowerPoint 侧助手，负责当前打开的 PowerPoint 演示文稿；网页相关操作请由用户在浏览器侧边栏端完成。你只负责本端能力；若需求属于另一客户端，请说明并建议用户切换。";
        return "";
    }
}
