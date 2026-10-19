namespace OpenAgent.Runtime.Acp.Processes;

/// <summary>How a launched agent process receives the host environment (SPEC: EnvironmentMode).</summary>
public enum AcpEnvironmentMode
{
    /// <summary>The child inherits this process's environment unchanged. The default.</summary>
    Inherit,

    /// <summary>
    /// The child starts from a cleared environment and receives only the
    /// <see cref="AcpProcessOptions.AllowedEnvironment"/> entries. Multi-tenant hosts pick
    /// this mode and curate the entries (SPEC: controlled environment by explicit choice).
    /// </summary>
    AllowList
}
