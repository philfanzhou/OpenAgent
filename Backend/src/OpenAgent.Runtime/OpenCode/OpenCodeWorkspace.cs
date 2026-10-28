namespace OpenAgent.Runtime.OpenCode;

/// <summary>Private storage for a trusted service identity; does not isolate processes sharing that identity.</summary>
internal static class OpenCodeWorkspace
{
    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows ACLs must be provisioned for the service identity by the operator.
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        RejectLink(new DirectoryInfo(path));
        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            throw new IOException("OpenCode storage must be accessible only to the service identity (mode 0700).");
        }
    }

    internal static void RejectLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("OpenCode storage must not contain symbolic links or reparse points.");
        }
    }
}
