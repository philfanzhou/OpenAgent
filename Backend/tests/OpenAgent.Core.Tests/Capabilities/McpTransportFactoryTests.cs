using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Capabilities.Mcp;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public sealed class McpTransportFactoryTests
{
    [Fact]
    public void Create_RejectsStdioWhenDisabled()
    {
        McpTransportFactory factory = CreateFactory(new McpExecutionOptions());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            factory.Create(CreateServer("node")));

        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_RejectsCommandPathWhenOnlyBasenameIsAllowed()
    {
        McpTransportFactory factory = CreateFactory(new McpExecutionOptions
        {
            AllowStdio = true,
            AllowedCommands = ["node"]
        });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            factory.Create(CreateServer(Path.Combine(Path.GetTempPath(), "node"))));

        Assert.Contains("not allowed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_RejectsEnvironmentVariableOutsidePolicy()
    {
        McpTransportFactory factory = CreateFactory(new McpExecutionOptions
        {
            AllowStdio = true,
            AllowedCommands = ["node"]
        });
        McpServerConfig server = CreateServer("node");
        server.EnvironmentVariables["PRIVATE_TOKEN"] = "secret";

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            factory.Create(server));

        Assert.Contains("PRIVATE_TOKEN", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsWorkingDirectoryOutsidePolicy()
    {
        McpTransportFactory factory = CreateFactory(new McpExecutionOptions
        {
            AllowStdio = true,
            AllowedCommands = ["node"],
            AllowedWorkingDirectories = [Path.Combine(Path.GetTempPath(), "approved")]
        });
        McpServerConfig server = CreateServer("node");
        server.WorkingDirectory = Path.Combine(Path.GetTempPath(), "unapproved");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            factory.Create(server));

        Assert.Contains("working directory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_AllowsCuratedBareCommand()
    {
        McpTransportFactory factory = CreateFactory(new McpExecutionOptions
        {
            AllowStdio = true,
            AllowedCommands = ["node"]
        });

        IClientTransport transport = factory.Create(CreateServer("node"));

        Assert.IsType<StdioClientTransport>(transport);
    }

    private static McpTransportFactory CreateFactory(McpExecutionOptions options) => new(
        new ThrowingHttpClientFactory(),
        NullLoggerFactory.Instance,
        Options.Create(options));

    private static McpServerConfig CreateServer(string command) => new()
    {
        Name = "local-tools",
        Type = McpServerType.Stdio,
        Command = command
    };

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("HTTP transport was not expected.");
    }
}
