namespace OfficeCopilot.Server.Services.Telemetry;

/// <summary>与 <c>TryEnqueueFromSession</c> 使用的 <c>eventType</c> 及 AI Gateway <c>availableEventKinds</c> 默认集对齐。</summary>
public static class TelemetryKnownEventKinds
{
    public const string AssistantTurnFinal = "assistant_turn_final";
    public const string ToolInvocationEnd = "tool_invocation_end";
    public const string PlanCreated = "plan_created";
    public const string PlanStepRead = "plan_step_read";
    public const string PlanCompleted = "plan_completed";
}
