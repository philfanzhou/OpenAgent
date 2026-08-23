using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Host.Extensions;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class ManagementEndpointExtensionsTests
{
    [Fact]
    public void Redact_RemovesEmbeddedLlmSecret()
    {
        var entity = new AgentConfigEntity
        {
            AgentId = "finance",
            Config = new AgentConfig
            {
                Llm = new LlmConfig { ApiKey = "llm-secret" }
            }
        };

        AgentConfigEntity redacted = ManagementEndpointExtensions.Redact(entity);

        Assert.Equal("***", redacted.Config.Llm.ApiKey);
    }

    [Fact]
    public void Redact_RemovesMcpEnvironmentVariableValues()
    {
        var entity = new AgentConfigEntity
        {
            AgentId = "finance",
            Config = new AgentConfig
            {
                Mcp = new McpConfig
                {
                    EnabledServerIds = ["billing"],
                    Servers =
                    [
                        new McpServerConfig
                        {
                            Name = "billing",
                            EnvironmentVariables = new Dictionary<string, string>
                            {
                                ["API_TOKEN"] = "mcp-secret",
                                ["EMPTY_VALUE"] = string.Empty
                            }
                        }
                    ]
                }
            }
        };

        AgentConfigEntity redacted = ManagementEndpointExtensions.Redact(entity);

        Assert.Equal(["billing"], redacted.Config.Mcp.EnabledServerIds);
        McpServerConfig server = Assert.Single(redacted.Config.Mcp.Servers);
        Assert.Equal("***", server.EnvironmentVariables["API_TOKEN"]);
        Assert.Equal(string.Empty, server.EnvironmentVariables["EMPTY_VALUE"]);
    }

    [Fact]
    public void MergeMcpSecrets_PreservesRedactedValuesAndAllowsExplicitClear()
    {
        var existing = new McpServerConfig
        {
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["API_TOKEN"] = "mcp-secret",
                ["CLEAR_ME"] = "old-value"
            }
        };
        var requested = new McpServerConfig
        {
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["API_TOKEN"] = "***",
                ["CLEAR_ME"] = string.Empty
            }
        };

        McpServerConfig merged = ManagementEndpointExtensions.MergeMcpSecrets(existing, requested);

        Assert.Equal("mcp-secret", merged.EnvironmentVariables["API_TOKEN"]);
        Assert.Equal(string.Empty, merged.EnvironmentVariables["CLEAR_ME"]);
    }

    [Fact]
    public void RedactLlm_RemovesProviderSecret()
    {
        var profile = new LlmProviderProfile
        {
            Id = "primary",
            ApiKey = "provider-secret"
        };

        LlmProviderProfile redacted = ManagementEndpointExtensions.RedactLlm(profile);

        Assert.Equal("***", redacted.ApiKey);
        Assert.Equal("provider-secret", profile.ApiKey);
    }
}
