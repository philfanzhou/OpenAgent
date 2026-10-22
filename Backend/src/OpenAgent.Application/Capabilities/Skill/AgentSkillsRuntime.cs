using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Application.Capabilities.Skill;

/// <summary>
/// 本轮启用的 skill 包目录与配套工具。包目录来自进程级内容缓存，由租约保护跨轮复用；
/// 一次性目录（未登记哈希的记录）随租约释放删除。运行时驱动把包目录链接进每轮工作区。
/// </summary>
internal sealed class AgentSkillsRuntime(
    IReadOnlyList<RuntimeSkill> skills,
    IReadOnlyList<AITool> tools,
    IReadOnlyList<SkillPackageCache.Lease>? leases) : IAsyncDisposable
{
    internal static AgentSkillsRuntime Empty { get; } = new([], [], []);

    internal IReadOnlyList<RuntimeSkill> Skills { get; } = skills;
    internal IReadOnlyList<AITool> Tools { get; } = tools;
    private IReadOnlyList<SkillPackageCache.Lease> Leases { get; } = leases ?? [];

    public ValueTask DisposeAsync()
    {
        foreach (SkillPackageCache.Lease lease in Leases)
        {
            try
            {
                lease.Dispose();
            }
            catch (Exception)
            {
                // 租约释放不得掩盖执行结果；缓存条目仍会按引用计数回收。
            }
        }
        return ValueTask.CompletedTask;
    }
}
