using System.Diagnostics;
using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;

namespace OpenAgent.Engine.Host.Endpoints.Admin.Mcp;

internal static class McpConnectionDiagnostic
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    internal static async Task<McpConnectionTestResult> TestAsync(
        Uri endpoint, McpServerType type, string? protocolVersion, CancellationToken cancellationToken)
    {
        var result = new McpConnectionTestResult();
        var stopwatch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        // Apply the same deadline to transport cleanup (including HTTP session DELETE), not just RPC calls.
        using var httpClient = new HttpClient(new DeadlineHandler(deadline.Token)
        {
            InnerHandler = new SocketsHttpHandler()
        }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        try
        {
            await using var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                Name = "MCP connection diagnostic",
                TransportMode = type == McpServerType.SSE ? HttpTransportMode.Sse : HttpTransportMode.StreamableHttp,
                ConnectionTimeout = Timeout
            }, httpClient);
            await using McpClient client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new() { Name = "OpenAgent connection diagnostic", Version = "1" },
                // A specified version is an exact requirement in the SDK, not a best-effort HTTP header.
                ProtocolVersion = protocolVersion,
                InitializationTimeout = Timeout
            }, cancellationToken: deadline.Token).ConfigureAwait(false);
            result.Connected = true;
            result.ServerName = client.ServerInfo.Name;
            result.ServerVersion = client.ServerInfo.Version;
            result.ProtocolVersion = client.NegotiatedProtocolVersion;
            if (client.ServerCapabilities.Tools is not null)
            {
                // This SDK overload follows all nextCursor pages before returning any catalog.
                IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
                result.Tools = tools.Select(tool => new McpToolDefinition
                {
                    Name = tool.ProtocolTool.Name,
                    Title = tool.ProtocolTool.Title,
                    Description = tool.ProtocolTool.Description,
                    InputSchema = tool.ProtocolTool.InputSchema.Clone(),
                    OutputSchema = tool.ProtocolTool.OutputSchema?.Clone()
                }).ToArray();
            }
            result.Success = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            result.Error = "MCP connection test timed out after 15 seconds.";
        }
        catch (HttpRequestException exception)
        {
            string status = exception.StatusCode is { } code ? $" (HTTP {(int)code})" : string.Empty;
            result.Error = result.Connected
                ? $"MCP tool catalog HTTP request failed{status}."
                : $"MCP HTTP connection failed{status}.";
        }
        catch (Exception)
        {
            // Neither SDK exception messages nor remote response bodies are safe to return to the browser.
            result.Error = result.Connected
                ? "MCP tool catalog could not be read. Check the server's tools/list support."
                : "MCP initialization failed. Check the transport and protocol version.";
        }
        finally
        {
            stopwatch.Stop();
            result.LatencyMs = stopwatch.ElapsedMilliseconds;
        }
        if (result.Error is not null)
        {
            result.Success = false;
            result.Tools = [];
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private sealed class DeadlineHandler(CancellationToken deadline) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline, cancellationToken);
            return await base.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
    }
}
