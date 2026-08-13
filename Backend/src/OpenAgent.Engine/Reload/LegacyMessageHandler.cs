using Microsoft.Extensions.Logging;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Reload;

internal sealed class LegacyMessageHandler
{
    private readonly FullConfigRefresher _refresher;
    private readonly LlmProfileRefresher _llmProfileRefresher;
    private readonly ILogger<LegacyMessageHandler> _logger;

    public LegacyMessageHandler(
        FullConfigRefresher refresher,
        LlmProfileRefresher llmProfileRefresher,
        ILogger<LegacyMessageHandler> logger)
    {
        _refresher = refresher;
        _llmProfileRefresher = llmProfileRefresher;
        _logger = logger;
    }

    internal bool Process(string channel, string message)
    {
        var payload = message.Trim();
        if (string.IsNullOrWhiteSpace(payload))
        {
            EngineLog.HotReloadLegacyBlankPayloadIgnored(_logger, channel);
            return false;
        }

        if (string.Equals(channel, HotReloadService.CurrentUpdatesChannel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(channel, "agent:config:changed", StringComparison.OrdinalIgnoreCase))
        {
            bool refreshed = _refresher.Refresh(payload);
            if (refreshed)
            {
                EngineLog.HotReloadLegacyRefreshed(_logger, channel, payload);
            }

            return refreshed;
        }

        if (string.Equals(channel, "llm:registry:changed", StringComparison.OrdinalIgnoreCase))
        {
            return _llmProfileRefresher.Refresh(payload);
        }

        EngineLog.HotReloadLegacyNotificationReceived(_logger, channel, payload);
        return true;
    }
}
