namespace OpenAgent.Runtime.Acp.Processes;

/// <summary>The platform refused or failed to start the executable; no process was left behind.</summary>
public sealed class AcpProcessStartException : Exception
{
    public AcpProcessStartException(string executable, Exception innerException)
        : base($"The agent executable '{executable}' could not start: {innerException.Message}", innerException)
    {
        Executable = executable;
    }

    /// <summary>The executable the launcher tried to start.</summary>
    public string Executable { get; }
}
