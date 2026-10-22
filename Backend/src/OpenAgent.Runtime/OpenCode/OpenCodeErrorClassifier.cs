using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Runtime.Acp.Rpc;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>
/// 把 OpenCode 轮次失败分类为携带 <see cref="AgentErrorCode"/> 的 <see cref="AgentException"/>。
/// Provider 错误经 harness 以 JSON-RPC error（<see cref="AcpRpcException"/>）回到平台，
/// 实测（OpenCode 1.19.3）其 Message 会**逐字回带 provider 响应体**（如
/// "Internal error: Incorrect API key provided: sk-***"、"Internal error: Unauthorized: &lt;body&gt;"），
/// 因此：产出消息只能是固定中文模板，绝不引用原文片段；InnerException 一律为 null
/// （防止 ToString() 进入日志时泄露）；仅 HTTP 状态码与 RPC 错误码两个纯数字字段
/// 经 <see cref="AgentException.Details"/> 进入服务器日志，供管理员按 TraceId 排查。
/// </summary>
internal static class OpenCodeErrorClassifier
{
    /// <summary>
    /// 上下文超限。OpenCode 会把 provider 的 context_length_exceeded 改写成自己的
    /// 文案（实测形态 "Session too large to compact - context exceeds model limit
    /// even after stripping media"），因此必须同时覆盖 harness 与 provider 两侧形态。
    /// </summary>
    private static readonly Regex ContextOverflowPattern = new(
        "too large to compact|context exceeds|exceeds model limit"
        + "|context_length_exceeded|prompt_too_long|prompt is too long|maximum context length"
        + "|context (window|length|limit).{0,20}exceed|exceed.{0,40}context (window|length|limit)"
        + "|requests too long"
        + "|超出.{0,8}上下文|上下文.{0,8}超|超过.{0,8}上下文",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>认证/授权失败：无效 Key、过期 Key、无权访问模型。</summary>
    private static readonly Regex AuthPattern = new(
        "invalid[ _-]?api[ _-]?key|incorrect api key|api key (is )?(invalid|incorrect|not valid)"
        + "|unauthorized|authentication|invalid token|permission denied"
        + "|无效.{0,6}密钥|密钥.{0,6}(无效|错误|过期)|鉴权失败|认证失败",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>限流/额度类：429、配额耗尽、账户欠费。</summary>
    private static readonly Regex QuotaPattern = new(
        "rate.?limit|too many requests|quota|insufficient|balance"
        + "|限流|额度(不足|用完|耗尽)|余额不足|欠费",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>模型不存在/无权使用。</summary>
    private static readonly Regex ModelNotFoundPattern = new(
        "model.{0,24}(not found|does not exist|is unknown)|(not found|unknown|no such).{0,16}model|model_not_found"
        + "|模型.{0,8}(不存在|未找到|不可用)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>超时类（连接/请求超时，非轮次级 TurnTimeout）。</summary>
    private static readonly Regex TimeoutPattern = new(
        "timeout|timed out|etimedout|deadline exceeded|超时",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>从原始错误提取 HTTP 状态码（仅限常见状态码集合，纯数字无泄露面）。</summary>
    private static readonly Regex HttpStatusPattern = new(
        "\\b(400|401|402|403|404|408|409|413|422|429|500|502|503|504)\\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static AgentException Classify(Exception error)
    {
        AcpRpcException? rpc = FindAcpRpcException(error);
        if (rpc is not null)
        {
            string message = rpc.Message;
            string? status = ExtractHttpStatus(message);
            string diagnostic = $"rpc={rpc.Code}";
            if (ContextOverflowPattern.IsMatch(message))
            {
                return new AgentException(
                    AgentErrorCode.LlmContextOverflow,
                    "会话内容超出模型上下文窗口上限：请开启新会话，或精简历史消息与附件后重试。",
                    diagnostic);
            }
            if (AuthPattern.IsMatch(message) || status is "401" or "403")
            {
                return new AgentException(
                    AgentErrorCode.LlmAuthFailed,
                    $"模型服务拒绝认证{StatusCore(status)}：API Key 无效、已过期或无权访问该模型。请在模型配置中核对 API Key 后重试。",
                    diagnostic);
            }
            if (QuotaPattern.IsMatch(message) || status is "429")
            {
                return new AgentException(
                    AgentErrorCode.LlmQuotaExceeded,
                    $"模型服务限流或额度不足{StatusCore(status)}：请稍后重试，或检查账户额度与速率限制配置。",
                    diagnostic);
            }
            if (ModelNotFoundPattern.IsMatch(message))
            {
                return new AgentException(
                    AgentErrorCode.LlmModelNotFound,
                    $"模型不存在或无权访问{StatusCore(status)}：请核对模型 ID、API 格式与接入端点配置。",
                    diagnostic);
            }
            if (TimeoutPattern.IsMatch(message))
            {
                return new AgentException(
                    AgentErrorCode.LlmTimeout,
                    "模型服务响应超时：请稍后重试；若持续超时请联系管理员调整超时配置。",
                    diagnostic);
            }
            // 未识别形态的 provider 错误：沿用通用执行失败码，摘要只带状态码。
            return new AgentException(
                AgentErrorCode.PipelineExecutionFailed,
                $"模型服务返回错误{StatusCore(status)}：请检查模型配置后重试；若持续失败请联系管理员并提供 TraceId。",
                diagnostic);
        }

        return error switch
        {
            TimeoutException => new AgentException(
                AgentErrorCode.LlmTimeout,
                "模型服务响应超时：请稍后重试；若持续超时请联系管理员调整超时配置。",
                error.GetType().Name),
            // 进程退出/连接中断/协议无法解析：harness 侧故障，与 provider 无关。
            IOException or EndOfStreamException or ObjectDisposedException or JsonException =>
                new AgentException(
                    AgentErrorCode.DependencyUnavailable,
                    "OpenCode 运行时异常退出或连接中断：请稍后重试；若持续失败请联系管理员并提供 TraceId。",
                    error.GetType().Name),
            _ => new AgentException(
                AgentErrorCode.PipelineExecutionFailed,
                "OpenCode runtime failed; check the executable, provider configuration and connectivity.",
                error.GetType().Name),
        };
    }

    private static string? ExtractHttpStatus(string? message)
    {
        Match match = message is null ? Match.Empty : HttpStatusPattern.Match(message);
        return match.Success ? match.Value : null;
    }

    private static string StatusCore(string? status) =>
        string.IsNullOrEmpty(status) ? string.Empty : $"（HTTP {status}）";

    private static AcpRpcException? FindAcpRpcException(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is AcpRpcException rpc)
            {
                return rpc;
            }
        }
        return null;
    }
}
