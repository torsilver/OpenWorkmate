using System.ComponentModel;
using System.Text.Json;
using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.CrossAgentTask;

namespace OfficeCopilot.Server.Plugins;

/// <summary>跨 Agent 任务：由当前端发起，让另一端的 Agent 执行任务。</summary>
[CopilotPluginId("CrossAgentTask")]
public sealed class CrossAgentTaskPlugin
{
    private readonly ICrossAgentTaskStore _store;
    private readonly SessionManager _sessionManager;
    private readonly ILogger<CrossAgentTaskPlugin>? _logger;

    public CrossAgentTaskPlugin(ICrossAgentTaskStore store, SessionManager sessionManager, ILogger<CrossAgentTaskPlugin>? logger = null)
    {
        _store = store;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>创建跨 Agent 任务：让目标端的 Agent 执行某任务。目标端在下次对话时会看到待办并执行。</summary>
    [ToolFunction("create_cross_agent_task")]
    [Description("当用户要求「让 Word/Chrome/Excel/WPS/PowerPoint 的 Agent 做某事」时调用。创建一条发给目标端的待办，目标端在下次对话时会看到并执行。targetClientType 取 office-word、chrome、office-excel、office-powerpoint、wps 之一。")]
    public async Task<string> CreateCrossAgentTaskAsync(
        [Description("目标端类型：office-word（Word）、chrome（浏览器）、office-excel（Excel）、office-powerpoint（PowerPoint）、wps（WPS）")] string targetClientType,
        [Description("要目标端执行的任务描述，清晰具体")] string description,
        [Description("可选；若指定则仅该 session 会收到，否则该 clientType 下所有在线 session 都会收到")] string? targetSessionId = null)
    {
        var fromSessionId = SessionContext.GetSessionId() ?? "";
        var tct = (targetClientType ?? "").Trim();
        if (string.IsNullOrEmpty(tct))
            return "[无效] 请指定 targetClientType：office-word、chrome、office-excel、office-powerpoint、wps 之一。";
        var desc = (description ?? "").Trim();
        if (string.IsNullOrEmpty(desc))
            return "[无效] 任务描述不能为空。";
        var normalized = tct.ToLowerInvariant();
        if (normalized != "office-word" && normalized != "chrome" && normalized != "office-excel" && normalized != "office-powerpoint" && normalized != "wps")
            return "[无效] targetClientType 应为 office-word、chrome、office-excel、office-powerpoint、wps 之一。";

        try
        {
            var item = await _store.AddAsync(fromSessionId, tct, string.IsNullOrWhiteSpace(targetSessionId) ? null : targetSessionId.Trim(), desc).ConfigureAwait(false);
            _logger?.LogInformation("create_cross_agent_task: id={Id} target={Target} from={From}", item.Id, tct, fromSessionId);

            // 可选：向目标端在线 session 推送「新任务」通知
            var targetIds = string.IsNullOrWhiteSpace(targetSessionId)
                ? _sessionManager.GetSessionIdsByClientType(tct)
                : new List<string> { targetSessionId.Trim() };
            var notifyPayload = JsonSerializer.Serialize(new { type = "cross_agent_task", taskId = item.Id, description = item.Description });
            foreach (var sid in targetIds)
            {
                try
                {
                    await _sessionManager.SendToAsync(sid, notifyPayload).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Push cross_agent_task to session {SessionId} failed", sid);
                }
            }

            return $"[已创建] 已向「{tct}」端下发任务（id={item.Id}）。目标端在下次对话时会看到并执行：{desc}";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "create_cross_agent_task failed");
            return $"[创建失败] {ex.Message}";
        }
    }

    /// <summary>将跨 Agent 任务标记为已完成或失败（由执行端在完成后调用）。</summary>
    [ToolFunction("complete_cross_agent_task")]
    [Description("当本端完成了一条来自其他端的待办任务后调用，标记该任务为 done 或 failed，并可选写入结果摘要。")]
    public async Task<string> CompleteCrossAgentTaskAsync(
        [Description("任务 id（从待办列表中获取）")] string taskId,
        [Description("done 或 failed")] string status = "done",
        [Description("执行结果简要说明")] string? resultSummary = null)
    {
        var id = (taskId ?? "").Trim();
        if (string.IsNullOrEmpty(id))
            return "[无效] 请提供任务 id。";
        var st = (status ?? "done").Trim().ToLowerInvariant();
        if (st != "done" && st != "failed")
            st = "done";

        try
        {
            // 先读取任务信息以获取发起方 sessionId
            var task = await _store.GetAsync(id).ConfigureAwait(false);
            var ok = await _store.UpdateStatusAsync(id, st, resultSummary?.Trim()).ConfigureAwait(false);
            if (!ok)
                return "[未找到] 未找到该任务或已更新。";
            _logger?.LogInformation("complete_cross_agent_task: id={Id} status={Status}", id, st);

            // 向发起方推送完成通知
            if (task != null && !string.IsNullOrEmpty(task.FromSessionId))
            {
                try
                {
                    var callbackPayload = JsonSerializer.Serialize(new
                    {
                        type = "cross_agent_task_completed",
                        taskId = id,
                        status = st,
                        resultSummary = resultSummary?.Trim() ?? ""
                    });
                    await _sessionManager.SendToAsync(task.FromSessionId, callbackPayload).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Callback to originator session {SessionId} failed", task.FromSessionId);
                }
            }

            return st == "done" ? "[已完成] 任务已标记为完成。" : "[已标记] 任务已标记为失败。";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "complete_cross_agent_task failed");
            return $"[更新失败] {ex.Message}";
        }
    }
}
