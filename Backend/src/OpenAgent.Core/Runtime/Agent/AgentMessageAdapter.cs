using System.Text.Json;
using System.Text;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;

namespace OpenAgent.Core.Runtime.Agent;

internal static class AgentMessageAdapter
{
    internal static ChatMessage CreateUser(
        string input,
        IReadOnlyList<FileAssetContent> files)
    {
        var message = new ChatMessage(Microsoft.Extensions.AI.ChatRole.User, input);
        AddFiles(message, files);
        return message;
    }

    internal static ChatMessage? FromStored(ConversationMessage message)
    {
        Microsoft.Extensions.AI.ChatRole? role = message.Role.ToLowerInvariant() switch
        {
            "user" => Microsoft.Extensions.AI.ChatRole.User,
            "assistant" => Microsoft.Extensions.AI.ChatRole.Assistant,
            "tool" => Microsoft.Extensions.AI.ChatRole.Tool,
            "summary" => Microsoft.Extensions.AI.ChatRole.Assistant,
            _ => null
        };
        if (role == null)
        {
            return null;
        }

        string content = string.Equals(
            message.Role,
            "summary",
            StringComparison.OrdinalIgnoreCase)
                ? $"[Conversation summary]\n{message.Content}"
                : message.Content;
        var chatMessage = new ChatMessage(role.Value, content);
        if (role == Microsoft.Extensions.AI.ChatRole.Tool
            && !string.IsNullOrEmpty(message.ToolCallId))
        {
            chatMessage.Contents.Clear();
            chatMessage.Contents.Add(new FunctionResultContent(
                message.ToolCallId,
                message.Content));
        }
        else if (role == Microsoft.Extensions.AI.ChatRole.Assistant
            && !string.IsNullOrEmpty(message.ToolCallId)
            && !string.IsNullOrEmpty(message.ToolName))
        {
            IDictionary<string, object?>? arguments = ParseArguments(
                message.Metadata?.GetValueOrDefault("ToolArguments"));
            chatMessage.Contents.Add(new FunctionCallContent(
                message.ToolCallId,
                message.ToolName,
                arguments));
        }
        else if (role == Microsoft.Extensions.AI.ChatRole.Assistant
            && string.IsNullOrWhiteSpace(content))
        {
            // 中止/失败时可能存储正文为空、仅含 reasoning 元数据的 assistant 消息；
            // 空正文的 assistant 消息会让部分模型拒绝续接请求，加载历史时跳过。
            return null;
        }

        return chatMessage;
    }

    internal static IEnumerable<ConversationMessage> ToStored(
        IEnumerable<ChatMessage> messages,
        ref int nextSequence)
    {
        var result = new List<ConversationMessage>();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ChatMessage message in messages)
        {
            string? role = message.Role == Microsoft.Extensions.AI.ChatRole.User
                ? "user"
                : message.Role == Microsoft.Extensions.AI.ChatRole.Assistant
                    ? "assistant"
                    : message.Role == Microsoft.Extensions.AI.ChatRole.Tool
                        ? "tool"
                        : null;
            if (role == null)
            {
                continue;
            }

            List<FunctionCallContent> calls = message.Contents
                .OfType<FunctionCallContent>()
                .ToList();
            List<FunctionResultContent> functionResults = message.Contents
                .OfType<FunctionResultContent>()
                .ToList();
            string text = message.Text ?? string.Empty;
            string reasoning = string.Concat(message.Contents
                .OfType<TextReasoningContent>()
                .Select(content => content.Text));

            if (!string.IsNullOrEmpty(text)
                || !string.IsNullOrEmpty(reasoning)
                || (calls.Count == 0 && functionResults.Count == 0))
            {
                FunctionCallContent? firstCall = calls.FirstOrDefault();
                if (firstCall != null && !string.IsNullOrWhiteSpace(firstCall.CallId))
                {
                    toolNames[firstCall.CallId] = firstCall.Name;
                }
                result.Add(CreateStored(
                    nextSequence++,
                    role,
                    text,
                    firstCall?.CallId,
                    firstCall?.Name,
                    CreateMessageMetadata(firstCall, reasoning)));
            }

            // 若 text 或 reasoning 非空，第一个 tool_call 已随上面的消息一并存储，需跳过；
            // 仅当两者都为空（纯 tool_call 消息）时才全部展开，避免重复存储同一调用。
            foreach (FunctionCallContent call in calls.Skip(
                string.IsNullOrEmpty(text) && string.IsNullOrEmpty(reasoning) ? 0 : 1))
            {
                if (!string.IsNullOrWhiteSpace(call.CallId))
                {
                    toolNames[call.CallId] = call.Name;
                }
                result.Add(CreateStored(
                    nextSequence++,
                    "assistant",
                    string.Empty,
                    call.CallId,
                    call.Name,
                    CreateToolMetadata(call)));
            }

            foreach (FunctionResultContent functionResult in functionResults)
            {
                string? toolName = string.IsNullOrWhiteSpace(functionResult.CallId)
                    ? null
                    : toolNames.GetValueOrDefault(functionResult.CallId);
                result.Add(CreateStored(
                    nextSequence++,
                    "tool",
                    functionResult.Result?.ToString() ?? string.Empty,
                    functionResult.CallId,
                    toolName,
                    metadata: null));
            }
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, string>? BuildFileMetadata(
        IReadOnlyList<FileAsset> files)
    {
        if (files.Count == 0)
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            ["Files"] = JsonSerializer.Serialize(files.Select(file => new
            {
                fileId = file.FileId,
                fileName = file.FileName,
                mediaType = file.MediaType,
                length = file.Length
            }))
        };
    }

