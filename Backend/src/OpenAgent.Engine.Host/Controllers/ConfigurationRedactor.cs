using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;

namespace OpenAgent.Engine.Host.Controllers;

internal static class ConfigurationRedactor
{
    internal static AgentConfigEntity Redact(AgentConfigEntity entity)
    {
        entity.Config.Mcp = RedactMcp(entity.Config.Mcp);
        entity.Config.Rag.Instances = entity.Config.Rag.Instances.Select(RedactRag).ToList();
        return entity;
    }

    internal static McpConfig RedactMcp(McpConfig config) => new()
    {
        EnabledServerIds = [.. config.EnabledServerIds],
        Servers = config.Servers.Select(RedactMcpServer).ToList()
    };

    internal static McpServerConfig RedactMcpServer(McpServerConfig server) => new()
    {
        Name = server.Name,
        Url = server.Url,
        Type = server.Type,
        ProtocolVersion = server.ProtocolVersion
    };

    internal static RagInstanceConfig RedactRag(RagInstanceConfig instance)
    {
        return new RagInstanceConfig
        {
            Id = instance.Id,
            Name = instance.Name,
            Enabled = instance.Enabled,
            Type = instance.Type,
            CollectionName = instance.CollectionName,
            ApiEndpoint = instance.ApiEndpoint,
            ApiKeySecretRef = instance.ApiKeySecretRef,
            ApiKey = string.Empty,
            AdapterConfig = instance.AdapterConfig,
            AllowedUserIds = [.. instance.AllowedUserIds],
            AllowedGroups = [.. instance.AllowedGroups],
            AllowedTenantIds = [.. instance.AllowedTenantIds],
            AllowedRoles = [.. instance.AllowedRoles]
        };
    }

    internal static LlmProviderProfile RedactLlm(LlmProviderProfile profile)
    {
        return new LlmProviderProfile
        {
            TenantId = profile.TenantId,
            Id = profile.Id,
            Name = profile.Name,
            Format = profile.Format,
            ModelId = profile.ModelId,
            Endpoint = profile.Endpoint,
            ApiKey = string.Empty,
            Temperature = profile.Temperature,
            ContextTokens = profile.ContextTokens,
            Modality = profile.Modality
        };
    }
}
