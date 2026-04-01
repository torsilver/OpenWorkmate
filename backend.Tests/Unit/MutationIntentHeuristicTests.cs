using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public sealed class MutationIntentHeuristicTests
{
    [Theory]
    [InlineData("请取消合并 A1:C1。", true)]
    [InlineData("请合并 Sheet1 的 A1:C1。", true)]
    [InlineData("写入公式到 D2", true)]
    [InlineData("删除工作表 Sheet2", true)]
    [InlineData("另存为 backup.xlsx", true)]
    [InlineData("修改 A1 单元格为 1", true)]
    [InlineData("run_command dir", true)]
    [InlineData("执行命令 notepad", true)]
    [InlineData("今天天气怎么样", false)]
    [InlineData("什么是单元格", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void LikelyRequiresLocalMutationTool_MatchesExpected(string? message, bool expected)
    {
        Assert.Equal(expected, MutationIntentHeuristic.LikelyRequiresLocalMutationTool(message));
    }

    [Fact]
    public void PatternHint_IsNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(MutationIntentHeuristic.PatternHint));
    }
}
