namespace OpenAgent.Engine.Config;

internal sealed class AgentConfigSourceOptions
{
    internal const string SectionName = "ConfigurationStore";

    public bool UsePostgreSqlForAgents { get; set; }
    public int RedisCacheTtlSeconds { get; set; } = 300;
}
