namespace OpenAgent.Core.Execution;

internal sealed class AcpOptions
{
    public bool LocalTrusted { get; set; }
    public bool Enabled { get; set; }
    public string WorkspaceRoot { get; set; } = Path.Combine(Path.GetTempPath(), "openagent-acp");
    public int TurnTimeoutSeconds { get; set; } = 600;
    public int RequestTimeoutSeconds { get; set; } = 60;
    public List<AcpHarness> Harnesses { get; set; } = [];
}
