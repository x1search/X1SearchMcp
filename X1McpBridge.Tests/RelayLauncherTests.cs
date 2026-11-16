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
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using NUnit.Framework;

namespace X1.McpBridge.Tests
{
    /// <summary>
    /// XS-1819: the relay must not inherit the launching proxy's handles, and the stale-relay rules
    /// must never pick a relay that is merely starting up, another session's relay, or ourselves.
    /// </summary>
    [TestFixture]
    public class RelayLauncherTests
    {
        private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        // A child that stays alive ~3s and does nothing else.
        private const string SleepArgs = "/c ping -n 4 127.0.0.1 >nul";

        /// <summary>
        /// Holds the write end of an inheritable pipe, starts a child, then drops our copy. If the
        /// child inherited the write end, the read end can't see EOF until the child exits; if it
        /// didn't, EOF is immediate. Returns how long EOF took.
        /// </summary>
        private static TimeSpan EofDelayAfterLaunching(Func<int> launch)
        {
            using (var server = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable))
            {
                int pid = launch();
                Assert.That(pid, Is.GreaterThan(0), "child did not start");
                try
                {
                    server.DisposeLocalCopyOfClientHandle();
                    var sw = Stopwatch.StartNew();
                    var read = Task.Run(() => server.Read(new byte[1], 0, 1));
                    read.Wait(TimeSpan.FromSeconds(10));
                    return sw.Elapsed;
                }
                finally
                {
                    try { using (var p = Process.GetProcessById(pid)) p.Kill(); } catch { /* already exited */ }
                }
            }
        }

        [Test]
        public void LaunchDetached_ChildDoesNotInheritHandles()
        {
            var delay = EofDelayAfterLaunching(() => RelayLauncher.LaunchDetached(Cmd, SleepArgs, Environment.SystemDirectory));
            Assert.That(delay, Is.LessThan(TimeSpan.FromSeconds(1)),
                "the pipe stayed open while the child ran, so the child inherited our handle");
        }

        [Test]
        public void ProcessStart_WithoutShellExecute_DoesInheritHandles_WhichIsWhyItWasReplaced()
        {
            // Pins the behaviour XS-1819 is about, so nobody "simplifies" RelayLauncher back to it.
            var delay = EofDelayAfterLaunching(() =>
            {
                var p = Process.Start(new ProcessStartInfo(Cmd, SleepArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.SystemDirectory,
                });
                return p.Id;
            });
            Assert.That(delay, Is.GreaterThan(TimeSpan.FromSeconds(1.5)));
        }

        [Test]
        public void LaunchDetached_MissingExe_ReturnsZeroWithoutThrowing()
        {
            Assert.That(RelayLauncher.LaunchDetached(@"C:\does\not\exist\X1McpBridge.exe", "--host", null), Is.EqualTo(0));
        }

        [TestCase("\"C:\\x\\X1McpBridge.exe\" --host", true)]
        [TestCase("C:\\x\\X1McpBridge.exe --host", true)]
        [TestCase("\"C:\\x\\X1McpBridge.exe\" --proxy", false)]
        [TestCase("\"C:\\x\\X1McpBridge.exe\"", false)]
        [TestCase("\"C:\\x\\X1McpBridge.exe\" --hostile", false)]
        [TestCase(null, false)]
        public void IsHostCommandLine(string commandLine, bool expected)
        {
            Assert.That(RelayLauncher.IsHostCommandLine(commandLine), Is.EqualTo(expected));
        }

        [Test]
        public void SelectStaleHosts_KeepsOnlyOldHostsInOurSessionThatAreNotUs()
        {
            var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
            var hosts = new List<RelayLauncher.HostProcess>
            {
                new RelayLauncher.HostProcess { Pid = 10, SessionId = 1, StartedUtc = now.AddMinutes(-5) },  // stale
                new RelayLauncher.HostProcess { Pid = 11, SessionId = 1, StartedUtc = now.AddSeconds(-2) },  // just launched
                new RelayLauncher.HostProcess { Pid = 12, SessionId = 2, StartedUtc = now.AddMinutes(-5) },  // other session
                new RelayLauncher.HostProcess { Pid = 99, SessionId = 1, StartedUtc = now.AddMinutes(-5) },  // us
                new RelayLauncher.HostProcess { Pid = 13, SessionId = 1, StartedUtc = DateTime.MinValue },   // unknown age
            };

            var stale = RelayLauncher.SelectStaleHosts(hosts, 1, 99, now, TimeSpan.FromSeconds(10));

            Assert.That(stale.ConvertAll(h => h.Pid), Is.EquivalentTo(new[] { 10, 13 }));
        }
    }
}
