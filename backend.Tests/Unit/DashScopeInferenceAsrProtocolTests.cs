using OpenWorkmate.Server;
using OpenWorkmate.Server.Services.Stt;
using Xunit;

namespace backend.Tests.Unit;

public class DashScopeInferenceAsrProtocolTests
{
    [Fact]
    public void TryParseUpstreamEvent_ResultGenerated_FinalSentence()
    {
        const string json = """
            {
              "header": { "task_id": "t1", "event": "result-generated", "attributes": {} },
              "payload": {
                "output": {
                  "sentence": {
                    "text": "你好",
                    "sentence_end": true,
                    "heartbeat": false
                  }
                }
              }
            }
            """;

        var ok = DashScopeInferenceAsrProtocol.TryParseUpstreamEvent(json, out var ev, out var err, out var text, out var sentEnd, out var hb);
        Assert.True(ok);
        Assert.Equal("result-generated", ev);
        Assert.Null(err);
        Assert.Equal("你好", text);
        Assert.True(sentEnd);
        Assert.False(hb);
    }

    [Fact]
    public void TryParseUpstreamEvent_TaskFailed_HasMessage()
    {
        const string json = """
            {"header":{"event":"task-failed","error_message":"bad","task_id":"x"},"payload":{}}
            """;

        var ok = DashScopeInferenceAsrProtocol.TryParseUpstreamEvent(json, out var ev, out var err, out _, out _, out _);
        Assert.True(ok);
        Assert.Equal("task-failed", ev);
        Assert.Equal("bad", err);
    }

    [Fact]
    public void BuildRunTaskJson_FunAsr_ContainsModelAndPcm()
    {
        var cfg = new RealtimeAsrConfig { ModelId = "fun-asr-realtime", ApiKey = "k", Heartbeat = false };
        var j = DashScopeInferenceAsrProtocol.BuildRunTaskJson("abc123", cfg, 16000, "pcm", meetingMode: false);
        Assert.Contains("fun-asr-realtime", j, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"pcm\"", j, StringComparison.Ordinal);
        Assert.Contains("run-task", j, StringComparison.Ordinal);
    }
}
