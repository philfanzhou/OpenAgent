using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Observability;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Engine.Models;

internal class ConfigSnapshot : IConfigSnapshot
{
    private const string AbsoluteExpirationMinutesConfigKey = "ConfigSnapshot:AbsoluteExpirationMinutes";

    private static readonly string[] FullConfigTypes =
    [
        "FullAgentConfig",
        "LLMSettings",
        "RAGSettings",
        "MCPSettings",
        "SkillsSettings"
    ];

    private readonly IMemoryCache _cache;
    private readonly MemoryCache? _strongCache;
    private readonly TimeSpan _absoluteExpiration;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    public ConfigSnapshot(
        IOptions<ConfigSnapshotOptions> options,
        IMemoryCache memoryCache,
        ILogger<ConfigSnapshot> logger)
    {
        if (options.Value.AbsoluteExpirationMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                AbsoluteExpirationMinutesConfigKey,
                options.Value.AbsoluteExpirationMinutes,
                $"{AbsoluteExpirationMinutesConfigKey} must be greater than 0. A non-positive TTL would make every cache entry immediately stale.");
        }

        _cache = memoryCache;
        // Cast is safe in practice (AddMemoryCache registers MemoryCache), but keep a
        // fallback so a custom IMemoryCache implementation degrades gracefully.
        _strongCache = memoryCache as MemoryCache;
        _absoluteExpiration = TimeSpan.FromMinutes(options.Value.AbsoluteExpirationMinutes);
        _logger = logger;
    }

    public T? GetConfig<T>(string key)
    {
        _cache.TryGetValue(key, out T? value);
        return value;
    }

    public bool TryGetConfig<T>(string key, out T? config)
    {
        return _cache.TryGetValue(key, out config);
    }

    public void SetConfig<T>(string key, T value)
    {
        lock (_lock)
        {
            _cache.Set(key, value, CreateEntryOptions());
        }
    }

    public T? GetConfig<T>(string agentId, string configType)
    {
        var cacheKey = BuildCacheKey(agentId, configType);
        _cache.TryGetValue(cacheKey, out T? value);
        return value;
    }

    public void SetConfig<T>(string agentId, string configType, T value)
    {
        var cacheKey = BuildCacheKey(agentId, configType);
        lock (_lock)
        {
            _cache.Set(cacheKey, value, CreateEntryOptions());
        }
    }

    public bool TryGetConfig<T>(string agentId, string configType, out T? config)
    {
        var cacheKey = BuildCacheKey(agentId, configType);
        return _cache.TryGetValue(cacheKey, out config);
    }

    public void SetFullConfig(string agentId, AgentConfig config)
    {
        SetConfig(agentId, "FullAgentConfig", config);
        SetConfig(agentId, "LLMSettings", config.Llm);
        SetConfig(agentId, "RAGSettings", config.Rag);
        SetConfig(agentId, "MCPSettings", config.Mcp);
        SetConfig(agentId, "SkillsSettings", config.Skills);
    }

    public void Evict(string agentId)
    {
        lock (_lock)
        {
            foreach (var configType in FullConfigTypes)
            {
                _cache.Remove(BuildCacheKey(agentId, configType));
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_strongCache != null)
            {
                // .NET 7+ deterministic clear — matches IConfigSnapshot.Clear() contract.
                _strongCache.Clear();
            }
            else
            {
                // Fallback for non-MemoryCache implementations: best-effort eviction.
                // Compact(1.0) is intentionally not used — its percentage-based reclaim is
                // "best effort", not a deterministic clear.
                EngineLog.ConfigSnapshotClearFallback(_logger);
            }
        }
    }

    private MemoryCacheEntryOptions CreateEntryOptions()
    {
        return new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _absoluteExpiration
        };
    }

    private static string BuildCacheKey(string agentId, string configType)
    {
        return $"agent:{agentId}:config:{configType}";
    }
}
