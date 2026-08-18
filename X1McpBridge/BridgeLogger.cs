// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.IO;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;

namespace X1.McpBridge
{
    /// <summary>
    /// Configures log4net with a rolling file appender and exposes per-class
    /// ILog factories.  Call <see cref="Configure"/> once at startup before any
    /// other use.
    ///
    /// Log destination and level are configurable via the same registry convention
    /// other X1 products use (HKCU, falling back to HKLM, under
    /// Software\{X1 product name} — "X1 Search" for this build):
    ///
    ///   "X1McpConnectorLog" (string) — directory to write the log file into (consistent
    ///     with how other X1 products treat their log-location registry values). The log
    ///     file itself is always named X1McpBridge.log within that directory. Defaults to
    ///     %LOCALAPPDATA%\X1 Discovery\McpBridge\logs\X1McpBridge.log if unset.
    ///
    ///   "Verbosity" (string) — the same key other X1 products use to control log
    ///     level (ALL, DEBUG, INFO, WARN, ERROR, FATAL, OFF). Defaults to DEBUG
    ///     for this bridge if unset/unrecognized, preserving the bridge's original
    ///     always-verbose behavior — set Verbosity in the registry to quiet it down.
    ///
    /// Up to 5 x 5 MB backup files, UTF-8, date+level+thread pattern.
    ///
    /// stdout is reserved for the MCP JSON-RPC transport and must never receive
    /// log output; all log output goes to the file only.
    /// </summary>
    internal static class BridgeLogger
    {
        private const string LogPathRegistryValue = "X1McpConnectorLog";
        private const string VerbosityRegistryValue = "Verbosity";
        private const string LogFileName = "X1McpBridge.log";

        private static volatile bool configured;
        private static readonly object configureLock = new object();

        public static ILog GetLogger(Type type) => LogManager.GetLogger(type);

        /// <summary>
        /// Idempotent.  Configures log4net if not already done.
        /// </summary>
        public static void Configure()
        {
            if (configured)
                return;
            lock (configureLock)
            {
                if (configured)
                    return;
                DoConfigure();
                configured = true;
            }
        }

        private static void DoConfigure()
        {
            var hierarchy = (Hierarchy)LogManager.GetRepository();
            hierarchy.ResetConfiguration();

            string logFile = ResolveLogFilePath();
            string logDir = Path.GetDirectoryName(logFile);

            try
            {
                if (!string.IsNullOrEmpty(logDir))
                    Directory.CreateDirectory(logDir);
            }
            catch
            {
                // If we cannot create the log directory (e.g. permissions), leave log4net
                // unconfigured rather than crashing the bridge process.
                return;
            }

            var layout = new PatternLayout
            {
                ConversionPattern = "%date{yyyy-MM-dd HH:mm:ss.fff} %-5level [%thread] %logger{1} - %message%newline"
            };
            layout.ActivateOptions();

            var appender = new RollingFileAppender
            {
                Name = "BridgeRollingFile",
                File = logFile,
                AppendToFile = true,
                RollingStyle = RollingFileAppender.RollingMode.Size,
                MaxSizeRollBackups = 5,
                MaximumFileSize = "5MB",
                StaticLogFileName = true,
                Encoding = System.Text.Encoding.UTF8,
                Layout = layout,
                LockingModel = new RollingFileAppender.MinimalLock()
            };
            appender.ActivateOptions();

            hierarchy.Root.AddAppender(appender);
            hierarchy.Root.Level = ResolveVerbosity();
            hierarchy.Configured = true;
        }

        /// <summary>
        /// Reads the "X1McpConnectorLog" registry value (HKCU, falling back to HKLM, under
        /// Software\{product name}) as the log DIRECTORY, then appends the fixed
        /// <see cref="LogFileName"/>. Falls back to the original default directory if unset
        /// or unreadable — never throws.
        /// </summary>
        private static string ResolveLogFilePath()
        {
            string defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "X1 Discovery", "McpBridge", "logs");

            string dir;
            try
            {
                string configured = RegistrySettings.ReadString(LogPathRegistryValue, "");
                dir = string.IsNullOrWhiteSpace(configured) ? defaultDir : configured;
            }
            catch
            {
                dir = defaultDir;
            }

            return Path.Combine(dir, LogFileName);
        }

        /// <summary>
        /// Reads the "Verbosity" registry value using the same convention and vocabulary
        /// (ALL/DEBUG/INFO/WARN/ERROR/FATAL/OFF) as other X1 products' LoggingConfig. Defaults
        /// to DEBUG — not INFO — if unset/unrecognized, since that was this bridge's original
        /// always-verbose behavior; set Verbosity explicitly to quiet it down.
        /// </summary>
        private static Level ResolveVerbosity()
        {
            string raw;
            try
            {
                raw = RegistrySettings.ReadString(VerbosityRegistryValue, "");
            }
            catch
            {
                return Level.Debug;
            }

            switch ((raw ?? "").Trim().ToUpperInvariant())
            {
                case "ALL": return Level.All;
                case "DEBUG": return Level.Debug;
                case "INFO": return Level.Info;
                case "WARN": return Level.Warn;
                case "ERROR": return Level.Error;
                case "FATAL": return Level.Fatal;
                case "OFF": return Level.Off;
                default: return Level.Debug;
            }
        }

        /// <summary>
        /// Resets the log4net configuration.  For testing only.
        /// </summary>
        internal static void ResetForTesting()
        {
            lock (configureLock)
            {
                var hierarchy = (Hierarchy)LogManager.GetRepository();
                hierarchy.ResetConfiguration();
                configured = false;
            }
        }
    }
}
