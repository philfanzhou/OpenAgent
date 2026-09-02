using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Reload;
using OpenAgent.Engine.Reload.Dtos;
using StackExchange.Redis;

namespace OpenAgent.Engine.Config;

internal sealed class LlmProfileManagementService(
    IRedisConnectionProvider redis,
    ILlmRegistry registry,
    ConfigUpdateDispatcher configUpdates)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal async Task<IReadOnlyList<LlmProviderProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable)
        {
            return registry.GetAllProfiles();
        }

        try
        {
            RedisValue[] ids = await redis.SetMembersAsync("llm:published:index").ConfigureAwait(false);
            var profiles = new List<LlmProviderProfile>();
            foreach (RedisValue id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LlmProviderProfile? profile = await GetFromRedisAsync(id.ToString()).ConfigureAwait(false);
                if (profile != null)
                    profiles.Add(profile);
            }

            return profiles.Count > 0 ? profiles : registry.GetAllProfiles();
        }
        catch (RedisException)
        {
            return registry.GetAllProfiles();
        }
    }

    internal async Task<IReadOnlyList<LlmProviderProfile>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LlmProviderProfile> profiles = await ListAsync(cancellationToken).ConfigureAwait(false);
        // 调试用：空租户（存量）profile 视为全局可见。
        return profiles
            .Where(profile => string.IsNullOrWhiteSpace(profile.TenantId)
                || string.Equals(profile.TenantId, tenantId, StringComparison.Ordinal))
            .ToArray();
    }

    internal async Task<LlmProviderProfile?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable)
        {
            return registry.GetProfile(id);
        }

        try
        {
            return await GetFromRedisAsync(id).ConfigureAwait(false) ?? registry.GetProfile(id);
        }
        catch (RedisException)
        {
            return registry.GetProfile(id);
        }
    }

    internal async Task<LlmProviderProfile?> GetAsync(
        string id,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        LlmProviderProfile? profile = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        // 调试用：空租户（存量）profile 视为全局可见。
        return profile != null
            && (string.IsNullOrWhiteSpace(profile.TenantId)
                || string.Equals(profile.TenantId, tenantId, StringComparison.Ordinal))
            ? profile
            : null;
    }

    internal async Task<LlmProviderProfile> SaveAsync(
        LlmProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            throw new ArgumentException(
                "LLM profiles cannot persist inline API keys. Use ApiKeySecretRef.",
                nameof(profile));
        }
        if (!redis.IsAvailable)
        {
            registry.Register(profile);
            return profile;
        }

        string key = $"llm:registry:{profile.Id}";
        IDatabase database = redis.GetDatabase();
        RedisValue currentValue = await database.StringGetAsync(key).ConfigureAwait(false);
        LlmProviderProfile? current = currentValue.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<LlmProviderProfile>(currentValue.ToString(), JsonOptions);
        if (current != null
            && !string.IsNullOrWhiteSpace(current.TenantId)
            && !string.Equals(current.TenantId, profile.TenantId, StringComparison.Ordinal))
        {
            throw new TenantDataIsolationException(
                profile.TenantId,
                current.TenantId,
                "LLM profile does not belong to the authenticated tenant.");
        }

        string payload = JsonSerializer.Serialize(profile, JsonOptions);
        string notification = CreateNotification(
            profile.Id,
            ConfigUpdate.UpsertOperation);
        ITransaction transaction = database.CreateTransaction();
        transaction.AddCondition(currentValue.IsNullOrEmpty
            ? Condition.KeyNotExists(key)
            : Condition.StringEqual(key, currentValue));
        Task<bool> setTask = transaction.StringSetAsync(key, payload);
        Task<long> publishTask = transaction.PublishAsync(
            RedisChannel.Literal(HotReloadService.CurrentUpdatesChannel),
            notification);
        bool executed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!executed || !await setTask.ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Failed to save LLM profile '{profile.Id}' to Redis.");
        }

        await publishTask.ConfigureAwait(false);
        EnsureLocalReload(profile.Id, notification);
        await redis.SetAddAsync("llm:published:index", profile.Id).ConfigureAwait(false);
        return profile;
    }

    internal async Task<LlmProviderProfile> SaveAsync(
        LlmProviderProfile profile,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        LlmProviderProfile? existing = await GetAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        if (existing != null
            && !string.Equals(existing.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new TenantDataIsolationException(
                tenantId,
                existing.TenantId,
                "LLM profile does not belong to the authenticated tenant.");
        }

        profile.TenantId = tenantId;
        return await SaveAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!redis.IsAvailable)
        {
            return registry.Remove(id);
        }

        bool existedLocally = registry.GetProfile(id) != null;
        string notification = CreateNotification(id, ConfigUpdate.DeleteOperation);
        ITransaction transaction = redis.GetDatabase().CreateTransaction();
        Task<bool> deleteTask = transaction.KeyDeleteAsync($"llm:registry:{id}");
        Task<long> publishTask = transaction.PublishAsync(
            RedisChannel.Literal(HotReloadService.CurrentUpdatesChannel),
            notification);
        bool executed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!executed)
        {
            throw new InvalidOperationException($"Failed to delete LLM profile '{id}' from Redis.");
        }

        bool deleted = await deleteTask.ConfigureAwait(false);
        await publishTask.ConfigureAwait(false);
        EnsureLocalReload(id, notification);
        await redis.SetRemoveAsync("llm:published:index", id).ConfigureAwait(false);
        return deleted || existedLocally;
    }

    internal async Task<bool> DeleteAsync(
        string id,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        LlmProviderProfile? existing = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing == null
            || !string.Equals(existing.TenantId, tenantId, StringComparison.Ordinal))
        {
            return false;
        }

        return await DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LlmProviderProfile?> GetFromRedisAsync(string id)
    {
        RedisValue value = await redis.StringGetAsync($"llm:registry:{id}").ConfigureAwait(false);
        return value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<LlmProviderProfile>(value.ToString(), JsonOptions);
    }

    private static string CreateNotification(string profileId, string operation)
    {
        return JsonSerializer.Serialize(new ConfigUpdate
        {
            ResourceType = ConfigUpdate.LlmResourceType,
            ResourceId = profileId,
            Operation = operation,
            Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            Timestamp = DateTimeOffset.UtcNow
        }, ConfigUpdateDispatcher.JsonOptions);
    }

    private void EnsureLocalReload(string profileId, string notification)
    {
        if (!configUpdates.Process(HotReloadService.CurrentUpdatesChannel, notification))
        {
            throw new InvalidOperationException(
                $"LLM profile '{profileId}' was changed, but the local registry could not be refreshed.");
        }
    }
}
