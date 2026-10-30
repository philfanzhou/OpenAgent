using System.Text.Json;
using OpenAgent.Capabilities.Files.Requests;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Capabilities.Files.Tools;

internal sealed class FileExchangeCapabilitySource(FileExchangeContext exchange) : ICapabilitySource
{
    public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId, AgentConfig config, IAgentUserContext user, CancellationToken cancellationToken)
    {
        IReadOnlyList<CapabilityDefinition> definitions = exchange.Enabled
            ? [new("import_file",
                "Import an original file referenced by this conversation into the runtime workspace. "
                + "Returns metadata and a relativePath for native read/bash tools; does not put binary data in the prompt. "
                + "Call list_files to discover IDs. Originals are restored after workspace eviction. Write revisions under outputs/.",
                """{"type":"object","properties":{"fileId":{"type":"string"}},"required":["fileId"],"additionalProperties":false}""",
                AgentResourceType.Tool, "file-assets", ImportAsync)]
            : [];
        return Task.FromResult(definitions);
    }

    private async Task<ToolResult> ImportAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        if (!arguments.TryGetValue("fileId", out object? value) || string.IsNullOrWhiteSpace(value?.ToString()))
        {
            return ToolResult.Error("'fileId' is required.", "invalid_arguments");
        }
        try
        {
            return JsonSerializer.Serialize(await exchange.ImportAsync(value!.ToString()!, ct).ConfigureAwait(false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (AgentException error)
        {
            return ToolResult.Error(error.Message, "file_exchange_failed");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error("The workspace file could not be transferred.", "file_exchange_failed");
        }
    }
}
