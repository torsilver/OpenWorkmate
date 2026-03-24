using OfficeCopilot.Server;
using OfficeCopilot.Server.Services;
using OfficeCopilot.Server.Services.Stt;
using Xunit;

namespace backend.Tests.Unit;

public class SttTranscriberResolverTests
{
    [Fact]
    public void Resolve_ExplicitDashScopeKind_UsesDashScopeEvenIfUrlIsOpenAI()
    {
        var k = SttTranscriberResolver.Resolve(
            "https://api.openai.com/v1",
            ModelConnectionKind.Stt.DashScopeOpenAiChatAudio,
            "openai");
        Assert.Equal(SttUpstreamAdapter.UpstreamKind.DashScopeQwenOpenAICompatible, k);
    }

    [Fact]
    public void Resolve_VendorAliyunBailian_UsesDashScope()
    {
        var k = SttTranscriberResolver.Resolve(
            "https://api.openai.com/v1",
            "",
            AiVendorIds.AliyunBailian);
        Assert.Equal(SttUpstreamAdapter.UpstreamKind.DashScopeQwenOpenAICompatible, k);
    }

    [Fact]
    public void Resolve_VendorOpenAI_UsesWhisper()
    {
        var k = SttTranscriberResolver.Resolve(
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "",
            "openai");
        Assert.Equal(SttUpstreamAdapter.UpstreamKind.WhisperCompatible, k);
    }

    [Fact]
    public void Resolve_EmptyKindAndVendor_UsesUrlHeuristic()
    {
        var k = SttTranscriberResolver.Resolve(
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "",
            AiVendorIds.OtherAuto);
        Assert.Equal(SttUpstreamAdapter.UpstreamKind.DashScopeQwenOpenAICompatible, k);
    }

    [Fact]
    public void Resolve_InvalidKind_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SttTranscriberResolver.Resolve("https://api.openai.com/v1", "bad_kind", null));
    }
}
