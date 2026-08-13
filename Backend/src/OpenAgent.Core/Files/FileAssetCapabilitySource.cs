using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;

namespace OpenAgent.Core.Files;

internal sealed class FileAssetCapabilitySource(
    IFileAssetService files,
    FileAssetExecutionContext executionContext,
    IOptions<FileAssetOptions> options) : ICapabilitySource
{
    public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || executionContext.Scope == null)
        {
            return Task.FromResult<IReadOnlyList<CapabilityDefinition>>([]);
        }

        IReadOnlyList<CapabilityDefinition> definitions =
        [
            new CapabilityDefinition(
                "read_file",
                "Read a UTF-8 text file that belongs to the current user or conversation.",
                """{"type":"object","properties":{"fileId":{"type":"string"}},"required":["fileId"]}""",
                AgentResourceType.Tool,
                "file-assets",
                ReadAsync),
            new CapabilityDefinition(
                "write_file",
                "Create a UTF-8 text file for the current user and conversation.",
                """{"type":"object","properties":{"fileName":{"type":"string"},"content":{"type":"string"},"mediaType":{"type":"string"}},"required":["fileName","content"]}""",
                AgentResourceType.Tool,
                "file-assets",
                WriteAsync)
        ];
        return Task.FromResult(definitions);
    }

    private async Task<string> ReadAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string fileId = RequireString(arguments, "fileId");
        FileAssetScope scope = RequireScope();
        string content = await files.ReadTextAsync(fileId, scope, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { fileId, content });
    }

    private async Task<string> WriteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string fileName = RequireString(arguments, "fileName");
        string content = RequireString(arguments, "content");
        string mediaType = ReadString(arguments, "mediaType") ?? "text/plain";
        byte[] data = new UTF8Encoding(false).GetBytes(content);
        await using var input = new MemoryStream(data, writable: false);
        FileAssetScope scope = RequireScope();
        FileAsset asset = await files.UploadAsync(
            new FileAssetCreateRequest
            {
                FileName = fileName,
                MediaType = mediaType,
                Source = FileAssetSource.Agent
            },
            input,
            scope,
            cancellationToken).ConfigureAwait(false);
        await files.EnsureReferencesAsync([asset.FileId], scope, cancellationToken).ConfigureAwait(false);
        executionContext.RecordCreated(asset);
        return JsonSerializer.Serialize(new
        {
            fileId = asset.FileId,
            fileName = asset.FileName,
            mediaType = asset.MediaType,
            length = asset.Length
        });
    }

    private FileAssetScope RequireScope() => executionContext.Scope
        ?? throw new InvalidOperationException("File asset execution context is not available.");

    private static string RequireString(IReadOnlyDictionary<string, object?> arguments, string name) =>
        ReadString(arguments, name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"'{name}' is required.", name);

    private static string? ReadString(IReadOnlyDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out object? value) ? value?.ToString() : null;
}
