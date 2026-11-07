using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DreamBooks.Pdf;

/// <summary>
/// Whether two paths are the same file on disk, however they are spelled (a short 8.3 name, a drive letter or a UNC share,
/// a <c>\\?\</c> prefix, a junction, a hard link): the volume and the file index Windows reports for the open file. Two
/// paths that differ only as strings are caught here; a string comparison is not enough to keep an export off its source.
/// </summary>
public static partial class FileIdentity
{
    /// <summary>True when both files exist and are one and the same file. Never throws: a file that cannot be opened is "not the same".</summary>
    public static bool AreSame(string pathA, string pathB)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathA);
        ArgumentException.ThrowIfNullOrEmpty(pathB);
        try
        {
            if (!File.Exists(pathA) || !File.Exists(pathB))
            {
                return false;
            }

            using var a = File.OpenHandle(pathA, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var b = File.OpenHandle(pathB, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(a, out var infoA)
                && GetFileInformationByHandle(b, out var infoB)
                && infoA.VolumeSerialNumber == infoB.VolumeSerialNumber
                && infoA.FileIndexHigh == infoB.FileIndexHigh
                && infoA.FileIndexLow == infoB.FileIndexLow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
