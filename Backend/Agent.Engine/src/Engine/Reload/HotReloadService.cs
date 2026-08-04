using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Observability;
using OpenAgent.Engine.Reload;
using StackExchange.Redis;

namespace OpenAgent.Engine.Reload;

internal sealed class HotReloadService : BackgroundService
{
    internal const string CurrentUpdatesChannel = "agent:config:updates";
    internal static readonly string[] LegacyChannels =
    [
        "agent:config:changed",
        "skill:registry:changed",
        "llm:registry:changed",
        "rag:registry:changed",
        "engine:config:changed"
    ];

    private readonly IRedisConnectionProvider _redis;
    private readonly ConfigUpdateDispatcher _dispatcher;
    private readonly ILogger<HotReloadService> _logger;

    public HotReloadService(
        IRedisConnectionProvider redis,
        ConfigUpdateDispatcher dispatcher,
        ILogger<HotReloadService> logger)
    {
        _redis = redis;
        _logger = logger;
        _dispatcher = dispatcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Subscribe(CurrentUpdatesChannel);
        foreach (var channelName in LegacyChannels)
        {
            Subscribe(channelName);
        }

        EngineLog.HotReloadSubscribed(_logger);
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    internal void ProcessMessage(string channel, string message)
    {
        _dispatcher.Process(channel, message);
    }

    private void Subscribe(string channelName)
    {
        var channel = RedisChannel.Literal(channelName);
        EngineLog.HotReloadSubscribingChannel(_logger, channel.ToString());
        _redis.Subscribe(channel, (redisChannel, value) =>
        {
            try
            {
                _dispatcher.Process(redisChannel.ToString(), value.ToString());
            }
            catch (Exception exception)
            {
                EngineLog.HotReloadProcessMessageError(
                    _logger, exception, redisChannel.ToString());
            }
        });
    }
}
