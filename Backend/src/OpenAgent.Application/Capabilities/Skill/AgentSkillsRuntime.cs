using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Application.Capabilities.Skill;

/// <summary>
/// 本轮启用的 skill 包目录与配套工具。包目录来自进程级内容缓存（键含包哈希，
/// 跨轮复用，不由本轮释放）；未登记哈希的记录使用一次性目录，仍随轮次删除。
/// 运行时驱动把包目录链接进每轮工作区。
/// </summary>
internal sealed class AgentSkillsRuntime(
    IReadOnlyList<RuntimeSkill> skills,
    IReadOnlyList<AITool> tools,
    IReadOnlyList<string>? ephemeralRoots) : IAsyncDisposable
{
    internal static AgentSkillsRuntime Empty { get; } = new([], [], []);

    internal IReadOnlyList<RuntimeSkill> Skills { get; } = skills;
    internal IReadOnlyList<AITool> Tools { get; } = tools;
    private IReadOnlyList<string> EphemeralRoots { get; } = ephemeralRoots ?? [];

    public ValueTask DisposeAsync()
    {
        foreach (string root in EphemeralRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // 一次性包目录清理不得掩盖执行结果；缓存目录不在此释放。
            }
        }
        return ValueTask.CompletedTask;
    }
}
