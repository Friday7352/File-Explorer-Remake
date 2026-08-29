using System.Runtime.InteropServices;

namespace Clearspace.Shell;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public NativeRect(int width, int height)
    {
        Left = 0;
        Top = 0;
        Right = width;
        Bottom = height;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct FolderSettings
{
    public uint ViewMode;
    public uint Flags;

    public FolderSettings(uint viewMode, uint flags)
    {
        ViewMode = viewMode;
        Flags = flags;
    }
}

[ComImport]
[Guid("dfd3b6b5-c10c-4be9-85f6-a66969f402f6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExplorerBrowser
{
    [PreserveSig] int Initialize(IntPtr hwndParent, ref NativeRect rect, IntPtr folderSettings);
    [PreserveSig] int Destroy();
    [PreserveSig] int SetRect(IntPtr deferredWindowPos, NativeRect rect);
    [PreserveSig] int SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string propertyBag);
    [PreserveSig] int SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string emptyText);
    [PreserveSig] int SetFolderSettings(ref FolderSettings folderSettings);
    [PreserveSig] int Advise(IntPtr events, out uint cookie);
    [PreserveSig] int Unadvise(uint cookie);
    [PreserveSig] int SetOptions(uint options);
    [PreserveSig] int GetOptions(out uint options);
    [PreserveSig] int BrowseToIDList(IntPtr pidl, uint flags);
    [PreserveSig] int BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object? unknown, uint flags);
    [PreserveSig] int FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object? unknown, uint flags);
    [PreserveSig] int RemoveAll();
    [PreserveSig] int GetCurrentView(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object view);
}

[ComImport]
[Guid("71F96385-DDD6-48d3-A0C1-AE06E8B055FB")]
internal class ExplorerBrowserClass
{
}

internal static class ShellNative
{
    internal const uint NavigateBack = 0x00004000;
    internal const uint NavigateForward = 0x00008000;
    internal const uint NavigateUp = 0x00002000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr pidl,
        uint attributes,
        out uint attributesOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    internal static extern int SHGetKnownFolderIDList(
        ref Guid knownFolderId,
        uint flags,
        IntPtr token,
        out IntPtr pidl);

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr memory);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SetWindowTheme(IntPtr windowHandle, string? subAppName, string? subIdList);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int value,
        int valueSize);

    internal delegate bool EnumChildProc(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll", PreserveSig = true)]
    internal static extern bool EnumChildWindows(IntPtr parentWindowHandle, EnumChildProc callback, IntPtr parameter);
}