    internal static ConversationMessage AssociateFiles(
        ConversationMessage message,
        IReadOnlyList<FileAsset> files)
    {
        if (files.Count == 0)
        {
            return message;
        }

        Dictionary<string, string> metadata = message.Metadata == null
            ? []
            : new Dictionary<string, string>(message.Metadata, StringComparer.Ordinal);
        metadata["Files"] = BuildFileMetadata(files)!["Files"];
        return new ConversationMessage
        {
            MessageId = message.MessageId,
            Sequence = message.Sequence,
            Role = message.Role,
            Content = message.Content,
            ToolCallId = message.ToolCallId,
            ToolName = message.ToolName,
            IdempotencyKey = message.IdempotencyKey,
            Timestamp = message.Timestamp,
            Metadata = metadata,
            FileIds = message.FileIds
                .Concat(files.Select(file => file.FileId))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static ConversationMessage CreateStored(
        int sequence,
        string role,
        string content,
        string? toolCallId,
        string? toolName,
        IReadOnlyDictionary<string, string>? metadata) =>
        new()
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Sequence = sequence,
            Role = role,
            Content = content,
            ToolCallId = toolCallId,
            ToolName = toolName,
            Timestamp = DateTimeOffset.UtcNow,
            Metadata = metadata
        };

    private static IReadOnlyDictionary<string, string>? CreateToolMetadata(
        FunctionCallContent? call) =>
        call == null
            ? null
            : new Dictionary<string, string>
            {
                ["ToolArguments"] = JsonSerializer.Serialize(
                    call.Arguments ?? new Dictionary<string, object?>())
            };

    private static IReadOnlyDictionary<string, string>? CreateMessageMetadata(
        FunctionCallContent? call,
        string reasoning)
    {
        IReadOnlyDictionary<string, string>? toolMetadata = CreateToolMetadata(call);
        if (string.IsNullOrEmpty(reasoning))
        {
            return toolMetadata;
        }

        Dictionary<string, string> metadata = toolMetadata == null
            ? []
            : new Dictionary<string, string>(toolMetadata, StringComparer.Ordinal);
        metadata["Reasoning"] = reasoning;
        return metadata;
    }

    private static IDictionary<string, object?>? ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddFiles(
        ChatMessage chatMessage,
        IReadOnlyList<FileAssetContent> files)
    {
        foreach (FileAssetContent file in files)
        {
            AttachFile(chatMessage, file);
        }
    }

    /// <summary>把一个文件资产以文本或二进制附件形式挂到某条 ChatMessage 上（供续接历史重建附件使用）。</summary>
    internal static void AttachFile(ChatMessage chatMessage, FileAssetContent file)
    {
        if (IsTextFile(file.Asset.MediaType))
        {
            chatMessage.Contents.Add(new TextContent(
                $"[File: {file.Asset.FileName}]\n{DecodeUtf8(file)}"));
        }
        else
        {
            chatMessage.Contents.Add(new DataContent(file.Data, file.Asset.MediaType)
            {
                Name = file.Asset.FileName
            });
        }
    }

    private static bool IsTextFile(string mediaType)
    {
        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeUtf8(FileAssetContent file)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(file.Data);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"File '{file.Asset.FileName}' is not valid UTF-8 text.", exception);
        }
    }

}
