using System.ClientModel;
using System.Text.RegularExpressions;
using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Core.Runtime;

/// <summary>
/// 把执行失败异常分类为可直接展示给用户的脱敏文案。同一份文案既用于 SSE error
/// 事件（Engine 的 StreamingPayloadFactory），也随失败消息持久化进
/// <see cref="ConversationMessageMetadata.Error"/>——否则错误只存在于一次流式
/// 事件里，前端刷新后只剩"响应未完成"占位，用户无从得知真实原因。
/// 输出绝不包含 provider 原始错误体（可能携带账户标识或密钥材料），只保留
/// 括号形态解析出的错误码摘要。
/// </summary>
public static class ExecutionFailureDescriptor
{
    public const string ContextOverflowTitle = "超出模型上下文窗口";

    /// <summary>
    /// 上下文超限的错误码/文案标记（大小写不敏感）。覆盖主流提供方形态：
    /// OpenAI "context_length_exceeded" / "maximum context length"、
    /// Anthropic "prompt_too_long" / "prompt is too long" / "exceed context limit"、
    /// Gemini "exceed ... context window"、DashScope "Requests too long"、
    /// 以及国内服务的中文文案。新的提供方形态在此追加标记即可。
    /// </summary>
    private static readonly Regex ContextOverflowPattern = new(
        "context_length_exceeded|prompt_too_long|prompt is too long|maximum context length"
        + "|context length.{0,20}exceed|exceed.{0,40}context (window|length|limit)"
        + "|requests too long"
        + "|超出.{0,8}上下文|上下文.{0,8}超|超过.{0,8}上下文",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsContextLengthExceeded(string? text) =>
        !string.IsNullOrEmpty(text) && ContextOverflowPattern.IsMatch(text);

    /// <summary>沿 InnerException 链逐层匹配：MAF/FICC 可能包装 provider 异常。</summary>
    public static bool IsContextLengthExceeded(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (IsContextLengthExceeded(current.Message))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 失败原因的持久化形态：上下文超限给出精确可执行建议，其余 provider 错误
    /// 给脱敏摘要，未知异常给通用文案（原文不透出）。取消返回 null（正常路径）。
    /// </summary>
    public static MessageErrorMetadata? Describe(Exception exception, string? traceId = null)
    {
        if (exception is OperationCanceledException)
        {
            return null;
        }

        ClientResultException? provider = FindClientResultException(exception);
        if (IsContextLengthExceeded(exception))
        {
            return new MessageErrorMetadata(
                ContextOverflowTitle,
                BuildContextOverflowDetail(provider?.Status, provider?.Message),
                traceId);
        }
        if (provider != null)
        {
            return new MessageErrorMetadata(
                "模型服务返回错误",
                FormatProviderError(provider.Status, provider.Message),
                traceId);
        }
        return new MessageErrorMetadata(
            "执行失败",
            "Agent 执行失败，请稍后重试；若持续失败请联系管理员并提供 TraceId。",
            traceId);
    }

    public static string BuildContextOverflowDetail(int? status, string? message)
    {
        string core = status is int value ? $"（{ProviderCore(value, message)}）" : string.Empty;
        return $"请求内容超出当前模型的上下文窗口上限{core}。请开启新会话，或精简历史消息与附件后重试。";
    }

    /// <summary>
    /// provider 错误的脱敏摘要："HTTP {status}" 或 "HTTP {status} · {type}"。
    /// type 取错误消息最后一对括号内 "Type: Detail" 形态的 Type 部分；
    /// 解析不出时只保留状态码，原始错误体绝不透出。
    /// </summary>
    public static string FormatProviderError(int status, string? message)
    {
        (string type, string detail) = ParseParenthetical(message);
        string core = ProviderCore(status, message);
        return string.IsNullOrWhiteSpace(detail)
            ? $"模型服务返回错误（{core}）。请检查模型配置后重试。"
            : $"模型服务返回错误（{core}）：{detail}";
    }

    private static string ProviderCore(int status, string? message) =>
        ParseParenthetical(message).Type is { Length: > 0 } type
            ? $"HTTP {status} · {type}"
            : $"HTTP {status}";

    private static (string Type, string Detail) ParseParenthetical(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return (string.Empty, string.Empty);
        }

        int open = message.LastIndexOf('(');
        int close = message.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return (string.Empty, string.Empty);
        }

        string inner = message.Substring(open + 1, close - open - 1);
        int colon = inner.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0
            ? (inner[..colon].Trim(), inner[(colon + 2)..].Trim())
            : (inner.Trim(), string.Empty);
    }

    private static ClientResultException? FindClientResultException(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is ClientResultException provider)
            {
                return provider;
            }
        }
        return null;
    }
}
