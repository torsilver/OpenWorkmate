namespace OfficeCopilot.Server.Services.DynamicTooling;

/// <summary>拼入 system/身份后缀，约束模型先检索再激活再调业务工具。</summary>
public static class DynamicToolingInstruction
{
    public const string Text =
        "【动态工具】本回合工具列表可能仅为子集。请先调用 search_available_tools 按任务关键词检索（尽量具体，勿无故传空 query），"
        + "若存在已启用的用户技能，在 activate_tools 之前还须先至少调用一次 search_available_skills（与工具检索不同轨；空 query 亦可）。"
        + "再调用 activate_tools（可传检索结果中的裸函数名或 Plugin.function 形式）。"
        + "发起 tool_calls 调用业务工具时，名称必须与 OpenAPI 工具 schema 中的裸函数名一致，勿使用 Plugin.function。"
        + "然后再调用已激活的业务工具完成操作。不要编造未出现在工具列表中的函数名。";

    /// <summary>首轮已含非检索类工具（脚本、run_command、渐进式技能加载 load_user_skill_instructions 等）时追加，避免模型误以为必须先 activate 才能调用。</summary>
    public const string BootstrapDirectToolsHint =
        "首轮工具列表已含技能链：search_available_skills、select_skill_for_turn、load_user_skill_instructions（与 search_available_tools 无关；技能清单见 system「渐进式用户技能」）。"
        + " search_available_skills 只用于发现技能 Id，不能把检索结果当成已加载规则；凡须按某用户技能（含公文版式、写作规范等）操作前，必须先 load_user_skill_instructions 再调用 Word 等写盘工具。"
        + " 若已调用 search_available_tools 且存在已启用用户技能，activate_tools 前还须先 search_available_skills 一次（产品内置顺序）。"
        + " 端侧脚本与 run_command 等若已在列表中可直接调用；其它业务工具仍请先 search_available_tools 再 activate_tools。";
}
