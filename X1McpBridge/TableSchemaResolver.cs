// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using log4net;
using X1.Service;

namespace X1.McpBridge
{
    /// <summary>
    /// Resolves a caller-supplied table token to the one schema value x1_search's tables /
    /// x1_get_schema_fields' table parameter actually accepts (XS-1640).
    ///
    /// x1_list_sources reports three different identifiers for one source - the scanner's own
    /// name, a per-account displayName, and the account's schemas[] - without ever saying which
    /// one is safe to search with. Only a schemas[] value is. A caller who copies name or
    /// displayName (both reasonable things to copy from x1_list_sources' own output) gets an
    /// opaque failure today: x1_search fails generically, x1_get_schema_fields silently returns
    /// an empty field list. This resolver reconciles all three so any of them works transparently,
    /// or produces a descriptive error naming the bad/ambiguous token and listing valid schemas -
    /// never an opaque backend failure or silent empty result.
    /// </summary>
    internal sealed class TableSchemaResolver
    {
        private static readonly ILog Log = BridgeLogger.GetLogger(typeof(TableSchemaResolver));
        private readonly X1MCPServiceConnection _connection;

        private sealed class Snapshot
        {
            public readonly HashSet<string> ValidSchemas;
            public readonly Dictionary<string, string[]> TokenToSchemas;

            public Snapshot(HashSet<string> validSchemas, Dictionary<string, string[]> tokenToSchemas)
            {
                ValidSchemas = validSchemas;
                TokenToSchemas = tokenToSchemas;
            }
        }

        private static Snapshot Empty() => new Snapshot(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));

        // There is no per-token refresh primitive for sources the way there is per-table for
        // schema fields (GetDataSourcesInfo always returns the whole list), so the cache is one
        // atomically-swapped snapshot rather than ColumnNameResolver's per-table shards.
        private volatile Snapshot _snapshot = Empty();

        // XS-1662: overrides the files-only tier probe in unit tests, where there's no live service
        // connection to ask (see SeedForTest). Null in production, where IsFilesOnlyTier() consults
        // the real connection instead.
        private bool? _testFullSuiteLicensed;

        public TableSchemaResolver(X1MCPServiceConnection connection)
        {
            _connection = connection;
        }

        /// <summary>
        /// XS-1662: error-path helper. Returns true only when we can positively determine this
        /// connection is restricted to the files-only tier, so <see cref="ResolveOrThrowAsync"/>'s
        /// "unknown table" branch can explain it as a licensing limit (pointing to the landing page)
        /// rather than a schema typo. A null connection (unit tests / no live service) or a probe that
        /// throws is treated as NOT files-only, so we fall back to the generic "unknown table" message
        /// and never mislabel a genuine typo on a full-suite connection.
        /// </summary>
        private bool IsFilesOnlyTier()
        {
            if (_testFullSuiteLicensed.HasValue)
                return !_testFullSuiteLicensed.Value;
            if (_connection == null)
                return false;
            try { return !_connection.IsFullSuiteLicensed(); }
            catch { return false; }
        }

        public enum Kind { Identity, Resolved, Ambiguous, Unknown }

        public struct Result
        {
            public Kind Kind;
            public string Schema;       // set for Identity/Resolved
            public string[] Candidates; // set for Ambiguous
        }

