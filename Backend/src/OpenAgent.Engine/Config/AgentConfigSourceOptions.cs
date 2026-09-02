namespace OpenAgent.Engine.Config;

internal sealed class AgentConfigSourceOptions
{
    internal const string SectionName = "ConfigurationStore";

    public int RedisCacheTtlSeconds { get; set; } = 300;
    public int RedisCacheReconciliationSeconds { get; set; } = 60;
}
