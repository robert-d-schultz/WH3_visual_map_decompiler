using System.Runtime.InteropServices;

namespace TwMapDecompiler.Core.IO;

/// <summary>
/// NTFS hard links: a second name for the same file data, so a devastated project can list the
/// main project's terrain maps without a second copy of them. Both names must be on one volume.
/// Writing into either name changes both, but a program that saves by replacing the file (write
/// elsewhere, then rename over) quietly gives that name its own copy again.
/// </summary>
public static class HardLink
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [StructLayout(LayoutKind.Sequential, Pack = 4)] // FILETIMEs are two DWORDs, 4-aligned
    private struct FileInformation
    {
        public uint Attributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation information);

    /// <summary>How many names the file's data has; 1 when that cannot be asked (not Windows).</summary>
    public static int LinkCount(string path)
    {
        if (!OperatingSystem.IsWindows())
            return 1;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(handle, out var information) ? (int)information.NumberOfLinks : 1;
    }

    /// <summary>
    /// Makes <paramref name="link"/> another name for <paramref name="existing"/>, replacing whatever
    /// file had that name (removing a name never touches data another name still holds). Where no
    /// hard link can be made (another volume, a FAT drive) the file is copied instead.
    /// </summary>
    /// <returns>Null when linked, otherwise why it was copied.</returns>
    public static string? CreateOrCopy(string existing, string link)
    {
        if (string.Equals(Path.GetFullPath(existing), Path.GetFullPath(link), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Cannot link {existing} to itself.");
        if (File.Exists(link))
            File.Delete(link);

        if (OperatingSystem.IsWindows())
        {
            // The \\?\ form lifts the 260-character path limit, which the Win32 call still has.
            if (CreateHardLink(@"\\?\" + Path.GetFullPath(link), @"\\?\" + Path.GetFullPath(existing), IntPtr.Zero))
                return null;
            string reason = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
            File.Copy(existing, link);
            return reason;
        }
        File.Copy(existing, link);
        return "hard links are only made on Windows";
    }
}
