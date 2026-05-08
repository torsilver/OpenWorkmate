namespace OpenWorkmate.Server.Services.DynamicTooling;

/// <summary>主会话 tooling 阶段对本轮用户消息的轻量路由（启发式），用于首轮小工具表与 Verifier 门控。</summary>
public enum TurnRoute
{
    /// <summary>默认：完整 bootstrap（与任务向一致）。</summary>
    Standard,

    /// <summary>极短或含糊输入：首轮仅 meta + 渐进技能检索，不含 run_command / 子代理 / 宿主脚本。</summary>
    UnclearOrChitchat,

    /// <summary>明确办公/自动化类意图关键词。</summary>
    TaskOriented,
}
