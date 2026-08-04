using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenAgent.Core.Tests.Mcp;

internal sealed class SdkSseMessageHandler : HttpMessageHandler
{
    private readonly Pipe _events = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/sse")
        {
            return await CreateSseResponseAsync(cancellationToken).ConfigureAwait(false);
        }

        if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/messages")
        {
            return await HandleMessageAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> CreateSseResponseAsync(CancellationToken cancellationToken)
    {
        await WriteEventAsync("endpoint", "/messages", cancellationToken).ConfigureAwait(false);

        var content = new StreamContent(_events.Reader.AsStream());
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        };
    }

    private async Task<HttpResponseMessage> HandleMessageAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var payload = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var method = root.GetProperty("method").GetString()
            ?? throw new InvalidOperationException("MCP test request method is required.");

        if (!root.TryGetProperty("id", out var id))
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        var parameters = root.TryGetProperty("params", out var requestParameters)
            ? requestParameters.Clone()
            : JsonSerializer.SerializeToElement(new { });
        var responseJson = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = id.Clone(),
            result = CreateResult(method, parameters)
        });

        await WriteEventAsync("message", responseJson, cancellationToken).ConfigureAwait(false);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
    }

    private static object CreateResult(string method, JsonElement parameters) => method switch
    {
        "initialize" => new
        {
            protocolVersion = parameters.GetProperty("protocolVersion").GetString(),
            capabilities = new { tools = new { }, resources = new { } },
            serverInfo = new { name = "sdk-test-server", version = "1.0.0" }
        },
        "tools/list" => new
        {
            tools = new[]
            {
                new
                {
                    name = "lookup",
                    description = "Lookup data",
                    inputSchema = new { type = "object", properties = new { } },
                    annotations = new { destructiveHint = true }
                }
            }
        },
        "tools/call" => new
        {
            content = new[] { new { type = "text", text = "sdk-tool-result" } },
            isError = false
        },
        "resources/read" => new
        {
            contents = new[]
            {
                new { uri = "resource://text", mimeType = "text/plain", text = "sdk-resource" }
            }
        },
        _ => throw new InvalidOperationException($"Unsupported test method '{method}'.")
    };

    private async Task WriteEventAsync(
        string eventType,
        string data,
        CancellationToken cancellationToken)
    {
        var eventBytes = Encoding.UTF8.GetBytes($"event: {eventType}\ndata: {data}\n\n");
        await _events.Writer.WriteAsync(eventBytes, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _events.Writer.Complete();
            _events.Reader.Complete();
        }

        base.Dispose(disposing);
    }
}
