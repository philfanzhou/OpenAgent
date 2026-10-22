using System.Text.Json;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

/// <summary>
/// 分类器契约：产出消息为固定中文模板（绝不引用 RPC 原文片段）、InnerException 恒为 null
/// （实测 OpenCode 的 RPC 错误 Message 会逐字回带 provider 响应体，ToString 不得泄露）、
/// 仅纯数字诊断字段（rpc 码 / HTTP 状态码）经 Details 进入日志。
/// InlineData 中的 "Internal error: ..." 形态来自 OpenCode 1.19.3 实测回传。
/// </summary>
public sealed class OpenCodeErrorClassifierTests
{
    [Theory]
    // 实测：OpenAI 形态 JSON body（含脱敏 Key 片段）
    [InlineData("Internal error: Incorrect API key provided: sk-probe***")]
    // 实测：非 JSON body 时 OpenCode 回带 "Unauthorized: <body 原文>"
    [InlineData("Internal error: Unauthorized: remote-secret-must-not-be-saved")]
    // Anthropic / OpenAI 兼容网关形态
    [InlineData("Error code: 401 - authentication_error: invalid x-api-key")]
    [InlineData("HTTP 403 (invalid_api_key: Invalid API key provided)")]
    // 国内服务中文文案
    [InlineData("Internal error: 鉴权失败：无效的API密钥")]
    public void Classify_AuthFailure_MapsToLlmAuthFailed(string rpcMessage)
    {
        AgentException classified = OpenCodeErrorClassifier.Classify(Rpc(rpcMessage));

        Assert.Equal(AgentErrorCode.LlmAuthFailed, classified.ErrorCode);
        Assert.Contains("API Key", classified.Message);
    }

    [Fact]
    public void Classify_NeverLeaksRawProviderBodyAndKeepsNoInnerException()
    {
        AgentException classified = OpenCodeErrorClassifier.Classify(
            Rpc("Internal error: Unauthorized: remote-secret-must-not-be-saved with sk-abc***"));

        Assert.DoesNotContain("remote-secret", classified.Message);
        Assert.DoesNotContain("sk-abc", classified.Message);
        Assert.Null(classified.InnerException);
        Assert.DoesNotContain("remote-secret", classified.ToString());
        // 纯数字诊断字段进日志：RPC 错误码。
        Assert.Equal("rpc=-32603", classified.Details);
    }

    [Theory]
    // 实测：OpenCode 把 provider 的 context_length_exceeded 改写成自己的文案
    [InlineData("Internal error: Session too large to compact - context exceeds model limit even after stripping media", AgentErrorCode.LlmContextOverflow)]
    // provider 原生形态（不经 OpenCode 改写时）
    [InlineData("HTTP 400 (context_length_exceeded: This model's maximum context length is 8192 tokens)", AgentErrorCode.LlmContextOverflow)]
    [InlineData("HTTP 429 (rate_limit_exceeded: Too many requests)", AgentErrorCode.LlmQuotaExceeded)]
    // 实测：模型不存在
    [InlineData("Internal error: The model 'fixture' does not exist or you do not have access to it.", AgentErrorCode.LlmModelNotFound)]
    [InlineData("model_not_found: no such model claude-xx", AgentErrorCode.LlmModelNotFound)]
    [InlineData("Request timeout after 60000ms", AgentErrorCode.LlmTimeout)]
    [InlineData("HTTP 500 (internal_server_error: provider exploded)", AgentErrorCode.PipelineExecutionFailed)]
    public void Classify_ProviderVariants_MapToExpectedCodes(string rpcMessage, AgentErrorCode expected)
    {
        AgentException classified = OpenCodeErrorClassifier.Classify(Rpc(rpcMessage));

        Assert.Equal(expected, classified.ErrorCode);
        Assert.Null(classified.InnerException);
    }

    [Fact]
    public void Classify_StatusInMessage_BecomesSanitizedHttpStatusCore()
    {
        AgentException classified = OpenCodeErrorClassifier.Classify(
            Rpc("HTTP 429 (rate_limit_exceeded: Too many requests)"));

        Assert.Equal(AgentErrorCode.LlmQuotaExceeded, classified.ErrorCode);
        Assert.Contains("（HTTP 429）", classified.Message);
    }

    [Fact]
    public void Classify_NumberInsideLargerNumber_IsNotTakenAsHttpStatus()
    {
        // 14013 中的 "401" 不构成词边界命中，不得误报为认证失败。
        AgentException classified = OpenCodeErrorClassifier.Classify(Rpc("input of 14013 tokens rejected"));

        Assert.Equal(AgentErrorCode.PipelineExecutionFailed, classified.ErrorCode);
        Assert.DoesNotContain("HTTP", classified.Message);
    }

    [Fact]
    public void Classify_WalksInnerExceptionChainWithoutPreservingIt()
    {
        var wrapped = new InvalidOperationException("session/prompt failed", Rpc("401 Unauthorized"));

        AgentException classified = OpenCodeErrorClassifier.Classify(wrapped);

        Assert.Equal(AgentErrorCode.LlmAuthFailed, classified.ErrorCode);
        Assert.Null(classified.InnerException);
    }

    [Fact]
    public void Classify_ProcessDeath_MapsToDependencyUnavailable()
    {
        AgentException classified = OpenCodeErrorClassifier.Classify(new IOException("ACP connection closed."));

        Assert.Equal(AgentErrorCode.DependencyUnavailable, classified.ErrorCode);
        Assert.Contains("OpenCode", classified.Message);
        Assert.Null(classified.InnerException);
    }

    [Fact]
    public void Classify_TimeoutException_MapsToLlmTimeout()
    {
        AgentException classified = OpenCodeErrorClassifier.Classify(new TimeoutException("request timed out"));

        Assert.Equal(AgentErrorCode.LlmTimeout, classified.ErrorCode);
    }

    [Fact]
    public void Classify_UnknownException_MapsToPipelineFailureWithTypeNameDiagnostic()
    {
        var error = new Exception("boom");

        AgentException classified = OpenCodeErrorClassifier.Classify(error);

        Assert.Equal(AgentErrorCode.PipelineExecutionFailed, classified.ErrorCode);
        Assert.Null(classified.InnerException);
        Assert.Equal("Exception", classified.Details);
    }

    private static AcpRpcException Rpc(string message) => new(-32603, message, JsonDocument.Parse("{}").RootElement);
}
