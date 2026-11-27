using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenAgent.Router.Endpoints;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Tests.Endpoints;

/// <summary>转发失败的降级响应必须和成功时用同一种协议：Engine 的聊天流式端点只有 SSE。</summary>
public sealed class StreamingForwardingErrorTests
{
    [Theory]
    [InlineData("stream", ForwarderError.RequestTimedOut, "GatewayTimeout")]
    [InlineData("stream", ForwarderError.RequestCanceled, "ServiceUnavailable")]
    [InlineData("sse", ForwarderError.RequestCanceled, "ServiceUnavailable")]
    public async Task HandleAsync_StreamingAction_WritesSseErrorAndDone(
        string action, ForwarderError error, string title)
    {
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();

        await StreamingForwardingError.HandleAsync(context, action, error, "trace-1", CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/event-stream", context.Response.ContentType);
        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.StartsWith("event: error\ndata: {", body, StringComparison.Ordinal);
        Assert.Contains($"\"title\":\"{title}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"traceId\":\"trace-1\"", body, StringComparison.Ordinal);
        Assert.Contains("event: done\ndata: {\"done\":true,\"status\":\"error\"}", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ForwarderError.RequestTimedOut, StatusCodes.Status504GatewayTimeout)]
    [InlineData(ForwarderError.RequestCanceled, StatusCodes.Status503ServiceUnavailable)]
    public async Task HandleAsync_NonStreamingAction_ReturnsProblemDetails(ForwarderError error, int statusCode)
    {
        DefaultHttpContext context = new();

        IResult result = await StreamingForwardingError.HandleAsync(context, "intent", error, "trace-1", CancellationToken.None);

        Assert.Equal(statusCode, Assert.IsType<ProblemHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task HandleAsync_ResponseAlreadyStarted_WritesNothing()
    {
        // 流已经开始后再失败，只能结束连接：往 SSE 中途插入 error/done 会让客户端误以为这是新的轮次。
        DefaultHttpContext context = new();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());

        IResult result = await StreamingForwardingError.HandleAsync(
            context, "stream", ForwarderError.RequestCanceled, "trace-1", CancellationToken.None);

        Assert.Same(Results.Empty, result);
        Assert.Null(context.Response.ContentType);
        Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
