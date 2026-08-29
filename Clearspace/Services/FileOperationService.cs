using System.Runtime.InteropServices;
using Clearspace.Native;

namespace Clearspace.Services;

/// <summary>
/// Routes destructive work through the shell so Clearspace inherits Explorer's
/// progress dialog, conflict prompts, undo stack, and Recycle Bin semantics
/// instead of reimplementing them badly.
/// </summary>
public static class FileOperationService
{
    /// <summary>Sends items to the Recycle Bin, or deletes permanently when asked.</summary>
    public static bool Delete(IReadOnlyList<string> paths, IntPtr owner, bool permanent = false)
    {
        if (paths.Count == 0)
            return false;

        ushort flags = permanent
            ? NativeMethods.FOF_WANTNUKEWARNING
            : (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_WANTNUKEWARNING);

        return Run(NativeMethods.FO_DELETE, paths, null, flags, owner);
    }

    public static bool Copy(IReadOnlyList<string> paths, string destinationFolder, IntPtr owner)
        => Run(NativeMethods.FO_COPY, paths, destinationFolder, NativeMethods.FOF_ALLOWUNDO, owner);

    public static bool Move(IReadOnlyList<string> paths, string destinationFolder, IntPtr owner)
        => Run(NativeMethods.FO_MOVE, paths, destinationFolder, NativeMethods.FOF_ALLOWUNDO, owner);

    public static bool Rename(string path, string newFullPath, IntPtr owner)
        => Run(NativeMethods.FO_RENAME, [path], newFullPath, NativeMethods.FOF_ALLOWUNDO, owner);

    private static bool Run(uint operation, IReadOnlyList<string> from, string? to, ushort flags, IntPtr owner)
    {
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = operation,
            pFrom = ToDoubleNullTerminated(from),
            pTo = to is null ? null : ToDoubleNullTerminated([to]),
            fFlags = flags,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        var result = NativeMethods.SHFileOperationW(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>
    /// SHFileOperation takes a list as one buffer of null-separated strings with an
    /// extra trailing null. The LPWStr marshaller copies the managed string by length,
    /// so embedded nulls survive the transition.
    /// </summary>
    private static string ToDoubleNullTerminated(IReadOnlyList<string> paths)
        => string.Join('\0', paths) + "\0\0";

    /// <summary>Opens the shell's Properties dialog for a single item.</summary>
    public static bool ShowProperties(string path, IntPtr owner)
    {
        var info = new NativeMethods.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFO>(),
            fMask = NativeMethods.SEE_MASK_INVOKEIDLIST,
            hwnd = owner,
            lpVerb = "properties",
            lpFile = path,
            nShow = NativeMethods.SW_SHOW
        };

        return NativeMethods.ShellExecuteExW(ref info);
    }
}
