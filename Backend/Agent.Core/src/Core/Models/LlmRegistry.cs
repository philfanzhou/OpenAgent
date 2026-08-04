using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Models;

internal class LlmRegistry : ILlmRegistry
{
    private readonly Dictionary<string, LlmProviderProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public void Register(LlmProviderProfile profile)
    {
        if (string.IsNullOrEmpty(profile.Id)) return;
        _profiles[profile.Id] = profile;
    }

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
            Provider = llmConfig.Provider,
            Format = profile.Format,
            ModelId = llmConfig.ModelId,
            ApiKey = profile.ApiKey,
            Endpoint = profile.Endpoint,
            Temperature = llmConfig.Temperature
        };
    }
}
