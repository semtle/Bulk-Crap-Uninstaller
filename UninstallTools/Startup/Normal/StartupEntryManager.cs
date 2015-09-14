using System;
using System.IO;
using System.Linq;
using System.Text;
using Klocman.Tools;
using Microsoft.Win32;

namespace UninstallTools.Startup.Normal
{
    public static class StartupEntryManager
    {
        private static IStartupDisable _disableFunctions;

        internal static IStartupDisable DisableFunctions
        {
            get
            {
                if (_disableFunctions == null)
                {
                    // 6.2 is windows 8 and 2012, they are using a new startup disable scheme
                    _disableFunctions = Environment.OSVersion.Version < new Version(6, 2, 0, 0)
                        ? new OldStartupDisable()
                        : (IStartupDisable) new NewStartupDisable();
                }
                return _disableFunctions;
            }
        }

        /// <summary>
        ///     Delete startup entry data from registry and file system.
        ///     Only needed items are removed, for example if entry is disabled the entry from "Run" key is
        ///     not removed if it exists, same for the "Startup" folder. To remove them change the Disabled
        ///     property and run this command again.
        /// </summary>
        /// <param name="startupEntry">Entry to delete</param>
        public static void Delete(StartupEntry startupEntry)
        {
            if (startupEntry.Disabled)
                DisableFunctions.Enable(startupEntry);

            if (startupEntry.IsRegKey)
                RegistryTools.RemoveRegistryValue(startupEntry.ParentLongName, startupEntry.EntryLongName);
            else
                File.Delete(startupEntry.FullLongName);
        }

        public static void MoveToRegistry(StartupEntry startupEntry)
        {
            if (startupEntry.IsRegKey)
                return;

            // Don't want to deal with the disable wizardry
            var wasDisabled = startupEntry.Disabled;
            if (wasDisabled)
                Enable(startupEntry);

            // Delete old entry
            startupEntry.Delete();

            // Plug in new data
            startupEntry.IsRegKey = true;

            var newPoint =
                StartupEntryFactory.RunLocations.First(x => x.IsRegKey && (x.AllUsers == startupEntry.AllUsers)
                                                            && !x.IsRunOnce && !x.IsWow);

            startupEntry.SetParentLongName(newPoint.Path);
            startupEntry.SetParentFancyName(newPoint.Name);

            // Recreate registry entry
            CreateRegValue(startupEntry);

            // Restore disable status
            if (wasDisabled)
                Disable(startupEntry);
        }

        /// <summary>
        ///     Disable startup entry to stop it from being processed at startup. It is stored in the backup store.
        /// </summary>
        /// <param name="startupEntry"></param>
        public static void Disable(StartupEntry startupEntry)
        {
            if (startupEntry.DisabledStore)
                return;

            DisableFunctions.Disable(startupEntry);
        }

        /// <summary>
        ///     Restore the entry from the backup store, so that it can be executed again.
        /// </summary>
        /// <param name="startupEntry"></param>
        public static void Enable(StartupEntry startupEntry)
        {
            if (!startupEntry.DisabledStore)
                return;

            DisableFunctions.Enable(startupEntry);
        }

        /// <summary>
        ///     Set if this startup entry should run for all users or only for the current user.
        /// </summary>
        public static void SetAllUsers(StartupEntry startupEntry, bool allUsers)
        {
            // Find the suitable replacement
            var target = StartupEntryFactory.RunLocations.First(x => (x.IsRegKey == startupEntry.IsRegKey)
                                                                     && (x.IsRunOnce == startupEntry.IsRunOnce) &&
                                                                     (x.AllUsers == allUsers) && !x.IsWow);

            // Don't want to deal with the disable wizardry
            var wasDisabled = startupEntry.Disabled;
            if (wasDisabled)
                Enable(startupEntry);

            // Remove old entry or move the link to the new directory.
            if (startupEntry.IsRegKey)
            {
                try
                {
                    // Can't do this with links as they would get deleted
                    startupEntry.Delete();
                }
                catch
                {
                    // Key doesn't exist
                }
            }
            else
            {
                if (File.Exists(startupEntry.FullLongName))
                {
                    var newPath = Path.Combine(target.Path, startupEntry.EntryLongName);
                    File.Delete(newPath);
                    File.Move(startupEntry.FullLongName, newPath);
                }
            }

            // Plug in new data
            startupEntry.SetParentLongName(target.Path);
            startupEntry.AllUsersStore = allUsers;

            // Update registry stuff
            if (startupEntry.IsRegKey)
                CreateRegValue(startupEntry);

            // Restore disable status
            if (wasDisabled)
                Disable(startupEntry);
        }

        /// <summary>
        ///     Check if the startup entry still exists in registry or on disk.
        ///     If the entry is disabled, but it exists in the backup store, this method will return true.
        /// </summary>
        public static bool StillExists(StartupEntry startupEntry)
        {
            try
            {
                if (startupEntry.Disabled)
                {
                    return DisableFunctions.StillExists(startupEntry);
                }

                if (startupEntry.IsRegKey)
                {
                    using (var key = RegistryTools.OpenRegistryKey(startupEntry.ParentLongName))
                        return !string.IsNullOrEmpty(key.GetValue(startupEntry.EntryLongName) as string);
                }

                return File.Exists(startupEntry.FullLongName);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        ///     Create a registry value for the specified entry. Works for drive links as well.
        /// </summary>
        /// <param name="startupEntry"></param>
        internal static void CreateRegValue(StartupEntry startupEntry)
        {
            if (string.IsNullOrEmpty(startupEntry.Command))
                return;

            using (var runKey = RegistryTools.OpenRegistryKey(startupEntry.ParentLongName, true))
            {
                runKey.SetValue(startupEntry.EntryLongName, startupEntry.Command, RegistryValueKind.String);
            }
        }

        /// <summary>
        ///     Crate backup of the entry in the specified directory. If backup file already exists, it is overwritten.
        /// </summary>
        public static void CreateBackup(StartupEntry startupEntry, string backupPath)
        {
            var newPath = Path.Combine(backupPath, "Startup - " + startupEntry.EntryLongName);
            if (startupEntry.IsRegKey)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Windows Registry Editor Version 5.00");
                sb.AppendLine();
                sb.AppendLine($@"[{startupEntry.ParentLongName}]");
                sb.AppendLine(
                    $"\"{startupEntry.EntryLongName}\"=\"{startupEntry.Command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
                File.WriteAllText(newPath + ".reg", sb.ToString());
            }
            else
            {
                if (!File.Exists(newPath))
                    File.Delete(newPath);

                if (startupEntry.Disabled)
                {
                    var disabledFile = DisableFunctions.GetDisabledEntryPath(startupEntry);
                    if (File.Exists(disabledFile))
                        File.Copy(disabledFile, newPath);
                }
                else
                {
                    if (File.Exists(startupEntry.FullLongName))
                        File.Copy(startupEntry.FullLongName, newPath);
                }
            }
        }
    }
}