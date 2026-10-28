using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAgent.Engine.Host;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class StreamingPayloadFactoryTests
{
    [Fact]
    public void FormatProviderError_ParsesStatusTypeAndMessage()
    {
        string result = StreamingPayloadFactory.FormatProviderError(400, "HTTP 400 (invalid_request_error: bad request body)");

        Assert.StartsWith("模型服务返回错误", result);
        Assert.Contains("HTTP 400", result);
        Assert.Contains("invalid_request_error", result);
        Assert.Contains("bad request body", result);
    }

    [Fact]
    public void FormatProviderError_EmptyProviderMessage_IncludesRetryHint()
    {
        string result = StreamingPayloadFactory.FormatProviderError(400, "HTTP 400 (invalid_request_error: )");

        Assert.Contains("HTTP 400", result);
        Assert.Contains("invalid_request_error", result);
        Assert.Contains("请检查模型配置后重试", result);
    }

    [Fact]
    public void FormatProviderError_MessageWithoutType_StillShowsStatus()
    {
        string result = StreamingPayloadFactory.FormatProviderError(503, "connection refused");

        Assert.Contains("HTTP 503", result);
        Assert.Contains("请检查模型配置后重试", result);
    }

    [Fact]
    public void CreateErrorPayload_GenericException_AlwaysShowsMessage()
    {
        var payload = StreamingPayloadFactory.CreateErrorPayload(new Exception("boom"), "trace-1");

        Assert.Equal("boom", payload.Detail);
    }

    [Fact]
    public void CreateErrorPayload_ClientResultException_UsesProviderTitleAndDetail()
    {
        var exception = new ClientResultException(
            "HTTP 400 (invalid_request_error: bad request body)",
            (PipelineResponse)null!,
            null!);
        var payload = StreamingPayloadFactory.CreateErrorPayload(exception, "trace-1");

        Assert.Equal("模型服务返回错误", payload.Title);
        Assert.Equal("https://error.agent.com/provider-request-error", payload.Type);
        Assert.Contains("invalid_request_error", payload.Detail);
        Assert.Equal("trace-1", payload.TraceId);
    }
}
