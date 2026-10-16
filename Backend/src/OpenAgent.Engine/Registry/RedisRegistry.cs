using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Observability;
using OpenAgent.Router;

namespace OpenAgent.Engine.Registry;

internal class RedisRegistry : IEngineRegistry, IDisposable
{
    private readonly IRedisConnectionProvider _redis;
    private readonly EngineRegistryEntry _entry;
    private readonly ILogger<RedisRegistry> _logger;
    private readonly IOptionsMonitor<HeartbeatOptions> _options;
    private bool _isRegistered;
    private bool _disposed;
    private readonly LoadCollector _loadCollector = new();
    private readonly HttpClient? _routerClient;

    private string RegistryKey => EngineRegistryKeys.ForEngine(_entry.EngineId);

    public bool IsRegistered => _isRegistered;

    public RedisRegistry(
        IRedisConnectionProvider redis,
        IOptionsMonitor<HeartbeatOptions> options,
        ILogger<RedisRegistry> logger,
        HttpMessageHandler? routerHandler = null)
    {
        _logger = logger;
        _redis = redis;
        _options = options;

        var heartbeatOptions = _options.CurrentValue;
        if (!string.IsNullOrWhiteSpace(heartbeatOptions.RouterUrl))
        {
            _routerClient = routerHandler == null ? new HttpClient() : new HttpClient(routerHandler);
            _routerClient.BaseAddress = new Uri(heartbeatOptions.RouterUrl);
            _routerClient.Timeout = TimeSpan.FromSeconds(5);
        }

        _entry = new EngineRegistryEntry
        {
            EngineId = Guid.NewGuid().ToString("N")[..8],
            Host = string.IsNullOrWhiteSpace(heartbeatOptions.AdvertisedHost) ? Dns.GetHostName() : heartbeatOptions.AdvertisedHost,
            Port = heartbeatOptions.AdvertisedPort ?? 0,
            Load = 0,
            LastHeartbeat = DateTime.UtcNow,
            Intents = NormalizeTags(heartbeatOptions.Intents)
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

            bool stored = await StoreAsync(key, json, cancellationToken).ConfigureAwait(false);

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

            bool stored = await StoreAsync(key, json, cancellationToken).ConfigureAwait(false);

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
            await RemoveAsync(cancellationToken).ConfigureAwait(false);
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

    // Router registration needs the shared secret; without a RouterUrl the legacy Redis keys are written directly.
    private async Task<bool> StoreAsync(string key, string json, CancellationToken cancellationToken)
    {
        if (_routerClient != null)
        {
            using HttpRequestMessage request = CreateRouterRequest(HttpMethod.Put);
            request.Content = JsonContent.Create(new RegistrationRequest
            {
                BaseUrl = $"http://{_entry.Host}:{_entry.Port}",
                TtlSeconds = Math.Max(_options.CurrentValue.RegistryTtlSeconds, 1),
                Load = _entry.Load,
                Intents = _entry.Intents
            });
            using HttpResponseMessage response = await _routerClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        bool stored = await _redis.StringSetAsync(key, json, GetTtl())
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (stored)
        {
            await _redis.SetAddAsync(EngineRegistryKeys.Index, _entry.EngineId)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return stored;
    }

    private async Task RemoveAsync(CancellationToken cancellationToken)
    {
        if (_routerClient != null)
        {
            using HttpRequestMessage request = CreateRouterRequest(HttpMethod.Delete);
            using HttpResponseMessage response = await _routerClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return;
        }

        await _redis.KeyDeleteAsync(RegistryKey).WaitAsync(cancellationToken).ConfigureAwait(false);
        await _redis.SetRemoveAsync(EngineRegistryKeys.Index, _entry.EngineId)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRouterRequest(HttpMethod method)
    {
        HttpRequestMessage request = new(method, RegistrationRequest.Route(_entry.EngineId).TrimStart('/'));
        request.Headers.Add(RegistrationRequest.KeyHeader, _options.CurrentValue.RegistrationKey ?? string.Empty);
        return request;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _routerClient?.Dispose();
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
    }

    private static string[] NormalizeTags(IEnumerable<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];

}
