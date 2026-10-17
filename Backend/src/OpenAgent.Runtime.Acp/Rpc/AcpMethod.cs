namespace OpenAgent.Runtime.Acp.Rpc;

/// <summary>A typed request method handle. Registration and calls are explicit; nothing is discovered by reflection.</summary>
public sealed record AcpMethod<TRequest, TResponse>(string Name)
{
    public override string ToString() => Name;
}

/// <summary>A typed notification method handle.</summary>
public sealed record AcpNotificationMethod<TParameters>(string Name)
{
    public override string ToString() => Name;
}
