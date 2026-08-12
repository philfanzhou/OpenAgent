using System.Diagnostics;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;
using Microsoft.Extensions.Options;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpConnectionTester(
    IMcpClientFactory clients,
    AgentAuthorizationGate authorization,
    IOptions<McpExecutionOptions> options) : IMcpConnectionTester
{
    private readonly McpExecutionOptions _options = options.Value;

    public async Task<McpConnectionTestResult> TestAsync(
        McpConnectionTestRequest request,
        IAgentUserContext user,
        string? traceId,
        CancellationToken cancellationToken = default)
    {
        string agentId = request.AgentId ?? string.Empty;
        string resourceId = string.IsNullOrWhiteSpace(request.Server.Name)
            ? request.Server.Url
            : request.Server.Name;
        Stopwatch stopwatch = Stopwatch.StartNew();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ConnectionTimeoutSeconds)));
        CancellationToken operationToken = timeout.Token;

        bool authorized = await authorization.IsAuthorizedAsync(
            agentId,
            AgentResourceType.Mcp,
            resourceId,
            request.Action,
            user,
            operationToken).ConfigureAwait(false);
        if (!authorized)
        {
            return new McpConnectionTestResult
            {
                Success = false,
                Connected = false,
                Authorized = false,
                Transport = request.Server.Type.ToString(),
                RequestedProtocolVersion = request.Server.ProtocolVersion,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Error = "MCP discovery permission denied",
                TraceId = traceId
            };
        }

        IMcpClient? client = null;
        try
        {
            client = clients.Create();
            await client.ConnectAsync(request.Server, operationToken).ConfigureAwait(false);
            IReadOnlyList<McpTool> tools = await client.ListToolsAsync(operationToken).ConfigureAwait(false);
            return new McpConnectionTestResult
            {
                Success = true,
                Connected = client.IsConnected,
                Authorized = true,
                Transport = request.Server.Type.ToString(),
                RequestedProtocolVersion = request.Server.ProtocolVersion,
                NegotiatedProtocolVersion = client.NegotiatedProtocolVersion,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                ToolCount = tools.Count,
                TraceId = traceId
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(request, stopwatch, "MCP connection timed out", traceId);
        }
        catch (Exception exception)
        {
            return Failure(request, stopwatch, exception.Message, traceId);
        }
        finally
        {
            if (client is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static McpConnectionTestResult Failure(
        McpConnectionTestRequest request,
        Stopwatch stopwatch,
        string error,
        string? traceId) => new()
        {
            Success = false,
            Connected = false,
            Authorized = true,
            Transport = request.Server.Type.ToString(),
            RequestedProtocolVersion = request.Server.ProtocolVersion,
            LatencyMs = stopwatch.ElapsedMilliseconds,
            Error = error,
            TraceId = traceId
        };
}
