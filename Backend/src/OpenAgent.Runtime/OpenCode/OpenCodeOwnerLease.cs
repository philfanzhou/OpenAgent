namespace OpenAgent.Runtime.OpenCode;

/// <summary>Coordinates owner creation and removal across Engine instances sharing a storage root.</summary>
internal static class OpenCodeOwnerLease
{
    internal const string FileName = "owner.use";

    internal static async Task<FileStream> AcquireAsync(string parent, CancellationToken ct)
    {
        // The gate lives outside the owner so deleting and recreating the owner cannot split the lock.
        // Only filesystem setup shares this gate. Each running or kept process pins its own owner.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using FileStream? gate = TryOpenGate(parent, exclusive: false);
            if (gate is not null)
            {
                OpenCodeWorkspace.CreatePrivateDirectory(parent);
                return Pin(parent);
            }
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    internal static FileStream Pin(string parent) => Open(Path.Combine(parent, FileName), exclusive: false);

    internal static FileStream? TryOpenGate(string parent, bool exclusive)
    {
        // At most 256 permanent files per root. Cleaning one bucket does not stall other buckets.
        string root = Path.GetDirectoryName(parent)!;
        string bucket = Path.GetFileName(parent)[..2];
        try
        {
            return Open(Path.Combine(root, $".openagent-sweep-{bucket}.lock"), exclusive);
        }
        catch (IOException error) when (IsLockContention(error))
        {
            return null;
        }
    }

    private static FileStream Open(string path, bool exclusive)
    {
        FileInfo file = new(path);
        if (file.Exists || file.LinkTarget is not null) OpenCodeWorkspace.RejectLink(file);
        FileStreamOptions options = new()
        {
            Mode = FileMode.OpenOrCreate,
            Access = exclusive ? FileAccess.ReadWrite : FileAccess.Read,
            Share = exclusive ? FileShare.None : FileShare.Read
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    // EAGAIN/EWOULDBLOCK on Linux/macOS and sharing/lock violations on Windows.
    private static bool IsLockContention(IOException error) => (error.HResult & 0xffff) is 11 or 35 or 32 or 33;
}
