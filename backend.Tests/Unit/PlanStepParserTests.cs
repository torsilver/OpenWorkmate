using OfficeCopilot.Server.Services.Plan;
using Xunit;

namespace backend.Tests.Unit;

public class PlanStepParserTests
{
    [Fact]
    public void ParsePlanSteps_Null_ReturnsEmpty()
    {
        var result = PlanStepParser.ParsePlanSteps(null);
        Assert.Empty(result);
    }

    [Fact]
    public void ParsePlanSteps_Empty_ReturnsEmpty()
    {
        var result = PlanStepParser.ParsePlanSteps("");
        Assert.Empty(result);
    }

    [Fact]
    public void ParsePlanSteps_WhitespaceOnly_ReturnsEmpty()
    {
        var result = PlanStepParser.ParsePlanSteps("  \n  ");
        Assert.Empty(result);
    }

    [Fact]
    public void ParsePlanSteps_StepHeadings_ParsesByStepNumber()
    {
        var content = @"## 步骤 1
Do first.

## 步骤 2
Do second.

## 步骤 3
Do third.";
        var result = PlanStepParser.ParsePlanSteps(content);
        Assert.Equal(3, result.Count);
        Assert.Contains("## 步骤 1", result[0]);
        Assert.Contains("Do first", result[0]);
        Assert.Contains("## 步骤 2", result[1]);
        Assert.Contains("Do second", result[1]);
        Assert.Contains("## 步骤 3", result[2]);
        Assert.Contains("Do third", result[2]);
    }

    [Fact]
    public void ParsePlanSteps_StepHeadings_OneToSixHashes_Accepted()
    {
        var content = @"### 步骤 1
Content.";
        var result = PlanStepParser.ParsePlanSteps(content);
        Assert.Single(result);
        Assert.Contains("步骤 1", result[0]);
    }

    [Fact]
    public void ParsePlanSteps_DelimiterStyle_ParsesByDelimiter()
    {
        var content = "Step A content.\n\n---\n\nStep B content.";
        var result = PlanStepParser.ParsePlanSteps(content);
        Assert.Equal(2, result.Count);
        Assert.Contains("Step A", result[0]);
        Assert.Contains("Step B", result[1]);
    }

    [Fact]
    public void ParsePlanSteps_DelimiterStyle_CRLF()
    {
        var content = "Step A\r\n\r\n---\r\n\r\nStep B";
        var result = PlanStepParser.ParsePlanSteps(content);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParsePlanSteps_NoHeading_UsesDelimiter()
    {
        var content = "Only one block without ## or ---";
        var result = PlanStepParser.ParsePlanSteps(content);
        Assert.Single(result);
        Assert.Equal("Only one block without ## or ---", result[0]);
    }

    [Fact]
    public void GetStepAt_StepIndexLessThanOne_ReturnsNull()
    {
        var content = "## 步骤 1\nContent";
        Assert.Null(PlanStepParser.GetStepAt(content, 0));
        Assert.Null(PlanStepParser.GetStepAt(content, -1));
    }

    [Fact]
    public void GetStepAt_ValidIndex_ReturnsStep()
    {
        var content = @"## 步骤 1
First.

## 步骤 2
Second.";
        var step1 = PlanStepParser.GetStepAt(content, 1);
        var step2 = PlanStepParser.GetStepAt(content, 2);
        Assert.NotNull(step1);
        Assert.NotNull(step2);
        Assert.Contains("First", step1);
        Assert.Contains("Second", step2);
    }

    [Fact]
    public void GetStepAt_IndexOutOfRange_ReturnsNull()
    {
        var content = "## 步骤 1\nOnly one.";
        Assert.Null(PlanStepParser.GetStepAt(content, 2));
        Assert.Null(PlanStepParser.GetStepAt(content, 10));
    }

    [Fact]
    public void GetStepAt_NullContent_ReturnsNull()
    {
        Assert.Null(PlanStepParser.GetStepAt(null, 1));
    }
}
