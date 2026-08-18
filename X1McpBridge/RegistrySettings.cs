// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using Microsoft.Win32;

namespace X1.McpBridge
{
    /// <summary>
    /// XS-1672: vendored replacement for X1.Common.Utils.RegUtils.ReadString/ReadInteger, since
    /// the bridge no longer references X1.Common (proprietary, closed-source). Every call site in
    /// this project passes RegUtils.Hive.FirstHKCUThenHKLM, so that policy is hardcoded here rather
    /// than ported as a parameter. Deliberately narrower than the original: no ISettingsProvider
    /// override hook, no registry-change-notify cache, no 32-on-64-bit RegistryView.Registry64
    /// fallback, no "X1E." value-name aliasing - none of those paths are exercised by any bridge
    /// call site, and RegUtils.GetProductRegKey() resolves to this same "Software\X1 Search" root
    /// for this product line regardless.
    /// </summary>
    internal static class RegistrySettings
    {
        private const string ProductRegistryKey = @"Software\X1 Search";

        /// <summary>
        /// Reads a string value from HKCU, falling back to HKLM. Matches RegUtils'
        /// ReadStringInternal: only returns a registry value whose kind is REG_SZ; any other kind,
        /// a missing key/value, or an access error falls through to <paramref name="defaultValue"/>.
        /// </summary>
        public static string ReadString(string valueName, string defaultValue)
        {
            try
            {
                using (RegistryKey subkey = Registry.CurrentUser.OpenSubKey(ProductRegistryKey, false))
                {
                    if (subkey != null && subkey.GetValue(valueName) != null &&
                        subkey.GetValueKind(valueName) == RegistryValueKind.String)
                        return (string)subkey.GetValue(valueName, defaultValue);
                }

                using (RegistryKey subkey = Registry.LocalMachine.OpenSubKey(ProductRegistryKey, false))
                {
                    if (subkey != null && subkey.GetValue(valueName) != null &&
                        subkey.GetValueKind(valueName) == RegistryValueKind.String)
                        return (string)subkey.GetValue(valueName, defaultValue);
                }
            }
            catch
            {
                // Fall through to defaultValue - matches RegUtils' catch-and-default behavior.
            }

            return defaultValue;
        }

        /// <summary>
        /// Reads an integer value from HKCU, falling back to HKLM. Matches RegUtils'
        /// ReadIntegerInternal: accepts a REG_DWORD directly, or a REG_SZ that parses as an int;
        /// any other kind, a missing key/value, or an access error falls through to
        /// <paramref name="defaultValue"/>.
        /// </summary>
        public static int ReadInteger(string valueName, int defaultValue)
        {
            try
            {
                int result;
                if (TryReadInteger(Registry.CurrentUser, valueName, defaultValue, out result))
                    return result;
                if (TryReadInteger(Registry.LocalMachine, valueName, defaultValue, out result))
                    return result;
            }
            catch
            {
                // Fall through to defaultValue - matches RegUtils' catch-and-default behavior.
            }

            return defaultValue;
        }

        private static bool TryReadInteger(RegistryKey hive, string valueName, int defaultValue, out int result)
        {
            result = defaultValue;

            using (RegistryKey subkey = hive.OpenSubKey(ProductRegistryKey, false))
            {
                if (subkey == null || subkey.GetValue(valueName) == null)
                    return false;

                switch (subkey.GetValueKind(valueName))
                {
                    case RegistryValueKind.DWord:
                        result = (int)subkey.GetValue(valueName, defaultValue);
                        return true;

                    case RegistryValueKind.String:
                        string str = (string)subkey.GetValue(valueName, "");
                        return !string.IsNullOrWhiteSpace(str) && int.TryParse(str, out result);

                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// Writes a REG_DWORD value to HKCU (never HKLM — this is a per-user preference the
        /// current process sets for itself, not a machine-wide policy). Unlike the Read* methods
        /// above, this deliberately does NOT catch-and-default: it runs once per explicit caller
        /// request (e.g. a tool call), not on every startup/search, so the caller needs to know if
        /// the write actually failed rather than have it silently no-op.
        /// </summary>
        public static void WriteDWord(string valueName, int value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ProductRegistryKey))
                key.SetValue(valueName, value, RegistryValueKind.DWord);
        }
    }
}
