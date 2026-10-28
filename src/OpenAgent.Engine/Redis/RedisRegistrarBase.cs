using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Redis;

internal abstract class RedisRegistrarBase<TItem> : IHostedService
    where TItem : class
{
    private readonly IRedisConnectionProvider _redis;
    private readonly ILogger _logger;

    protected RedisRegistrarBase(IRedisConnectionProvider redis, ILogger logger)
    {
        _redis = redis;
        _logger = logger;
    }

    protected abstract string RegistrarName { get; }
    protected abstract string IndexKey { get; }
    protected abstract string ItemKeyPrefix { get; }

    protected abstract TItem? Deserialize(string json);
    protected abstract string? GetItemId(TItem item);
    protected abstract void Register(TItem item);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_redis.IsAvailable)
        {
            EngineLog.RedisRegistrarSkipped(_logger, RegistrarName);
            return;
        }

        await LoadFromRedisAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task LoadFromRedisAsync()
    {
        var itemIds = new List<string>();

        try
        {
            var members = await _redis.SetMembersAsync(IndexKey);
            if (members != null && members.Length > 0)
            {
                foreach (var member in members)
                {
                    if (!member.IsNullOrEmpty)
                    {
                        itemIds.Add(member.ToString());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            EngineLog.RedisRegistrarIndexReadFailed(_logger, ex, RegistrarName);
            return;
        }

        if (itemIds.Count == 0)
        {
            EngineLog.RedisRegistrarNoneFound(_logger, RegistrarName);
            return;
        }

        var registered = 0;
        foreach (var itemId in itemIds)
        {
            var itemJson = _redis.StringGet($"{ItemKeyPrefix}:{itemId}");
            if (itemJson.IsNullOrEmpty) continue;

            try
            {
                var item = Deserialize(itemJson.ToString());
                if (item == null || string.IsNullOrEmpty(GetItemId(item))) continue;

                Register(item);
                registered++;

                EngineLog.RedisRegistrarRegistered(_logger, RegistrarName, GetItemId(item)!);
            }
            catch (Exception ex)
            {
                EngineLog.RedisRegistrarRegisterFailed(_logger, ex, RegistrarName, itemId);
            }
        }

        EngineLog.RedisRegistrarComplete(_logger, RegistrarName, registered);
    }
}
