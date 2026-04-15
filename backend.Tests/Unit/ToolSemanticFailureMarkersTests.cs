using OfficeCopilot.Server.Services.ToolInvocation;
using Xunit;

namespace backend.Tests.Unit;

public sealed class ToolSemanticFailureMarkersTests
{
    [Fact]
    public void LooksLikeSemanticFailure_skill_search_body_with_error_word_inside_description_is_false()
    {
        var s =
            "[search_available_skills] 共 1 条（query=excel）：\n"
            + "1. Id: Excel / XLSX — 具体错误以工具返回的 [错误] 文案为准并转述用户。\n"
            + "请用 select_skill_for_turn 传入上列技能 Id；";
        Assert.False(ToolSemanticFailureMarkers.LooksLikeSemanticFailure(s));
    }

    [Fact]
    public void LooksLikeSemanticFailure_bracket_error_prefix_is_true()
    {
        Assert.True(ToolSemanticFailureMarkers.LooksLikeSemanticFailure("[错误] jsonData 不能为空"));
    }

    [Fact]
    public void LooksLikeSemanticFailure_tool_invoke_failure_prefix_is_true()
    {
        Assert.True(ToolSemanticFailureMarkers.LooksLikeSemanticFailure("[工具调用失败] Excel.excel_range_write: missing"));
    }

    [Fact]
    public void LooksLikeSemanticFailure_binding_prefix_is_true()
    {
        Assert.True(ToolSemanticFailureMarkers.LooksLikeSemanticFailure("[参数绑定失败] 工具 x.y 的 JSON 参数"));
    }

    [Fact]
    public void LooksLikeSemanticFailure_plain_success_text_is_false()
    {
        Assert.False(ToolSemanticFailureMarkers.LooksLikeSemanticFailure("已写入 3 行到 Sheet1!A1"));
    }

    [Theory]
    [InlineData("Error: Function failed.")]
    [InlineData("Error: Function failed. Exception: missing jsonData")]
    [InlineData("Error: Requested function \"x\" not found.")]
    [InlineData("Error: Unknown error.")]
    public void LooksLikeSemanticFailure_meai_error_prefix_is_true(string s)
    {
        Assert.True(ToolSemanticFailureMarkers.LooksLikeSemanticFailure(s));
    }

    [Fact]
    public void ClassifyFailureKind_binding_vs_mcp_vs_business()
    {
        Assert.Equal(ToolInvocationFailureKind.Binding, ToolSemanticFailureMarkers.ClassifyFailureKind("[参数绑定失败] x"));
        Assert.Equal(ToolInvocationFailureKind.Mcp, ToolSemanticFailureMarkers.ClassifyFailureKind("[MCP 工具错误] x"));
        Assert.Equal(ToolInvocationFailureKind.Mcp, ToolInvocationFailureClassifier.Classify("[MCP 调用异常] y"));
        Assert.Equal(ToolInvocationFailureKind.Business, ToolSemanticFailureMarkers.ClassifyFailureKind("[错误] z"));
        Assert.Equal(ToolInvocationFailureKind.Business, ToolInvocationFailureClassifier.Classify(""));
    }

    [Fact]
    public void LooksLikeSemanticFailure_leading_whitespace_still_matches_prefix()
    {
        Assert.True(ToolSemanticFailureMarkers.LooksLikeSemanticFailure("  \n[错误] bad"));
    }
}
