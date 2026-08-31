using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Clearspace.Native;

namespace Clearspace.Services;

/// <summary>
/// On-demand administrator access.
///
/// Clearspace deliberately runs as the invoking user. Marking the manifest
/// requireAdministrator would make every folder readable, and would also make the
/// app worse in ways that are hard to undo: User Interface Privilege Isolation
/// blocks drag and drop from a normal-rights Explorer window into an elevated
/// one, drive letters mapped by the user are invisible to the elevated token, and
/// every ordinary browsing session would then run with rights it does not need.
/// Explorer itself runs unelevated for exactly these reasons.
///
/// So instead of elevating the session, a second instance is launched elevated and
/// pointed straight at the folder that was refused. The two windows are separate
/// processes, which is precisely what keeps the normal one safe.
/// </summary>
public static class ElevationService
{
    private static bool? _isElevated;

    /// <summary>True when this process is already running with an elevated token.</summary>
    public static bool IsElevated => _isElevated ??= Evaluate();

    private static bool Evaluate()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts a second Clearspace with the elevation prompt and opens it on
    /// <paramref name="path"/>. Returns false when the user dismisses the prompt,
    /// which is a normal outcome and not an error worth a dialog.
    /// </summary>
    public static bool TryRelaunchAt(string path, out string? message)
    {
        message = null;

        var executable = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executable))
        {
            message = "Clearspace could not locate its own executable.";
            return false;
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = executable,
                // The path is the whole argument list, quoted so that folders with
                // spaces survive the round trip through the shell.
                Arguments = $"\"{path}\"",
                // runas is only honoured through the shell; a plain CreateProcess
                // has no way to raise the token.
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(info);
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == NativeMethods.ERROR_CANCELLED)
        {
            // The user chose No at the prompt.
            return false;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            return false;
        }
    }
}
