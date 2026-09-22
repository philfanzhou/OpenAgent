using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Code;
using OpenAgent.Core.Tests.TestDoubles;
using OpenAgent.Core.Capabilities.Workspace;
using OpenAgent.Core.Files;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class WorkspaceCapabilitySourceTests
{
    private readonly FakeWorkspaceClient _client = new();

    private WorkspaceCapabilitySource CreateSource(
        bool hostEnabled = true,
        bool agentEnabled = true,
        bool withScope = true,
        IAgentAuthorizationService? authorization = null)
    {
        var context = new FileAssetExecutionContext();
        if (withScope)
        {
            context.Set(TurnContexts.Create("tenant-a", "user-a", "conversation-a"));
        }
        return new WorkspaceCapabilitySource(
            _client,
            null!,
            context,
            new AgentAuthorizationGate(authorization ?? new AllowAllAgentAuthorizationService()),
            Options.Create(new CodeExecutionOptions { Enabled = hostEnabled }));
    }

    private static AgentConfig Config(bool enabled) => new()
    {
        CodeExecution = new CodeExecutionConfig { Enabled = enabled }
    };

    private static async Task<ToolResult> InvokeAsync(
        WorkspaceCapabilitySource source,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        IReadOnlyList<CapabilityDefinition> definitions = await source.DiscoverAsync(
            "agent-1", Config(true), User, CancellationToken.None);
        CapabilityDefinition definition = Assert.Single(definitions, item => item.Name == toolName);
        return await definition.Invoke(arguments, CancellationToken.None);
    }

    [Fact]
    public async Task DiscoverAsync_RequiresHostAgentAndScope()
    {
        Assert.Empty(await CreateSource(hostEnabled: false).DiscoverAsync("a", Config(true), User, CancellationToken.None));
        Assert.Empty(await CreateSource(agentEnabled: false).DiscoverAsync("a", Config(false), User, CancellationToken.None));
        Assert.Empty(await CreateSource(withScope: false).DiscoverAsync("a", Config(true), User, CancellationToken.None));
    }

    [Fact]
    public async Task DiscoverAsync_ExposesFiveToolsWithReadOnlyReads()
    {
        IReadOnlyList<CapabilityDefinition> definitions = await CreateSource().DiscoverAsync(
            "a", Config(true), User, CancellationToken.None);

        string[] expected =
        [
            "list_workspace_files", "read_workspace_file", "write_workspace_file",
            "edit_workspace_file", "export_workspace_file"
        ];
        Assert.Equal(expected, definitions.Select(item => item.Name).ToArray());
        Assert.Equal(ToolConcurrency.ReadOnly, definitions.Single(item => item.Name == "read_workspace_file").Concurrency);
        Assert.Equal(ToolConcurrency.ReadOnly, definitions.Single(item => item.Name == "list_workspace_files").Concurrency);
        Assert.All(
            definitions.Where(item => item.Name.Contains("write") || item.Name.Contains("edit") || item.Name.Contains("export")),
            item => Assert.Equal(ToolConcurrency.Exclusive, item.Concurrency));
    }

    [Fact]
    public async Task ReadAsync_ReturnsNumberedContentJson()
    {
        _client.ReadResult = new WorkspaceReadResult
        {
            Path = "a.txt", TotalLines = 10, StartLine = 1, EndLine = 2, Content = "1\tx\n2\ty\n", Truncated = true
        };
        WorkspaceCapabilitySource source = CreateSource();

        ToolResult result = await InvokeAsync(source, "read_workspace_file",
            new Dictionary<string, object?> { ["path"] = "a.txt", ["limit"] = 2 });

        Assert.False(result.IsError);
        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.Equal(10, document.RootElement.GetProperty("totalLines").GetInt32());
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("conversation-a", _client.LastSessionKey);
        Assert.Equal(2, _client.LastLimit);
    }

    [Fact]
    public async Task EditAsync_ConflictFromRunner_MapsToActionableEnvelope()
    {
        _client.EditThrows = new WorkspaceOperationException(
            409, "'old_string' occurs 2 times in 'a.txt'. Include more surrounding lines...");
        WorkspaceCapabilitySource source = CreateSource();

        ToolResult result = await InvokeAsync(source, "edit_workspace_file",
            new Dictionary<string, object?>
            {
                ["path"] = "a.txt",
                ["old_string"] = "dup",
                ["new_string"] = "y"
            });

        Assert.True(result.IsError);
        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.Equal("workspace_409", document.RootElement.GetProperty("code").GetString());
        Assert.Contains("2 times", document.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditAsync_MissingArguments_ReturnsInvalidArguments()
    {
        WorkspaceCapabilitySource source = CreateSource();

        ToolResult result = await InvokeAsync(source, "edit_workspace_file",
            new Dictionary<string, object?> { ["path"] = "a.txt" });

        Assert.True(result.IsError);
        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.Equal("invalid_arguments", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invocation_UnauthorizedAgent_ReturnsUnauthorizedEnvelope()
    {
        WorkspaceCapabilitySource source = CreateSource(authorization: new DenyAllAuthorization());

        ToolResult result = await InvokeAsync(source, "read_workspace_file",
            new Dictionary<string, object?> { ["path"] = "a.txt" });

        Assert.True(result.IsError);
        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.Equal("unauthorized", document.RootElement.GetProperty("code").GetString());
    }

    private sealed class DenyAllAuthorization : IAgentAuthorizationService
    {
        public Task<bool> IsAuthorizedAsync(
            AgentAuthorizationRequest request,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private static readonly AgentUserContext User = new()
    {
        UserId = "user-a",
        TenantId = "tenant-a",
        Claims = new Dictionary<string, string>(),
        IsAuthenticated = true
    };

    private sealed class FakeWorkspaceClient : IWorkspaceClient
    {
        public WorkspaceReadResult ReadResult { get; set; } = new();
        public WorkspaceOperationException? EditThrows { get; set; }
        public string? LastSessionKey { get; private set; }
        public int LastLimit { get; private set; }

        public Task<WorkspaceListResult> ListAsync(
            string sessionKey, string? path, string? pattern, CancellationToken cancellationToken)
        {
            LastSessionKey = sessionKey;
            return Task.FromResult(new WorkspaceListResult { Path = path ?? "." });
        }

        public Task<WorkspaceReadResult> ReadAsync(
            string sessionKey, string path, int offsetLine, int limitLines, CancellationToken cancellationToken)
        {
            LastSessionKey = sessionKey;
            LastLimit = limitLines;
            return Task.FromResult(ReadResult);
        }

        public Task<WorkspaceWriteResult> WriteAsync(
            string sessionKey, string path, string content, CancellationToken cancellationToken)
        {
            LastSessionKey = sessionKey;
            return Task.FromResult(new WorkspaceWriteResult { Path = path, LengthBytes = content.Length });
        }

        public Task<WorkspaceEditResult> EditAsync(
            string sessionKey, string path, string oldString, string newString, bool replaceAll,
            CancellationToken cancellationToken)
        {
            LastSessionKey = sessionKey;
            if (EditThrows != null)
            {
                throw EditThrows;
            }
            return Task.FromResult(new WorkspaceEditResult { Path = path, Replacements = 1 });
        }

        public Task<WorkspaceBytesResult> ReadBytesAsync(
            string sessionKey, string path, CancellationToken cancellationToken)
        {
            LastSessionKey = sessionKey;
            return Task.FromResult(new WorkspaceBytesResult
            {
                Path = path,
                LengthBytes = 4,
                ContentBase64 = Convert.ToBase64String([1, 2, 3, 4])
            });
        }

        public Task<WorkspaceWriteResult> UploadAsync(
            string sessionKey, string path, byte[] content, CancellationToken cancellationToken)
        {
            LastSessionKey = sessionKey;
            return Task.FromResult(new WorkspaceWriteResult { Path = path, LengthBytes = content.Length });
        }
    }
}
