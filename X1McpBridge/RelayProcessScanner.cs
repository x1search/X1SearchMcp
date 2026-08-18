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
using Newtonsoft.Json.Linq;

namespace X1.McpBridge
{
    /// <summary>
    /// Finds every X1McpBridge and X1McpGraphQL process running on this machine - not just the ones
    /// belonging to this install. Two installs coexisting (the Cowork plugin's copy beside the
    /// standalone one) is invisible from inside a single process, yet it is a leading cause of "I
    /// deployed the fix and it didn't take": the relay is shared on a fixed port, so whichever
    /// install got there first serves everyone.
    ///
    /// net4.8/C#7.3 counterpart of the daemon's Gateway/BridgeProcessScanner.cs, needed because in
    /// the Lean flavor there is no daemon to do the scanning - the bridge answers x1_version itself.
    /// Extended here to also enumerate X1McpGraphQL, which the daemon-side version had no reason to
    /// do: on a Lean machine the most likely coexisting install IS a leftover net10 daemon (e.g. one
    /// a plugin still ships, or one a surviving logon task keeps restarting), and finding it is
    /// exactly the condition this scanner exists to surface.
    ///
    /// Never throws. This feeds a diagnostic call, so a process it cannot read is reported as a row
    /// carrying an error rather than dropped - "there is a relay here I can't identify" is itself a
    /// finding. It also touches no WCF, so x1_version stays answerable with X1ServiceHost down.
    /// </summary>
    internal static class RelayProcessScanner
    {
        public static JArray ScanBridges()
        {
            return Scan("X1McpBridge");
        }

        public static JArray ScanDaemons()
        {
            return Scan("X1McpGraphQL");
        }

        private static JArray Scan(string processName)
        {
            var result = new JArray();

            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(processName);
            }
            catch (Exception ex)
            {
                result.Add(new JObject
                {
                    ["processId"] = -1,
                    ["error"] = "could not enumerate processes: " + ex.Message,
                });
                return result;
            }

            foreach (var p in procs)
            {
                int pid = -1;
                try
                {
                    pid = p.Id;

                    // MainModule reads the process's loaded modules and can fail on access - it is
                    // what makes `Get-Process | Select Path` come back blank in some shells. Failing
                    // to read it must not lose the row: the pid alone still says a relay is running.
                    string path = null;
                    string readError = null;
                    try
                    {
                        path = p.MainModule != null ? p.MainModule.FileName : null;
                    }
                    catch (Exception ex)
                    {
                        readError = "could not read the process path: " + ex.Message;
                    }

                    // Version comes from the file on disk at that path. If the exe was replaced
                    // after the process started, this reports the NEW file's version while the
                    // process is still running the old code. startTime is included so that case
                    // stays visible.
                    string version = null;
                    if (!string.IsNullOrEmpty(path))
                    {
                        try
                        {
                            version = FileVersionInfo.GetVersionInfo(path).FileVersion;
                        }
                        catch (Exception ex)
                        {
                            if (readError == null)
                                readError = "could not read the file version: " + ex.Message;
                        }
                    }

                    string started = null;
                    try
                    {
                        started = p.StartTime.ToString("o");
                    }
                    catch
                    {
                        // Exited between enumeration and now, or unreadable. Not worth reporting on
                        // its own - the path and version are what identify the build.
                    }

                    var row = new JObject { ["processId"] = pid };
                    row["path"] = path;
                    row["version"] = version;
                    row["startTime"] = started;
                    row["error"] = readError;
                    result.Add(row);
                }
                catch (Exception ex)
                {
                    result.Add(new JObject { ["processId"] = pid, ["error"] = ex.Message });
                }
                finally
                {
                    p.Dispose();
                }
            }

            return result;
        }

        /// <summary>
        /// True when the scanned rows report more than one distinct version - i.e. two different
        /// builds are live on this machine and which one answers depends on who won the port.
        /// </summary>
        public static bool VersionsDisagree(JArray rows)
        {
            string first = null;
            foreach (var row in rows)
            {
                var v = row.Value<string>("version");
                if (string.IsNullOrEmpty(v))
                    continue;
                if (first == null)
                    first = v;
                else if (!string.Equals(first, v, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
