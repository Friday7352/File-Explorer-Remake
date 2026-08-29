using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Clearspace.Native;

/// <summary>
/// Win32 entry points used by Clearspace. Everything here is deliberately narrow:
/// enumeration, natural sorting, icons, and shell file operations. Nothing else
/// should P/Invoke directly.
/// </summary>
internal static class NativeMethods
{
    internal const int MAX_PATH = 260;

    // ---------- Directory enumeration ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public readonly long ToLong() => ((long)dwHighDateTime << 32) | dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WIN32_FIND_DATA
    {
        public FileAttributes dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    internal enum FINDEX_INFO_LEVELS
    {
        FindExInfoStandard = 0,
        // Skips the 8.3 short name, which the shell never shows and which costs
        // an extra lookup per item on NTFS volumes that still generate them.
        FindExInfoBasic = 1
    }

    internal enum FINDEX_SEARCH_OPS
    {
        FindExSearchNameMatch = 0
    }

    /// <summary>Asks the filesystem for larger batches per transition into the kernel.</summary>
    internal const int FIND_FIRST_EX_LARGE_FETCH = 2;

    internal sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFindHandle() : base(true) { }

        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFindHandle FindFirstFileExW(
        string lpFileName,
        FINDEX_INFO_LEVELS fInfoLevelId,
        out WIN32_FIND_DATA lpFindFileData,
        FINDEX_SEARCH_OPS fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextFileW(SafeFindHandle hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);

    // ---------- Natural sorting ----------

    /// <summary>
    /// The comparison Explorer uses, so "file10" sorts after "file9".
    /// </summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int StrCmpLogicalW(string psz1, string psz2);

    // ---------- Icons and type names ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    internal const uint SHGFI_ICON = 0x00000100;
    internal const uint SHGFI_LARGEICON = 0x00000000;
    internal const uint SHGFI_SMALLICON = 0x00000001;
    internal const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;
    internal const uint SHGFI_TYPENAME = 0x00000400;

    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // ---------- Shell file operations ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    internal const uint FO_MOVE = 0x0001;
    internal const uint FO_COPY = 0x0002;
    internal const uint FO_DELETE = 0x0003;
    internal const uint FO_RENAME = 0x0004;

    internal const ushort FOF_NOCONFIRMATION = 0x0010;
    internal const ushort FOF_ALLOWUNDO = 0x0040;
    internal const ushort FOF_WANTNUKEWARNING = 0x4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    // ---------- Shell verbs (Properties, Open With, ...) ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    internal const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    internal const int SW_SHOW = 5;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO lpExecInfo);

    // ---------- Window chrome ----------

    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
