using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Abstract;

public interface ILlmRegistry
{
    List<LlmProviderProfile> GetAllProfiles();

    LlmProviderProfile? GetProfile(string id);

    void Register(LlmProviderProfile profile);

    bool Remove(string id);

    LlmConfig ResolveConfig(LlmConfig llmConfig);
}
