namespace OpenAgent.Contracts.Execution;

public sealed class CodeExecutionRequest
{
    public string Code { get; init; } = string.Empty;
    public List<ExecutionFile> Files { get; init; } = [];

    /// <summary>Runner language identifier; see <see cref="ExecutionLanguage"/>. Defaults to Python.</summary>
    public string Language { get; init; } = ExecutionLanguage.Python;

    /// <summary>
    /// Entry file the sandbox launches instead of the language default
    /// (<c>main.py</c>/<c>main.mjs</c>). Must carry the language's entry extension.
    /// </summary>
    public string? EntryFileName { get; init; }

    /// <summary>
    /// Conversation-scoped sandbox key. Executions that share a key reuse one
    /// persistent sandbox (files under /work, /tmp, /input and /output survive
    /// between calls until the sandbox is reclaimed after its idle period);
    /// restricted to letters, digits, '-' and '_'.
    /// </summary>
    public string? SessionKey { get; init; }
}
