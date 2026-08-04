using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using SdkMcpClient = ModelContextProtocol.Client.McpClient;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpResourceReader
{
    private readonly McpSessionState _state;
    private readonly ILogger<McpClient> _logger;

    internal McpResourceReader(
        McpSessionState state,
        ILogger<McpClient> logger)
    {
        _state = state;
        _logger = logger;
    }

    internal async Task<Stream> ReadAsync(string uri, CancellationToken cancellationToken)
    {
        SdkMcpClient? client = _state.Client;
        if (client == null || client.Completion.IsCompleted)
        {
            throw new InvalidOperationException("MCP Client not connected.");
        }

        try
        {
            ReadResourceResult result = await client.ReadResourceAsync(
                uri,
                options: null,
                cancellationToken).ConfigureAwait(false);
            return CreateStream(result.Contents.FirstOrDefault(), uri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            McpLog.ReadMcpResourceFailed(_logger, exception, uri);
            throw;
        }
    }

    internal static Stream CreateStream(ResourceContents? content, string uri)
    {
        return content switch
        {
            TextResourceContents text => new MemoryStream(Encoding.UTF8.GetBytes(text.Text)),
            BlobResourceContents blob => new MemoryStream(blob.DecodedData.ToArray(), writable: false),
            null => throw new InvalidOperationException($"Resource '{uri}' returned no content."),
            _ => throw new InvalidOperationException(
                $"Resource '{uri}' returned an unsupported content type.")
        };
    }
}
