using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Application.Capabilities.Skill;

/// <summary>
/// 一轮执行物化出的 skill 包目录与配套工具。包源仍在对象存储；临时目录
/// 仅在本轮存活，由运行时驱动链接进每轮工作区。
/// </summary>
internal sealed class AgentSkillsRuntime(
    IReadOnlyList<RuntimeSkill> skills,
    IReadOnlyList<AITool> tools,
    string? temporaryRoot) : IAsyncDisposable
{
    internal static AgentSkillsRuntime Empty { get; } = new([], [], null);

    internal IReadOnlyList<RuntimeSkill> Skills { get; } = skills;
    internal IReadOnlyList<AITool> Tools { get; } = tools;
    private string? TemporaryRoot { get; } = temporaryRoot;

    public ValueTask DisposeAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(TemporaryRoot) && Directory.Exists(TemporaryRoot))
            {
                Directory.Delete(TemporaryRoot, recursive: true);
            }
        }
        catch
        {
            // Temporary package cleanup must not hide the agent result.
        }
        return ValueTask.CompletedTask;
    }
}
