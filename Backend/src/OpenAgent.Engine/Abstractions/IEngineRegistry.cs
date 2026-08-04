namespace OpenAgent.Engine.Abstractions;

public interface IEngineRegistry
{
    Task RegisterAsync(CancellationToken cancellationToken = default);
    Task HeartbeatAsync(CancellationToken cancellationToken = default);
    Task DeregisterAsync(CancellationToken cancellationToken = default);
    bool IsRegistered { get; }
}
