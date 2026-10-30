using System.Runtime.InteropServices;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>Open only regular files; nonblocking Unix open cannot hang on a FIFO swapped into the path.</summary>
internal static class WorkspaceFileHandle
{
    internal static FileStream Open(string path)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] information = new byte[52];
            if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, information)
                || BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(40)) != 1)
            {
                handle.Dispose();
                throw Rejected();
            }
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC, with the platform's native flag values.
            int flags = OperatingSystem.IsLinux() ? 0x800 | 0x20000 | 0x80000 : 0x4 | 0x100 | 0x1000000;
            int descriptor = OpenUnix(path, flags);
            if (descriptor < 0)
            {
                throw Rejected();
            }
            handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            if (FStat(handle, out FileStatus status) != 0 || (status.Mode & 0xF000) != 0x8000 || !HasSingleLink(handle))
            {
                handle.Dispose();
                throw Rejected();
            }
        }
        else
        {
            throw Rejected();
        }
        try
        {
            return new FileStream(handle, FileAccess.Read, 65536, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static AgentException Rejected() => new(AgentErrorCode.InvalidRequest, "Only readable regular workspace files can be transferred.");

    private static bool HasSingleLink(SafeFileHandle handle)
    {
        // Native stat layouts for supported 64-bit Linux/macOS targets. Fail closed on other ABIs.
        if (RuntimeInformation.ProcessArchitecture is not Architecture.X64 and not Architecture.Arm64)
        {
            return false;
        }
        byte[] status = new byte[512];
        if (FStatNative(handle, status) != 0)
        {
            return false;
        }
        return OperatingSystem.IsMacOS() ? BinaryPrimitives.ReadUInt16LittleEndian(status.AsSpan(6)) == 1
            : RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(16)) == 1
                : BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(20)) == 1;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatNative(SafeFileHandle handle, [Out] byte[] status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, [Out] byte[] information);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static extern int FStat(SafeFileHandle handle, out FileStatus status);

    // .NET 10 PAL ABI, shared by Linux and macOS. Keep in sync with the pinned runtime:
    // https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_io.h
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStatus
    {
        internal int Flags;
        internal int Mode;
        internal uint Uid;
        internal uint Gid;
        internal long Size;
        internal long ATime;
        internal long ATimeNsec;
        internal long MTime;
        internal long MTimeNsec;
        internal long CTime;
        internal long CTimeNsec;
        internal long BirthTime;
        internal long BirthTimeNsec;
        internal long Dev;
        internal long RDev;
        internal long Ino;
        internal uint UserFlags;
    }
}
