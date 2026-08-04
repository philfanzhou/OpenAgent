using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Observability;
using OpenAgent.Engine.Reload.Dtos;

namespace OpenAgent.Engine.Reload;

internal sealed class ConfigUpdateDispatcher
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly FullConfigRefresher _fullConfigRefresher;
    private readonly LegacyMessageHandler _legacyMessageHandler;
    private readonly ConfigSnapshot _snapshot;
    private readonly ILogger _logger;

    public ConfigUpdateDispatcher(
        FullConfigRefresher fullConfigRefresher,
        LegacyMessageHandler legacyMessageHandler,
        ConfigSnapshot snapshot,
        ILogger<ConfigUpdateDispatcher> logger)
    {
        _logger = logger;
        _fullConfigRefresher = fullConfigRefresher;
        _legacyMessageHandler = legacyMessageHandler;
        _snapshot = snapshot;
    }

    internal void Process(string channel, string message)
    {
        try
        {
            EngineLog.HotReloadMessageReceived(_logger, channel, message);
            if (string.IsNullOrWhiteSpace(message))
            {
                EngineLog.HotReloadEmptyPayloadIgnored(_logger, channel);
                return;
            }

            if (!LooksLikeJson(message))
            {
                _legacyMessageHandler.Process(channel, message);
                return;
            }

            var update = JsonSerializer.Deserialize<ConfigUpdate>(message, JsonOptions);
            if (update == null)
            {
                EngineLog.HotReloadParseNullResult(_logger);
                return;
            }

            // FullSync is a broadcast signal and requires no AgentId
            if (string.Equals(update.Type, "FullSync", StringComparison.OrdinalIgnoreCase))
            {
                _snapshot.Clear();
                EngineLog.HotReloadFullSyncSnapshotCleared(_logger);
                return;
            }

            if (string.IsNullOrWhiteSpace(update.AgentId))
            {
                EngineLog.HotReloadMissingAgentId(_logger);
                return;
            }

            if (_fullConfigRefresher.Refresh(update.AgentId))
            {
                EngineLog.HotReloadFullConfigReloaded(_logger, update.AgentId);
            }
        }
        catch (Exception exception)
        {
            EngineLog.HotReloadProcessError(_logger, exception, message);
        }
    }

    private static bool LooksLikeJson(string payload)
    {
        var trimmed = payload.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal)
            || trimmed.StartsWith("[", StringComparison.Ordinal);
    }
}