        /// <summary>
        /// Kicks off the initial cache build in the background; never blocks the caller.
        /// Mirrors ColumnNameResolver.StartBackgroundBuild - a token missing from this initial
        /// pass is picked up lazily by ResolveAsync's own refresh-on-miss.
        /// </summary>
        public void StartBackgroundBuild()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RebuildAsync().ConfigureAwait(false);
                    Log.Info("TableSchemaResolver: built table-name cache for " + _snapshot.ValidSchemas.Count + " schema(s).");
                }
                catch (Exception ex)
                {
                    Log.Warn("TableSchemaResolver: initial cache build failed (tables will still resolve lazily on first use): " + ex.Message);
                }
            });
        }

        private async Task RebuildAsync()
        {
            if (_connection == null)
            {
                // No live connection at all (e.g. an in-process test that only ever calls
                // SeedForTest) - leave the existing snapshot untouched entirely, matching
                // ColumnNameResolver.BuildTableAsync's "can't fetch -> retain last-known-good
                // state" contract. Rebuilding to an empty snapshot here would silently wipe out
                // whatever SeedForTest already populated.
                return;
            }

            ConfiguredDataSourceInfo[] sources;
            try
            {
                sources = await _connection.GetDataSourcesInfoAsync(timeoutMs: 15000).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("TableSchemaResolver: source-info refresh failed - retaining last cached snapshot, if any: " + ex.Message);
                return;
            }

            var validSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tokenToSchemas = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in sources ?? new ConfiguredDataSourceInfo[0])
            {
                if (d.schemas == null || d.schemas.Length == 0)
                    continue;

                foreach (var schema in d.schemas)
                    if (!string.IsNullOrEmpty(schema))
                        validSchemas.Add(schema);

                // Index by the cleaned scanner name (same XS-1605 unmangling x1_list_sources
                // applies) so a caller who copy-pastes the name shown there matches this cache.
                string cleanName = DataSourceInfoMapper.CleanScannerName(d.scannerName, d.accountName);
                AddToken(tokenToSchemas, cleanName, d.schemas);

                // scannerDisplayName is per-account (e.g. "IMAP - Gmail" vs "IMAP - Yahoo") and can
                // legitimately differ from the scanner name - give it its own entry, not an alias.
                if (!string.IsNullOrEmpty(d.scannerDisplayName))
                    AddToken(tokenToSchemas, d.scannerDisplayName, d.schemas);
            }

            var finalTokenMap = tokenToSchemas.ToDictionary(
                kv => kv.Key,
                kv => (string[])kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);

            _snapshot = new Snapshot(validSchemas, finalTokenMap);
        }

        private static void AddToken(Dictionary<string, List<string>> map, string token, string[] schemas)
        {
            if (string.IsNullOrEmpty(token))
                return;
            if (!map.TryGetValue(token, out var list))
            {
                list = new List<string>();
                map[token] = list;
            }
            list.AddRange(schemas.Where(s => !string.IsNullOrEmpty(s)));
        }

        /// <summary>
        /// Resolves <paramref name="callerToken"/> against the live schema/source cache. On a
        /// total miss, refreshes the whole cache once (there is no per-token refresh primitive
        /// for sources) before giving up.
        ///
        /// Safety valve: if the cache has never successfully learned ANY real schema (the service
        /// host is unreachable, or this is a test process with no live connection), resolution is
        /// inconclusive - there is no way to distinguish "genuinely unknown table" from "we don't
        /// know anything yet". Trust the caller's token unchanged in that case rather than
        /// inventing a validation failure; the underlying call will surface its own natural error.
        /// </summary>
        public async Task<Result> ResolveAsync(string callerToken)
        {
            if (string.IsNullOrEmpty(callerToken))
                return new Result { Kind = Kind.Identity, Schema = callerToken };

            if (TryResolve(callerToken, out var result))
                return result;

            await RebuildAsync().ConfigureAwait(false);

            if (TryResolve(callerToken, out result))
                return result;

            if (_snapshot.ValidSchemas.Count == 0)
            {
                Log.Debug("TableSchemaResolver: no schema info known yet (service unreachable?) - trusting caller's table '" + callerToken + "' unchanged.");
                return new Result { Kind = Kind.Identity, Schema = callerToken };
            }

            Log.Warn("TableSchemaResolver: table '" + callerToken + "' did not match any known schema, scanner name, or display name even after a cache refresh.");
            return new Result { Kind = Kind.Unknown };
        }

        private bool TryResolve(string callerToken, out Result result)
        {
            var snap = _snapshot;
            if (snap.ValidSchemas.Contains(callerToken))
            {
                result = new Result { Kind = Kind.Identity, Schema = callerToken };
                return true;
            }
            if (snap.TokenToSchemas.TryGetValue(callerToken, out var schemas))
            {
                if (schemas.Length == 1)
                {
                    Log.Debug("TableSchemaResolver: resolved '" + callerToken + "' -> '" + schemas[0] + "'.");
                    result = new Result { Kind = Kind.Resolved, Schema = schemas[0] };
                }
                else
                {
                    result = new Result { Kind = Kind.Ambiguous, Candidates = schemas };
                }
                return true;
            }
            result = default(Result);
            return false;
        }

        /// <summary>
        /// Convenience wrapper for the common call-site pattern: resolve or throw a descriptive
        /// ArgumentException naming the bad/ambiguous token and listing valid schemas (XS-1640) -
        /// used by every table-taking tool so the error shape is identical everywhere.
        /// </summary>
        public async Task<string> ResolveOrThrowAsync(string callerToken)
        {
            var result = await ResolveAsync(callerToken).ConfigureAwait(false);
            switch (result.Kind)
            {
                case Kind.Identity:
                case Kind.Resolved:
                    return result.Schema;
                case Kind.Ambiguous:
                    throw new ArgumentException("table '" + callerToken + "' matches multiple schemas: " +
                        string.Join(", ", result.Candidates) + "; specify one of these directly in tables.");
                default:
                    // XS-1662: on a files-only license the non-Files sources aren't in metadata, so a
                    // "Teams"/"Email"/etc. token lands here as Unknown - the exact path QA observed
                    // returning a bare "unknown table 'Teams'; valid values: Files" schema error. Explain
                    // it as the licensing limit it actually is (same wording as the -1 session-gate
                    // exception) and route to the landing page. A genuinely-unknown token on a full-suite
                    // connection still gets the schema-error listing below.
                    if (IsFilesOnlyTier())
                        throw new ArgumentException(BridgeConstants.FilesOnlyTableRejection(callerToken));
                    throw new ArgumentException("unknown table '" + callerToken + "'; valid values: " +
                        string.Join(", ", _snapshot.ValidSchemas.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)) + ".");
            }
        }

        /// <summary>
        /// Test-only: seeds the resolver directly, bypassing the WCF source-info fetch entirely.
        /// </summary>
        internal void SeedForTest(Dictionary<string, string[]> tokenToSchemas, IEnumerable<string> validSchemas,
            bool? fullSuiteLicensed = null)
        {
            _testFullSuiteLicensed = fullSuiteLicensed;
            var validSet = new HashSet<string>(validSchemas ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var tokenMap = new Dictionary<string, string[]>(
                tokenToSchemas ?? new Dictionary<string, string[]>(), StringComparer.OrdinalIgnoreCase);
            _snapshot = new Snapshot(validSet, tokenMap);
        }
    }
}
