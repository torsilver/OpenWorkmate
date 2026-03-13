using LLama;
using LLama.Common;
using LLama.Native;
using LLamaSharp.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.ChatCompletion;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// 提供进程内嵌入式 GGUF 模型用于工具选择兜底；路径来自配置或默认 Models 目录，懒加载，路径变更时重新加载。
/// </summary>
public interface IEmbeddedToolSelectionModel
{
    /// <summary>若已加载则返回 IChatCompletionService，否则尝试加载后返回；失败返回 null。</summary>
    IChatCompletionService? GetChatCompletionService();
}

/// <inheritdoc />
public sealed class EmbeddedToolSelectionModel : IEmbeddedToolSelectionModel
{
    public const string ServiceId = "EmbeddedToolSelection";

    private readonly ConfigService _configService;
    private readonly ILogger<EmbeddedToolSelectionModel> _logger;
    private readonly object _loadLock = new();
    private string? _loadedPath;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private InteractiveExecutor? _executor;
    private IChatCompletionService? _chatCompletion;

    public EmbeddedToolSelectionModel(ConfigService configService, ILogger<EmbeddedToolSelectionModel> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public IChatCompletionService? GetChatCompletionService()
    {
        var path = ResolveModelPath();
        if (string.IsNullOrEmpty(path))
        {
            _logger.LogDebug("EmbeddedToolSelection: no model path configured and no default file found.");
            return null;
        }

        lock (_loadLock)
        {
            if (string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase) && _chatCompletion != null)
                return _chatCompletion;

            TryUnload();
            if (!TryLoad(path))
                return null;
            _loadedPath = path;
            return _chatCompletion;
        }
    }

    private string? ResolveModelPath()
    {
        var configured = (_configService.Current.EmbeddedToolSelectionModelPath ?? "").Trim();
        if (configured.Length > 0 && File.Exists(configured))
            return Path.GetFullPath(configured);

        var modelsDir = Path.Combine(AppContext.BaseDirectory, "Models");
        if (!Directory.Exists(modelsDir))
            return null;
        var firstGguf = Directory.EnumerateFiles(modelsDir, "*.gguf", SearchOption.TopDirectoryOnly).FirstOrDefault();
        return firstGguf != null ? Path.GetFullPath(firstGguf) : null;
    }

    private static bool _cudaPreferenceSet;

    private bool TryLoad(string path)
    {
        try
        {
            // 双保险：若 Program 未先执行到，此处再设一次（必须在 ModelParams/LoadFromFile 之前）
            if (!_cudaPreferenceSet)
            {
                try { NativeLibraryConfig.All.WithCuda(true); } catch { /* 无 CUDA 或已加载 native 时忽略 */ }
                _cudaPreferenceSet = true;
            }
            var parameters = new ModelParams(path)
            {
                ContextSize = 1024,
                BatchSize = 256,
                Threads = 2,
                GpuLayerCount = 99,
            };
            _weights = LLamaWeights.LoadFromFile(parameters);
            _context = _weights.CreateContext(parameters);
            _executor = new InteractiveExecutor(_context);
            _chatCompletion = new LLamaSharpChatCompletion(_executor);
            _logger.LogInformation("EmbeddedToolSelection: loaded model from {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EmbeddedToolSelection: failed to load model from {Path}", path);
            TryUnload();
            return false;
        }
    }

    private void TryUnload()
    {
        _chatCompletion = null;
        try
        {
            (_executor as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EmbeddedToolSelection: dispose executor");
        }
        _executor = null;
        try
        {
            _context?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EmbeddedToolSelection: dispose context");
        }
        _context = null;
        try
        {
            _weights?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EmbeddedToolSelection: dispose weights");
        }
        _weights = null;
        _loadedPath = null;
    }
}
