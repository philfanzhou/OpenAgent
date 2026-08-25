using System.Collections.Concurrent;
using OpenAgent.Contracts.Configuration;
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

    public LlmConfig ResolveConfig(LlmConfig llmConfig)
    {
        if (string.IsNullOrEmpty(llmConfig.Provider))
        {
            return llmConfig;
        }

        var profile = GetProfile(llmConfig.Provider);
        if (profile == null)
        {
            throw new InvalidOperationException(
                $"LLM provider '{llmConfig.Provider}' is not registered in the LLM registry. " +
                "Ensure the provider profile has been synced to Redis and the Engine has loaded it.");
        }

        return new LlmConfig
        {
            TenantId = profile.TenantId,
            Provider = llmConfig.Provider,
            Format = profile.Format,
            // AgentConfig.Llm.ModelId is the new source of truth. Profile.ModelId
            // remains only as a backwards-compatible fallback for old Redis data.
            ModelId = string.IsNullOrWhiteSpace(llmConfig.ModelId) ? profile.ModelId ?? string.Empty : llmConfig.ModelId,
            ApiKey = profile.ApiKey,
            Endpoint = profile.Endpoint,
            Temperature = llmConfig.Temperature == 0.7 ? profile.Temperature : llmConfig.Temperature,
            AllowInsecureTls = profile.AllowInsecureTls
        };
    }
}
