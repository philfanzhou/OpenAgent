namespace OpenAgent.Contracts.Conversation;

/// <summary>
/// Opaque serialized MAF session state. The Contracts layer deliberately does not
/// reference Microsoft Agent Framework types.
/// </summary>
public sealed record AgentSessionSnapshot(
    string StateJson,
    string ConfigFingerprint,
    int FormatVersion = 1);
