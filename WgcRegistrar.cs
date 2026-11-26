//
// Ported from wgc_registrar.cpp
// Copyright 2026 Andon "Kaldaien" Coleman
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//
// C# port: same registry algorithm, using Microsoft.Win32.Registry +
// System.Security.Cryptography.ProtectedData instead of the raw Win32 APIs.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// Registers a game executable in HKCU\System\GameConfigStore so Windows
    /// treats it as a "game" (Game Mode, GPU scheduling priority, Xbox Game
    /// Bar overlay) even when Windows' own heuristic detection misses it.
    /// This mirrors what Special K's WGC registrar does, minus the SK-only bits.
    /// </summary>
    public static class WgcRegistrar
    {
        private const string ChildrenPath = @"System\GameConfigStore\Children";
        private const string ParentsPath  = @"System\GameConfigStore\Parents";

        /// <summary>
        /// Registers wszExePath as a game if it isn't already.
        /// Returns true if a new registration was created, false if it was
        /// already registered, the store doesn't exist, or anything failed.
        /// </summary>
        public static bool AddGameIfNeeded(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return false;

            try
            {
                using (var childrenRoot = Registry.CurrentUser.OpenSubKey(ChildrenPath, writable: true))
                using (var parentsRoot  = Registry.CurrentUser.OpenSubKey(ParentsPath,  writable: true))
                {
                    // GameConfigStore is created by Windows itself; if it's not
                    // there this OS/build doesn't support it, mirror native behavior.
                    if (childrenRoot == null || parentsRoot == null)
                        return false;

                    string exeFile      = Path.GetFileName(exePath);
                    string exeFileLower = exeFile.ToLowerInvariant();
                    string parentKeyName = Sha1Hex(exeFileLower);

                    if (IsAlreadyRegistered(childrenRoot, parentsRoot, parentKeyName, exePath))
                        return false;

                    return RegisterNewGame(childrenRoot, parentsRoot, parentKeyName, exeFile, exeFileLower, exePath);
                }
            }
            catch
            {
                // Never let a registry quirk take down game launch.
                return false;
            }
        }

        private static bool IsAlreadyRegistered(RegistryKey childrenRoot, RegistryKey parentsRoot, string parentKeyName, string exePath)
        {
            using (var parentKey = parentsRoot.OpenSubKey(parentKeyName))
            {
                if (parentKey == null)
                    return false;

                var childKeyNames = parentKey.GetValue("Children") as string[] ?? Array.Empty<string>();

                foreach (var childKeyName in childKeyNames)
                {
                    using (var childKey = childrenRoot.OpenSubKey(childKeyName))
                    {
                        var matchedPath = childKey?.GetValue("MatchedExeFullPath") as string;

                        if (!string.IsNullOrEmpty(matchedPath) &&
                            string.Equals(Path.GetFullPath(matchedPath), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool RegisterNewGame(RegistryKey childrenRoot, RegistryKey parentsRoot, string parentKeyName,
                                             string exeFile, string exeFileLower, string exePath)
        {
            string exeParentDir = Path.GetDirectoryName(exePath) ?? string.Empty;
            byte[] parentBlob   = ProtectLocalMachine(exeFileLower);
            long   lastAccessed = DateTime.UtcNow.ToFileTimeUtc();

            string childGuid = Guid.NewGuid().ToString();
            string gameGuid  = Guid.NewGuid().ToString();

            using (var parentSubKey = parentsRoot.CreateSubKey(parentKeyName))
            using (var childSubKey  = childrenRoot.CreateSubKey(childGuid))
            {
                if (parentSubKey == null || childSubKey == null)
                    return false;

                childSubKey.SetValue("ExeParentDirectory", exeParentDir, RegistryValueKind.String);
                childSubKey.SetValue("Type", 0x1, RegistryValueKind.DWord);
                childSubKey.SetValue("Revision", 0x1, RegistryValueKind.DWord);
                childSubKey.SetValue("Flags", 0x11, RegistryValueKind.DWord);
                childSubKey.SetValue("Parent", parentBlob, RegistryValueKind.Binary);
                childSubKey.SetValue("GameDVR_GameGUID", gameGuid, RegistryValueKind.String);
                childSubKey.SetValue("MatchedExeFullPath", exePath, RegistryValueKind.String);
                childSubKey.SetValue("LastAccessed", lastAccessed, RegistryValueKind.QWord);

                var childrenArray = (parentSubKey.GetValue("Children") as string[])?.ToList()
                                     ?? new List<string>();

                if (!childrenArray.Contains(childGuid, StringComparer.OrdinalIgnoreCase))
                    childrenArray.Add(childGuid);

                // Drop stale entries that don't have a matching Child key -
                // Windows ignores the *entire* list if one entry is dangling.
                var validChildren = new List<string>();
                foreach (var guid in childrenArray)
                {
                    using (var probe = childrenRoot.OpenSubKey(guid))
                    {
                        if (probe != null)
                            validChildren.Add(guid);
                    }
                }

                parentSubKey.SetValue("Children", validChildren.ToArray(), RegistryValueKind.MultiString);

                return true;
            }
        }

        private static string Sha1Hex(string text)
        {
            using (var sha1 = SHA1.Create())
            {
                byte[] bytes = Encoding.Unicode.GetBytes(text); // UTF-16LE, matches native GetSha1Utf16Le
                byte[] hash  = sha1.ComputeHash(bytes);

                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    sb.Append(b.ToString("x2"));

                return sb.ToString();
            }
        }

        private static byte[] ProtectLocalMachine(string text)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text);
            return ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
        }
    }
}
