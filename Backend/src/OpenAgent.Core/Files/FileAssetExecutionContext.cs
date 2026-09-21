using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Core.Files;

/// <summary>
/// Scoped 级轮次上下文的唯一入口：工具回调深处（能力源/技能脚本/平台历史）
/// 经它读取当前轮的身份坐标，无需逐层穿参。
/// </summary>
internal sealed class FileAssetExecutionContext
{
    private readonly List<FileAsset> _published = [];

    internal TurnContext? Turn { get; private set; }

    /// <summary>按需投影的文件资产作用域；本轮尚未 Set 过时为 null。</summary>
    internal FileAssetScope? Scope => Turn?.ToFileAssetScope();

    internal IReadOnlyList<FileAsset> Published => _published.AsReadOnly();

    /// <summary>
    /// 一个 DI scope 只承载一轮执行：重复 Set 等值幂等，异值说明同一 scope
    /// 被并发多轮覆盖，显式失败而不是静默串数据。
    /// </summary>
    internal void Set(TurnContext turn)
    {
        if (Turn != null && !Turn.Equals(turn))
        {
            throw new InvalidOperationException(
                "FileAssetExecutionContext was already set for a different turn.");
        }

        Turn = turn;
    }

    internal void RecordPublished(FileAsset asset)
    {
        if (_published.All(item => !string.Equals(item.FileId, asset.FileId, StringComparison.Ordinal)))
        {
            _published.Add(asset);
        }
    }
}
