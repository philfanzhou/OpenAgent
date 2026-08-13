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
    private readonly LlmProfileRefresher _llmProfileRefresher;
    private readonly LegacyMessageHandler _legacyMessageHandler;
    private readonly ConfigSnapshot _snapshot;
    private readonly ILogger _logger;

    public ConfigUpdateDispatcher(
        FullConfigRefresher fullConfigRefresher,
        LlmProfileRefresher llmProfileRefresher,
        LegacyMessageHandler legacyMessageHandler,
        ConfigSnapshot snapshot,
        ILogger<ConfigUpdateDispatcher> logger)
    {
        _logger = logger;
        _fullConfigRefresher = fullConfigRefresher;
        _llmProfileRefresher = llmProfileRefresher;
        _legacyMessageHandler = legacyMessageHandler;
        _snapshot = snapshot;
    }

    internal bool Process(string channel, string message)
    {
        try
        {
            EngineLog.HotReloadMessageReceived(_logger, channel, message);
            if (string.IsNullOrWhiteSpace(message))
            {
                EngineLog.HotReloadEmptyPayloadIgnored(_logger, channel);
                return false;
            }

            if (!LooksLikeJson(message))
            {
                return _legacyMessageHandler.Process(channel, message);
            }

            var update = JsonSerializer.Deserialize<ConfigUpdate>(message, JsonOptions);
            if (update == null)
            {
                EngineLog.HotReloadParseNullResult(_logger);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(update.ResourceType))
            {
                return ProcessResourceUpdate(update);
            }

            // FullSync is a broadcast signal and requires no AgentId
            if (string.Equals(update.Type, "FullSync", StringComparison.OrdinalIgnoreCase))
            {
                _snapshot.Clear();
                EngineLog.HotReloadFullSyncSnapshotCleared(_logger);
                return true;
            }

            if (string.IsNullOrWhiteSpace(update.AgentId))
            {
                EngineLog.HotReloadMissingAgentId(_logger);
                return false;
            }

            bool refreshed = _fullConfigRefresher.Refresh(update.AgentId);
            if (refreshed)
            {
                EngineLog.HotReloadFullConfigReloaded(_logger, update.AgentId);
            }

            return refreshed;
        }
        catch (Exception exception)
        {
            EngineLog.HotReloadProcessError(_logger, exception, message);
            return false;
        }
    }

    private bool ProcessResourceUpdate(ConfigUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.ResourceId))
        {
            EngineLog.HotReloadMissingResourceId(_logger, update.ResourceType!);
            return false;
        }

        if (string.Equals(
                update.ResourceType,
                ConfigUpdate.AgentResourceType,
                StringComparison.OrdinalIgnoreCase))
        {
            bool refreshed = _fullConfigRefresher.Refresh(update.ResourceId);
            if (refreshed)
            {
                EngineLog.HotReloadFullConfigReloaded(_logger, update.ResourceId);
            }

            return refreshed;
        }

        if (string.Equals(
                update.ResourceType,
                ConfigUpdate.LlmResourceType,
                StringComparison.OrdinalIgnoreCase))
        {
            return _llmProfileRefresher.Refresh(update.ResourceId);
        }

        EngineLog.HotReloadUnknownResourceType(_logger, update.ResourceType!);
        return false;
    }

    private static bool LooksLikeJson(string payload)
    {
        var trimmed = payload.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal)
            || trimmed.StartsWith("[", StringComparison.Ordinal);
    }
}
