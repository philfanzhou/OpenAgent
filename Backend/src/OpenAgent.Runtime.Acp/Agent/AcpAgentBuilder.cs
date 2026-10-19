using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime.Acp.Agent;

/// <summary>
/// Registers the handlers and advertised capabilities of one agent host. Registration is
/// explicit and freezes when the builder is handed to <see cref="AcpAgentConnection"/> —
/// the read loop never sees late registrations (SPEC).
/// </summary>
public sealed class AcpAgentBuilder
{
    /// <summary>Wire values of the non-base prompt content branches; text and resource_link are always base.</summary>
    internal static readonly string[] KnownPromptCapabilities = ["image", "audio", "embeddedContext"];

    private Implementation? _info;
    private readonly SortedSet<string> _promptCapabilities = new(StringComparer.Ordinal);
    private Func<AcpNewSessionContext, CancellationToken, Task>? _newSession;
    private Func<AcpPromptContext, CancellationToken, Task<string>>? _prompt;
    private bool _frozen;

    internal Implementation? Info => _info;
    internal IReadOnlyCollection<string> PromptCapabilities => _promptCapabilities;
    internal Func<AcpNewSessionContext, CancellationToken, Task>? NewSessionHandler => _newSession;
    internal Func<AcpPromptContext, CancellationToken, Task<string>>? PromptHandler => _prompt;

    /// <summary>Identifies the agent in the initialize response. Omitted on the wire unless set.</summary>
    public AcpAgentBuilder WithInfo(string name, string version, string? title = null)
    {
        ThrowIfFrozen();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _info = title is null ? new Implementation(name, version) : new Implementation(name, version, title);
        return this;
    }

    /// <summary>
    /// Declares one non-base prompt capability the handlers actually accept
    /// (<c>image</c>, <c>audio</c>, or <c>embeddedContext</c>). Declaring a capability without
    /// handler support is rejected here — capability advertisement follows handlers (SPEC).
    /// </summary>
    public AcpAgentBuilder WithPromptCapability(string capability)
    {
        ThrowIfFrozen();
        if (!KnownPromptCapabilities.Contains(capability, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown prompt capability '{capability}'; expected one of {string.Join(", ", KnownPromptCapabilities)}.", nameof(capability));
        }

        _promptCapabilities.Add(capability);
        return this;
    }

    /// <summary>
    /// Validates each session/new before the host allocates state. The context exposes the
    /// allocated session id, so the handler may send updates before the response leaves.
    /// Omitting the handler accepts every session.
    /// </summary>
    public AcpAgentBuilder HandleNewSession(Func<AcpNewSessionContext, CancellationToken, Task> handler)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(handler);
        if (_newSession is not null) throw new InvalidOperationException("HandleNewSession was already registered.");
        _newSession = handler;
        return this;
    }

    /// <summary>Handles one prompt turn and returns its stop reason. Required: a host without a prompt handler cannot start.</summary>
    public AcpAgentBuilder HandlePrompt(Func<AcpPromptContext, CancellationToken, Task<string>> handler)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(handler);
        if (_prompt is not null) throw new InvalidOperationException("HandlePrompt was already registered.");
        _prompt = handler;
        return this;
    }

    internal void ThrowIfIncomplete()
    {
        if (_prompt is null) throw new InvalidOperationException("The agent host requires a prompt handler; register HandlePrompt before building.");
    }

    private void ThrowIfFrozen()
    {
        if (_frozen) throw new InvalidOperationException("The builder was already handed to an agent connection; handler registration is frozen.");
    }

    internal void Freeze() => _frozen = true;
}
