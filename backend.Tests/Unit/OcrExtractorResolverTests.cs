using OpenWorkmate.Server;
using OpenWorkmate.Server.Services;
using OpenWorkmate.Server.Services.Ocr;
using Xunit;

namespace backend.Tests.Unit;

public class OcrExtractorResolverTests
{
    [Fact]
    public void Resolve_ExplicitMultipart_IgnoresCompatibleModeInUrl()
    {
        var e = new OcrModelEntry
        {
            Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            ConnectionKind = ModelConnectionKind.Ocr.OpenAiCompatibleMultipart,
            VendorId = ""
        };
        Assert.Equal(OcrBackendKind.OpenAiMultipart, OcrExtractorResolver.Resolve(e));
    }

    [Fact]
    public void Resolve_VendorBailian_UsesDashScope()
    {
        var e = new OcrModelEntry
        {
            Endpoint = "https://example.com/v1",
            ConnectionKind = "",
            VendorId = AiVendorIds.AliyunBailian
        };
        Assert.Equal(OcrBackendKind.DashScopeOpenAiChatImage, OcrExtractorResolver.Resolve(e));
    }

    [Fact]
    public void Resolve_Empty_UsesUrlForCompatibleMode()
    {
        var e = new OcrModelEntry
        {
            Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            ConnectionKind = "",
            VendorId = ""
        };
        Assert.Equal(OcrBackendKind.DashScopeOpenAiChatImage, OcrExtractorResolver.Resolve(e));
    }

    [Fact]
    public void Resolve_InvalidKind_Throws()
    {
        var e = new OcrModelEntry
        {
            Endpoint = "https://api.openai.com/v1",
            ConnectionKind = "not_a_kind",
            VendorId = ""
        };
        Assert.Throws<InvalidOperationException>(() => OcrExtractorResolver.Resolve(e));
    }
}
