namespace OpenAgent.Router;

internal interface IIdempotencyStore
{
    Task<IdempotencyAcquireResult> AcquireAsync(
        string key,
        string requestDigest,
        string ownerToken,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(
        string key,
        string requestDigest,
        string ownerToken,
        CachedResponse response,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(
        string key,
        string requestDigest,
        string ownerToken,
        CancellationToken cancellationToken = default);
}

internal sealed record IdempotencyAcquireResult(
    IdempotencyAcquireStatus Status,
    CachedResponse? Response = null);

internal enum IdempotencyAcquireStatus
{
    Acquired,
    InProgress,
    Completed,
    RequestMismatch
}
