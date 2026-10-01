using System.Text;

namespace OpenAgent.Runner;

/// <summary>Connects to long workspace paths through a private, temporary directory alias.</summary>
internal sealed class UnixSocketPath(string path, string? temporaryDirectory = null) : IDisposable
{
    internal string Path { get; } = path;

    internal static UnixSocketPath Create(string socketPath)
    {
        // sockaddr_un has a small byte limit (108 on Linux, 104 on macOS).
        if (Encoding.UTF8.GetByteCount(socketPath) <= 100)
        {
            return new UnixSocketPath(socketPath);
        }

        DirectoryInfo temporary = Directory.CreateTempSubdirectory("oar-");
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary.FullName,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            string alias = System.IO.Path.Combine(temporary.FullName, "c");
            Directory.CreateSymbolicLink(alias, System.IO.Path.GetDirectoryName(socketPath)!);
            return new UnixSocketPath(System.IO.Path.Combine(alias, System.IO.Path.GetFileName(socketPath)), temporary.FullName);
        }
        catch
        {
            temporary.Delete(recursive: true);
            throw;
        }
    }

    public void Dispose()
    {
        if (temporaryDirectory == null)
        {
            return;
        }
        try
        {
            // Unlink only the alias; never recursively remove the workspace target.
            Directory.Delete(System.IO.Path.Combine(temporaryDirectory, "c"));
            Directory.Delete(temporaryDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not replace a completed execution's result.
        }
    }
}
