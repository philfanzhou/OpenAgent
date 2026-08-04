using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Mcp;
using SdkMcpClient = ModelContextProtocol.Client.McpClient;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpToolInvoker
{
    private readonly McpSessionState _state;
    private readonly McpToolCatalog _catalog;
    private readonly ILogger<McpClient> _logger;

    internal McpToolInvoker(
        McpSessionState state,
        McpToolCatalog catalog,
        ILogger<McpClient> logger)
    {
        _state = state;
        _catalog = catalog;
        _logger = logger;
    }

    internal async Task<string> InvokeAsync(
        string toolName,
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        McpTool? tool = _catalog.Find(toolName);
        if (tool == null)
        {
            _logger.LogWarning(
                "MCP tool unavailable. Tool={Tool} Status={Status}",
                toolName,
                "tool_not_found");
            return $"Error: Tool '{toolName}' not found.";
        }

        if (tool.IsDangerous)
        {
            _logger.LogWarning("Dangerous MCP tool requested. Tool={Tool}", toolName);
        }

        SdkMcpClient? client = _state.Client;
        if (client == null || client.Completion.IsCompleted)
        {
            _logger.LogWarning(
                "MCP tool unavailable. Tool={Tool} Status={Status}",
                toolName,
                "not_connected");
            return "Error: MCP Client not connected.";
        }

        try
        {
            Dictionary<string, object?> sdkArguments = arguments.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.Ordinal);
            CallToolResult result = await client.CallToolAsync(
                toolName,
                sdkArguments,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            bool isToolError = result.IsError == true;
            string resultText = result.Content
                .OfType<TextContentBlock>()
                .FirstOrDefault()?.Text
                ?? JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);

            if (isToolError)
            {
                _logger.LogWarning(
                    "MCP tool returned an error. Tool={Tool} DurationMs={DurationMs}",
                    toolName,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
            return isToolError
                ? $"Error executing tool {toolName}: {resultText}"
                : resultText;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (McpProtocolException exception)
        {
            int errorCode = (int)exception.ErrorCode;
            LogFailure(toolName, "rpc_error", exception, stopwatch);
            return $"Error executing tool {toolName}: {exception.Message} (Code: {errorCode})";
        }
        catch (McpException exception)
        {
            LogFailure(toolName, "rpc_error", exception, stopwatch);
            return $"Error executing tool {toolName}: {exception.Message}";
        }
        catch (TimeoutException exception)
        {
            LogFailure(toolName, "timeout", exception, stopwatch);
            return $"Error: Tool {toolName} execution timed out.";
        }
        catch (HttpRequestException exception)
        {
            LogFailure(toolName, "http_error", exception, stopwatch);
            return $"Error executing tool {toolName}: {exception.Message}";
        }
        catch (Exception exception)
        {
            LogFailure(toolName, "unexpected_error", exception, stopwatch);
            return $"Error executing tool {toolName}: {exception.Message}";
        }
    }

    private void LogFailure(
        string toolName,
        string status,
        Exception exception,
        Stopwatch stopwatch)
    {
        _logger.LogError(
            exception,
            "MCP tool failed. Tool={Tool} Status={Status} DurationMs={DurationMs}",
            toolName,
            status,
            stopwatch.Elapsed.TotalMilliseconds);
    }
}
