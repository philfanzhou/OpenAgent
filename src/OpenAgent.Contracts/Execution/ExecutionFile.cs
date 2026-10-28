namespace OpenAgent.Contracts.Execution;

public sealed class ExecutionFile
{
    public string Name { get; init; } = string.Empty;
    public byte[] Content { get; init; } = [];
}
