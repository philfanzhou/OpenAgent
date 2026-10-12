namespace OpenAgent.Contracts.Runtime;

public sealed record RuntimeDescriptor(string Adapter, string? Harness, string Name,
    bool RequiresLlmProfile = true, string EnvironmentMode = "isolated");
