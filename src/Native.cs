using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskScope;

static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFilePointerEx(SafeFileHandle handle, long offset, out long position, uint origin);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, int size, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int type, byte[] info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, byte[] info, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    public struct FileInfoNative
    {
        public uint Attributes;
        public uint CreateLow, CreateHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Serial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        public ulong Identity => ((ulong)IndexHigh << 32) | IndexLow;
        public long Size => ((long)SizeHigh << 32) | SizeLow;
        public long Write => ((long)WriteHigh << 32) | WriteLow;
    }
    public static SafeFileHandle Open(string path, uint access = 0, uint share = 7)
    {
        var h = CreateFileW(path, access, share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); // OPEN_REPARSE_POINT | BACKUP_SEMANTICS
        if (h.IsInvalid) { h.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {path}"); }
        return h;
    }
    public static byte[] Ioctl(SafeFileHandle h, uint code, byte[]? input, int size)
    {
        byte[] output = new byte[size];
        if (!DeviceIoControl(h, code, input, input?.Length ?? 0, output, size, out int n, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"NTFS ioctl 0x{code:x} failed");
        return output[..n];
    }
    public static void ReadAt(SafeFileHandle h, long offset, byte[] buf, int size)
    {
        if (!SetFilePointerEx(h, offset, out _, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Seek failed at {offset}");
        if (!ReadFile(h, buf, size, out int n, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Raw volume read failed at {offset}, size {size}, Win32 {error}: {new Win32Exception(error).Message}");
        }
        if (n != size) throw new InvalidDataException($"Short raw volume read at {offset}: expected {size}, got {n}");
    }
    public static FileInfoNative Info(SafeFileHandle h)
    {
        if (!GetFileInformationByHandle(h, out var i)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return i;
    }
    public static long Allocation(SafeFileHandle h)
    {
        var b = new byte[24];
        if (!GetFileInformationByHandleEx(h, 1, b, b.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return BitConverter.ToInt64(b, 0);
    }
    public static string FinalPath(SafeFileHandle h)
    {
        var path = new StringBuilder(512);
        while (true)
        {
            uint n = GetFinalPathNameByHandleW(h, path, (uint)path.Capacity, 0); // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
            if (n == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (n < path.Capacity)
            {
                string result = path.ToString();
                return result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + result[8..] : result.StartsWith(@"\\?\") ? result[4..] : result;
            }
            path = new StringBuilder(checked((int)n));
        }
    }
    public static string LongPath(string p) => p.StartsWith(@"\\?\") ? p : @"\\?\" + p;
}
