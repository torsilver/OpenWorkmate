namespace OfficeCopilot.Server.Mcp;

public class McpServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();
    /// <summary>是否启用此 MCP；仅启用的会被启动并注册到 Kernel。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>可选环境变量，启动 MCP 进程时注入（如 ACCURATE_DATA_DIRECTORY）。</summary>
    public Dictionary<string, string>? Env { get; set; }
}
