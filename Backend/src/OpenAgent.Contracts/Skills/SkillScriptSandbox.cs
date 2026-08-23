namespace OpenAgent.Contracts.Skills;

public interface ISkillScriptSandbox
{
    SkillScriptSandboxStatus Status { get; }

    Task<SkillScriptExecutionResult> ExecuteAsync(
        SkillScriptExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class SkillScriptSandboxStatus
{
    public bool Enabled { get; init; }
    public string Isolation { get; init; } = "disabled";
    public IReadOnlyList<string> SupportedExtensions { get; init; } = [];
    public int TimeoutSeconds { get; init; }
    public int MaxScriptBytes { get; init; }
    public int MaxOutputBytes { get; init; }
}

public sealed class SkillScriptExecutionRequest
{
    public string SkillName { get; init; } = string.Empty;
    public string ScriptName { get; init; } = string.Empty;
    public byte[] Script { get; init; } = [];
    public IReadOnlyList<string> Arguments { get; init; } = [];
}

public sealed class SkillScriptExecutionResult
{
    public bool Success { get; init; }
    public int? ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
    public bool OutputTruncated { get; init; }
    public long DurationMs { get; init; }
}

public sealed class SkillScriptExecutionPolicy
{
    public bool Enabled { get; init; }
}
