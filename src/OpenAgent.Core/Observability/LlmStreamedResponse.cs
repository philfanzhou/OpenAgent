using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Core.Mapping;

namespace OpenAgent.Core.Observability;

/// <summary>流式响应的累积状态，完成后投影为响应载荷。</summary>
internal sealed class LlmStreamedResponse
{
    private readonly System.Text.StringBuilder _text = new();
    private readonly System.Text.StringBuilder _reasoning = new();
    private readonly List<FunctionCallContent> _calls = [];
    private readonly List<FunctionResultContent> _results = [];
    private UsageDetails? _usage;
    private string? _modelId;
    private ChatFinishReason? _finishReason;

    public int UpdateCount { get; private set; }

    public string? ModelId => _modelId;

    public string? FinishReason => _finishReason?.ToString();

    public LlmInteractionPayload.LlmPayloadUsage? Usage { get; private set; }

    public void Add(ChatResponseUpdate update)
    {
        UpdateCount++;
        _modelId = string.IsNullOrWhiteSpace(update.ModelId) ? _modelId : update.ModelId;
        if (update.FinishReason != null)
        {
            _finishReason = update.FinishReason;
        }

        foreach (AIContent content in update.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    _text.Append(text.Text);
                    break;
                case TextReasoningContent reasoning:
                    _reasoning.Append(reasoning.Text);
                    break;
                case FunctionCallContent call:
                    _calls.Add(call);
                    break;
                case FunctionResultContent result:
                    _results.Add(result);
                    break;
                case UsageContent usage:
                    _usage = usage.Details;
                    break;
            }
        }
    }

    /// <summary>提取最终 usage（平台 TokenUsage 口径）。</summary>
    public Contracts.Requests.TokenUsage? ToTokenUsage() => AgentResponseAdapter.ConvertUsage(_usage);

    internal ChatMessage ToMessage()
    {
        List<AIContent> contents = [];
        if (_reasoning.Length > 0)
        {
            contents.Add(new TextReasoningContent(_reasoning.ToString()));
        }
        contents.AddRange(_calls);
        contents.AddRange(_results);
        // 纯工具调用迭代没有正文文本，输出空 TextContent 只会在日志里制造空对象。
        if (_text.Length > 0)
        {
            contents.Add(new TextContent(_text.ToString()));
        }
        Usage = new LlmInteractionPayload.LlmPayloadUsage
        {
            InputTokens = _usage?.InputTokenCount,
            OutputTokens = _usage?.OutputTokenCount,
            TotalTokens = _usage?.TotalTokenCount,
            CachedInputTokens = _usage?.CachedInputTokenCount,
            ReasoningTokens = _usage?.ReasoningTokenCount
        };
        return new ChatMessage(ChatRole.Assistant, contents);
    }
}
