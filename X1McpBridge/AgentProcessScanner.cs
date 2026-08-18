// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;

namespace X1.McpBridge
{
    /// <summary>
    /// Detects the version of whatever Claude application is actually driving this MCP connection,
    /// by inspecting other processes on the machine rather than trusting anything self-reported over
    /// the wire.
    ///
    /// MCP's own initialize handshake carries a client-declared clientInfo {name, version}, but
    /// that's only as accurate as the specific integration layer that populates it - in the
    /// "local-agent-mode" plugin wiring this connector is often launched under, it's a static
    /// placeholder ("local-agent-mode-x1-search"/"1.0.0") tied to the plugin wrapper, not the real
    /// Claude Code build. Environment-variable inheritance doesn't help either: Claude Code does not
    /// pass its own identifying environment variables through to spawned MCP server processes
    /// (verified directly - every CLAUDE_*/AI_AGENT variable reads back unset inside this process).
    ///
    /// What DOES work: both "Claude" (the desktop app) and "Claude Code" (the CLI/agent harness)
    /// ship a binary literally named claude.exe, each carrying its own real product version in the
    /// PE file's Win32 version resource - readable via FileVersionInfo with zero cooperation from
    /// whichever spawned this connector. ProductName ("Claude" vs "Claude Code") is what
    /// disambiguates the two, since they're otherwise identically-named files at different paths.
    ///
    /// Bitness note: this connector ships Prefer32Bit (confirmed via corflags: 32BITPREF=1), so it
    /// runs as a 32-bit WOW64 process even on a 64-bit machine - and a 32-bit process cannot read a
    /// 64-bit process's MainModule (Process.MainModule throws "A 32 bit process cannot access
    /// modules of a 64 bit process" for every one of Claude's 64-bit processes, confirmed directly).
    /// QueryFullProcessImageName doesn't enumerate modules - it just asks the OS for the process's
    /// own image path - so it works across that bitness boundary where MainModule cannot. Tried
    /// first regardless (native 64-bit AnyCPU would hit it, so the cheaper path is checked first),
    /// falling back to the Win32 call only when that throws.
    ///
    /// Caveat: this is correlation, not lineage. It reports "a Claude Code process is running on
    /// this machine," not "the exact process that spawned this connection" - the OS doesn't expose a
    /// cheap way to prove the latter across this connector's shared-relay hop. In practice this
    /// rarely matters: concurrent sessions on one machine almost always share the same installed
    /// binary, so the version found is the right one even when the specific PID isn't provably so.
    /// </summary>
    internal static class AgentProcessScanner
    {
        public const string ProductNameClaudeCode = "Claude Code";
        public const string ProductNameClaudeDesktop = "Claude";

        /// <summary>
        /// Scans every running "claude"-named process and reports each DISTINCT
        /// (productName, productVersion, path) combination found - deduped rather than one row per
        /// OS process, since a single Claude Desktop install fans out into many same-version
        /// Electron helper processes (main, renderer, GPU, utility, ...) that would otherwise flood
        /// this diagnostic with near-duplicate rows.
        ///
        /// Never throws - this feeds a diagnostic call (x1_version). Unlike RelayProcessScanner's
        /// X1McpBridge/X1McpGraphQL scan, where every relay instance matters and an unreadable one is
        /// itself a finding worth surfacing, a "claude" process whose path/version can't be read
        /// (e.g. cross-user-session access denied) is silently skipped here rather than reported as
        /// an error row - one unreadable Electron helper process among a dozen identical siblings
        /// isn't informative on its own.
        /// </summary>
        public static JArray ScanClaudeProcesses()
        {
            var result = new JArray();
            foreach (var identity in DistinctIdentities())
            {
                result.Add(new JObject
                {
                    ["productName"] = identity.ProductName,
                    ["productVersion"] = identity.ProductVersion,
                    ["companyName"] = identity.CompanyName,
                    ["path"] = identity.Path
                });
            }
            return result;
        }

        internal struct ClientIdentity
        {
            public string ProductName;
            public string ProductVersion;
            public string CompanyName;
            public string Path;
        }

        /// <summary>
        /// XS-1684: every distinct detected client folded into one (name, version) pair for
        /// ReportClientInfo, which takes a single value per field. When both the desktop app and
        /// Claude Code are running the x1_version diagnostic shows the full array, but the old
        /// single-pick reported only one; this reports them all so the X1 Search Options tab
        /// reflects everything that's connected. Returns null when no "claude"-named process could
        /// be read at all, so the caller falls back to the MCP-declared clientInfo. See the
        /// class-level caveat - still correlation, not lineage.
        /// </summary>
        public static ClientIdentity? GetConcatenatedIdentity() => ConcatenateIdentities(DistinctIdentities());

        /// <summary>
        /// Pure join half of <see cref="GetConcatenatedIdentity"/>, split out so the concatenation
        /// behavior is unit-testable without scanning live processes (see AgentProcessScannerTests).
        /// Names and versions are joined in the same order, so the Nth name lines up with the Nth
        /// version. Dedupes by (name, version) so identical pairs - e.g. the same product found at
        /// more than one path - aren't repeated. Returns null for an empty sequence.
        /// </summary>
        internal static ClientIdentity? ConcatenateIdentities(IEnumerable<ClientIdentity> identities)
        {
            var names = new List<string>();
            var versions = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in identities)
            {
                string name = id.ProductName ?? "";
                string version = id.ProductVersion ?? "";
                if (!seen.Add(name + "|" + version))
                    continue;
                names.Add(name);
                versions.Add(version);
            }
            if (names.Count == 0)
                return null;
            return new ClientIdentity
            {
                ProductName = string.Join(", ", names),
                ProductVersion = string.Join(", ", versions)
            };
        }

        private static IEnumerable<ClientIdentity> DistinctIdentities()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var identities = new List<ClientIdentity>();

            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName("claude");
            }
            catch
            {
                return identities;
            }

            foreach (var p in procs)
            {
                try
                {
                    string path = TryGetProcessPath(p);
                    if (string.IsNullOrEmpty(path))
                        continue;

                    FileVersionInfo vi;
                    try { vi = FileVersionInfo.GetVersionInfo(path); }
                    catch { continue; }

                    string key = (vi.ProductName ?? "") + "|" + (vi.ProductVersion ?? "") + "|" + path;
                    if (!seen.Add(key))
                        continue;

                    identities.Add(new ClientIdentity
                    {
                        ProductName = vi.ProductName,
                        ProductVersion = vi.ProductVersion,
                        CompanyName = vi.CompanyName,
                        Path = path
                    });
                }
                finally
                {
                    p.Dispose();
                }
            }

            return identities;
        }

        private static string TryGetProcessPath(Process p)
        {
            try
            {
                return p.MainModule != null ? p.MainModule.FileName : null;
            }
            catch
            {
                // Cross-bitness (this connector runs 32-bit, Claude's processes are 64-bit) or an
                // access-denied case - fall back to the Win32 API, which doesn't enumerate modules
                // and so isn't subject to the same restriction.
                return TryGetProcessPathViaWinApi(p.Id);
            }
        }

        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static string TryGetProcessPathViaWinApi(int pid)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
