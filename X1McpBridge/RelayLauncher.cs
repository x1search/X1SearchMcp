// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace X1.McpBridge
{
    /// <summary>
    /// Starts the shared relay as a genuinely independent process, and finds relays that have gone
    /// stale (XS-1819).
    ///
    /// WHY NOT Process.Start: with UseShellExecute=false, .NET Framework always calls CreateProcess
    /// with bInheritHandles=TRUE. The relay - which lives for an hour and serves every session -
    /// then inherits every inheritable handle the launching proxy holds at that moment: the
    /// client's stdio pipes, and (because .NET Framework sockets are inheritable) the proxy's open
    /// HTTP connections. It also stays inside whatever job object the client launched the proxy
    /// in, and so inherits that job's limits and policy. A relay started by hand from a terminal
    /// has none of that, which is exactly the "it works when I start it myself" split in XS-1819:
    /// requests reached a proxy-launched relay and were answered, but the replies never made it
    /// back to any caller.
    ///
    /// So the launch here is CreateProcess with bInheritHandles=FALSE and CREATE_BREAKAWAY_FROM_JOB,
    /// i.e. as close to "started from a terminal" as a child process can get. If the job forbids
    /// breakaway, CreateProcess fails with ERROR_ACCESS_DENIED and we retry without it - the relay
    /// still gets no inherited handles, and the log says it is job-bound so a support engineer can
    /// see it.
    /// </summary>
    internal static class RelayLauncher
    {
        private static readonly log4net.ILog Log = BridgeLogger.GetLogger(typeof(RelayLauncher));

        private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
        private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const int ERROR_ACCESS_DENIED = 5;

        /// <summary>
        /// Launches <paramref name="fileName"/> detached: no inherited handles, no window, out of
        /// the caller's job when the job allows it. Returns the new pid, or 0 on failure (logged).
        /// Never throws.
        /// </summary>
        public static int LaunchDetached(string fileName, string arguments, string workingDirectory)
        {
            var commandLine = new StringBuilder("\"" + fileName + "\"");
            if (!string.IsNullOrEmpty(arguments))
                commandLine.Append(' ').Append(arguments);

            uint baseFlags = CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP;

            int error;
            int pid = TryCreate(fileName, commandLine.ToString(), workingDirectory, baseFlags | CREATE_BREAKAWAY_FROM_JOB, out error);
            if (pid > 0)
            {
                Log.Info("Started relay pid " + pid + " detached (no inherited handles, out of the caller's job).");
                return pid;
            }

            if (error == ERROR_ACCESS_DENIED)
            {
                // The caller's job doesn't permit breakaway. Still worth launching: dropping the
                // inherited handles is the larger half of the fix. But the relay will share the
                // job's fate, so say so.
                pid = TryCreate(fileName, commandLine.ToString(), workingDirectory, baseFlags, out error);
                if (pid > 0)
                {
                    Log.Warn("Started relay pid " + pid + " with no inherited handles, but INSIDE the caller's job: " +
                             "the job does not allow breakaway, so the relay will exit when this client's job closes.");
                    return pid;
                }
            }

            Log.Error("Failed to launch the relay '" + fileName + "': " + new Win32Exception(error).Message +
                      " (Win32 error " + error + ").");
            return 0;
        }

        private static int TryCreate(string fileName, string commandLine, string workingDirectory, uint flags, out int error)
        {
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION pi;

            // CreateProcessW may write to lpCommandLine, so it must be a mutable buffer.
            var cmd = new StringBuilder(commandLine);
            bool ok = CreateProcess(fileName, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
                string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory, ref si, out pi);
            if (!ok)
            {
                error = Marshal.GetLastWin32Error();
                return 0;
            }

            error = 0;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }

        /// <summary>True when this process runs inside a job object. Diagnostic only; false if unknown.</summary>
        public static bool IsCurrentProcessInJob()
        {
            try
            {
                bool inJob;
                return IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out inJob) && inJob;
            }
            catch
            {
                return false;
            }
        }

        // ── Stale-relay discovery ────────────────────────────────────────────────

        /// <summary>A running "X1McpBridge.exe --host" process, as seen from outside it.</summary>
        internal struct HostProcess
        {
            public int Pid;
            public int SessionId;
            public DateTime StartedUtc;   // DateTime.MinValue = unknown
            public string CommandLine;
        }

        /// <summary>
        /// Every X1McpBridge process whose command line says --host. Needs WMI because the command
        /// line is the only thing that tells a relay apart from the --proxy shims of other sessions,
        /// which must never be killed. Never throws; an empty list on failure (logged).
        /// </summary>
        public static List<HostProcess> FindHostProcesses()
        {
            var result = new List<HostProcess>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, SessionId, CreationDate, CommandLine FROM Win32_Process WHERE Name = 'X1McpBridge.exe'"))
                using (var rows = searcher.Get())
                {
                    foreach (ManagementObject row in rows)
                    {
                        using (row)
                        {
                            var commandLine = row["CommandLine"] as string;
                            if (!IsHostCommandLine(commandLine))
                                continue;

                            var started = DateTime.MinValue;
                            var created = row["CreationDate"] as string;
                            if (!string.IsNullOrEmpty(created))
                            {
                                try { started = ManagementDateTimeConverter.ToDateTime(created).ToUniversalTime(); }
                                catch { /* leave unknown */ }
                            }

                            result.Add(new HostProcess
                            {
                                Pid = Convert.ToInt32(row["ProcessId"]),
                                SessionId = Convert.ToInt32(row["SessionId"]),
                                StartedUtc = started,
                                CommandLine = commandLine,
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not enumerate relay processes: " + ex.Message);
            }
            return result;
        }

        internal static bool IsHostCommandLine(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine))
                return false;
            foreach (var token in commandLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(token, "--host", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Which of <paramref name="hosts"/> are stale, given that the relay port is NOT answering a
        /// health check right now. A host counts only if it is in our Windows session (the
        /// single-instance mutex is per session, so a host in another session is not what is
        /// blocking us, and may not even be our user's), is not us, and has been up longer than
        /// <paramref name="minAge"/> - so one proxy can't kill a relay another proxy launched a
        /// moment ago and that simply hasn't bound the port yet. An unknown start time is treated
        /// as old: it is still holding the lock, and leaving it there is what wedges every session.
        /// </summary>
        internal static List<HostProcess> SelectStaleHosts(
            IEnumerable<HostProcess> hosts, int ourSessionId, int ourPid, DateTime nowUtc, TimeSpan minAge)
        {
            var stale = new List<HostProcess>();
            foreach (var h in hosts)
            {
                if (h.Pid == ourPid || h.SessionId != ourSessionId)
                    continue;
                if (h.StartedUtc != DateTime.MinValue && nowUtc - h.StartedUtc < minAge)
                    continue;
                stale.Add(h);
            }
            return stale;
        }

        /// <summary>The parent pid of the current process, or 0 if unknown. Diagnostic only.</summary>
        public static int GetParentProcessId()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = " + Process.GetCurrentProcess().Id))
                using (var rows = searcher.Get())
                {
                    foreach (ManagementObject row in rows)
                        using (row)
                            return Convert.ToInt32(row["ParentProcessId"]);
                }
            }
            catch
            {
                // diagnostic only
            }
            return 0;
        }

        // ── P/Invoke ─────────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
        private static extern bool CreateProcess(
            string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    }
}
