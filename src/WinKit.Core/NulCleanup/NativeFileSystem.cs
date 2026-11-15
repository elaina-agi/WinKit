using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinKit.Core.NulCleanup;

// All child opens are relative to an already checked, pinned directory handle. No child
// operation asks Win32 to resolve an absolute path again, and no ordinary File.Delete is used.
internal static unsafe class NativeFileSystem
{
    internal const uint DirectoryAttribute = 0x10;
    internal const uint ReparseAttribute = 0x400;
    internal const uint ReadOnlyAttribute = 0x1;
    private const uint ReadAttributes = 0x80;
    private const uint Synchronize = 0x100000;
    private const uint DeleteAccess = 0x10000;
    private const uint ListDirectory = 1;
    private const uint ShareRead = 1;
    private const uint ShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint FileOpen = 1;
    private const uint DirectoryFile = 1;
    private const uint NonDirectoryFile = 0x40;
    private const uint SynchronousNonAlert = 0x20;
    private const uint NtOpenReparsePoint = 0x00200000;
    private const int BasicInfo = 0;
    private const int StandardInfo = 1;
    private const int DispositionInfo = 4;
    private const int BothDirectoryInfo = 10;
    private const int BothDirectoryRestartInfo = 11;
    private const int IdInfo = 18;

    internal sealed record Metadata(uint Attributes, bool IsDirectory, bool DeletePending,
        long Size, DateTime LastWriteTimeUtc, string Identity);
    internal sealed record Entry(string Name, uint Attributes);

    internal static SafeFileHandle OpenAnchor(string extendedPath)
    {
        var handle = CreateFileW(extendedPath, ListDirectory | ReadAttributes | Synchronize,
            ShareRead, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }

    internal static SafeFileHandle OpenChild(SafeFileHandle parent, string name, bool directory, bool deleting = false)
    {
        if (name.Length is 0 or > 255 || name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
            throw new SafetyException("不安全的子项名称，已拒绝解析。");

        fixed (char* namePointer = name)
        {
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)(name.Length * 2)),
                Buffer = (IntPtr)namePointer
            };
            var addedRef = false;
            try
            {
                parent.DangerousAddRef(ref addedRef);
                var attributes = new ObjectAttributes
                {
                    Length = (uint)sizeof(ObjectAttributes),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = (IntPtr)(&unicode),
                    Attributes = 0 // Respect the parent filesystem's case semantics; exact in case-sensitive directories.
                };
                var status = NtCreateFile(out var rawHandle,
                    ReadAttributes | Synchronize | (directory ? ListDirectory : 0) | (deleting ? DeleteAccess : 0),
                    ref attributes, out _, IntPtr.Zero, 0,
                    deleting ? 0u : directory ? ShareRead : ShareRead | ShareWrite,
                    FileOpen, SynchronousNonAlert | NtOpenReparsePoint | (directory ? DirectoryFile : NonDirectoryFile),
                    IntPtr.Zero, 0);
                if (status < 0)
                {
                    if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
                        new SafeFileHandle(rawHandle, true).Dispose();
                    throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));
                }
                return new SafeFileHandle(rawHandle, true);
            }
            finally
            {
                if (addedRef) parent.DangerousRelease();
            }
        }
    }

    internal static Metadata GetMetadata(SafeFileHandle handle)
    {
        FileBasicInformation basic;
        if (!GetFileInformationByHandleEx(handle, BasicInfo, &basic, (uint)sizeof(FileBasicInformation)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        FileStandardInformation standard;
        if (!GetFileInformationByHandleEx(handle, StandardInfo, &standard, (uint)sizeof(FileStandardInformation)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        FileIdInformation id;
        if (!GetFileInformationByHandleEx(handle, IdInfo, &id, (uint)sizeof(FileIdInformation)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new Metadata(basic.FileAttributes, standard.Directory != 0, standard.DeletePending != 0,
            standard.EndOfFile, DateTime.FromFileTimeUtc(basic.LastWriteTime),
            $"{id.VolumeSerialNumber:X16}:{id.IdPart1:X16}{id.IdPart2:X16}:{basic.CreationTime:X16}");
    }

    internal static void RequireDirectory(SafeFileHandle handle)
    {
        var metadata = GetMetadata(handle);
        if (!metadata.IsDirectory || (metadata.Attributes & ReparseAttribute) != 0 || metadata.DeletePending)
            throw new SafetyException("目录不是普通目录，或是重解析点/待删除目录，已拒绝跟随。");
    }

    internal static IEnumerable<Entry> Enumerate(SafeFileHandle directory)
    {
        // FILE_ID_BOTH_DIR_INFO: FileNameLength at 60, FileAttributes at 56, FileName at 104.
        // Separate managed buffer from the unsafe P/Invoke so iterator state contains no pointers.
        var buffer = new byte[65536];
        var first = true;
        while (true)
        {
            var error = ReadDirectoryBatch(directory, buffer, first);
            first = false;
            if (error == 18) yield break; // ERROR_NO_MORE_FILES
            if (error != 0) throw new Win32Exception(error);
            var offset = 0;
            while (true)
            {
                if (offset > buffer.Length - 104) throw new SafetyException("目录枚举数据无效。");
                var next = BitConverter.ToUInt32(buffer, offset);
                var attributes = BitConverter.ToUInt32(buffer, offset + 56);
                var length = BitConverter.ToUInt32(buffer, offset + 60);
                if ((length & 1) != 0 || length > buffer.Length - offset - 104)
                    throw new SafetyException("目录枚举名称长度无效。");
                var name = System.Text.Encoding.Unicode.GetString(buffer, offset + 104, (int)length);
                if (name is not "." and not "..") yield return new Entry(name, attributes);
                if (next == 0) break;
                if (next < 104 || next > buffer.Length - offset)
                    throw new SafetyException("目录枚举偏移无效。");
                offset += (int)next;
            }
        }
    }

    private static int ReadDirectoryBatch(SafeFileHandle directory, byte[] buffer, bool restart)
    {
        fixed (byte* pointer = buffer)
        {
            return GetFileInformationByHandleEx(directory,
                restart ? BothDirectoryRestartInfo : BothDirectoryInfo, pointer, (uint)buffer.Length)
                ? 0 : Marshal.GetLastWin32Error();
        }
    }

    internal static void MarkForDeletion(SafeFileHandle handle)
    {
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, DispositionInfo, &delete, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public nuint Information; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize, EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending, Directory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation { public ulong VolumeSerialNumber, IdPart1, IdPart2; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, void* information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, void* information, uint size);
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out IntPtr handle, uint desiredAccess, ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess,
        uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}

internal sealed class SafetyException(string message) : Exception(message);
