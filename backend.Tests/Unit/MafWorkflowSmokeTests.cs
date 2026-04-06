using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace OfficeCopilot.Server.Tests.Unit;

/// <summary>验证 Microsoft.Agents.AI.Workflows 包可用（阶段 1/4：Workflow spike）。</summary>
public sealed class MafWorkflowSmokeTests
{
    [Fact]
    public async Task InProcessExecution_Runs_Sequential_FuncExecutors()
    {
        Func<string, string> uppercaseFunc = s => s.ToUpperInvariant();
        var uppercase = uppercaseFunc.BindAsExecutor("UppercaseExecutor");
        Func<string, string> reverseFunc = s => string.Concat(s.Reverse());
        var reverse = reverseFunc.BindAsExecutor("ReverseExecutor");

        var builder = new WorkflowBuilder(uppercase);
        builder.AddEdge(uppercase, reverse).WithOutputFrom(reverse);
        var workflow = builder.Build();

        await using Run run = await InProcessExecution.RunAsync(workflow, "Hello, World!");
        var completed = run.NewEvents.OfType<ExecutorCompletedEvent>().ToList();
        Assert.Contains(completed, e => e.ExecutorId == "ReverseExecutor");
    }
}
