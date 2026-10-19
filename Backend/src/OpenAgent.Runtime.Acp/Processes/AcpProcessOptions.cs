using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.Processes;

/// <summary>
/// How to launch one local agent process: an executable with an explicit argument list (never
/// a composed shell command), an optional working directory, an environment policy, a stderr
/// line callback, and the staged shutdown grace. AcpProcessLauncher validates the values.
/// </summary>
public sealed record AcpProcessOptions
{
    /// <summary>The executable to start. A bare name resolves through PATH; a path is used as given.</summary>
    public required string Executable { get; init; }

    /// <summary>Arguments passed verbatim, one per element; no quoting or shell interpretation.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The child's working directory, or null for this process's working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>The environment policy. <see cref="AcpEnvironmentMode.Inherit"/> is the default.</summary>
    public AcpEnvironmentMode EnvironmentMode { get; init; } = AcpEnvironmentMode.Inherit;

    /// <summary>
    /// Environment entries for the child. Under <see cref="AcpEnvironmentMode.Inherit"/> they
    /// are merged over the inherited environment, overriding same-named variables. Under
    /// <see cref="AcpEnvironmentMode.AllowList"/> they are the complete curated environment —
    /// nothing is inherited (SPEC: multi-tenant hosts choose a controlled environment).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>
    /// Invoked for every stderr line. The lease always drains stderr — with or without a
    /// callback, and regardless of callback speed — so a chatty child can never block on a
    /// full pipe. A throwing callback is contained and the drain continues.
    /// </summary>
    public Action<string>? OnStdErrorLine { get; init; }

    /// <summary>
    /// Stage 2 of the shutdown: how long the child may take to exit on stdin EOF before the
    /// lease kills the whole tree. Zero skips the graceful window. The default is 5 seconds
    /// (SPEC: ProcessShutdownGracePeriod).
    /// </summary>
    public TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Options for the framed transport built over the child's stdio.</summary>
    public AcpTransportOptions? Transport { get; init; }
}
