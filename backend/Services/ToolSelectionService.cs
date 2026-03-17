using System.Collections.Frozen;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OfficeCopilot.Server;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 按需工具选择：两阶段均由主模型完成。启用两阶段时：阶段一主模型根据对话内容+分类信息选子类（无 tools）；阶段二仅将选中子类下的具体工具交给主模型一次对话并调用。未启用时可按插件名由主模型筛选。已完全移除嵌入式/本地小模型。
/// </summary>
public sealed class ToolSelectionService : IToolSelector
{
    private readonly ConfigService _configService;
    private readonly IKernelAccessor _kernelAccessor;
    private readonly ILogger<ToolSelectionService> _logger;

    private const string FallbackAllKeyword = "全部";

    /// <summary>插件名 -> 简短描述，用于 LLM 工具选择的 system prompt。</summary>
    private static readonly FrozenDictionary<string, string> PluginDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CLI"] = "执行系统命令、终端",
        ["Excel"] = "读写 Excel 表格",
        ["Word"] = "读写 Word 文档",
        ["Ppt"] = "读写 PPT 演示文稿",
        ["Browser"] = "网页截图、高亮、页面脚本",
        ["File"] = "附件路径、文件大小、保存截图到下载",
        ["Tavily"] = "网页搜索、查资料",
        ["ClawhubSkill"] = "运行 Clawhub 技能脚本",
        ["CurrentDocument"] = "当前打开的 Word/Excel/PPT 文档（任务窗格连接时）：插入/读正文、选区、表格、查找替换、Excel 区域/公式/工作表、PPT 幻灯片、预定义脚本",
        ["Context"] = "对话上下文管理：主动压缩对话以释放上下文",
        ["Subagent"] = "同会话内子代理：将多步或耗上下文的子任务交给子代理执行，仅收回最终总结",
        ["System"] = "当前日期与时间，用于回答今天几号、现在几点等时间相关问题",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>子类 id -> 描述；内置子类（不含动态 技能/外部）。</summary>
    private static readonly FrozenDictionary<string, string> SubcategoryDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Excel-获取信息"] = "列出/读取工作表、区域、命名区域、验证、条件格式、图表",
        ["Excel-编辑内容"] = "写区域、写公式、定义命名区域、超链接",
        ["Excel-修改样式"] = "合并/取消合并、列宽行高、数据验证、条件格式",
        ["Excel-结构"] = "增删工作表",
        ["Word-获取信息"] = "正文、表格、批注、XML、页眉页脚、书签、图片、节",
        ["Word-编辑内容"] = "创建文档、查找替换、批注、页眉页脚、书签、图片、超链接",
        ["Word-修改样式"] = "段落格式、文字格式",
        ["Browser-截图与页面"] = "整页截图、页面脚本",
        ["Browser-高亮与笔记"] = "高亮、浮动笔记",
        ["File"] = "附件路径解析、文件大小查询、截图保存到下载",
        ["CLI"] = "执行 CMD 命令",
        ["Tavily-搜索"] = "网页搜索",
        ["Tavily-提取"] = "URL 内容提取",
        ["ClawhubSkill"] = "运行 Clawhub 技能脚本",
        ["技能"] = "用户技能与 Clawhub 脚本",
        ["外部"] = "MCP 工具",
        ["CurrentDocument-Word"] = "当前文档 Word（任务窗格）：读正文/选区、插段落/表格、查找替换",
        ["CurrentDocument-Excel"] = "当前文档 Excel（任务窗格）：读/写区域、列工作表、UsedRange、读/写公式",
        ["CurrentDocument-Ppt"] = "当前文档 PPT（任务窗格）：列幻灯片、读/写指定幻灯片、插入/删除幻灯片",
        ["Ppt-获取信息"] = "列出幻灯片、读取指定幻灯片文本",
        ["Ppt-编辑内容"] = "写幻灯片标题/正文、插入新幻灯片、删除幻灯片",
        ["CrossAgentTask"] = "跨端派发任务：让 Word/Chrome/Excel/WPS/PowerPoint 端的 Agent 执行某任务；或标记本端已完成的任务",
        ["Context"] = "对话上下文管理：在换任务或已总结完后主动压缩对话以释放上下文",
        ["Subagent"] = "同会话内子代理：将多步或会产出大量中间结果的任务交给子代理，仅收回最终总结",
        ["AccurateData"] = "准确数据临时存盘：保存结构化数据到磁盘以减少上下文占用，需要时按 id 精确取回",
        ["System"] = "当前日期与时间：回答用户问今天几号、现在几点、本周/本月等时间相关问题时使用",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>子类 id -> (插件名, 函数名) 列表；仅内置固定子类，技能/外部由 Kernel 动态收集。</summary>
    private static readonly FrozenDictionary<string, List<(string Plugin, string Function)>> SubcategoryToFunctions = BuildSubcategoryToFunctions();

