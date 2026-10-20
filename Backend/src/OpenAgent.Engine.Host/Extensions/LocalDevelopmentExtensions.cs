using System.Collections.Concurrent;
using System.Text.Json;
using OpenAgent.Capabilities.Skills;
using OpenAgent.Application.Conversation.Store;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Skills;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenAgent.Engine.Host.Extensions;

/// <summary>
/// 本地开发组合（LocalDevelopment:UseInMemoryStores，仅 Development 生效）：
/// 用进程内实现替换 PostgreSQL/对象存储/Redis 依赖，重启即失。ACP 执行链路
/// （opencode 进程、工具桥、模型调用、SSE）保持完全真实；仅存储是内存的。
/// 供 VS/dotnet run 在无 Docker、无外部基础设施的机器上一键调试。
/// </summary>
internal static class LocalDevelopmentExtensions
{
    internal static IServiceCollection AddLocalInMemoryStores(this IServiceCollection services)
    {
        // 会话存储必须单例（跨请求保留会话）；其 ICurrentUserContext 依赖用固定的
        // 开发身份满足——匿名开发认证下每个请求本来就是同一身份。
        services.AddSingleton<IConversationStore>(_ => new InMemoryConversationStore(new LocalDevelopmentCurrentUserContext()));
        services.AddSingleton<InMemoryFileAssetRepository>();
        services.AddSingleton<IFileAssetRepository>(provider => provider.GetRequiredService<InMemoryFileAssetRepository>());
        services.AddSingleton<IFileObjectStore, LocalInMemoryFileObjectStore>();
        services.AddSingleton<ILlmInteractionStore, NullLlmInteractionStore>();
        services.AddSingleton<IFileShareRepository, LocalInMemoryFileShareRepository>();
        services.AddSingleton<ISkillDefinitionRepository, LocalInMemorySkillDefinitionRepository>();
        services.AddSingleton<IThirdPartyApiKeyIdentityResolver, EmptyThirdPartyApiKeyResolver>();
        services.AddSingleton<InMemoryAgentConfigRepository>();
        services.AddSingleton<IAgentConfigRepository>(provider => provider.GetRequiredService<InMemoryAgentConfigRepository>());
        services.AddSingleton<InMemoryLlmConfigRepository>();
        services.AddSingleton<ILlmConfigRepository>(provider => provider.GetRequiredService<InMemoryLlmConfigRepository>());
        return services;
    }

    /// <summary>
    /// Engine 组合会把技能目录替换为 Redis 版；本地档改回 Application 的内存目录，
    /// 必须在 AddAgentEngine 之后调用（后注册者胜出）。
    /// </summary>
    internal static IServiceCollection UseLocalInMemorySkillCatalog(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ISkillCatalog, SkillCatalog>());
        return services;
    }
}

/// <summary>与 AllowDevelopmentAnonymous 一致的开发身份：development-user / development。</summary>
internal sealed class LocalDevelopmentCurrentUserContext : ICurrentUserContext
{
    public string UserId => "development-user";
    public string? Username => "development-user";
    public string? Email => null;
    public string? TenantId => "development";
    public IReadOnlyList<string> Groups => [];
    public IReadOnlyList<string> Roles => [];
    public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Audience => [];
    public bool IsAuthenticated => true;
    public bool IsInRole(string role) => true;
}

internal sealed class NullLlmInteractionStore : ILlmInteractionStore
{
    public Task RecordAsync(LlmInteractionRecord record, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<LlmInteractionRecord>> ListAsync(
        string tenantId,
        string conversationId,
        int skip,
        int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LlmInteractionRecord>>([]);
}

internal sealed class LocalInMemoryFileObjectStore : IFileObjectStore
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = [];

    public Task<FileObjectReference> WriteAsync(FileObjectWriteRequest request, Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        string objectKey = $"local/{request.FileId}";
        _objects[objectKey] = buffer.ToArray();
        return Task.FromResult(new FileObjectReference { ObjectKey = objectKey });
    }

    public Task<byte[]> ReadAsync(string objectKey, CancellationToken cancellationToken) =>
        Task.FromResult(_objects.TryGetValue(objectKey, out byte[]? data)
            ? data
            : throw new FileNotFoundException(objectKey));

    public Task<byte[]> ReadAsync(string objectKey, long maxBytes, CancellationToken cancellationToken) =>
        ReadAsync(objectKey, cancellationToken);

    public Task<FileObjectAccessReference> CreateReadUrlAsync(string objectKey, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Local in-memory object storage does not issue read URLs.");

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        _objects.TryRemove(objectKey, out _);
        return Task.CompletedTask;
    }
}

internal sealed class LocalInMemoryFileShareRepository : IFileShareRepository
{
    private readonly ConcurrentDictionary<string, FileShareLinkRecord> _records = [];

