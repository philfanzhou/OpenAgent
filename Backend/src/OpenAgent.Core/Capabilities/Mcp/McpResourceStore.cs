using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Files;

namespace OpenAgent.Core.Capabilities.Mcp;

/// <summary>
/// MCP 二进制资源的落盘出口：字节经 <see cref="IFileAssetService"/> 写入对象存储
/// （生产为 S3）并登记会话引用，返回可供模型操作的文件资产。失败一律返回 null，
/// 由调用方回退为占位符渲染——存储不可用不应拖垮整个工具结果。
/// </summary>
internal interface IMcpResourceStore
{
    ValueTask<FileAsset?> TryStoreAsync(
        string fileName,
        string? mediaType,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken);
}

internal sealed class McpResourceStore(
    IFileAssetService files,
    FileAssetExecutionContext context) : IMcpResourceStore
{
    public async ValueTask<FileAsset?> TryStoreAsync(
        string fileName,
        string? mediaType,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        FileAssetScope? scope = context.Scope;
        if (scope == null || data.Length == 0)
        {
            return null;
        }

        try
        {
            await using var input = new MemoryStream(data.ToArray(), writable: false);
            FileAsset asset = await files.UploadAsync(
                new FileAssetCreateRequest
                {
                    FileName = fileName,
                    MediaType = mediaType,
                    Source = FileAssetSource.Agent
                },
                input,
                scope,
                cancellationToken).ConfigureAwait(false);
            // 与 write_file/execute_code 产物一致：登记会话引用后 read_file /
            // publish_files / create_file_transfer_url 才可用。
            await files.EnsureReferencesAsync(
                [asset.FileId],
                scope,
                cancellationToken).ConfigureAwait(false);
            return asset;
        }
        catch (Exception exception) when (
            exception is AgentException or InvalidOperationException or NotSupportedException)
        {
            // 存储层拒绝（白名单收窄、超限、功能停用等）：静默降级为占位符。
            return null;
        }
    }
}
