namespace OpenAgent.Core.Capabilities.Code;

internal sealed class CodeExecutionOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 180;
    public int MaxExecutionsPerRequest { get; set; } = 8;
}
