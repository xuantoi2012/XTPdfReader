using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// Makes this app a candidate for opening .pdf and tells whether it is the current default. Windows 10/11 does not let a program
    /// choose itself as the default (the choice is protected by a hash), so the app registers itself as a candidate and opens the
    /// "Default apps" page of Windows Settings, where the user picks it with one click. The setup registers the app machine-wide; a copy
    /// run without setup registers itself for the current user the first time the button is used.
    /// </summary>
    internal static class DefaultPdfAppService
    {
        internal const string ProgId = "PDFReaderPro.Document";
        internal const string AppName = "PDF Reader Pro";
        private const string CapabilitiesPath = @"Software\XT\PDFReaderPro\Capabilities";
        private const string RegisteredAppsPath = @"Software\RegisteredApplications";

        private static string ExePath => Environment.ProcessPath ?? "";

        private static bool HasCapabilities(RegistryKey hive)
        {
            try
            {
                using var key = hive.OpenSubKey(CapabilitiesPath);
                return key?.GetValue("ApplicationName") as string == AppName;
            }
            catch { return false; }
        }

        internal static bool IsRegisteredForMachine => HasCapabilities(Registry.LocalMachine);

        /// <summary>The setup (machine-wide) or an earlier click of the button (this user) has made the app a candidate.</summary>
        internal static bool IsRegistered => IsRegisteredForMachine || HasCapabilities(Registry.CurrentUser);

        /// <summary>The ProgId Windows opens .pdf files with for this user, or null.</summary>
        internal static string? CurrentProgId()
        {
            try
            {
                using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice");
                if (choice?.GetValue("ProgId") as string is { Length: > 0 } progId) return progId;
                using var pdf = Registry.ClassesRoot.OpenSubKey(".pdf");
                return pdf?.GetValue("") as string;
            }
            catch { return null; }
        }

        internal static bool IsDefault => string.Equals(CurrentProgId(), ProgId, StringComparison.OrdinalIgnoreCase);

        /// <summary>A readable name of the app that currently opens .pdf, for the settings hint.</summary>
        internal static string CurrentDefaultName()
        {
            string? progId = CurrentProgId();
            if (string.IsNullOrEmpty(progId)) return "none";
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey(progId);
                if (key?.GetValue("FriendlyTypeName") as string is { Length: > 0 and < 80 } friendly && !friendly.StartsWith("@")) return friendly;
                if (key?.GetValue("") as string is { Length: > 0 and < 80 } text) return text;
            }
            catch { /* fall through */ }
            return progId;
        }

        /// <summary>Registers this copy for the current user when nothing registered it yet (a copy that was not installed by the setup).</summary>
        internal static void EnsureRegistered()
        {
            if (IsRegistered || string.IsNullOrEmpty(ExePath)) return;
            using (var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                progId.SetValue("", "PDF document");
                progId.SetValue("FriendlyTypeName", AppName);
                using var icon = progId.CreateSubKey("DefaultIcon");
                icon.SetValue("", ExePath + ",0");
                using var command = progId.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{ExePath}\" \"%1\"");
            }
            using (var openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
            {
                capabilities.SetValue("ApplicationName", AppName);
                capabilities.SetValue("ApplicationDescription", "Read, review and edit PDF drawing sets");
                using var associations = capabilities.CreateSubKey("FileAssociations");
                associations.SetValue(".pdf", ProgId);
            }
            using (var registered = Registry.CurrentUser.CreateSubKey(RegisteredAppsPath))
                registered.SetValue(AppName, CapabilitiesPath);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }

        /// <summary>Opens the Windows Settings page where this app can be chosen for .pdf. False when the page could not be opened.</summary>
        internal static bool OpenDefaultAppsSettings()
        {
            try
            {
                EnsureRegistered();
                string kind = IsRegisteredForMachine ? "registeredAppMachine" : "registeredAppUser";
                Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?{kind}={Uri.EscapeDataString(AppName)}") { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
