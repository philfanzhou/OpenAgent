namespace OpenAgent.Runtime.Acp.Agent;

/// <summary>
/// Custom error codes this SDK's agent host may answer with (PROTOCOL §5: 10001 SessionBusy,
/// 10002 ResourceLimit). They are SDK agent conventions; peers must not rely on arbitrary
/// agents using them.
/// </summary>
public static class AcpAgentErrorCode
{
    public const int SessionBusy = 10001;
    public const int ResourceLimit = 10002;

    /// <summary>Namespace key carried in the error data of SDK agent errors.</summary>
    internal const string DataNamespace = "openagent.runtime.acp";
}
