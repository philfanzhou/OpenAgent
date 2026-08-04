using Microsoft.Extensions.Logging;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Reload;

internal sealed class LegacyMessageHandler
{
    private readonly FullConfigRefresher _refresher;
    private readonly ILogger<LegacyMessageHandler> _logger;

    public LegacyMessageHandler(
        FullConfigRefresher refresher,
        ILogger<LegacyMessageHandler> logger)
    {
        _refresher = refresher;
        _logger = logger;
    }

    internal void Process(string channel, string message)
    {
        var payload = message.Trim();
        if (string.IsNullOrWhiteSpace(payload))
        {
            EngineLog.HotReloadLegacyBlankPayloadIgnored(_logger, channel);
            return;
        }

        if (string.Equals(channel, HotReloadService.CurrentUpdatesChannel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(channel, "agent:config:changed", StringComparison.OrdinalIgnoreCase))
        {
            if (_refresher.Refresh(payload))
            {
                EngineLog.HotReloadLegacyRefreshed(_logger, channel, payload);
            }

            return;
        }

        EngineLog.HotReloadLegacyNotificationReceived(_logger, channel, payload);
    }
}