    private static FrozenDictionary<string, List<(string Plugin, string Function)>> BuildSubcategoryToFunctions()
    {
        var d = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Excel-获取信息"] = new List<(string, string)>
            {
                ("Excel", "excel_sheets_list"), ("Excel", "excel_range_read"), ("Excel", "excel_named_ranges_list"),
                ("Excel", "excel_named_range_read"), ("Excel", "excel_validations_list"), ("Excel", "excel_conditional_formats_list"), ("Excel", "excel_charts_list")
            },
            ["Excel-编辑内容"] = new List<(string, string)> { ("Excel", "excel_range_write"), ("Excel", "excel_formula_write"), ("Excel", "excel_named_range_define"), ("Excel", "excel_hyperlink_set") },
            ["Excel-修改样式"] = new List<(string, string)>
            {
                ("Excel", "excel_cells_merge"), ("Excel", "excel_cells_unmerge"), ("Excel", "excel_column_width_set"), ("Excel", "excel_row_height_set"),
                ("Excel", "excel_validation_set"), ("Excel", "excel_validation_clear"), ("Excel", "excel_conditional_format_add"), ("Excel", "excel_conditional_format_clear")
            },
            ["Excel-结构"] = new List<(string, string)> { ("Excel", "excel_sheet_add"), ("Excel", "excel_sheet_remove") },
            ["Word-获取信息"] = new List<(string, string)>
            {
                ("Word", "word_body_read"), ("Word", "word_tables_list"), ("Word", "word_tables_read"), ("Word", "word_comments_list"), ("Word", "word_comments_read"),
                ("Word", "word_part_xml_read"), ("Word", "word_headers_footers_list"), ("Word", "word_header_read"), ("Word", "word_footer_read"),
                ("Word", "word_bookmarks_list"), ("Word", "word_bookmark_read"), ("Word", "word_images_list"), ("Word", "word_sections_list")
            },
            ["Word-编辑内容"] = new List<(string, string)>
            {
                ("Word", "word_document_create"), ("Word", "word_find_replace"), ("Word", "word_comment_add"), ("Word", "word_comments_delete"),
                ("Word", "word_header_write"), ("Word", "word_footer_write"), ("Word", "word_bookmark_insert"), ("Word", "word_image_insert"), ("Word", "word_hyperlink_insert")
            },
            ["Word-修改样式"] = new List<(string, string)> { ("Word", "word_paragraphs_format"), ("Word", "word_text_format") },
            ["Browser-截图与页面"] = new List<(string, string)> { ("Browser", "capture_full_page"), ("Browser", "run_page_script") },
            ["Browser-高亮与笔记"] = new List<(string, string)> { ("Browser", "highlight_webpage_text"), ("Browser", "add_floating_note") },
            ["File"] = new List<(string, string)> { ("File", "get_attachment_path"), ("File", "get_file_size"), ("File", "save_screenshot_to_downloads") },
            ["CLI"] = new List<(string, string)> { ("CLI", "run_command") },
            ["Tavily-搜索"] = new List<(string, string)> { ("Tavily", "tavily_search") },
            ["Tavily-提取"] = new List<(string, string)> { ("Tavily", "tavily_extract") },
            ["ClawhubSkill"] = new List<(string, string)> { ("ClawhubSkill", "run_clawhub_script") },
            ["CurrentDocument-Word"] = new List<(string, string)>
            {
                ("CurrentDocument", "current_word_insert_text"), ("CurrentDocument", "current_word_read_body"),
                ("CurrentDocument", "current_word_read_selection"), ("CurrentDocument", "current_word_insert_table"),
                ("CurrentDocument", "current_word_search_replace")
            },
            ["CurrentDocument-Excel"] = new List<(string, string)>
            {
                ("CurrentDocument", "current_excel_read_range"), ("CurrentDocument", "current_excel_write_range"),
                ("CurrentDocument", "current_excel_list_sheets"), ("CurrentDocument", "current_excel_get_used_range"),
                ("CurrentDocument", "current_excel_read_formulas"), ("CurrentDocument", "current_excel_write_formulas")
            },
            ["CurrentDocument-Ppt"] = new List<(string, string)>
            {
                ("CurrentDocument", "current_ppt_slides_list"), ("CurrentDocument", "current_ppt_slide_read"),
                ("CurrentDocument", "current_ppt_slide_write"), ("CurrentDocument", "current_ppt_slide_insert"), ("CurrentDocument", "current_ppt_slide_delete")
            },
            ["Ppt-获取信息"] = new List<(string, string)> { ("Ppt", "ppt_slides_list"), ("Ppt", "ppt_slide_read") },
            ["Ppt-编辑内容"] = new List<(string, string)> { ("Ppt", "ppt_slide_write"), ("Ppt", "ppt_slide_insert"), ("Ppt", "ppt_slide_delete") },
            ["CrossAgentTask"] = new List<(string, string)> { ("CrossAgentTask", "create_cross_agent_task"), ("CrossAgentTask", "complete_cross_agent_task") },
            ["Context"] = new List<(string, string)> { ("Context", "compact_conversation") },
            ["Subagent"] = new List<(string, string)> { ("Subagent", "run_subtask") },
            ["AccurateData"] = new List<(string, string)> { ("AccurateData", "accurate_data_write"), ("AccurateData", "accurate_data_read"), ("AccurateData", "accurate_data_list"), ("AccurateData", "accurate_data_delete") },
            ["System"] = new List<(string, string)> { ("System", "get_current_time") },
        };
        return d.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public ToolSelectionService(ConfigService configService, IKernelAccessor kernelAccessor, ILogger<ToolSelectionService> logger)
    {
        _configService = configService;
        _kernelAccessor = kernelAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> SelectPluginNamesAsync(
        string userMessage,
        ChatHistory? recentHistory,
        IReadOnlyList<string> availablePluginNames,
        CancellationToken ct = default)
    {
        if (availablePluginNames == null || availablePluginNames.Count == 0)
            return Array.Empty<string>();

        var ai = _configService.Current.AI;
        return await SelectByLlmAsync(userMessage, recentHistory, availablePluginNames, ai, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string PluginName, string FunctionName)>?> SelectFunctionsAsync(
        string userMessage,
        ChatHistory? recentHistory,
        Kernel kernel,
        CancellationToken ct = default)
    {
        if (kernel == null)
        {
            _logger.LogDebug("ToolSelection two-stage: Kernel null, return null (use all tools).");
            return null;
        }

        var ai = _configService.Current.AI;
        var allKernelFunctions = GetAllFunctionsFromKernel(kernel);
        if (allKernelFunctions.Count == 0)
        {
            _logger.LogDebug("ToolSelection two-stage: no functions in kernel, return null.");
            return null;
        }

        var subcategories = BuildSubcategoryListFromKernel(kernel, allKernelFunctions);
        if (subcategories.Count == 0)
        {
            _logger.LogDebug("ToolSelection two-stage: no subcategories, return null (use all tools).");
            return null;
        }

        var userPrompt = BuildUserPromptWithHistory(userMessage, recentHistory);

        // 一阶段：选子类
        var selectedSubcategoryIds = await SelectSubcategoriesWithMainModelAsync(kernel, subcategories, userPrompt, ct).ConfigureAwait(false);
        if (selectedSubcategoryIds == null)
        {
            _logger.LogDebug("ToolSelection two-stage stage1: 全部 or fallback, return null (use all tools).");
            return null;
        }

        var candidateFunctions = GetCandidateFunctionsFromSubcategoryIds(kernel, selectedSubcategoryIds, allKernelFunctions);
        if (candidateFunctions.Count == 0)
        {
            _logger.LogDebug("ToolSelection two-stage: no candidate functions after stage1, return null.");
            return null;
        }

        _logger.LogDebug("ToolSelection two-stage stage1 selected {SubCount} subcategories, {FuncCount} candidate functions (stage2: use these as tool set, no second LLM).", selectedSubcategoryIds.Count, candidateFunctions.Count);

        // 二阶段：不再单独调 LLM 选函数，直接使用选中子类下的全部函数作为本轮工具集，由主模型在对话轮中按需调用
        var merged = MergeFunctionsWithAlwaysInclude(candidateFunctions, ai, allKernelFunctions);
        _logger.LogDebug("ToolSelection two-stage result: {Count} functions.", merged.Count);
        return merged;
    }

    /// <summary>从 Kernel 收集所有 (PluginName, FunctionName)，用于子类过滤与描述。</summary>
    private static List<(string Plugin, string Function)> GetAllFunctionsFromKernel(Kernel kernel)
    {
        var list = new List<(string, string)>();
        foreach (var plugin in kernel.Plugins)
        {
            foreach (KernelFunction func in plugin)
                list.Add((plugin.Name, func.Name));
        }
        return list;
    }

    /// <summary>构建一阶段可选的子类列表（仅包含在 Kernel 中至少有一个函数的子类）；含动态 技能/外部；自动为未映射的插件生成子类。</summary>
    private static List<(string Id, string Description)> BuildSubcategoryListFromKernel(Kernel kernel, List<(string Plugin, string Function)> allFunctions)
    {
        var presentSet = new HashSet<(string, string)>(allFunctions, new PluginFunctionComparer());
        var result = new List<(string, string)>();
        var coveredPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in SubcategoryToFunctions)
        {
            var hasAny = kv.Value.Any(pf => presentSet.Contains((pf.Plugin, pf.Function)));
            if (hasAny && SubcategoryDescriptions.TryGetValue(kv.Key, out var desc))
            {
                result.Add((kv.Key, desc));
                foreach (var pf in kv.Value)
                    coveredPlugins.Add(pf.Plugin);
            }
        }

        var skillPlugins = kernel.Plugins.Where(p => string.Equals(p.Name, "ClawhubSkill", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("UserSkill_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (skillPlugins.Count > 0)
        {
            if (SubcategoryDescriptions.TryGetValue("技能", out var skillDesc))
                result.Add(("技能", skillDesc));
            foreach (var p in skillPlugins)
                coveredPlugins.Add(p.Name);
        }

        var mcpPlugins = kernel.Plugins.Where(p => p.Name.StartsWith("MCP_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (mcpPlugins.Count > 0)
        {
            if (SubcategoryDescriptions.TryGetValue("外部", out var extDesc))
                result.Add(("外部", extDesc));
            foreach (var p in mcpPlugins)
                coveredPlugins.Add(p.Name);
        }

        // Auto-discover plugins not covered by hardcoded mappings
        foreach (var plugin in kernel.Plugins)
        {
            if (coveredPlugins.Contains(plugin.Name)) continue;
            var funcCount = plugin.Count();
            if (funcCount == 0) continue;
            var autoId = $"Auto_{plugin.Name}";
            var firstFunc = plugin.FirstOrDefault();
            var descHint = firstFunc?.Description;
            var autoDesc = !string.IsNullOrWhiteSpace(descHint)
                ? $"{plugin.Name}: {descHint}"
                : $"{plugin.Name} 插件（{funcCount} 个工具）";
            result.Add((autoId, autoDesc));
        }

        return result;
    }

    private sealed class PluginFunctionComparer : IEqualityComparer<(string Plugin, string Function)>
    {
        public bool Equals((string Plugin, string Function) x, (string Plugin, string Function) y) =>
            string.Equals(x.Plugin, y.Plugin, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Function, y.Function, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Plugin, string Function) obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Plugin) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Function);
    }

    /// <summary>一阶段选子类：主模型输出子类 id 或「全部」（无 tools，轻量调用）。</summary>
    private async Task<List<string>?> SelectSubcategoriesWithMainModelAsync(Kernel kernel, List<(string Id, string Description)> subcategories, string userPrompt, CancellationToken ct)
    {
        var systemPrompt = BuildStage1SystemPrompt(subcategories);
        var chat = new ChatHistory(systemPrompt);
        chat.AddUserMessage(userPrompt);

        var serviceId = _kernelAccessor.ActiveModelId;
        if (string.IsNullOrEmpty(serviceId)) serviceId = null;
        var chatService = serviceId != null ? kernel.Services.GetKeyedService<IChatCompletionService>(serviceId) : null;
        chatService ??= kernel.Services.GetService<IChatCompletionService>();
        if (chatService == null)
        {
            _logger.LogDebug("ToolSelection stage1: No chat service for main model.");
            return null;
        }

        var userPreview = userPrompt.Length > 300 ? userPrompt[..300] + "..." : userPrompt;
        _logger.LogDebug("ToolSelection stage1 request serviceId={ServiceId} subcategoriesCount={Count} userPreview={UserPreview}", serviceId ?? "(default)", subcategories.Count, userPreview);

        try
        {
            var settings = new OpenAIPromptExecutionSettings { MaxTokens = 256, Temperature = 0.1f };
            var responseText = new System.Text.StringBuilder();
            await foreach (var msg in chatService.GetStreamingChatMessageContentsAsync(chat, settings, kernel, ct).ConfigureAwait(false))
            {
                if (msg.Content is { Length: > 0 } content)
                    responseText.Append(content);
            }
            var raw = responseText.ToString().Trim();
            _logger.LogDebug("ToolSelection stage1 response serviceId={ServiceId} raw={Raw}", serviceId ?? "(default)", raw);
            if (string.IsNullOrEmpty(raw)) return null;

            if (raw.Contains(FallbackAllKeyword, StringComparison.OrdinalIgnoreCase) && raw.Length < 20)
            {
                _logger.LogDebug("ToolSelection stage1 returned 全部, use all tools.");
                return null;
            }

            var ids = ParseSubcategoryIdsFromResponse(raw, subcategories);
            if (ids.Count > 0)
            {
                _logger.LogDebug("ToolSelection stage1 selected subcategories: {Ids}", string.Join(", ", ids));
                return ids;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ToolSelection stage1 failed.");
        }

        _logger.LogDebug("ToolSelection stage1 fallback exhausted, treat as 全部.");
        return null;
    }

    private static string BuildStage1SystemPrompt(List<(string Id, string Description)> subcategories)
    {
        var lines = new List<string>
        {
            "根据用户消息，从下列子类中选出会用到的，只输出子类id，多个用英文逗号分隔。不要输出序号、步骤或解释。",
            "尽量只输出会用到的子类，不要输出「全部」；只有完全无法判断时才输出：全部。",
            "子类列表："
        };
        foreach (var (id, desc) in subcategories)
            lines.Add($"- {id}: {desc}");
        lines.Add("示例：读Excel某区域→Excel-获取信息。搜索并写Word→Tavily-搜索, Word-编辑内容。总结当前页面并生成excel放到下载→Browser-截图与页面, Excel-编辑内容, File。改当前 Word 选中文字、在文档末尾加表格→CurrentDocument-Word。读当前 Excel 某表、写公式→CurrentDocument-Excel。读 PPT 或当前演示文稿幻灯片→Ppt-获取信息 或 CurrentDocument-Ppt；写/插/删 PPT 幻灯片→Ppt-编辑内容 或 CurrentDocument-Ppt。用户问今天几号、现在几点→System。用户附带图片要提取文字或判断文件大小→File, MCP_OCR 或 外部。");
        return string.Join("\n", lines);
    }

    private static List<string> ParseSubcategoryIdsFromResponse(string raw, List<(string Id, string Description)> subcategories)
    {
        var idSet = new HashSet<string>(subcategories.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var parts = raw.Split(new[] { ',', '\n', '\r', ';', '、' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var id = part.Trim().Trim('"', '\'', '`', ' ').Trim();
            if (string.IsNullOrEmpty(id)) continue;
            if (idSet.Contains(id))
                result.Add(id);
        }
        return result;
    }

    /// <summary>根据一阶段选中的子类 id 得到候选函数列表（与 Kernel 取交集）。</summary>
    private static List<(string Plugin, string Function)> GetCandidateFunctionsFromSubcategoryIds(Kernel kernel, List<string> subcategoryIds, List<(string Plugin, string Function)> allKernelFunctions)
    {
        var presentSet = new HashSet<(string, string)>(allKernelFunctions, new PluginFunctionComparer());
        var result = new HashSet<(string, string)>(new PluginFunctionComparer());

        foreach (var id in subcategoryIds)
        {
            if (SubcategoryToFunctions.TryGetValue(id, out var list))
            {
                foreach (var pf in list)
                {
                    if (presentSet.Contains((pf.Plugin, pf.Function)))
                        result.Add((pf.Plugin, pf.Function));
                }
                continue;
            }

            if (string.Equals(id, "技能", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var plugin in kernel.Plugins)
                {
                    if (!string.Equals(plugin.Name, "ClawhubSkill", StringComparison.OrdinalIgnoreCase) && !plugin.Name.StartsWith("UserSkill_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (KernelFunction f in plugin)
                        result.Add((plugin.Name, f.Name));
                }
                continue;
            }

            if (string.Equals(id, "外部", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var plugin in kernel.Plugins)
                {
                    if (!plugin.Name.StartsWith("MCP_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (KernelFunction f in plugin)
                        result.Add((plugin.Name, f.Name));
                }
                continue;
            }

            // Auto-discovered plugins
            if (id.StartsWith("Auto_", StringComparison.OrdinalIgnoreCase))
            {
                var pluginName = id["Auto_".Length..];
                foreach (var plugin in kernel.Plugins)
                {
                    if (!string.Equals(plugin.Name, pluginName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (KernelFunction f in plugin)
                        result.Add((plugin.Name, f.Name));
                }
            }
        }

        return result.ToList();
    }

    /// <summary>二阶段选中的函数与 AlwaysIncludePlugins 对应插件的全部函数合并。</summary>
    private static List<(string Plugin, string Function)> MergeFunctionsWithAlwaysInclude(List<(string Plugin, string Function)> selected, AiConfig ai, List<(string Plugin, string Function)> allKernelFunctions)
    {
        var result = new HashSet<(string, string)>(selected, new PluginFunctionComparer());
        var alwaysPlugins = ai.AlwaysIncludePlugins ?? new List<string>();
        var alwaysSet = new HashSet<string>(alwaysPlugins.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var (plugin, func) in allKernelFunctions)
        {
            if (alwaysSet.Contains(plugin))
                result.Add((plugin, func));
        }
        return result.ToList();
    }

    private static string BuildUserPromptWithHistory(string userMessage, ChatHistory? recentHistory)
    {
        var userPrompt = (userMessage ?? "").Trim();
        if (userPrompt.Length > 1000)
            userPrompt = userPrompt[..1000] + "...";
        if (recentHistory != null && recentHistory.Count > 0)
        {
            var lastContent = recentHistory[^1].Content ?? "";
            if (lastContent.Length > 0 && lastContent.Length < 500)
                userPrompt = userPrompt + "\n[上一条] " + lastContent;
        }
        return userPrompt;
    }

    private async Task<IReadOnlyList<string>> SelectByLlmAsync(
        string userMessage,
        ChatHistory? recentHistory,
        IReadOnlyList<string> availablePluginNames,
        AiConfig ai,
        CancellationToken ct)
    {
        var kernel = _kernelAccessor.Kernel;
        if (kernel == null)
        {
            _logger.LogDebug("ToolSelection LLM: Kernel not ready, using all tools.");
            return Array.Empty<string>();
        }

        var availableSet = new HashSet<string>(availablePluginNames, StringComparer.OrdinalIgnoreCase);
        var systemPrompt = BuildToolSelectionSystemPrompt(availablePluginNames);
        var userPrompt = (userMessage ?? "").Trim();
        if (userPrompt.Length > 1000)
            userPrompt = userPrompt[..1000] + "...";
        if (recentHistory != null && recentHistory.Count > 0)
        {
            var lastContent = recentHistory[^1].Content ?? "";
            if (lastContent.Length > 0 && lastContent.Length < 500)
                userPrompt = userPrompt + "\n[上一条] " + lastContent;
        }

        var chat = new ChatHistory(systemPrompt);
        chat.AddUserMessage(userPrompt);

        var serviceId = _kernelAccessor.ActiveModelId;
        if (string.IsNullOrEmpty(serviceId)) serviceId = null;
        var chatService = serviceId != null
            ? kernel.Services.GetKeyedService<IChatCompletionService>(serviceId)
            : null;
        chatService ??= kernel.Services.GetService<IChatCompletionService>();
        if (chatService == null)
        {
            _logger.LogDebug("ToolSelection LLM: No chat service for main model, using all tools.");
            return Array.Empty<string>();
        }

        var userPreview = userPrompt.Length > 300 ? userPrompt[..300] + "..." : userPrompt;
        _logger.LogDebug("ToolSelection LLM request serviceId={ServiceId} systemLen={SystemLen} userPreview={UserPreview}", serviceId ?? "(default)", systemPrompt.Length, userPreview);

        try
        {
            var settings = new OpenAIPromptExecutionSettings { MaxTokens = 256, Temperature = 0.1f };
            var responseText = new System.Text.StringBuilder();
            await foreach (var msg in chatService.GetStreamingChatMessageContentsAsync(chat, settings, kernel, ct).ConfigureAwait(false))
            {
                if (msg.Content is { Length: > 0 } content)
                    responseText.Append(content);
            }
            var raw = responseText.ToString().Trim();
            _logger.LogDebug("ToolSelection LLM response serviceId={ServiceId} raw={Raw}", serviceId ?? "(default)", raw);
            if (string.IsNullOrEmpty(raw))
                return Array.Empty<string>();
            if (raw.Contains(FallbackAllKeyword, StringComparison.OrdinalIgnoreCase) && raw.Length < 20)
            {
                _logger.LogDebug("ToolSelection LLM returned 全部, using all tools.");
                return Array.Empty<string>();
            }
            var parsed = ParsePluginNamesFromResponse(raw, availableSet);
            if (parsed.Count > 0)
            {
                var merged = MergeWithAlwaysInclude(parsed, ai);
                _logger.LogDebug("ToolSelection LLM selected {Count} plugins: {Plugins}", merged.Count, string.Join(", ", merged));
                return merged;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ToolSelection LLM attempt failed.");
        }

        _logger.LogDebug("ToolSelection LLM fallback exhausted, using all tools.");
        return Array.Empty<string>();
    }

    private static string BuildToolSelectionSystemPrompt(IReadOnlyList<string> availablePluginNames)
    {
        var lines = new List<string>
        {
            "你是一个工具选择助手。根据用户当前消息，从下面列出的插件中选出本轮可能用到的插件名。",
            "只输出插件名，多个用英文逗号分隔；若无法判断或需要全部工具则只输出：全部。",
            "可用插件："
        };
        foreach (var name in availablePluginNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var desc = PluginDescriptions.TryGetValue(name, out var d) ? d : (name.StartsWith("UserSkill_", StringComparison.OrdinalIgnoreCase) ? "用户技能" : name.StartsWith("MCP_", StringComparison.OrdinalIgnoreCase) ? "MCP 工具" : name);
            lines.Add($"- {name}: {desc}");
        }
        lines.Add("示例：用户说「打开 Excel」→ 只输出 Excel。用户说「搜索并写进文档」→ 只输出 Tavily, Word。用户说「总结当前页面、生成 excel 放到下载」→ 只输出 Browser, Excel, File（或带 UserSkill_CaptureFullPageScreenshot, UserSkill_Excel___XLSX）。尽量只输出会用到的插件，不要输出 全部。");
        return string.Join("\n", lines);
    }

    private static List<string> ParsePluginNamesFromResponse(string raw, HashSet<string> availableSet)
    {
        var result = new List<string>();
        var parts = raw.Split(new[] { ',', '\n', '\r', ';', '、' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var name = part.Trim().Trim('"', '\'', '`', ' ');
            if (string.IsNullOrEmpty(name)) continue;
            if (availableSet.Contains(name))
                result.Add(name);
        }
        return result;
    }

    private static List<string> MergeWithAlwaysInclude(List<string> selected, AiConfig ai)
    {
        var set = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        foreach (var name in ai.AlwaysIncludePlugins ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(name))
                set.Add(name.Trim());
        }
        return set.ToList();
    }
}
