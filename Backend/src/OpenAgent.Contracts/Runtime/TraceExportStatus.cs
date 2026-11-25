namespace OpenAgent.Contracts.Runtime;

/// <summary>Whether the host configured an OTLP trace endpoint at startup.</summary>
public sealed record TraceExportStatus(bool IsConfigured);
