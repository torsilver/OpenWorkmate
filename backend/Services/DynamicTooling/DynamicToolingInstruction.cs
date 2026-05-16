namespace OpenWorkmate.Server.Services.DynamicTooling;

/// <summary>拼入 system/身份后缀：默认先技能链再业务工具检索/激活，并与 activate 前技能门控一致。</summary>
public static class DynamicToolingInstruction
{
    public const string Text =
        "【动态工具】本回合业务工具列表可能仅为子集。"
        + "当 system 含有「渐进式用户技能」元数据时，默认顺序为：search_available_skills（可空 query）→（按需）select_skill_for_turn → load_user_skill_instructions → search_available_tools（关键词尽量具体，勿无故空 query）→ activate_tools → 以裸函数名发起业务 tool_calls。"
        + "硬性要求：除首轮 OpenAPI 工具列表里已直接给出的函数外，凡要调用任何未出现在当前列表中的业务工具（如 Excel/Word/MCP 等），必须先通过 activate_tools 将其加入本轮；禁止在未激活时直接 tool_calls，否则会出现 Function not found / 参数绑定失败。"
        + "activate_tools 的 toolNames 为数组，支持一次传入多个裸函数名或 Plugin.function；若本轮需要多个业务工具，优先在同一次 activate_tools 中一并激活，减少遗漏与多轮搜索。"
        + "仅当纯闲聊、不写盘、且明确不需要任何用户技能与业务工具时，可跳过上述技能链前缀，直接使用首轮已给出的工具。"
        + "若 system 仍列出渐进式用户技能，只要你还要 search_available_tools 且随后需要 activate_tools，则在本轮须先至少调用一次 search_available_skills（空 query 亦可），再 activate_tools；不可在「仅工具检索」之后直接 activate（与产品内置门控一致）。"
        + "发起 tool_calls 时名称必须与 OpenAPI 工具 schema 中的裸函数名一致，勿使用 Plugin.function。不要编造未出现在工具列表中的函数名。"
        + "每次调用的 arguments 中 JSON 键名也须与该工具 schema 的 properties 完全一致（含大小写），勿用 data/content/values 等别名替代正式字段名。"
        + " 子任务：大范围只读探索用 run_explore_subtask；终端长输出用 run_cli_subtask；重浏览器页内脚本用 run_browser_subtask。"
        + " 特别地：凡准备 word_document_create 落盘 Word，技能链中须有针对 Word/版式/docx/公文的 search_available_skills 与 select_skill_for_turn 或 load_user_skill_instructions（绑定文档版式类 skill），再 activate 并调用；勿仅靠 documentPreset=default 硬写版式。";

    /// <summary>首轮已含非检索类工具（脚本、run_command、渐进式技能加载 load_user_skill_instructions 等）时追加，避免模型误以为必须先 activate 才能调用。</summary>
    public const string BootstrapDirectToolsHint =
        "首轮工具列表已含技能链（search_available_skills、select_skill_for_turn、load_user_skill_instructions）与动态工具检索（search_available_tools、activate_tools）；技能清单见 system「渐进式用户技能」。"
        + "当 system 列出渐进式用户技能时，默认顺序：先按需完成技能链（须按某技能写文档或版式前必须 load_user_skill_instructions，不能把 search_available_skills 的摘要当正文），再 search_available_tools → activate_tools；端侧脚本与 run_command 等若已在列表中可直接调用。"
        + "未出现在当前工具列表中的业务能力仍须先 activate_tools（可一次传入多个 toolNames）再调用。"
        + " 若 system 仍有渐进式用户技能且本轮已 search_available_tools，在 activate_tools 之前须先至少调用一次 search_available_skills（可空 query）；无启用技能时不受此限。";
}
