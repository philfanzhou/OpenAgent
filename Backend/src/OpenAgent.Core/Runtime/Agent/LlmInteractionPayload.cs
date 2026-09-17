using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// 大模型交互载荷的脱敏 JSON 投影。安全边界：
/// - 不接触 ApiKey（LlmConfig 的凭证字段从不参与投影）；
/// - 二进制内容（DataContent）只记录媒体类型与字节数占位符，附件字节永不落日志；
/// - 超长字段按 MaxContentLength 截断并标注。
/// </summary>
internal static class LlmInteractionPayload
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string SerializeRequest(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        int maxContentLength)
    {
        var payload = new LlmRequestPayload
        {
            Messages = messages.Select(message => ToMessage(message, maxContentLength)).ToList(),
            Options = ToOptions(options, maxContentLength)
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    internal static string SerializeResponse(ChatResponse response, int maxContentLength)
    {
        var payload = new LlmResponsePayload
        {
            Messages = response.Messages
                .Select(message => ToMessage(message, maxContentLength))
                .ToList(),
            ModelId = response.ModelId,
            FinishReason = response.FinishReason?.ToString(),
            Usage = ToUsage(response.Usage)
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    internal static string SerializeResponse(LlmStreamedResponse streamed, int maxContentLength)
    {
        var payload = new LlmResponsePayload
        {
            Messages = [ToMessage(streamed.ToMessage(), maxContentLength)],
            ModelId = streamed.ModelId,
            FinishReason = streamed.FinishReason,
            Usage = streamed.Usage,
            Updates = streamed.UpdateCount
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static LlmPayloadOptions? ToOptions(ChatOptions? options, int maxLength)
    {
        if (options == null)
        {
            return null;
        }

        return new LlmPayloadOptions
        {
            ModelId = options.ModelId,
            Temperature = options.Temperature,
            Instructions = Truncate(options.Instructions, maxLength),
            ToolMode = options.ToolMode?.ToString(),
            Tools = options.Tools?
                .Select(tool => tool.Name)
                .ToList()
        };
    }

    private static LlmPayloadMessage ToMessage(ChatMessage message, int maxLength)
    {
        List<LlmPayloadContent> contents = message.Contents
            .Select(content => ToContent(content, maxLength))
            .ToList();
        return new LlmPayloadMessage
        {
            Role = message.Role.Value ?? "unknown",
            AuthorName = message.AuthorName,
            Text = Truncate(string.Concat(message.Contents.OfType<TextContent>().Select(item => item.Text)), maxLength),
            Contents = contents
        };
    }

    private static LlmPayloadContent ToContent(AIContent content, int maxLength) => content switch
    {
        TextContent text => new LlmPayloadContent
        {
            Kind = "text",
            Text = Truncate(text.Text, maxLength)
        },
        TextReasoningContent reasoning => new LlmPayloadContent
        {
            Kind = "reasoning",
            Text = Truncate(reasoning.Text, maxLength)
        },
        FunctionCallContent call => new LlmPayloadContent
        {
            Kind = "functionCall",
            CallId = call.CallId,
            Name = call.Name,
            Arguments = ToJsonString(call.Arguments, maxLength)
        },
        FunctionResultContent result => new LlmPayloadContent
        {
            Kind = "functionResult",
            CallId = result.CallId,
            Result = ToJsonString(result.Result, maxLength),
            Text = result.Exception == null ? null : Truncate(result.Exception.Message, maxLength)
        },
        DataContent data => new LlmPayloadContent
        {
            // 二进制只留占位符：附件字节（Data）永不进入日志。
            Kind = "data",
            Name = data.Name,
            MediaType = data.MediaType,
            Bytes = data.Data.Length > 0 ? data.Data.Length : null,
            Text = data.Data.Length > 0 ? null : Truncate(data.Uri, maxLength)
        },
        ErrorContent error => new LlmPayloadContent
        {
            Kind = "error",
            Text = Truncate(error.Message, maxLength),
            Name = error.ErrorCode
        },
        _ => new LlmPayloadContent
        {
            Kind = "other",
            Name = content.GetType().Name
        }
    };

    private static LlmPayloadUsage? ToUsage(UsageDetails? usage)
    {
        if (usage == null)
        {
            return null;
        }

        return new LlmPayloadUsage
        {
            InputTokens = usage.InputTokenCount,
            OutputTokens = usage.OutputTokenCount,
            TotalTokens = usage.TotalTokenCount,
            CachedInputTokens = usage.CachedInputTokenCount,
            ReasoningTokens = usage.ReasoningTokenCount
        };
    }

    private static string? ToJsonString(object? value, int maxLength)
    {
        if (value == null)
        {
            return null;
        }

        if (value is string text)
        {
            return Truncate(text, maxLength);
        }

        try
        {
            return Truncate(JsonSerializer.Serialize(value, JsonOptions), maxLength);
        }
        catch
        {
            return Truncate(value.ToString(), maxLength);
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value == null || maxLength <= 0 || value.Length <= maxLength)
        {
            return value;
        }

        int keep = Math.Max(0, maxLength - 32);
        return $"{value[..keep]}…[truncated {value.Length - keep} chars]";
    }

    internal sealed record LlmRequestPayload
    {
        public required List<LlmPayloadMessage> Messages { get; init; }
        public LlmPayloadOptions? Options { get; init; }
    }

    internal sealed record LlmResponsePayload
    {
        public required List<LlmPayloadMessage> Messages { get; init; }
        public string? ModelId { get; init; }
        public string? FinishReason { get; init; }
        public LlmPayloadUsage? Usage { get; init; }
        public int? Updates { get; init; }
    }

    internal sealed record LlmPayloadMessage
    {
        public required string Role { get; init; }
        public string? AuthorName { get; init; }
        public string? Text { get; init; }
        public List<LlmPayloadContent> Contents { get; init; } = [];
    }

    internal sealed record LlmPayloadContent
    {
        public required string Kind { get; init; }
        public string? Text { get; init; }
        public string? CallId { get; init; }
        public string? Name { get; init; }
        public string? Arguments { get; init; }
        public string? Result { get; init; }
        public string? MediaType { get; init; }
        public long? Bytes { get; init; }
    }

    internal sealed record LlmPayloadOptions
    {
        public string? ModelId { get; init; }
        public float? Temperature { get; init; }
        public string? Instructions { get; init; }
        public string? ToolMode { get; init; }
        public List<string>? Tools { get; init; }
    }

    internal sealed record LlmPayloadUsage
    {
        public long? InputTokens { get; init; }
        public long? OutputTokens { get; init; }
        public long? TotalTokens { get; init; }
        public long? CachedInputTokens { get; init; }
        public long? ReasoningTokens { get; init; }
    }
}

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
        contents.Add(new TextContent(_text.ToString()));
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
