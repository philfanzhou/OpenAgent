namespace OpenAgent.Router;

public class DummyQueryCache : IQueryCache
{
    public Task<string?> GetCachedResponseAsync(string query, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task SetCachedResponseAsync(string query, string response, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
