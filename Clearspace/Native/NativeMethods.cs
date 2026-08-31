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
    internal const uint SHGFI_SYSICONINDEX = 0x00004000;

    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    // ---------- Cloud placeholder attributes ----------

    /// <summary>
    /// Files On-Demand state lives in file attributes, not in a separate API, and
    /// none of these bits exist in System.IO.FileAttributes. They arrive free in
    /// the find data we already read, so sync status costs no extra disk access.
    ///
    /// PINNED and UNPINNED are also writable: setting them is exactly what the
    /// shell's "Always keep on this device" and "Free up space" commands do, and
    /// what attrib +P / attrib +U do from a console. The sync engine watches for
    /// the change and hydrates or dehydrates in the background.
    /// </summary>
    internal const uint FILE_ATTRIBUTE_PINNED = 0x00080000;
    internal const uint FILE_ATTRIBUTE_UNPINNED = 0x00100000;
    internal const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
    internal const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;
    internal const uint FILE_ATTRIBUTE_OFFLINE = 0x00001000;

    internal const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetFileAttributesW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

    // ---------- Win32 error codes worth naming ----------

    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_PATH_NOT_FOUND = 3;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_INVALID_NAME = 123;
    internal const int ERROR_NO_MORE_FILES = 18;
    internal const int ERROR_NOT_READY = 21;
    internal const int ERROR_BAD_NETPATH = 53;
    internal const int ERROR_CANCELLED = 1223;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    // Explorer's high-DPI icon source. SHGetFileInfo gives us a stable index in
    // this list; SHIL_JUMBO then gives a real 256px shell image instead of the
    // 16px list icon that becomes blurry when a tile enlarges it.
    internal const int SHIL_JUMBO = 0x4;

    [DllImport("shell32.dll", EntryPoint = "#727", PreserveSig = true)]
    internal static extern int SHGetImageList(
        int iImageList,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IImageList ppv);

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, uint crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, uint flags, out IntPtr picon);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // ---------- Shell file operations ----------

    /// <summary>
    /// No Pack setting: this must use the platform's natural alignment.
    ///
    /// The widely-copied Pack = 1 version of this declaration is an x86 artifact.
    /// On x64 it packs the struct to 50 bytes where shell32 expects 56, so pFrom
    /// and every field after it land at the wrong offsets and the shell reads
    /// garbage pointers. That is an access violation, not a managed exception, so
    /// it takes the process down instead of surfacing in the error dialog.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

    // ---------- Known folders ----------

    /// <summary>
    /// Resolves a known folder to its actual location. Environment.SpecialFolder
    /// has no Downloads entry and does not honour relocation, so this is the only
    /// correct way to find where these folders really live.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr pv);

    // ---------- Thumbnails ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;

        public SIZE(int width, int height)
        {
            cx = width;
            cy = height;
        }
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    internal const int SIIGBF_RESIZETOFIT = 0x00;
    internal const int SIIGBF_BIGGERSIZEOK = 0x01;
    internal const int SIIGBF_ICONONLY = 0x04;
    internal const int SIIGBF_THUMBNAILONLY = 0x08;
    internal const int SIIGBF_INCACHEONLY = 0x10;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    internal static Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    // ---------- Property store (media metadata) ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(string formatId, uint propertyId)
        {
            fmtid = new Guid(formatId);
            pid = propertyId;
        }
    }

    /// <summary>
    /// 24 bytes on x64: a 8-byte header then a 16-byte union. The union is never
    /// read directly here; the propsys helpers below do the type coercion, which
    /// avoids hand-decoding every variant type audio tags can produce.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr value1;
        public IntPtr value2;
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    internal static Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    internal const int GPS_DEFAULT = 0;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHGetPropertyStoreFromParsingName(
        string pszPath,
        IntPtr pbc,
        int flags,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    // Coercion helpers. PropVariantToStringAlloc also flattens multi-value tags
    // (a track with three artists) into one delimited string.
    [DllImport("propsys.dll", PreserveSig = true)]
    internal static extern int PropVariantToStringAlloc(ref PROPVARIANT pv, out IntPtr ppsz);

    [DllImport("propsys.dll", PreserveSig = true)]
    internal static extern int PropVariantToUInt32(ref PROPVARIANT pv, out uint pui);

    [DllImport("propsys.dll", PreserveSig = true)]
    internal static extern int PropVariantToUInt64(ref PROPVARIANT pv, out ulong pull);

    [DllImport("ole32.dll", PreserveSig = true)]
    internal static extern int PropVariantClear(ref PROPVARIANT pv);

    // ---------- Storage device identification ----------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;

    /// <summary>
    /// Query only, so the volume handle is opened with no access rights at all.
    /// That is what lets this run without administrator: asking a device to
    /// describe itself needs no permission, whereas reading its contents does.
    /// </summary>
    internal const uint NO_ACCESS = 0;

    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    internal const int StorageDeviceProperty = 0;
    internal const int StorageDeviceSeekPenaltyProperty = 7;
    internal const int PropertyStandardQuery = 0;

    /// <summary>STORAGE_BUS_TYPE. Only the ones worth distinguishing are named.</summary>
    internal const int BusTypeNvme = 0x11;
    internal const int BusTypeSata = 0x0B;
    internal const int BusTypeSd = 0x0D;
    internal const int BusTypeMmc = 0x0E;
    internal const int BusTypeUsb = 0x07;

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    // ---------- Window chrome ----------

    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    /// <summary>Windows 11 rounded window corners. Ignored on Windows 10.</summary>
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
