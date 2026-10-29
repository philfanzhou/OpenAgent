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
                AgentResourceType.Tool, "file-assets", ImportAsync),
                new("export_file",
                "Save one finished regular file under outputs/ into this conversation's durable file storage. "
                + "Provide a workspace-relative path such as outputs/report.xlsx. Returns fileId and metadata. "
                + "This does not deliver it to the user: call publish_files with the returned fileId after checking the result. "
                + "Finish writing first; changed or over-limit files are rejected. Repeating the same path/content/name in this turn reuses its fileId.",
                """{"type":"object","properties":{"path":{"type":"string"},"fileName":{"type":"string"}},"required":["path"],"additionalProperties":false}""",
                AgentResourceType.Tool, "file-assets", ExportAsync)]
            : [];
        return Task.FromResult(definitions);
    }

    private async Task<ToolResult> ExportAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        if (!arguments.TryGetValue("path", out object? value) || string.IsNullOrWhiteSpace(value?.ToString()))
        {
            return ToolResult.Error("'path' is required.", "invalid_arguments");
        }
        arguments.TryGetValue("fileName", out object? name);
        try
        {
            OpenAgent.Contracts.Files.FileAsset asset = await exchange.ExportAsync(value!.ToString()!, name?.ToString(), ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                fileId = asset.FileId, fileName = asset.FileName, mediaType = asset.MediaType, length = asset.Length, sha256 = asset.Sha256,
                notice = "Saved in this conversation. Call publish_files to attach it to the assistant message."
            });
        }
        catch (AgentException error)
        {
            return ToolResult.Error(error.Message, "file_exchange_failed");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error("The workspace file could not be exported.", "file_exchange_failed");
        }
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
