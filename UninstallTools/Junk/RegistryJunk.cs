﻿/*
    Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/)
    Apache License Version 2.0
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using Klocman.Extensions;
using Klocman.Native;
using Klocman.Tools;
using Microsoft.Win32;
using UninstallTools.Uninstaller;

namespace UninstallTools.Junk
{
    public class RegistryJunk : JunkBase
    {
        private const string KeynameRegisteredApps = "RegisteredApplications";

        /// <summary>
        ///     Keys to step over when scanning
        /// </summary>
        private static readonly IEnumerable<string> KeyBlacklist = new[]
        {
            "Microsoft", "Wow6432Node", "Windows", "Classes", "Clients", KeynameRegisteredApps
        };

        /// <summary>
        ///     Always points to program's directory
        /// </summary>
        private static readonly IEnumerable<string> InstallDirKeyNames = new[]
        {
            "InstallDir",
            "Install_Dir",
            "Install Directory",
            "InstDir",
            "ApplicationPath",
            "Install folder",
            "Last Stable Install Path",
            "TARGETDIR",
            "JavaHome"
        };

        /// <summary>
        ///     Always points to program's main executable
        /// </summary>
        private static readonly IEnumerable<string> ExePathKeyNames = new[]
        {
            "exe64",
            "exe32",
            "Executable",
            "PathToExe",
            "ExePath"
        };

        /// <summary>
        ///     Can point to programs executable or directory
        /// </summary>
        private static readonly IEnumerable<string> ExeOrDirPathKeyNames = new[]
        {
            "Path",
            "Path64",
            "pth",
            "PlayerPath",
            "AppPath"
        };

        private static readonly string KeyCu = @"HKEY_CURRENT_USER\SOFTWARE";
        private static readonly string KeyCuWow = @"HKEY_CURRENT_USER\SOFTWARE\Wow6432Node";
        private static readonly string KeyLm = @"HKEY_LOCAL_MACHINE\SOFTWARE";
        private static readonly string KeyLmWow = @"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node";

        private static readonly string[] SoftwareRegKeys = ProcessTools.Is64BitProcess
            ? new[] {KeyLm, KeyCu, KeyLmWow, KeyCuWow}
            : new[] {KeyLm, KeyCu};

        private static readonly string[] ClsidKeys =
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\CLSID",
            @"HKEY_CURRENT_USER\SOFTWARE\Classes\CLSID",
            @"HKEY_CURRENT_USER\SOFTWARE\Classes\WOW6432Node\CLSID"
        };

        public RegistryJunk(ApplicationUninstallerEntry entry,
            IEnumerable<ApplicationUninstallerEntry> otherUninstallers)
            : base(entry, otherUninstallers)
        {
        }

        public override IEnumerable<JunkNode> FindJunk()
        {
            var returnList = new List<JunkNode>();

            returnList.AddRange(ScanForJunk());

            returnList.AddRange(ScanFirewallRules());

            returnList.AddRange(ScanTracing());

            returnList.AddRange(ScanClsid());

            if (Uninstaller.RegKeyStillExists())
            {
                var regKeyNode = new RegistryKeyJunkNode(PathTools.GetDirectory(Uninstaller.RegistryPath),
                    Uninstaller.RegistryKeyName, Uninstaller.DisplayName);
                regKeyNode.Confidence.Add(ConfidencePart.IsUninstallerRegistryKey);
                returnList.Add(regKeyNode);
            }

            return returnList;
        }

        private IEnumerable<JunkNode> FindJunkRecursively(RegistryKey softwareKey, int level = -1)
        {
            var returnList = new List<JunkNode>();

            try
            {
                // Don't try to scan root keys
                if (level > -1)
                {
                    var keyName = Path.GetFileName(softwareKey.Name);
                    var keyDir = Path.GetDirectoryName(softwareKey.Name);
                    var confidence = GenerateConfidence(keyName, keyDir, level).ToList();

                    // Check if application's location is explicitly mentioned in any of the values
                    if (GetValueNamesSafe(softwareKey).Any(valueName => TestValueForMatches(softwareKey, valueName)))
                        confidence.Add(ConfidencePart.ExplicitConnection);

                    if (confidence.Any())
                    {
                        // TODO Add extra confidence if the key is, or will be empty after junk removal
                        var newNode = new RegistryKeyJunkNode(keyDir, keyName, Uninstaller.DisplayName);
                        newNode.Confidence.AddRange(confidence);
                        returnList.Add(newNode);
                    }
                }

                // Limit recursion depth
                if (level <= 1)
                {
                    foreach (var subKeyName in softwareKey.GetSubKeyNames())
                    {
                        if (KeyBlacklist.Contains(subKeyName, StringComparison.InvariantCultureIgnoreCase))
                            continue;

                        using (var subKey = softwareKey.OpenSubKey(subKeyName, false))
                        {
                            if (subKey != null)
                                returnList.AddRange(FindJunkRecursively(subKey, level + 1));
                        }
                    }
                }
            }
                // Reg key invalid
            catch (ArgumentException)
            {
            }
            catch (SecurityException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            return returnList;
        }

        private bool TestValueForMatches(RegistryKey softwareKey, string valueName)
        {
            bool hit;
            if (InstallDirKeyNames.Contains(valueName, StringComparison.InvariantCultureIgnoreCase))
            {
                hit = TestPathsMatch(Uninstaller.InstallLocation, softwareKey.GetValue(valueName) as string);
            }
            else if (ExePathKeyNames.Contains(valueName, StringComparison.InvariantCultureIgnoreCase))
            {
                hit = TestPathsMatchExe(softwareKey.GetValue(valueName) as string);
            }
            else if (ExeOrDirPathKeyNames.Contains(valueName, StringComparison.InvariantCultureIgnoreCase))
            {
                var path = softwareKey.GetValue(valueName) as string;
                hit = File.Exists(path)
                    ? TestPathsMatchExe(softwareKey.GetValue(valueName) as string)
                    : TestPathsMatch(Uninstaller.InstallLocation, softwareKey.GetValue(valueName) as string);
            }
            else
            {
                hit = TestPathsMatch(Uninstaller.InstallLocation, softwareKey.GetValue(null) as string);
            }

            return hit;
        }

        private static IEnumerable<string> GetValueNamesSafe(RegistryKey key)
        {
            try
            {
                return key.GetValueNames();
            }
            catch (IOException)
            {
                return Enumerable.Empty<string>();
            }
        }

        private static readonly string WindowsDirectory = WindowsTools.GetEnvironmentPath(CSIDL.CSIDL_WINDOWS);

        private IEnumerable<JunkNode> ScanClsid()
        {
            var results = new List<JunkNode>();

            foreach (var keyName in ClsidKeys)
            {
                using (var key = RegistryTools.OpenRegistryKey(keyName))
                {
                    if (key == null)
                        continue;

                    foreach (var x in key.GetSubKeyNames())
                    {
                        var subKeyName = x.TrimEnd('\"'); // For some reason GetSubKeyNames puts quotes at end sometimes
                        RegistryKey subKey = null;

                        try
                        {
                            subKey = key.OpenSubKey(Path.Combine(subKeyName, "InprocServer32"));
                            var path = subKey?.GetValue(null) as string;

                            if (string.IsNullOrEmpty(path))
                                continue;

                            path = Environment.ExpandEnvironmentVariables(path).Trim('\"');

                            if (!Path.IsPathRooted(path) || TestPathsMatch(WindowsDirectory, path))
                                continue;

                            if (TestPathsMatchExe(path))
                            {
                                var node = new RegistryKeyJunkNode(keyName, subKeyName, Uninstaller.DisplayName);
                                node.Confidence.Add(ConfidencePart.ExplicitConnection);
                                results.Add(node);
                            }
                        }
                        catch
                        {
                            // TODO better handling?
                        }
                        finally
                        {
                            subKey?.Close();
                        }
                    }
                }
            }

            return results;
        }

        private IEnumerable<JunkNode> ScanFirewallRules()
        {
            const string firewallRulesKey =
                @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
            const string fullFirewallRulesKey = @"HKEY_LOCAL_MACHINE\" + firewallRulesKey;

            var results = new List<JunkNode>();
            if (string.IsNullOrEmpty(Uninstaller.InstallLocation))
                return results;

            using (var key = Registry.LocalMachine.OpenSubKey(firewallRulesKey))
            {
                if (key != null)
                {
                    foreach (var valueName in GetValueNamesSafe(key))
                    {
                        var value = key.GetValue(valueName) as string;
                        if (string.IsNullOrEmpty(value)) continue;

                        var start = value.IndexOf("|App=", StringComparison.InvariantCultureIgnoreCase) + 5;
                        var charCount = value.IndexOf('|', start) - start;
                        var fullPath = Environment.ExpandEnvironmentVariables(value.Substring(start, charCount));
                        if (fullPath.StartsWith(Uninstaller.InstallLocation, StringComparison.InvariantCultureIgnoreCase))
                        {
                            var node = new RegistryValueJunkNode(fullFirewallRulesKey, valueName,
                                Uninstaller.DisplayName);
                            node.Confidence.Add(ConfidencePart.ExplicitConnection);
                            results.Add(node);
                        }
                    }
                }
            }

            return results;
        }

        private IEnumerable<JunkNode> ScanForJunk()
        {
            var output = new List<JunkNode>();

            foreach (var softwareKeyName in SoftwareRegKeys)
            {
                using (var softwareKey = RegistryTools.OpenRegistryKey(softwareKeyName))
                {
                    if (softwareKey != null)
                        output.AddRange(FindJunkRecursively(softwareKey));
                }
            }

            return output.Concat(ScanRelatedKeys(output));
        }

        private IEnumerable<JunkNode> ScanRelatedKeys(IEnumerable<JunkNode> itemsToCompare)
        {
            var input = itemsToCompare.ToList();
            var output = new List<JunkNode>();

            foreach (var registryJunkNode in input)
            {
                var nodeName = registryJunkNode.FullName;

                // Check Wow first because non-wow path will match wow path
                var softwareKey = new[] {KeyLmWow, KeyCuWow, KeyLm, KeyCu}.First(
                    key => nodeName.StartsWith(key, StringComparison.InvariantCultureIgnoreCase));

                nodeName = nodeName.Substring(softwareKey.Length + 1);

                foreach (var keyToTest in SoftwareRegKeys.Except(new[] {softwareKey}))
                {
                    var nodePath = Path.Combine(keyToTest, nodeName);
                    // Check if the same node exists in other root keys
                    var node = input.FirstOrDefault(x => PathTools.PathsEqual(x.FullName, nodePath));

                    if (node != null)
                    {
                        // Add any non-duplicate confidence to the existing node
                        node.Confidence.AddRange(registryJunkNode.Confidence.ConfidenceParts
                            .Where(x => !node.Confidence.ConfidenceParts.Any(x.Equals)));
                    }
                    else
                    {
                        try
                        {
                            // Check if the key acually exists
                            using (var nodeKey = RegistryTools.OpenRegistryKey(nodePath, false))
                            {
                                if (nodeKey != null)
                                {
                                    var newNode = new RegistryKeyJunkNode(Path.GetDirectoryName(nodePath),
                                        Path.GetFileName(nodePath), Uninstaller.DisplayName);
                                    newNode.Confidence.AddRange(registryJunkNode.Confidence.ConfidenceParts);
                                    output.Add(newNode);
                                }
                            }
                        }
                        catch
                        {
                            // Ignore keys that don't exist
                        }
                    }
                }
            }

            return output;
        }

        private IEnumerable<JunkNode> ScanTracing()
        {
            const string tracingKey = @"SOFTWARE\Microsoft\Tracing";
            const string fullTracingKey = @"HKEY_LOCAL_MACHINE\" + tracingKey;

            var results = new List<JunkNode>();
            using (var key = Registry.LocalMachine.OpenSubKey(tracingKey))
            {
                if (key != null)
                {
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        var i = subKeyName.LastIndexOf('_');
                        if (i <= 0)
                            continue;

                        var str = subKeyName.Substring(0, i);

                        var conf = GenerateConfidence(str, Path.Combine(fullTracingKey, subKeyName), 0).ToList();
                        if (conf.Any())
                        {
                            var node = new RegistryKeyJunkNode(fullTracingKey, subKeyName, Uninstaller.DisplayName);
                            node.Confidence.AddRange(conf);
                            results.Add(node);
                        }
                    }
                }
            }
            return results;
        }

        private bool TestPathsMatch(string basePath, string childPath)
        {
            basePath = basePath?.Normalize().Trim().Trim('\\', '/');
            if (string.IsNullOrEmpty(basePath))
                return false;

            childPath = childPath?.Normalize().Trim().Trim('\\', '/');
            if (string.IsNullOrEmpty(childPath))
                return false;

            return childPath.StartsWith(basePath, StringComparison.InvariantCultureIgnoreCase);
        }

        private bool TestPathsMatchExe(string keyValue)
        {
            return TestPathsMatch(Uninstaller.InstallLocation, Path.GetDirectoryName(keyValue));
        }
    }
}