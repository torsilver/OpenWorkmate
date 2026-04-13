namespace OfficeCopilot.Server.Services.DynamicTooling;

/// <summary>拼入 system/身份后缀，约束模型先检索再激活再调业务工具。</summary>
public static class DynamicToolingInstruction
{
    public const string Text =
        "【动态工具】本回合工具列表可能仅为子集。请先调用 search_available_tools 按任务关键词检索（尽量具体，勿无故传空 query），"
        + "再调用 activate_tools（可传检索结果中的裸函数名或 Plugin.function 形式）。"
        + "发起 tool_calls 调用业务工具时，名称必须与 OpenAPI 工具 schema 中的裸函数名一致，勿使用 Plugin.function。"
        + "然后再调用已激活的业务工具完成操作。不要编造未出现在工具列表中的函数名。";

    /// <summary>首轮已含非检索类工具（脚本、run_command、渐进式技能加载 load_user_skill_instructions 等）时追加，避免模型误以为必须先 activate 才能调用。</summary>
    public const string BootstrapDirectToolsHint =
        "首轮工具列表已含 load_user_skill_instructions（按需加载用户技能正文，与 search_available_tools 无关；技能清单见 system 中「渐进式用户技能」）。"
        + " 端侧脚本与 run_command 等若已在列表中可直接调用；其它业务工具仍请先 search_available_tools 再 activate_tools。";
}
