using System.Text.Json;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>Single-writer, crash-safe pointer to a committed native session on durable local storage.</summary>
internal sealed class OpenCodeSessionState : IAsyncDisposable
{
    private readonly string _parent;
    private readonly FileStream _lease;
    private string ManifestPath => Path.Combine(_parent, "committed-session.json");

    internal OpenCodeSessionState(string parent)
    {
        _parent = parent;
        string path = Path.Combine(parent, "session.lock");
        if (File.Exists(path)) OpenCodeWorkspace.RejectLink(new FileInfo(path));
        FileStreamOptions leaseOptions = new()
        {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows()) leaseOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _lease = new FileStream(path, leaseOptions);
    }

    internal async Task<Snapshot?> TakeAsync(string key, string? checkpoint, CancellationToken ct)
    {
        if (!File.Exists(ManifestPath)) return null;
        OpenCodeWorkspace.RejectLink(new FileInfo(ManifestPath));
        Snapshot? snapshot;
        try
        {
            await using FileStream stream = File.OpenRead(ManifestPath);
            snapshot = await JsonSerializer.DeserializeAsync<Snapshot>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            snapshot = null;
        }
        // Invalidate before native state is mutated. A crash or failed platform commit cannot reuse it.
        File.Delete(ManifestPath);
        if (snapshot is null || snapshot.Key != key || string.IsNullOrEmpty(checkpoint)
            || snapshot.Checkpoint != checkpoint || string.IsNullOrWhiteSpace(snapshot.SessionId)
            || string.IsNullOrEmpty(snapshot.Home) || snapshot.Home.Length != 34 || !snapshot.Home.StartsWith("p-", StringComparison.Ordinal)
            || !Guid.TryParseExact(snapshot.Home[2..], "N", out _)) return null;
        string home = Path.Combine(_parent, snapshot.Home);
        if (!Directory.Exists(home)) return null;
        OpenCodeWorkspace.CreatePrivateDirectory(home);
        return snapshot;
    }

    internal async Task CommitAsync(OpenCodeAgentProcess process, CancellationToken ct)
    {
        Snapshot snapshot = new(process.Key, Path.GetFileName(process.Home), process.SessionId!, process.HistoryCheckpoint!, process.ToolSchema!, [.. process.ReportedCompactions]);
        string temporary = Path.Combine(_parent, "checkpoint-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            FileStreamOptions writeOptions = new()
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) writeOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (FileStream stream = new(temporary, writeOptions))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, ManifestPath, overwrite: true);
            process.Persisted = true;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal void Invalidate() => File.Delete(ManifestPath);

    public ValueTask DisposeAsync() => _lease.DisposeAsync();
    internal sealed record Snapshot(string Key, string Home, string SessionId, string Checkpoint, string ToolSchema, string[]? ReportedCompactions = null);
}
