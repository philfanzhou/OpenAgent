using System.Diagnostics;
using ModelContextProtocol.Client;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;
using Microsoft.Extensions.Options;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpConnectionTester(
    McpTransportFactory transports,
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

        McpClient? client = null;
        IClientTransport? transport = null;
        try
        {
            transport = transports.Create(request.Server);
            client = await McpClient.CreateAsync(
                transport,
                McpToolFactory.CreateClientOptions(request.Server),
                loggerFactory: null,
                operationToken).ConfigureAwait(false);
            IList<McpClientTool> tools = await client.ListToolsAsync(
                options: null,
                operationToken).ConfigureAwait(false);
            return new McpConnectionTestResult
            {
                Success = true,
                Connected = !client.Completion.IsCompleted,
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
            if (client != null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            else if (transport is IAsyncDisposable asyncTransport)
            {
                await asyncTransport.DisposeAsync().ConfigureAwait(false);
            }
            else if (transport is IDisposable disposableTransport)
            {
                disposableTransport.Dispose();
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