    public Task CreateAsync(FileShareLinkRecord record, CancellationToken cancellationToken)
    {
        _records[record.ShareIdHash] = record;
        return Task.CompletedTask;
    }

    public Task<FileShareLinkRecord?> GetAsync(string shareIdHash, CancellationToken cancellationToken) =>
        Task.FromResult(_records.TryGetValue(shareIdHash, out FileShareLinkRecord? record) ? record : null);

    public Task<bool> TryRedeemAsync(string shareIdHash, CancellationToken cancellationToken)
    {
        if (!_records.TryGetValue(shareIdHash, out FileShareLinkRecord? record)
            || record.ExpiresAt <= DateTimeOffset.UtcNow
            || record.DownloadCount >= record.MaxDownloads)
        {
            return Task.FromResult(false);
        }
        record.DownloadCount++;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<FileShareLinkRecord>> ListByOwnerAsync(
        string tenantId,
        string ownerId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FileShareLinkRecord>>(
        [
            .. _records.Values
                .Where(record => record.TenantId == tenantId
                    && record.OwnerUserId == ownerId
                    && record.ExpiresAt > DateTimeOffset.UtcNow
                    && record.DownloadCount < record.MaxDownloads)
                .OrderByDescending(record => record.CreatedAt)
        ]);

    public Task<bool> TryRevokeAsync(
        string shareIdHash,
        string tenantId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        if (!_records.TryGetValue(shareIdHash, out FileShareLinkRecord? record)
            || record.TenantId != tenantId
            || record.OwnerUserId != ownerId
            || record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return Task.FromResult(false);
        }
        record.ExpiresAt = DateTimeOffset.UtcNow;
        return Task.FromResult(true);
    }
}

internal sealed class LocalInMemorySkillDefinitionRepository : ISkillDefinitionRepository
{
    private readonly ConcurrentDictionary<string, SkillInstanceConfig> _skills = [];

    public Task<SkillInstanceConfig?> GetAsync(string tenantId, string skillId, CancellationToken cancellationToken) =>
        Task.FromResult(_skills.TryGetValue($"{tenantId}/{skillId}", out SkillInstanceConfig? skill) ? skill : null);

    public Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(string tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SkillInstanceConfig>>([.. _skills.Values.Where(skill => skill.TenantId == tenantId)]);

    public Task UpsertAsync(SkillInstanceConfig skill, CancellationToken cancellationToken)
    {
        _skills[$"{skill.TenantId}/{skill.Id}"] = skill;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string tenantId, string skillId, CancellationToken cancellationToken) =>
        Task.FromResult(_skills.TryRemove($"{tenantId}/{skillId}", out _));
}

internal sealed class EmptyThirdPartyApiKeyResolver : IThirdPartyApiKeyIdentityResolver
{
    public Task<ThirdPartyApiKeyIdentity?> ResolveAsync(string apiKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<ThirdPartyApiKeyIdentity?>(null);
}

/// <summary>
/// 内存版 Agent 配置仓储：保留 If-Match 乐观并发语义（版本不匹配返回 null），
/// 管理端点的保存/并发提示行为与生产一致。
/// </summary>
internal sealed class InMemoryAgentConfigRepository : IAgentConfigRepository
{
    private readonly ConcurrentDictionary<string, Tracked> _configs = [];
    private int _versions;

    public Task<AgentConfigEntity?> GetAsync(string tenantId, string agentId, CancellationToken cancellationToken = default)
    {
        _configs.TryGetValue($"{tenantId}/{agentId}", out Tracked? tracked);
        return Task.FromResult(tracked is null ? null : Clone(tracked.Entity));
    }

    public Task<IReadOnlyList<AgentConfigEntity>> ListAsync(string? tenantId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentConfigEntity>>(
        [.. _configs.Values
            .Where(tracked => tenantId == null || tracked.Entity.TenantId == tenantId)
            .Select(tracked => Clone(tracked.Entity))
            .OrderBy(entity => entity.AgentId)]);

    public Task<AgentConfigEntity?> UpsertAsync(
        string tenantId,
        string agentId,
        AgentConfigEntity entity,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        string key = $"{tenantId}/{agentId}";
        // 与 EF 仓储一致：空白版本号视为无条件创建/替换（If-Match 语义只在非空时生效）。
        if (!string.IsNullOrWhiteSpace(expectedVersion)
            && (!_configs.TryGetValue(key, out Tracked? existing) || existing.Version != expectedVersion))
        {
            return Task.FromResult<AgentConfigEntity?>(null);
        }
        string version = Interlocked.Increment(ref _versions).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // 深拷贝隔离：调用方（含响应脱敏）会在返回的同一实例上原地改写，
        // 共享引用会把密钥清空写回存储。
        AgentConfigEntity saved = Clone(entity);
        saved.CurrentVersion = version;
        _configs[key] = new Tracked(Clone(saved), version);
        return Task.FromResult<AgentConfigEntity?>(saved);
    }

    private static AgentConfigEntity Clone(AgentConfigEntity entity) =>
        JsonSerializer.Deserialize<AgentConfigEntity>(JsonSerializer.Serialize(entity))!;

    private sealed record Tracked(AgentConfigEntity Entity, string Version);
}

/// <summary>内存版 LLM profile 仓储，同样保留乐观并发版本。</summary>
internal sealed class InMemoryLlmConfigRepository : ILlmConfigRepository
{
    private readonly ConcurrentDictionary<string, Tracked> _profiles = [];
    private int _versions;

    public Task<LlmProviderProfile?> GetAsync(string tenantId, string profileId, CancellationToken cancellationToken = default)
    {
        _profiles.TryGetValue($"{tenantId}/{profileId}", out Tracked? tracked);
        return Task.FromResult(tracked is null ? null : Clone(tracked.Profile));
    }

    public Task<IReadOnlyList<LlmProviderProfile>> ListAsync(string tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LlmProviderProfile>>(
        [.. _profiles.Values
            .Where(tracked => tracked.Profile.TenantId == tenantId)
            .Select(tracked => tracked.Profile)]);

    public Task<LlmProviderProfile> UpsertAsync(
        string tenantId,
        string profileId,
        LlmProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        LlmProviderProfile saved = Clone(profile);
        _profiles[$"{tenantId}/{profileId}"] = new Tracked(Clone(saved), Interlocked.Increment(ref _versions)
            .ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Task.FromResult(saved);
    }

    public Task<bool> DeleteAsync(string tenantId, string profileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_profiles.TryRemove($"{tenantId}/{profileId}", out _));

    private static LlmProviderProfile Clone(LlmProviderProfile profile) =>
        JsonSerializer.Deserialize<LlmProviderProfile>(JsonSerializer.Serialize(profile))!;

    private sealed record Tracked(LlmProviderProfile Profile, string Version);
}

/// <summary>内存版文件资产仓储：支撑上传/引用登记/读取的完整本地链路。</summary>
internal sealed class InMemoryFileAssetRepository : IFileAssetRepository
{
    private readonly ConcurrentDictionary<string, FileAsset> _assets = [];
    private readonly ConcurrentDictionary<string, HashSet<string>> _references = [];

    public Task CreateAsync(FileAsset asset, CancellationToken cancellationToken)
    {
        _assets[asset.FileId] = asset;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken)
    {
        _assets[asset.FileId] = asset;
        return Task.CompletedTask;
    }

    public Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken) =>
        Task.FromResult(_assets.TryGetValue(fileId, out FileAsset? asset) ? asset : null);

    public Task<IReadOnlyList<FileAsset>> ListReferencedAsync(string conversationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FileAsset>>(
            _references.TryGetValue("conversation:" + conversationId, out HashSet<string>? ids)
                ? [.. ids.Select(id => _assets.TryGetValue(id, out FileAsset? asset) ? asset : null).OfType<FileAsset>()]
                : []);

    public Task EnsureConversationReferencesAsync(
        string conversationId,
        IReadOnlyList<string> fileIds,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        HashSet<string> ids = _references.GetOrAdd("conversation:" + conversationId, _ => []);
        foreach (string fileId in fileIds)
        {
            ids.Add(fileId);
        }
        return Task.CompletedTask;
    }

    public Task<bool> IsReferencedAsync(string conversationId, string fileId, CancellationToken cancellationToken) =>
        Task.FromResult(_references.TryGetValue("conversation:" + conversationId, out HashSet<string>? ids)
            && ids.Contains(fileId));
}
