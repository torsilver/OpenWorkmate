using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;

namespace OfficeCopilot.Server.Plugins;

public sealed class TavilyPlugin
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _apiKey;
    private readonly ILogger<TavilyPlugin>? _logger;

    public TavilyPlugin(string apiKey, ILogger<TavilyPlugin>? logger = null)
    {
        _apiKey = (apiKey ?? "").Trim();
        _logger = logger;
    }

    [KernelFunction("tavily_search")]
    [Description("使用 Tavily API 进行网页搜索，返回简洁相关结果，适合 AI 摘要。当用户需要查实时信息、新闻或网络资料时使用。")]
    public async Task<string> SearchAsync(
        [Description("搜索关键词或问题")] string query,
        [Description("返回结果数量，默认 5，最大 20")] int maxResults = 5,
        [Description("是否深度搜索，更全面但较慢")] bool deep = false,
        [Description("主题：general 或 news")] string topic = "general")
    {
        if (string.IsNullOrEmpty(_apiKey))
            return "[错误] 未配置 Tavily API Key，请在 user-config.json 中填写 tavilyApiKey（或在 skillEnv 中配置 TAVILY_API_KEY）。";

        var body = new Dictionary<string, object>
        {
            ["api_key"] = _apiKey,
            ["query"] = query,
            ["search_depth"] = deep ? "advanced" : "basic",
            ["topic"] = topic,
            ["max_results"] = Math.Clamp(maxResults, 1, 20),
            ["include_answer"] = true,
            ["include_raw_content"] = false
        };

        try
        {
            using var resp = await SharedHttp.PostAsJsonAsync("https://api.tavily.com/search", body);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                _logger?.LogWarning("Tavily Search failed {Status}: {Err}", resp.StatusCode, err);
                return $"[错误] Tavily 搜索失败 ({(int)resp.StatusCode}): {err}";
            }

            var data = await resp.Content.ReadFromJsonAsync<TavilySearchResponse>();
            if (data == null)
                return "[错误] Tavily 返回为空。";

            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(data.Answer))
            {
                sb.AppendLine("## Answer");
                sb.AppendLine(data.Answer);
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
            sb.AppendLine("## Sources");
            foreach (var r in data.Results ?? new List<TavilyResult>())
            {
                var title = (r.Title ?? "").Trim();
                var url = (r.Url ?? "").Trim();
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(url)) continue;
                var score = r.Score.HasValue ? $" (相关度: {(r.Score.Value * 100):F0}%)" : "";
                sb.AppendLine($"- **{title}**{score}");
                sb.AppendLine($"  {url}");
                if (!string.IsNullOrWhiteSpace(r.Content))
                {
                    var preview = r.Content.Length > 300 ? r.Content[..300] + "..." : r.Content;
                    sb.AppendLine($"  {preview}");
                }
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Tavily Search error");
            return $"[错误] Tavily 搜索异常: {ex.Message}";
        }
    }

    [KernelFunction("tavily_extract")]
    [Description("从指定 URL 提取正文内容，适用于需要阅读网页全文时。")]
    public async Task<string> ExtractAsync(
        [Description("要提取内容的网页 URL，可多个用逗号或空格分隔")] string urls)
    {
        if (string.IsNullOrEmpty(_apiKey))
            return "[错误] 未配置 Tavily API Key（user-config.json 中 tavilyApiKey 或 skillEnv.TAVILY_API_KEY）。";

        var urlList = urls.Split(new[] { ',', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(u => u.Trim())
            .Where(u => u.Length > 0)
            .ToList();
        if (urlList.Count == 0)
            return "[错误] 请提供至少一个 URL。";

        var body = new { api_key = _apiKey, urls = urlList };

        try
        {
            using var resp = await SharedHttp.PostAsJsonAsync("https://api.tavily.com/extract", body);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                return $"[错误] Tavily 提取失败 ({(int)resp.StatusCode}): {err}";
            }

            var data = await resp.Content.ReadFromJsonAsync<TavilyExtractResponse>();
            if (data?.Results == null)
                return "[错误] Tavily 返回为空。";

            var sb = new System.Text.StringBuilder();
            foreach (var r in data.Results)
            {
                var url = (r.Url ?? "").Trim();
                var content = (r.RawContent ?? "").Trim();
                sb.AppendLine($"# {url}");
                sb.AppendLine(content.Length > 0 ? content : "(未提取到内容)");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
            if (data.FailedResults?.Count > 0)
            {
                sb.AppendLine("## 失败的 URL");
                foreach (var f in data.FailedResults)
                    sb.AppendLine($"- {f.Url}: {f.Error}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Tavily Extract error");
            return $"[错误] Tavily 提取异常: {ex.Message}";
        }
    }

    private sealed class TavilySearchResponse
    {
        [JsonPropertyName("answer")]
        public string? Answer { get; set; }
        [JsonPropertyName("results")]
        public List<TavilyResult>? Results { get; set; }
    }

    private sealed class TavilyResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }
        [JsonPropertyName("url")]
        public string? Url { get; set; }
        [JsonPropertyName("content")]
        public string? Content { get; set; }
        [JsonPropertyName("score")]
        public double? Score { get; set; }
    }

    private sealed class TavilyExtractResponse
    {
        [JsonPropertyName("results")]
        public List<TavilyExtractResult>? Results { get; set; }
        [JsonPropertyName("failed_results")]
        public List<TavilyFailedResult>? FailedResults { get; set; }
    }

    private sealed class TavilyExtractResult
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
        [JsonPropertyName("raw_content")]
        public string? RawContent { get; set; }
    }

    private sealed class TavilyFailedResult
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
