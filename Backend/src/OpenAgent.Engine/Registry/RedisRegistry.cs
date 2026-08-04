using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Observability;
using System.Net;
using System.Text.Json;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Registry;

namespace OpenAgent.Engine.Registry;

internal class RedisRegistry : IEngineRegistry, IDisposable
{
    private readonly IRedisConnectionProvider _redis;
    private readonly RegistryEntry _entry;
    private readonly ILogger<RedisRegistry> _logger;
    private readonly IOptionsMonitor<HeartbeatOptions> _options;
    private bool _isRegistered;
    private bool _disposed;
    private readonly LoadCollector _loadCollector = new();

    private string RegistryKey => $"engine:registry:{_entry.EngineId}";

    public bool IsRegistered => _isRegistered;

    public RedisRegistry(
        IRedisConnectionProvider redis,
        IOptionsMonitor<HeartbeatOptions> options,
        ILogger<RedisRegistry> logger)
    {
        _logger = logger;
        _redis = redis;
        _options = options;

        var heartbeatOptions = _options.CurrentValue;
        _entry = new RegistryEntry
        {
            EngineId = Guid.NewGuid().ToString("N")[..8],
            Host = string.IsNullOrWhiteSpace(heartbeatOptions.AdvertisedHost) ? Dns.GetHostName() : heartbeatOptions.AdvertisedHost,
            Port = heartbeatOptions.AdvertisedPort ?? 0,
            Load = 0,
            LastHeartbeat = DateTime.UtcNow
        };
    }

    internal void SetPort(int port)
    {
        if (_options.CurrentValue.AdvertisedPort.HasValue)
        {
            _entry.Port = _options.CurrentValue.AdvertisedPort.Value;
            return;
        }

        _entry.Port = port;
    }

    internal void SetHost(string host)
    {
        if (!string.IsNullOrWhiteSpace(host))
        {
            _entry.Host = host;
        }
    }

    internal void UpdateLoad(int load)
    {
        _entry.Load = load;
    }

    public async Task RegisterAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var key = RegistryKey;
            var json = JsonSerializer.Serialize(_entry);

            var success = await _redis.StringSetAsync(key, json, GetTtl());
            _isRegistered = success;
            if (success)
            {
                EngineLog.EngineRegistered(_logger, _entry.EngineId, _entry.Host, _entry.Port);
            }
            else
            {
                EngineLog.EngineRegisterStringSetFailed(_logger);
            }
        }
        catch (Exception ex)
        {
            EngineLog.EngineRegisterFailed(_logger, ex);
            _isRegistered = false;
        }
    }

    public async Task HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRegistered)
        {
            _isRegistered = false;
            return;
        }

        try
        {
            _entry.LastHeartbeat = DateTime.UtcNow;
            _entry.Load = _loadCollector.GetCurrentLoad();

            var key = RegistryKey;
            var json = JsonSerializer.Serialize(_entry);

            await _redis.StringSetAsync(key, json, GetTtl());
        }
        catch (Exception ex)
        {
            EngineLog.HeartbeatSendFailed(_logger, ex);
            _isRegistered = false;
        }
    }

    public async Task DeregisterAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var key = RegistryKey;
            await _redis.KeyDeleteAsync(key);
            _isRegistered = false;
            EngineLog.EngineDeregisteredFromRedis(_logger, _entry.EngineId);
        }
        catch (Exception ex)
        {
            EngineLog.EngineDeregisterFailed(_logger, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    private TimeSpan GetTtl()
    {
        var ttlSeconds = Math.Max(_options.CurrentValue.RegistryTtlSeconds, 1);
        return TimeSpan.FromSeconds(ttlSeconds);
    }

}
