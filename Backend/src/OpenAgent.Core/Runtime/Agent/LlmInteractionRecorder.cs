using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// 挂在 provider client 最外层的交互记录器：捕获每一次发往大模型的
/// 请求与响应（含工具循环的多次迭代），按轮次 TraceId 落库。
/// 记录失败只降级为警告，绝不影响对话主流程。
/// </summary>
internal sealed class LlmInteractionRecorder : DelegatingChatClient
{
    private readonly LlmInteractionCapture _capture;
    private readonly LlmConfig _llm;
    private readonly ILlmInteractionStore _store;
    private readonly LlmInteractionOptions _options;
    private readonly ILogger _logger;
    private int _callIndex;

    public LlmInteractionRecorder(
        IChatClient innerClient,
        LlmInteractionCapture capture,
        LlmConfig llm,
        ILlmInteractionStore store,
        LlmInteractionOptions options,
        ILogger logger)
        : base(innerClient)
    {
        _capture = capture;
        _llm = llm;
        _store = store;
        _options = options;
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Attempt attempt = Begin(messages, options, streamed: false);
        try
        {
            ChatResponse response = await base.GetResponseAsync(
                messages,
                options,
                cancellationToken).ConfigureAwait(false);
            await RecordAsync(
                attempt,
                LlmInteractionStatus.Succeeded,
                responseJson: SafeSerialize(
                    () => LlmInteractionPayload.SerializeResponse(response, _options.MaxContentLength)),
                usage: AgentResponseAdapter.ConvertUsage(response.Usage),
                modelId: string.IsNullOrWhiteSpace(response.ModelId) ? _llm.ModelId : response.ModelId,
                errorMessage: null).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException)
        {
            await RecordAsync(attempt, LlmInteractionStatus.Cancelled, null, null, _llm.ModelId, "cancelled")
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await RecordAsync(attempt, LlmInteractionStatus.Failed, null, null, _llm.ModelId, ex.Message)
                .ConfigureAwait(false);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Attempt attempt = Begin(messages, options, streamed: true);
        LlmStreamedResponse streamed = new();
        IAsyncEnumerator<ChatResponseUpdate> enumerator = base.GetStreamingResponseAsync(
                messages,
                options,
                cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await RecordAsync(
                        attempt,
                        LlmInteractionStatus.Cancelled,
                        SafeSerialize(() => LlmInteractionPayload.SerializeResponse(streamed, _options.MaxContentLength)),
                        streamed.ToTokenUsage(),
                        streamed.ModelId ?? _llm.ModelId,
                        "cancelled").ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    await RecordAsync(
                        attempt,
                        LlmInteractionStatus.Failed,
                        SafeSerialize(() => LlmInteractionPayload.SerializeResponse(streamed, _options.MaxContentLength)),
                        streamed.ToTokenUsage(),
                        streamed.ModelId ?? _llm.ModelId,
                        ex.Message).ConfigureAwait(false);
                    throw;
                }

                if (!hasNext)
                {
                    break;
                }

                streamed.Add(enumerator.Current);
                yield return enumerator.Current;
            }

            await RecordAsync(
                attempt,
                LlmInteractionStatus.Succeeded,
                SafeSerialize(() => LlmInteractionPayload.SerializeResponse(streamed, _options.MaxContentLength)),
                streamed.ToTokenUsage(),
                streamed.ModelId ?? _llm.ModelId,
                errorMessage: null).ConfigureAwait(false);
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Attempt Begin(IEnumerable<ChatMessage> messages, ChatOptions? options, bool streamed) => new(
        Guid.NewGuid().ToString("N"),
        Interlocked.Increment(ref _callIndex) - 1,
        SafeSerialize(() => LlmInteractionPayload.SerializeRequest(messages, options, _options.MaxContentLength)),
        Stopwatch.GetTimestamp(),
        DateTimeOffset.UtcNow,
        streamed);

    private static string? SafeSerialize(Func<string> serialize)
    {
        try
        {
            return serialize();
        }
        catch
        {
            // 载荷投影失败不影响调用本身；日志会缺少载荷但保留元数据。
            return null;
        }
    }

    private async Task RecordAsync(
        Attempt attempt,
        LlmInteractionStatus status,
        string? responseJson,
        Contracts.Requests.TokenUsage? usage,
        string modelId,
        string? errorMessage)
    {
        int durationMs = (int)Math.Min(
            int.MaxValue,
            Stopwatch.GetElapsedTime(attempt.Started).TotalMilliseconds);
        var record = new LlmInteractionRecord
        {
            InteractionId = attempt.InteractionId,
            TenantId = _capture.TenantId,
            UserId = _capture.UserId,
            ConversationId = _capture.ConversationId,
            TraceId = _capture.TraceId,
            AgentId = _capture.AgentId,
            Source = _capture.Source,
            Provider = _llm.Provider,
            ApiFormat = _llm.Format.ToString(),
            ModelId = modelId,
            Streamed = attempt.Streamed,
            CallIndex = attempt.CallIndex,
            RequestJson = attempt.RequestJson,
            ResponseJson = responseJson,
            TokenUsage = usage,
            Status = status,
            ErrorMessage = errorMessage is null
                ? null
                : errorMessage.Length <= 1024 ? errorMessage : errorMessage[..1024],
            StartedAt = attempt.StartedAt,
            DurationMs = durationMs
        };

        try
        {
            await _store.RecordAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LlmInteractionLog.RecordFailed(_logger, record.ConversationId, record.TraceId, ex);
        }

        LlmInteractionLog.Recorded(
            _logger,
            record.ConversationId,
            record.TraceId,
            record.AgentId,
            record.ModelId,
            record.Source.ToString(),
            status.ToString(),
            durationMs,
            record.CallIndex);
    }

    private sealed record Attempt(
        string InteractionId,
        int CallIndex,
        string? RequestJson,
        long Started,
        DateTimeOffset StartedAt,
        bool Streamed);
}

internal static partial class LlmInteractionLog
{
    [LoggerMessage(
        EventId = 1460,
        Level = LogLevel.Information,
        Message = "LLM interaction recorded. ConversationId={ConversationId} TraceId={TraceId} AgentId={AgentId} Model={ModelId} Source={Source} Status={Status} DurationMs={DurationMs} CallIndex={CallIndex}")]
    internal static partial void Recorded(
        ILogger logger,
        string? conversationId,
        string traceId,
        string? agentId,
        string modelId,
        string source,
        string status,
        int durationMs,
        int callIndex);

    [LoggerMessage(
        EventId = 1461,
        Level = LogLevel.Warning,
        Message = "LLM interaction log write failed. ConversationId={ConversationId} TraceId={TraceId}")]
    internal static partial void RecordFailed(
        ILogger logger,
        string? conversationId,
        string traceId,
        Exception? exception);
}
