using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Engine.Config;

internal sealed class MockAgentResolver
{
    public MockAgentResolver(IHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration.GetValue("Engine:AllowMockAgent", (bool?)null);
        if (configured.HasValue)
        {
            IsEnabled = configured.Value;
            return;
        }

        var environmentValue = Environment.GetEnvironmentVariable("ALLOW_MOCK_AGENT");
        IsEnabled = !string.IsNullOrEmpty(environmentValue)
            && bool.TryParse(environmentValue, out var parsed)
                ? parsed
                : environment.IsDevelopment() || environment.IsEnvironment("Testing");
    }

    internal bool IsEnabled { get; }

    internal AgentConfig CreateFallback() => new()
    {
        Llm = new LlmConfig(),
        Rag = new RagConfig { Enabled = false },
        Mcp = new McpConfig()
    };
}
