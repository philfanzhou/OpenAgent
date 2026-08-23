using OpenAgent.Contracts.Skills;

namespace OpenAgent.Core.Capabilities.Skill;

internal sealed class DisabledSkillScriptSandbox : ISkillScriptSandbox
{
    internal const string DisabledMessage =
        "Skill scripts are disabled until an isolated script sandbox is configured.";

    public SkillScriptSandboxStatus Status { get; } = new();

    public Task<SkillScriptExecutionResult> ExecuteAsync(
        SkillScriptExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SkillScriptExecutionResult>(new InvalidOperationException(DisabledMessage));
}
