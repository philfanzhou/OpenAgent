using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Files;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Conversation;

/// <summary>Rebuilds attachment manifests and bounded inline images for model input.</summary>
internal sealed class HistoryAttachments(
    PlatformChatHistoryContext context,
    IFileAssetService fileService,
    IInlineImageOptimizer imageOptimizer,
    FileAssetOptions options)
{
    private readonly ConversationContext _conversation = context.Conversation;
    private readonly IReadOnlyList<FileAsset> _files = context.Files;
    private readonly string _input = context.Input;
    private readonly bool _supportsMultimodal = context.SupportsMultimodal;
    private readonly IFileAssetService _fileService = fileService;
    private readonly IInlineImageOptimizer _imageOptimizer = imageOptimizer;
    private readonly long _maxInlineImageBytes = options.MaxInlineImageBytes;
    private readonly int _maxInlineImageCount = options.MaxInlineImageCount;
    private readonly int _inlineImageHistoryTurns = options.InlineImageHistoryTurns;

    internal async Task<ChatMessage> CreateUserMessageAsync(CancellationToken cancellationToken)
    {
        List<FileAssetContent>? inlineImages = _files.Count == 0 || !_supportsMultimodal
            ? null
            : await ReadInlineImagesAsync(cancellationToken).ConfigureAwait(false);
        return AgentMessageAdapter.CreateUser(_input, _files, inlineImages);
    }

    private async Task<List<FileAssetContent>?> ReadInlineImagesAsync(CancellationToken cancellationToken)
    {
        FileAssetScope scope = CreateFileScope();
        List<FileAssetContent>? inline = null;
        int inlineImageCount = 0;
        foreach (FileAsset file in _files)
        {
            if (inlineImageCount >= _maxInlineImageCount
                || !IsImage(file.MediaType)
                || file.Length > _maxInlineImageBytes)
            {
                continue;
            }

            try
            {
                inline ??= [];
                inline.Add(await ReadInlineContentAsync(
                    file.FileId,
                    scope,
                    cancellationToken).ConfigureAwait(false));
                inlineImageCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 内联读取失败时保留元数据描述符，模型仍可通过工具读取该文件。
            }
        }

        return inline;
    }

    /// <summary>
    /// Converts stored messages to model input and rebuilds referenced attachments.
    /// Historical images are only inlined within the recent-turn window.
    /// </summary>
    internal async Task<List<ChatMessage>> BuildHistoryAsync(
        IReadOnlyList<ConversationMessage> stored,
        CancellationToken cancellationToken)
    {
        var history = new List<ChatMessage>(stored.Count);
        bool[] inlineWindow = ComputeInlineImageWindow(stored, _inlineImageHistoryTurns);
        for (int index = 0; index < stored.Count; index++)
        {
            ConversationMessage message = stored[index];
            ChatMessage? chatMessage = AgentMessageAdapter.FromStored(message);
            if (chatMessage == null)
            {
                continue;
            }
            if (message.FileIds.Count > 0)
            {
                await AttachFilesAsync(
                    chatMessage,
                    message.FileIds,
                    inlineWindow[index],
                    cancellationToken).ConfigureAwait(false);
            }
            history.Add(chatMessage);
        }
        return history;
    }

    /// <summary>
    /// 历史图片内联窗口：只有最近 N 个用户轮次（N=InlineImageHistoryTurns）重放内联图片，
    /// 更早轮次只保留文件描述符。内联图片每张约上千 token，全量重放会让长会话的
    /// 输入 token 随轮次线性膨胀；当轮用户消息不受此窗口限制。
    /// </summary>
    private static bool[] ComputeInlineImageWindow(
        IReadOnlyList<ConversationMessage> stored,
        int turns)
    {
        var allow = new bool[stored.Count];
        if (turns <= 0)
        {
            return allow;
        }

        // 自后向前统计该消息之后的 user 轮数：最后一条 user 行及其后的消息属于最近轮次。
        int userTurnsAfter = 0;
        for (int index = stored.Count - 1; index >= 0; index--)
        {
            allow[index] = userTurnsAfter < turns;
            if (string.Equals(stored[index].Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                userTurnsAfter++;
            }
        }

        return allow;
    }

    private async Task AttachFilesAsync(
        ChatMessage chatMessage,
        IReadOnlyList<string> fileIds,
        bool allowInlineImages,
        CancellationToken cancellationToken)
    {
        FileAssetScope scope = CreateFileScope();
        int inlineImageCount = 0;
        foreach (string fileId in fileIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                FileAsset? asset = await _fileService.GetReferencedAsync(
                    fileId, scope, cancellationToken).ConfigureAwait(false);
                if (asset != null)
                {
                    FileAssetContent? inlineImage = null;
                    if (allowInlineImages
                        && _supportsMultimodal
                        && inlineImageCount < _maxInlineImageCount
                        && IsImage(asset.MediaType)
                        && asset.Length <= _maxInlineImageBytes)
                    {
                        try
                        {
                            inlineImage = await ReadInlineContentAsync(
                                fileId,
                                scope,
                                cancellationToken).ConfigureAwait(false);
                            inlineImageCount++;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            // Keep the metadata manifest when an optional inline read fails.
                        }
                    }

                    AgentMessageAdapter.AttachFile(chatMessage, asset, inlineImage);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Ignore deleted or unauthorized historical files so continuation can proceed.
            }
        }
    }

    private FileAssetScope CreateFileScope() => new()
    {
        TenantId = _conversation.TenantId ?? string.Empty,
        UserId = _conversation.UserId ?? string.Empty,
        ConversationId = _conversation.ConversationId
    };

    /// <summary>读取内联图片并降采样：两条路径（当轮/历史重放）共用同一优化缓存。</summary>
    private async Task<FileAssetContent> ReadInlineContentAsync(
        string fileId,
        FileAssetScope scope,
        CancellationToken cancellationToken)
    {
        FileAssetContent content = await _fileService.ReadAsync(
            fileId,
            scope,
            cancellationToken,
            _maxInlineImageBytes).ConfigureAwait(false);
        return new FileAssetContent
        {
            Asset = content.Asset,
            Data = _imageOptimizer.Optimize(content.Asset, content.Data)
        };
    }

    private static bool IsImage(string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

}
