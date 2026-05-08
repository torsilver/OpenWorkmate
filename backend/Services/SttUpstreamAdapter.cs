using System.IO;
using System.Text;

namespace OpenWorkmate.Server.Services;

/// <summary>生成用于「测试连接」等场景的最小 PCM WAV（16kHz/16bit/单声道）。</summary>
public static class SttUpstreamAdapter
{
    /// <summary>
    /// 生成标准 PCM WAV（16kHz/16bit/单声道），用于百炼实时 ASR 探针等场景。
    /// </summary>
    public static byte[] BuildMinimalWavPcm16kMono(int durationMs = 100)
    {
        if (durationMs <= 0) durationMs = 100;
        const int sampleRate = 16000;
        const short bitsPerSample = 16;
        const short channels = 1;
        const short audioFormat = 1; // PCM

        var numSamples = (int)Math.Round(sampleRate * (durationMs / 1000.0));
        if (numSamples < 1) numSamples = 1;

        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;
        var dataSize = numSamples * blockAlign;

        var riffChunkSize = 36 + dataSize;

        using var ms = new MemoryStream(44 + dataSize);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(riffChunkSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));

        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write(audioFormat);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);

        for (var i = 0; i < numSamples; i++)
            bw.Write((short)0);

        bw.Flush();
        return ms.ToArray();
    }
}
