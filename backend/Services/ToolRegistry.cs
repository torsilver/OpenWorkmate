using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.AI;

namespace OfficeCopilot.Server.Services;

/// <summary>
/// <see cref="AITool"/> 注册表。
/// 按 (pluginName, functionName) 组织，供 <see cref="ClientTypeToolFilter"/> 过滤和 MAF Agent 消费。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, Dictionary<string, AITool>> _plugins = new(StringComparer.OrdinalIgnoreCase);

    public void Clear() => _plugins.Clear();

    public void Register(string pluginName, string functionName, AITool tool)
    {
        if (!_plugins.TryGetValue(pluginName, out var funcs))
        {
            funcs = new Dictionary<string, AITool>(StringComparer.OrdinalIgnoreCase);
            _plugins[pluginName] = funcs;
        }
        funcs[functionName] = tool;
    }

    /// <summary>通过反射扫描实例的 <c>[ToolFunction]</c> 方法，用 <see cref="AIFunctionFactory"/> 创建 <see cref="AITool"/>。</summary>
    /// <remarks>
    /// Microsoft.Extensions.AI 10.4.0 的 <c>AIFunctionFactoryOptions</c> 在公开 API 中未提供针对工具参数体的 <c>JsonSerializerOptions</c>（如字符串布尔宽松转换）；
    /// 绑定期类型错误会以 <see cref="System.Text.Json.JsonException"/> 抛出，由中间件 <c>ToolInvocationMiddleware</c> 转为模型可读说明，并在各插件中对易错标量使用 <c>ToolScalarArgumentParser</c>。
    /// 可选参数若用 <see cref="System.Text.Json.JsonElement"/> 做宽松解析，须写 <c>JsonElement? … = null</c>，勿用 <c>JsonElement … = default</c>：<c>AIFunctionFactory</c> 生成 JSON Schema 时会序列化默认值，<c>default(JsonElement)</c> 为 <c>Undefined</c> 会导致 <c>JsonElementConverter.Write</c> 抛错、宿主无法启动。
    /// </remarks>
    public void RegisterPluginFromObject(object instance, string pluginName)
    {
        var type = instance.GetType();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var tfAttr = method.GetCustomAttribute<ToolFunctionAttribute>();
            if (tfAttr == null) continue;
            var name = tfAttr.Name ?? method.Name;
            var desc = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
            var aiFunc = AIFunctionFactory.Create(method, instance, new AIFunctionFactoryOptions
            {
                Name = name,
                Description = desc
            });
            Register(pluginName, name, aiFunc);
        }
    }

    /// <summary>从实例类型上的 <see cref="CopilotPluginIdAttribute"/> 读取插件 Id 并注册工具（内置插件须标注该特性）。</summary>
    /// <exception cref="InvalidOperationException">类型上缺少 <see cref="CopilotPluginIdAttribute"/>。</exception>
    public void RegisterPluginFromObject(object instance)
    {
        var type = instance.GetType();
        var attr = type.GetCustomAttribute<CopilotPluginIdAttribute>();
        if (attr is null)
            throw new InvalidOperationException(
                $"类型 {type.FullName} 未标注 [CopilotPluginId(\"...\")]，无法使用无参 RegisterPluginFromObject。");
        RegisterPluginFromObject(instance, attr.Id);
    }

    public void RegisterPlugin(string pluginName, IEnumerable<AITool> tools)
    {
        foreach (var tool in tools)
        {
            var name = tool.Name ?? tool.GetHashCode().ToString();
            Register(pluginName, name, tool);
        }
    }

    public IReadOnlyList<AITool> GetAllTools()
    {
        var list = new List<AITool>();
        foreach (var funcs in _plugins.Values)
            list.AddRange(funcs.Values);
        return list;
    }

    /// <summary>返回 (pluginName, functionName, tool) 元组，用于工具选择和过滤。</summary>
    public IReadOnlyList<(string Plugin, string Function, AITool Tool)> GetAllWithMetadata()
    {
        var list = new List<(string, string, AITool)>();
        foreach (var (plugin, funcs) in _plugins)
        {
            foreach (var (func, tool) in funcs)
                list.Add((plugin, func, tool));
        }
        return list;
    }

    /// <summary>按 clientType 过滤后的工具列表（委托 <see cref="ClientTypeToolFilter.IsAllowed"/>）。</summary>
    public IReadOnlyList<AITool> GetAllowedTools(string? clientType, string? sessionId)
    {
        var list = new List<AITool>();
        foreach (var (plugin, funcs) in _plugins)
        {
            foreach (var (func, tool) in funcs)
            {
                if (ClientTypeToolFilter.IsAllowed(plugin, func, clientType, sessionId))
                    list.Add(tool);
            }
        }
        return list;
    }

    /// <summary>按 (PluginName, FunctionName) 对查找对应的 AITool。</summary>
    public AITool? FindTool(string pluginName, string functionName)
    {
        if (_plugins.TryGetValue(pluginName, out var funcs) && funcs.TryGetValue(functionName, out var tool))
            return tool;
        return null;
    }

    public IReadOnlyList<string> GetPluginNames() => _plugins.Keys.ToList();

    /// <summary>返回指定插件的所有 (pluginName, functionName, tool) 元组。</summary>
    public IReadOnlyList<(string Plugin, string Function, AITool Tool)> GetToolsByPlugin(string pluginName)
    {
        if (!_plugins.TryGetValue(pluginName, out var funcs))
            return Array.Empty<(string, string, AITool)>();
        var list = new List<(string, string, AITool)>(funcs.Count);
        foreach (var (func, tool) in funcs)
            list.Add((pluginName, func, tool));
        return list;
    }

    public IReadOnlyList<(string Plugin, string Function)> GetAllFunctionPairs()
    {
        var list = new List<(string, string)>();
        foreach (var (plugin, funcs) in _plugins)
        {
            foreach (var func in funcs.Keys)
                list.Add((plugin, func));
        }
        return list;
    }

    /// <summary>按 functionName 反查 pluginName（用于 MAF middleware 中获取 plugin 分组信息）。</summary>
    public bool TryGetPluginName(string functionName, out string pluginName)
    {
        foreach (var (plugin, funcs) in _plugins)
        {
            if (funcs.ContainsKey(functionName))
            {
                pluginName = plugin;
                return true;
            }
        }
        pluginName = "";
        return false;
    }

    /// <summary>同一裸函数名出现在多个插件时，<c>tool_calls</c> 仅用裸名可能歧义；供检索提示与测试。</summary>
    public IReadOnlyList<(string FunctionName, IReadOnlyList<string> PluginNames)> GetBareFunctionNameCollisions()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (plugin, func, _) in GetAllWithMetadata())
        {
            if (!map.TryGetValue(func, out var list))
            {
                list = new List<string>();
                map[func] = list;
            }

            if (!list.Exists(p => string.Equals(p, plugin, StringComparison.OrdinalIgnoreCase)))
                list.Add(plugin);
        }

        return map
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => (kv.Key, (IReadOnlyList<string>)kv.Value.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(t => t.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
