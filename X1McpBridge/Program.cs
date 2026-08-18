// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.IO;
using System.Text;
using X1.Service;

namespace X1.McpBridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // Redirect Console.Out to null BEFORE anything else — including
            // BridgeLogger.Configure() and TempSweep — runs. X1.Common's own internal
            // logger (touched the first time any X1.Common code, e.g. RegUtils, is used)
            // can write straight to Console.Out for some diagnostic messages, independent
            // of our own log4net configuration. If any of that happens before this
            // redirect, it corrupts the MCP JSON-RPC stdio stream with stray non-JSON
            // text. Capture the real stdout first so RunStdio() can still write actual
            // JSON-RPC responses to it.
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(false);
            var mcpOut = Console.Out;
            Console.SetOut(TextWriter.Null);

            // Configure logging early so TempSweep's log line lands in the bridge log
            // rather than a log4net init warning.
            BridgeLogger.Configure();

            // Startup sweep of %TEMP%\x1mcp_previews\ and orphaned x1mcp_extract_*/
            // x1mcp_content_* temps. Runs on a background thread — never blocks the
            // main entry point. Thresholds configurable via tempMaxAgeHours /
            // tempMaxTotalMB in x1mcp.config.json.
            TempSweep.RunInBackground();

            if (args.Length > 0 && args[0] == "--smoke-wcf")
                return RunSmokeWcf();

            // --proxy: fan-in shim mode (see ProxyMode.cs) - used by Claude Code/Desktop's own
            // stdio registration once they're pointed at the shared relay (XS-1613). Plain,
            // no-args startup remains full bridge/WCF mode unchanged, since that's also what the
            // daemon's own McpStdioBridgeClient spawns for itself.
            if (args.Length > 0 && args[0] == "--proxy")
                return ProxyMode.Run(mcpOut);

            // --host: BE the shared relay (see HostMode.cs) - the Lean flavor's replacement for the
            // net10 X1McpGraphQL daemon, launched detached by ProxyMode. Note this mode does not get
            // mcpOut: stdout is not a protocol channel here, so the redirect to TextWriter.Null
            // above is simply left in place, which also keeps X1.Common's stray Console.Out writes
            // from going to a detached process's dead stdout handle.
            if (args.Length > 0 && args[0] == "--host")
                return HostMode.Run();

            return McpServer.RunStdio(mcpOut);
        }

        /// <summary>
        /// Minimal connectivity check (requires X1ServiceHost). Prints JSON to stdout.
        /// </summary>
        private static int RunSmokeWcf()
        {
            try
            {
                var callbacks = new SearchManagerCallbacks();
                using (var conn = new X1MCPSearchConnection(callbacks))
                {
                    var ch = conn.GetChannel();
                    int sid = ch.CreateSearchSession(new[] { "Files" }, false, false);
                    callbacks.RegisterSession(sid);
                    try
                    {
                        var terms = new[] { new SearchTerm("", "", "test") };
                        ch.SetSearchTerms(sid, terms, new SortColumn[0],
                            new[] { new Column("", "Name") }, new MergeColumn[0], 5);
                        var t = callbacks.WaitResultsChangedAsync(sid);
                        if (!t.Wait(60000))
                        {
                            Console.WriteLine("{\"error\":\"timeout\"}");
                            return 2;
                        }
                        var r = t.Result;
                        Console.WriteLine("{\"sessionId\":" + sid + ",\"totalResults\":" + r.TotalResults + "}");
                        return 0;
                    }
                    finally
                    {
                        try
                        {
                            ch.DestroySearchSession(sid);
                        }
                        catch
                        {
                            // ignore
                        }
                        callbacks.UnregisterSession(sid);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("{\"error\":\"" + EscapeJson(ex.Message) + "\"}");
                return 1;
            }
        }

        private static string EscapeJson(string s)
        {
            if (s == null)
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
