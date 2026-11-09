using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VideoPlayer.Services;

/// <summary>Registers the player as a handler for media files in the current user's registry hive.</summary>
public static class FileAssociations
{
    private const string ProgId = "VideoPlayer.Media";
    private const string AppRegisteredName = "VideoPlayer";
    private const string CapabilitiesKey = @"Software\VideoPlayer\Capabilities";
    private const string UserChoiceRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    public static readonly string[] Extensions =
    [
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts",
        ".3gp", ".ogv", ".vob", ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma", ".opus", ".aiff",
    ];

    /// <summary>Writes the registration under HKCU. Idempotent, so it is safe to run on every start.</summary>
    public static void Register()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unknown.");

        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue("", "קובץ מדיה - נגן וידאו");
            using var icon = prog.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            using var command = prog.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }

        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            caps.SetValue("ApplicationName", "נגן וידאו");
            caps.SetValue("ApplicationDescription", "נגן וידאו מהיר עם כתוביות ורשימת השמעה");
            using var associations = caps.CreateSubKey("FileAssociations");
            foreach (var ext in Extensions)
            {
                associations.SetValue(ext, ProgId);
            }
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
        {
            registered.SetValue("VideoPlayer", CapabilitiesKey);
        }

        foreach (var ext in Extensions)
        {
            using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
    }

    /// <summary>
    /// Opens Windows' "Default apps" page for this player. Windows does not allow an app to set itself as default
    /// silently, so the user confirms the extensions there.
    /// </summary>
    public static void OpenDefaultAppsDialog()
    {
        try
        {
            var ui = (IApplicationAssociationRegistrationUI)new ApplicationAssociationRegistrationUI();
            ui.LaunchAdvancedAssociationUI(AppRegisteredName);
            Marshal.ReleaseComObject(ui);
        }
        catch (COMException)
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
    }

    /// <summary>True when Windows currently opens every supported extension with this player.</summary>
    public static bool IsDefault()
    {
        foreach (var ext in Extensions)
        {
            using var choice = Registry.CurrentUser.OpenSubKey($@"{UserChoiceRoot}\{ext}\UserChoice");
            if (choice?.GetValue("ProgId") as string != ProgId)
            {
                return false;
            }
        }

        return true;
    }

    [ComImport, Guid("1968106D-F3B5-44CF-890E-116FCB9ECEF1")]
    private class ApplicationAssociationRegistrationUI;

    [ComImport, Guid("1F76A169-F994-40AC-8FC8-0959E8874710"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationAssociationRegistrationUI
    {
        void LaunchAdvancedAssociationUI([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName);
    }
}
