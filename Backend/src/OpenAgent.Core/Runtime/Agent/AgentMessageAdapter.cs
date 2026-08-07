using System.Text.Json;
using System.Text;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Content;
using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Core.Runtime.Agent;

internal static class AgentMessageAdapter
{
    internal static ChatMessage CreateUser(
        string input,
        IReadOnlyList<AgentAttachment> attachments)
    {
        var message = new ChatMessage(Microsoft.Extensions.AI.ChatRole.User, input);
        AddAttachments(message, attachments);
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

            if (!string.IsNullOrEmpty(text) || (calls.Count == 0 && functionResults.Count == 0))
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
                    CreateToolMetadata(firstCall)));
            }

            foreach (FunctionCallContent call in calls.Skip(
                string.IsNullOrEmpty(text) ? 0 : 1))
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

    internal static IReadOnlyDictionary<string, string>? BuildAttachmentMetadata(
        IReadOnlyList<AgentAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            ["Attachments"] = JsonSerializer.Serialize(attachments.Select(attachment => new
            {
                attachment.FileName,
                attachment.MediaType,
                attachment.Length
            }))
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

    private static void AddAttachments(
        ChatMessage chatMessage,
        IReadOnlyList<AgentAttachment> attachments)
    {
        foreach (AgentAttachment attachment in attachments)
        {
            if (IsTextAttachment(attachment.MediaType))
            {
                chatMessage.Contents.Add(new TextContent(
                    $"[File: {attachment.FileName}]\n{DecodeUtf8(attachment)}"));
            }
            else
            {
                chatMessage.Contents.Add(new DataContent(attachment.Data, attachment.MediaType)
                {
                    Name = attachment.FileName
                });
            }
        }
    }

    private static bool IsTextAttachment(string mediaType)
    {
        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeUtf8(AgentAttachment attachment)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(attachment.Data);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"Attachment '{attachment.FileName}' is not valid UTF-8 text.", exception);
        }
    }

}
