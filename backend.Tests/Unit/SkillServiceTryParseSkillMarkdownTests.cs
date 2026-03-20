using Microsoft.Extensions.Logging.Abstractions;
using OfficeCopilot.Server.Services;
using Xunit;

namespace backend.Tests.Unit;

public sealed class SkillServiceTryParseSkillMarkdownTests
{
    private static SkillService CreateService() => new(NullLogger<SkillService>.Instance);

    [Fact]
    public void TryParse_Valid_ReturnsSkillWithSanitizedId()
    {
        var md = """
---
name: my_test_skill
description: 测试用途
title: 展示标题
---

## 步骤

使用 **Excel** 读取区域。
""";
        var svc = CreateService();
        var ok = svc.TryParseSkillMarkdown(md, out var skill, out var err);
        Assert.True(ok, err);
        Assert.NotNull(skill);
        Assert.Equal("my_test_skill", skill!.Id);
        Assert.Equal("展示标题", skill.Name);
        Assert.Equal("测试用途", skill.Description);
        Assert.Contains("Excel", skill.PromptTemplate);
        Assert.Null(err);
    }

    [Fact]
    public void TryParse_Empty_Fails()
    {
        var svc = CreateService();
        Assert.False(svc.TryParseSkillMarkdown("", out _, out var err));
        Assert.Contains("为空", err ?? "");
    }

    [Fact]
    public void TryParse_NoFrontmatter_Fails()
    {
        var svc = CreateService();
        var md = "# 只有正文\n\n没有 yaml。";
        Assert.False(svc.TryParseSkillMarkdown(md, out _, out var err));
        Assert.Contains("frontmatter", err ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_MissingDescription_Fails()
    {
        var md = """
---
name: x
---

body
""";
        var svc = CreateService();
        Assert.False(svc.TryParseSkillMarkdown(md, out _, out var err));
        Assert.Contains("description", err ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_EmptyBody_Fails()
    {
        var md = """
---
name: empty_body
description: d
---

""";
        var svc = CreateService();
        Assert.False(svc.TryParseSkillMarkdown(md, out _, out var err));
        Assert.Contains("正文", err ?? "");
    }
}
