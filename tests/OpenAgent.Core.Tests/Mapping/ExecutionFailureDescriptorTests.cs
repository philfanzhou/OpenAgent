using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAgent.Core.Runtime;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public class ExecutionFailureDescriptorTests
{
    [Theory]
    // OpenAI：错误码与消息正文两种形态
    [InlineData("HTTP 400 (invalid_request_error: This model's maximum context length is 4096 tokens. However, your messages resulted in 10000 tokens.")]
    [InlineData("HTTP 400 (context_length_exceeded: This model's maximum context length is 8192 tokens, however you requested 10000 tokens)")]
    // Anthropic：type 与消息正文
    [InlineData("Error code: 400 - prompt_too_long: input length and `max_tokens` exceed context limit: 10000 + 1024 > 8192")]
    [InlineData("Error code: 400 - prompt is too long: 10000 tokens > 8192 maximum")]
    // Gemini / DashScope / 其他
    [InlineData("Input tokens (130000) exceed the model's context window (128000)")]
    [InlineData("Requests too long, 12000 tokens exceeds the model's max context length 8192")]
    // 国内服务中文文案
    [InlineData("请求超出上下文长度限制，请精简后重试")]
    [InlineData("输入超过模型上下文窗口上限")]
    public void IsContextLengthExceeded_MatchesProviderVariants(string message) =>
        Assert.True(ExecutionFailureDescriptor.IsContextLengthExceeded(message));

    [Theory]
    [InlineData("HTTP 429 (rate_limit_exceeded: too many requests)")]
    [InlineData("HTTP 400 (invalid_request_error: bad request body)")]
    [InlineData("connection refused")]
    [InlineData("permission denied for model gpt-4")]
    [InlineData("")]
    [InlineData("服务内部错误")]
    public void IsContextLengthExceeded_IgnoresOtherProviderErrors(string? message) =>
        Assert.False(ExecutionFailureDescriptor.IsContextLengthExceeded(message));

    [Fact]
    public void IsContextLengthExceeded_WalksInnerExceptions()
    {
        // MAF/FICC 可能包装 provider 异常：原始 ClientResultException 在内层时也要识别。
        var wrapped = new InvalidOperationException(
            "The chat client failed to complete the run.",
            new ClientResultException(
                "HTTP 400 (invalid_request_error: This model's maximum context length is 8192 tokens, however you requested 10000 tokens)",
                (PipelineResponse)null!,
                null!));

        Assert.True(ExecutionFailureDescriptor.IsContextLengthExceeded(wrapped));
    }

    [Fact]
    public void Describe_ContextOverflowException_ReturnsActionableMetadataWithoutRawBody()
    {
        var exception = new ClientResultException(
            "HTTP 400 (invalid_request_error: This model's maximum context length is 8192 tokens, however you requested 10000 tokens)",
            (PipelineResponse)null!,
            null!);

        var error = ExecutionFailureDescriptor.Describe(exception, "trace-42");

        Assert.NotNull(error);
        Assert.Equal(ExecutionFailureDescriptor.ContextOverflowTitle, error!.Title);
        // 用户可执行的建议必须在场；provider 原始文本不得整段透出。
        Assert.Contains("上下文", error.Detail);
        Assert.Contains("新会话", error.Detail);
        Assert.DoesNotContain("however you requested", error.Detail);
        Assert.Equal("trace-42", error.TraceId);
    }

    [Fact]
    public void Describe_OtherProviderError_UsesParsedSummary()
    {
        var exception = new ClientResultException(
            "HTTP 429 (rate_limit_exceeded: too many requests)",
            (PipelineResponse)null!,
            null!);

        var error = ExecutionFailureDescriptor.Describe(exception, "trace-1");

        Assert.NotNull(error);
        // null PipelineResponse 构造的替身 Status 为 0，摘要仍带 HTTP 状态码形态。
        Assert.Contains("HTTP", error!.Detail);
        Assert.Contains("rate_limit_exceeded", error.Detail);
        Assert.Equal("trace-1", error.TraceId);
    }

    [Fact]
    public void Describe_ProviderBodyWithoutParens_DoesNotLeakRawText()
    {
        var exception = new ClientResultException(
            "raw provider body with account identifier",
            (PipelineResponse)null!,
            null!);

        var error = ExecutionFailureDescriptor.Describe(exception, "trace-1");

        Assert.NotNull(error);
        Assert.DoesNotContain("raw provider body", error!.Detail);
        Assert.Contains("HTTP", error.Detail);
    }

    [Fact]
    public void Describe_UnknownException_GenericTextWithTraceId()
    {
        var error = ExecutionFailureDescriptor.Describe(new Exception("boom with internal detail"), "trace-9");

        Assert.NotNull(error);
        Assert.DoesNotContain("boom with internal detail", error!.Detail);
        Assert.Equal("trace-9", error.TraceId);
    }
}
