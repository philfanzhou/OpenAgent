namespace OpenAgent.Contracts.Configuration;

public sealed class RuntimeBinding
{
    public string Adapter { get; set; } = "maf";
    public string? Harness { get; set; }
}
