// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace X1.McpBridge
{
    /// <summary>
    /// XS-1672: vendored replacement for X1.Common.Utils.Files.ReadStringFromMemoryMappedFile/
    /// IsMemoryMappedFile, since the bridge no longer references X1.Common (proprietary,
    /// closed-source). This reads a memory-mapped file actually created by the closed-source
    /// X1ServiceHost.exe process (large search-result/content payloads it writes out-of-band
    /// from the WCF channel), so the behavior - including opening the MMF via its full,
    /// prefixed name rather than a stripped one - is reproduced exactly, not "fixed": that
    /// full "x1:%%size%%name" string is the MMF's real OS-level name as X1ServiceHost created
    /// it, not a display-only prefix to strip before use.
    /// </summary>
    internal static class MemoryMappedFileReader
    {
        private static readonly log4net.ILog Log = BridgeLogger.GetLogger(typeof(MemoryMappedFileReader));

        public static bool IsMemoryMappedFile(string path)
        {
            return path != null && path.StartsWith("x1:");
        }

        public static string ReadStringFromMemoryMappedFile(string filename)
        {
            string result = null;
            if (IsMemoryMappedFile(filename))
            {
                try
                {
                    using (var file = MemoryMappedFile.OpenExisting(filename))
                    using (var vs = file.CreateViewStream())
                    using (var reader = new StreamReader(vs))
                        result = reader.ReadToEnd().Replace("\0", string.Empty).Trim();
                }
                catch (Exception e)
                {
                    Log.Error("ReadStringFromMemoryMappedFile failed on: " + filename, e);
                }
            }
            return result;
        }
    }
}
