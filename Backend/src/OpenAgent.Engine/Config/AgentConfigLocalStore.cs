using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAgent.Contracts.Models;

namespace OpenAgent.Engine.Config;

/// <summary>
/// Development-only configuration storage used while the Engine is running without Redis.
/// Values are serialized before they are stored so endpoint redaction cannot mutate the store.
/// </summary>
internal sealed class AgentConfigLocalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    internal AgentConfigEntity? Get(string agentId)
    {
        return _values.TryGetValue(agentId, out string? value)
            ? JsonSerializer.Deserialize<AgentConfigEntity>(value, JsonOptions)
            : null;
    }

    internal IReadOnlyList<AgentConfigEntity> List()
    {
        return _values.Values
            .Select(value => JsonSerializer.Deserialize<AgentConfigEntity>(value, JsonOptions))
            .OfType<AgentConfigEntity>()
            .ToArray();
    }

    internal AgentConfigEntity? Save(
        string agentId,
        AgentConfigEntity entity,
        string? expectedVersion)
    {
        entity.AgentId = agentId;
        AgentConfigEntity? current = Get(agentId);
        if (current != null
            && !string.IsNullOrWhiteSpace(current.TenantId)
            && !string.Equals(current.TenantId, entity.TenantId, StringComparison.Ordinal))
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(expectedVersion)
            && !string.Equals(current?.CurrentVersion, expectedVersion, StringComparison.Ordinal))
        {
            return null;
        }

        entity.CurrentVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        string payload = JsonSerializer.Serialize(entity, JsonOptions);
        if (current == null)
        {
            if (!_values.TryAdd(agentId, payload)) return null;
        }
        else if (!_values.TryUpdate(agentId, payload, JsonSerializer.Serialize(current, JsonOptions)))
        {
            return null;
        }

        return JsonSerializer.Deserialize<AgentConfigEntity>(payload, JsonOptions);
    }
}
