using Xunit;

namespace OpenAgent.Acp.Demo.Tests;

public sealed class ClientOptionsTests
{
    [Fact]
    public void Parse_ExternalAgent_KeepsArgumentsSeparateFromPrompt()
    {
        ClientOptions options = ClientOptions.Parse([
            "--agent", "node", "--agent-arg", "/path with spaces/agent.js", "--agent-arg", "--acp",
            "--cwd", Path.GetTempPath(), "--timeout-seconds", "120", "--cancel", "--", "你好", "ACP"
        ]);
        Assert.Equal("node", options.AgentCommand);
        Assert.Equal(["/path with spaces/agent.js", "--acp"], options.AgentArguments);
        Assert.Equal("你好 ACP", options.Prompt);
        Assert.Equal(TimeSpan.FromSeconds(120), options.Timeout);
        Assert.True(options.Cancel);
    }

    [Theory]
    [InlineData("--agent")]
    [InlineData("--timeout-seconds", "0")]
    [InlineData("--timeout-seconds", "invalid")]
    [InlineData("--agent-arg", "--acp")]
    [InlineData("--unknown")]
    public void Parse_InvalidOptions_ReturnsActionableError(params string[] arguments)
    {
        Assert.Throws<ArgumentException>(() => ClientOptions.Parse(arguments));
    }
}
