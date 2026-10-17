namespace OpenAgent.Runtime.Acp.Rpc;

/// <summary>A local connection quota rejected the operation before anything reached the wire.</summary>
public sealed class AcpQuotaExceededException : Exception
{
    public AcpQuotaExceededException(string limitName, long limit, long requested)
        : base($"The connection quota '{limitName}' ({limit}) is exhausted; requested {requested}.")
    {
        LimitName = limitName;
        Limit = limit;
    }

    public string LimitName { get; }

    public long Limit { get; }
}
