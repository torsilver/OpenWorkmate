namespace OfficeCopilot.Server.Services.DynamicTooling;

/// <summary>拼入 system/身份后缀：默认先技能链再业务工具检索/激活，并保留 activate 前的硬性门控。</summary>
public static class DynamicToolingInstruction
{
    public const string Text =
        "【动态工具】本回合业务工具列表可能仅为子集。"
        + "默认推荐顺序：若 system 中存在「渐进式用户技能」且任务可能受规范/文风/领域流程影响，请先按需完成技能链 search_available_skills（可空 query）→ select_skill_for_turn → load_user_skill_instructions，再调用 search_available_tools 按任务关键词检索（尽量具体，勿无故空 query），然后 activate_tools，最后以裸函数名发起业务 tool_calls。"
        + "纯闲聊、明确不依赖任何用户技能、且不需要业务写盘工具时，可跳过技能链，直接 search_available_tools。"
        + "硬性规则：若已调用过 search_available_tools 且仍有已启用用户技能，则在 activate_tools 之前必须至少再调用一次 search_available_skills（空 query 亦可）。"
        + "发起 tool_calls 时名称必须与 OpenAPI 工具 schema 中的裸函数名一致，勿使用 Plugin.function。不要编造未出现在工具列表中的函数名。";

    /// <summary>首轮已含非检索类工具（脚本、run_command、渐进式技能加载 load_user_skill_instructions 等）时追加，避免模型误以为必须先 activate 才能调用。</summary>
    public const string BootstrapDirectToolsHint =
        "首轮工具列表已含技能链（search_available_skills、select_skill_for_turn、load_user_skill_instructions）与动态工具检索（search_available_tools、activate_tools）；技能清单见 system「渐进式用户技能」。"
        + "默认推荐：在首次 search_available_tools 之前，若任务可能依赖用户技能，先走完技能链（须按某技能写文档或版式前必须 load_user_skill_instructions，不能把 search_available_skills 的摘要当正文）。"
        + "再按需 search_available_tools → activate_tools；端侧脚本与 run_command 等若已在列表中可直接调用。"
        + " 若已调用 search_available_tools 且存在已启用用户技能，activate_tools 前还须先 search_available_skills 一次（产品内置硬性顺序）。";
}
