using OpenWorkmate.Server.Services.DynamicTooling;
using Xunit;

namespace OpenWorkmate.Server.Tests.Unit;

public sealed class TurnRouteClassifierTests
{
    [Fact]
    public void Classify_bound_plan_is_task_oriented()
    {
        Assert.Equal(TurnRoute.TaskOriented, TurnRouteClassifier.Classify("hi", hasBoundPlan: true));
    }

    [Fact]
    public void Classify_short_digits_is_unclear()
    {
        Assert.Equal(TurnRoute.UnclearOrChitchat, TurnRouteClassifier.Classify("111", hasBoundPlan: false));
    }

    [Fact]
    public void Classify_excel_keyword_is_task()
    {
        Assert.Equal(TurnRoute.TaskOriented, TurnRouteClassifier.Classify("请读取这个 Excel 表格第一列", hasBoundPlan: false));
    }

    [Fact]
    public void LooksLikeTaskUserMessage_requires_length_and_keyword()
    {
        Assert.False(TurnRouteClassifier.LooksLikeTaskUserMessage("短"));
        Assert.True(TurnRouteClassifier.LooksLikeTaskUserMessage("请帮我把文档保存到桌面路径"));
    }
}
