using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Registry;

internal class RedisRegistry : IEngineRegistry, IDisposable
{
    internal const string RegistryIndexKey = "engine:registry:index";

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
            LastHeartbeat = DateTime.UtcNow,
            Intents = NormalizeTags(heartbeatOptions.Intents),
            Capabilities = NormalizeTags(heartbeatOptions.Capabilities)
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
            UpdateRoutingMetadata();
            string key = RegistryKey;
            string json = JsonSerializer.Serialize(_entry);

            bool stored = await _redis.StringSetAsync(key, json, GetTtl())
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (stored)
            {
                await _redis.SetAddAsync(RegistryIndexKey, _entry.EngineId)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            _isRegistered = stored;
            if (_isRegistered)
            {
                EngineLog.EngineRegistered(_logger, _entry.EngineId, _entry.Host, _entry.Port);
            }
            else
            {
                EngineLog.EngineRegisterStringSetFailed(_logger);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            UpdateRoutingMetadata();

            string key = RegistryKey;
            string json = JsonSerializer.Serialize(_entry);

            bool stored = await _redis.StringSetAsync(key, json, GetTtl())
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (stored)
            {
                await _redis.SetAddAsync(RegistryIndexKey, _entry.EngineId)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            _isRegistered = stored;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            string key = RegistryKey;
            await _redis.KeyDeleteAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false);
            await _redis.SetRemoveAsync(RegistryIndexKey, _entry.EngineId)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            _isRegistered = false;
            EngineLog.EngineDeregisteredFromRedis(_logger, _entry.EngineId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            EngineLog.EngineDeregisterFailed(_logger, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    private TimeSpan GetTtl()
    {
        var ttlSeconds = Math.Max(_options.CurrentValue.RegistryTtlSeconds, 1);
        return TimeSpan.FromSeconds(ttlSeconds);
    }

    private void UpdateRoutingMetadata()
    {
        HeartbeatOptions options = _options.CurrentValue;
        _entry.Intents = NormalizeTags(options.Intents);
        _entry.Capabilities = NormalizeTags(options.Capabilities);
    }

    private static string[] NormalizeTags(IEnumerable<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];

}
