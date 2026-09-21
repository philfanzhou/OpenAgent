using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Files;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Conversation;

/// <summary>
/// 从存储历史与本轮文件构建模型输入：经 AgentMessageAdapter 转换、重放授权的
/// 历史附件、为多模态模型内联降采样图片；文件访问限定在本轮 scope 内。
/// </summary>
internal sealed class HistoryFileInflater(
    FileAssetScope scope,
    IFileAssetService fileService,
    IInlineImageOptimizer imageOptimizer,
    bool supportsMultimodal,
    long maxInlineImageBytes,
    int maxInlineImageCount)
{
    /// <summary>
    /// 文件描述符必须与内联图片同一次附加：先写"内容未包含"再补图片时，
    /// 模型会服从前一句指令而拒绝识别已注入的图片。
    /// </summary>
    internal async Task<ChatMessage> CreateUserMessageAsync(
        string input,
        IReadOnlyList<FileAsset> files,
        CancellationToken cancellationToken)
    {
        List<FileAssetContent>? inlineImages = files.Count == 0 || !supportsMultimodal
            ? null
            : await ReadInlineImagesAsync(files, cancellationToken).ConfigureAwait(false);
        return AgentMessageAdapter.CreateUser(input, files, inlineImages);
    }

    /// <summary>
    /// Converts stored messages to model input and rebuilds referenced attachments.
    /// </summary>
    internal async Task<List<ChatMessage>> BuildHistoryAsync(
        IReadOnlyList<ConversationMessage> stored,
        CancellationToken cancellationToken)
    {
        var history = new List<ChatMessage>(stored.Count);
        foreach (ConversationMessage message in stored)
        {
            ChatMessage? chatMessage = AgentMessageAdapter.FromStored(message);
            if (chatMessage == null)
            {
                continue;
            }
            if (message.FileIds.Count > 0)
            {
                await AttachFilesAsync(chatMessage, message.FileIds, cancellationToken).ConfigureAwait(false);
            }
            history.Add(chatMessage);
        }
        return history;
    }

    private async Task<List<FileAssetContent>?> ReadInlineImagesAsync(
        IReadOnlyList<FileAsset> files,
        CancellationToken cancellationToken)
    {
        List<FileAssetContent>? inline = null;
        int inlineImageCount = 0;
        foreach (FileAsset file in files)
        {
            if (inlineImageCount >= maxInlineImageCount
                || !IsImage(file.MediaType)
                || file.Length > maxInlineImageBytes)
            {
                continue;
            }

            try
            {
                inline ??= [];
                inline.Add(await ReadInlineContentAsync(
                    file.FileId,
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

    private async Task AttachFilesAsync(
        ChatMessage chatMessage,
        IReadOnlyList<string> fileIds,
        CancellationToken cancellationToken)
    {
        int inlineImageCount = 0;
        foreach (string fileId in fileIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                FileAsset? asset = await fileService.GetReferencedAsync(
                    fileId, scope, cancellationToken).ConfigureAwait(false);
                if (asset != null)
                {
                    FileAssetContent? inlineImage = null;
                    if (supportsMultimodal
                        && inlineImageCount < maxInlineImageCount
                        && IsImage(asset.MediaType)
                        && asset.Length <= maxInlineImageBytes)
                    {
                        try
                        {
                            inlineImage = await ReadInlineContentAsync(
                                fileId,
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

    /// <summary>读取内联图片并降采样：两条路径（当轮/历史重放）共用同一优化缓存。</summary>
    private async Task<FileAssetContent> ReadInlineContentAsync(
        string fileId,
        CancellationToken cancellationToken)
    {
        FileAssetContent content = await fileService.ReadAsync(
            fileId,
            scope,
            cancellationToken,
            maxInlineImageBytes).ConfigureAwait(false);
        return new FileAssetContent
        {
            Asset = content.Asset,
            Data = imageOptimizer.Optimize(content.Asset, content.Data)
        };
    }

    private static bool IsImage(string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
