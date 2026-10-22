using OpenAgent.Engine.Hosting;
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAgent.Application.Runtime;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class StreamingPayloadFactoryTests
{
    [Fact]
    public void FormatProviderError_ParsesStatusTypeAndMessage()
    {
        string result = ExecutionFailureDescriptor.FormatProviderError(400, "HTTP 400 (invalid_request_error: bad request body)");

        Assert.StartsWith("模型服务返回错误", result);
        Assert.Contains("HTTP 400", result);
        Assert.Contains("invalid_request_error", result);
        Assert.Contains("bad request body", result);
    }

    [Fact]
    public void FormatProviderError_EmptyProviderMessage_IncludesRetryHint()
    {
        string result = ExecutionFailureDescriptor.FormatProviderError(400, "HTTP 400 (invalid_request_error: )");

        Assert.Contains("HTTP 400", result);
        Assert.Contains("invalid_request_error", result);
        Assert.Contains("请检查模型配置后重试", result);
    }

    [Fact]
    public void FormatProviderError_MessageWithoutType_StillShowsStatus()
    {
        string result = ExecutionFailureDescriptor.FormatProviderError(503, "connection refused");

        Assert.Contains("HTTP 503", result);
        Assert.Contains("请检查模型配置后重试", result);
    }

    [Fact]
    public void CreateErrorPayload_UnknownException_UsesSanitizedGenericDetail()
    {
        // 未知异常原文可能包含内部细节：SSE 只给通用文案，原文仅随服务器日志留痕。
        var payload = StreamingPayloadFactory.CreateErrorPayload(new Exception("boom with internal detail"), "trace-1");

        Assert.DoesNotContain("boom", payload.Detail);
        Assert.Contains("TraceId", payload.Detail);
    }

    [Fact]
    public void CreateErrorPayload_ClassifiedAgentException_MatchesPersistedMetadata()
    {
        var exception = new AgentException(
            AgentErrorCode.LlmAuthFailed,
            "模型服务拒绝认证（HTTP 401）：API Key 无效、已过期或无权访问该模型。请在模型配置中核对 API Key 后重试。");
        var payload = StreamingPayloadFactory.CreateErrorPayload(exception, "trace-2");

        Assert.Equal("模型服务认证失败", payload.Title);
        Assert.Equal("https://error.agent.com/llmauthfailed", payload.Type);
        Assert.Equal("LlmAuthFailed", payload.Code);
        Assert.Contains("API Key", payload.Detail);
        Assert.Equal("trace-2", payload.TraceId);
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
