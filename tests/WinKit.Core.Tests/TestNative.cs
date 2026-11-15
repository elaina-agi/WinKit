using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinKit.Core.Tests;

// This class is test-only. Every destructive caller is constrained to Fixture's random,
// freshly created temporary tree; no existing user file is ever used as a fixture.
internal static class TestNative
{
    internal static string Extended(string path) => path.StartsWith(@"\\", StringComparison.Ordinal)
        ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    internal static void CreateFile(string path, string text = "test")
    {
        using var handle = CreateFileW(Extended(path), 0x40000000, 7, IntPtr.Zero, 1, 0x80, IntPtr.Zero);
        CheckHandle(handle);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!WriteFile(handle, bytes, (uint)bytes.Length, out var count, IntPtr.Zero) || count != bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static void RewriteInPlace(string path, string text)
    {
        using var handle = CreateFileW(Extended(path), 0x40000000, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        CheckHandle(handle);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!WriteFile(handle, bytes, (uint)bytes.Length, out var count, IntPtr.Zero) || count != bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static bool TryEnableCaseSensitivity(string path, out int error)
    {
        using var handle = CreateFileW(Extended(path), 0x100, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        CheckHandle(handle);
        uint flags = 1;
        var success = SetFileInformationByHandle(handle, 23, ref flags, 4);
        error = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }
    internal static bool TrySymbolicLink(string path, string target, out int error)
    {
        var success = CreateSymbolicLinkW(Extended(path), Extended(target), 2);
        error = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }
    internal static byte[] DenyDirectoryAccess(string path)
    {
        GetFileSecurityW(Extended(path), 4, null, 0, out var required);
        var original = new byte[required];
        if (!GetFileSecurityW(Extended(path), 4, original, required, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!SetFileSecurityW(Extended(path), 4 | 0x80000000u, descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { LocalFree(descriptor); }
        return original;
    }
    internal static void RestoreDirectoryAccess(string path, byte[] descriptor)
    {
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            if (!SetFileSecurityW(Extended(path), 4 | 0x20000000u, pinned.AddrOfPinnedObject()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { pinned.Free(); }
    }
    internal static SafeFileHandle HoldFile(string path, uint access = 0x80000000, uint share = 1)
    {
        var handle = CreateFileW(Extended(path), access, share, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        CheckHandle(handle);
        return handle;
    }
    internal static bool Exists(string path) => GetFileAttributesW(Extended(path)) != uint.MaxValue;
    internal static uint Attributes(string path) => GetFileAttributesW(Extended(path));
    internal static void SetReadOnly(string path, bool value)
    {
        var attributes = GetFileAttributesW(Extended(path));
        if (!SetFileAttributesW(Extended(path), value ? attributes | 1u : attributes & ~1u))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static void DeleteFile(string path)
    { if (!DeleteFileW(Extended(path))) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    internal static bool TryMove(string oldPath, string newPath) => MoveFileExW(Extended(oldPath), Extended(newPath), 0);
    internal static void Move(string oldPath, string newPath)
    { if (!TryMove(oldPath, newPath)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    internal static void CreateDirectory(string path)
    { if (!CreateDirectoryW(Extended(path), IntPtr.Zero) && Marshal.GetLastWin32Error() != 183) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    internal static void HardLink(string existing, string newPath)
    { if (!CreateHardLinkW(Extended(newPath), Extended(existing), IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    internal static void Junction(string path, string target)
    {
        CreateDirectory(path);
        using var handle = CreateFileW(Extended(path), 0x40000000, 0, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        CheckHandle(handle);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.Unicode, true))
        {
            writer.Write(0xA0000003u); // IO_REPARSE_TAG_MOUNT_POINT
            writer.Write(checked((ushort)(8 + substitute.Length + 2 + print.Length + 2)));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(checked((ushort)substitute.Length));
            writer.Write(checked((ushort)(substitute.Length + 2)));
            writer.Write(checked((ushort)print.Length));
            writer.Write(substitute); writer.Write((ushort)0);
            writer.Write(print); writer.Write((ushort)0);
        }
        var data = stream.ToArray();
        if (!DeviceIoControl(handle, 0x000900A4, data, (uint)data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void CleanTree(string path)
    {
        // Fixture's trusted boundary is checked by its caller. Do not recurse into reparse points.
        var attributes = GetFileAttributesW(Extended(path));
        if (attributes == uint.MaxValue) return;
        if ((attributes & 0x10) == 0)
        {
            if ((attributes & 1) != 0) SetReadOnly(path, false);
            DeleteFile(path);
            return;
        }
        if ((attributes & 0x400) == 0)
        {
            using var search = FindFirstFileW(Extended(path) + @"\*", out var data);
            if (!search.IsInvalid)
            {
                do
                {
                    if (data.FileName is not "." and not "..") CleanTree(path + @"\" + data.FileName);
                } while (FindNextFileW(search, out data));
            }
            else if (Marshal.GetLastWin32Error() != 2) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (!RemoveDirectoryW(Extended(path))) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void CheckHandle(SafeFileHandle handle)
    {
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
        }
    }
    private sealed class SafeFindHandle() : SafeHandle(IntPtr.Zero, true)
    {
        public override bool IsInvalid => handle == new IntPtr(-1) || handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => FindClose(handle);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref uint information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CreateSymbolicLinkW(string link, string target, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFileSecurityW(string path, uint information, byte[]? descriptor, uint length, out uint required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileSecurityW(string path, uint information, IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string descriptor, uint revision, out IntPtr nativeDescriptor, out uint size);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint length, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileAttributesW(string path, uint attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string existing, string replacement, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool RemoveDirectoryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newPath, string existingPath, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindHandle FindFirstFileW(string path, out FindData data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileW(SafeFindHandle search, out FindData data);
    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr handle);
}

internal sealed class Fixture : IDisposable
{
    private readonly string _token = "WinKit-core-tests-" + Guid.NewGuid().ToString("N");
    internal string Root { get; }
    internal Fixture()
    {
        Root = Path.Combine(Path.GetTempPath(), _token);
        if (TestNative.Exists(Root)) throw new InvalidOperationException("Fixture collision.");
        TestNative.CreateDirectory(Root);
    }
    internal string Directory(params string[] parts)
    {
        var path = Root;
        foreach (var part in parts) { path = Path.Combine(path, part); TestNative.CreateDirectory(path); }
        return path;
    }
    internal string File(string parent, string name = "nul", string content = "test")
    {
        RequireOwned(parent);
        var path = Path.Combine(parent, name); TestNative.CreateFile(path, content); return path;
    }
    private void RequireOwned(string path)
    {
        if (path != Root && !path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Fixture operation outside owned tree.");
    }
    public void Dispose()
    {
        if (Path.GetFileName(Root) != _token || Path.GetDirectoryName(Root) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidOperationException("Refusing cleanup: fixture boundary mismatch.");
        TestNative.CleanTree(Root);
    }
}
