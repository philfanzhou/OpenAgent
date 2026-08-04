using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Engine.Config;

internal sealed class SecretInjector
{
    internal void Enrich(AgentConfig config)
    {
        if (config.Llm != null && string.IsNullOrEmpty(config.Llm.ApiKey))
        {
            config.Llm.ApiKey = Environment.GetEnvironmentVariable("LLM__APIKEY")
                ?? Environment.GetEnvironmentVariable("LLM_API_KEY")
                ?? string.Empty;
        }
    }
}
