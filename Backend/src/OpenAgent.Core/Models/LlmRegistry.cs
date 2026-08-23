using System.Collections.Concurrent;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Abstract;

namespace OpenAgent.Core.Models;

internal class LlmRegistry : ILlmRegistry
{
    private readonly ConcurrentDictionary<string, LlmProviderProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public void Register(LlmProviderProfile profile)
    {
        if (string.IsNullOrEmpty(profile.Id))
            return;
        _profiles[profile.Id] = profile;
    }

    public bool Remove(string id) => _profiles.TryRemove(id, out _);

    public List<LlmProviderProfile> GetAllProfiles()
    {
        return _profiles.Values.ToList();
    }

    public LlmProviderProfile? GetProfile(string id)
    {
        return _profiles.GetValueOrDefault(id);
    }

    public LlmConfig ResolveConfig(LlmConfig llmConfig) =>
        ResolveConfig(llmConfig, requireCatalogEntry: false);

    public LlmConfig ResolveConfig(LlmConfig llmConfig, bool requireCatalogEntry)
    {
        if (string.IsNullOrEmpty(llmConfig.Provider))
        {
            return llmConfig;
        }

        var profile = GetProfile(llmConfig.Provider);
        if (profile == null)
        {
            throw new AgentException(
                AgentErrorCode.DependencyUnavailable,
                $"LLM provider '{llmConfig.Provider}' is not available.");
        }

        if (!profile.IsEnabled
            || string.IsNullOrWhiteSpace(profile.Endpoint)
            || string.IsNullOrWhiteSpace(profile.ApiKey)
            || profile.ApiKey.StartsWith("***", StringComparison.Ordinal))
        {
            throw new AgentException(
                AgentErrorCode.DependencyUnavailable,
                $"LLM provider '{llmConfig.Provider}' is not available.");
        }

        string modelId = string.IsNullOrWhiteSpace(llmConfig.ModelId)
            ? profile.ModelId ?? string.Empty
            : llmConfig.ModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new AgentException(
                AgentErrorCode.LlmModelNotFound,
                $"No model is configured for LLM provider '{llmConfig.Provider}'.");
        }

        IReadOnlyList<string> modelIds = (profile.ModelIds ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requireCatalogEntry && modelIds.Count == 0)
        {
            throw new AgentException(
                AgentErrorCode.LlmModelNotFound,
                $"LLM provider '{llmConfig.Provider}' does not publish selectable models.");
        }
        if (requireCatalogEntry
            && !modelIds.Contains(modelId, StringComparer.OrdinalIgnoreCase))
        {
            throw new AgentException(
                AgentErrorCode.LlmModelNotFound,
                $"LLM model '{llmConfig.Provider}/{modelId}' does not exist.");
        }

        return new LlmConfig
        {
            TenantId = profile.TenantId,
            Provider = llmConfig.Provider,
            Format = profile.Format,
            // AgentConfig.Llm.ModelId is the new source of truth. Profile.ModelId
            // remains only as a backwards-compatible fallback for old Redis data.
            ModelId = modelId,
            ApiKey = profile.ApiKey,
            Endpoint = profile.Endpoint,
            Temperature = llmConfig.Temperature == 0.7 ? profile.Temperature : llmConfig.Temperature
        };
    }
}
