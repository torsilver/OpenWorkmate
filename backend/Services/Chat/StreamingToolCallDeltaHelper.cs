namespace OpenWorkmate.Server.Services.Chat;

/// <summary>Tool call delta stream constants shared between MAF and legacy extractors.</summary>
public static class StreamingToolCallDeltaHelper
{
    public const int MaxArgumentsCumulativeCharsPerCall = 32 * 1024;
}
