using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OpenWorkmate.AI.Gateway;

namespace OpenWorkmate.AI.Gateway.Storage;

public sealed class BlobStore
{
    private readonly IOptionsMonitor<AiGatewayOptions> _opt;

    public BlobStore(IOptionsMonitor<AiGatewayOptions> opt) => _opt = opt;

    private string Root => Path.Combine(Path.GetFullPath(_opt.CurrentValue.DataRoot), "blobs");

    /// <summary>超过内联上限时写入 blobs，返回 <c>sha256:hex</c>；否则返回 null。</summary>
    public string? TryStoreRef(ReadOnlySpan<byte> bytes)
    {
        var max = Math.Max(256, _opt.CurrentValue.BlobInlineMaxBytes);
        if (bytes.Length <= max)
            return null;
        var hash = SHA256.HashData(bytes);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        var dir = Path.Combine(Root, hex[..2]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, hex + ".bin");
        if (!File.Exists(path))
            File.WriteAllBytes(path, bytes.ToArray());
        return "sha256:" + hex;
    }

    public byte[]? TryRead(string? sha256Ref)
    {
        if (string.IsNullOrWhiteSpace(sha256Ref)) return null;
        var t = sha256Ref.Trim();
        if (!t.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var hex = t["sha256:".Length..].Trim().ToLowerInvariant();
        if (hex.Length != 64) return null;
        var path = Path.Combine(Root, hex[..2], hex + ".bin");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
