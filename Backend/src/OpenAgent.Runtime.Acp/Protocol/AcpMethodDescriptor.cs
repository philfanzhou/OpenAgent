namespace OpenAgent.Runtime.Acp.Protocol;

/// <summary>Model coverage only; a catalog entry does not provide an RPC implementation.</summary>
public sealed record AcpMethodDescriptor(string Name, string Direction, string Kind, string RequestSchema, string? ResponseSchema);
