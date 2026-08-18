// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using log4net;
using X1.Service;

namespace X1.McpBridge
{
    /// <summary>
    /// Resolves a caller-supplied column name (internal field name OR display name) to the display
    /// name X1's AddTermsByName actually indexes filter/sort/displayFields against, per table.
    ///
    /// Replaces FilterMapper's old ~20-entry static InternalToDisplayName dictionary, which was
    /// necessarily incomplete: any field not hand-added to it silently matched zero results instead
    /// of erroring (e.g. "date_received" was missing, breaking every "emails from the last N hours"
    /// query). This cache is built from the live schema (the same data x1_get_schema_fields
    /// surfaces), so it's complete and self-correcting rather than hand-maintained.
    /// </summary>
    internal sealed class ColumnNameResolver
    {
        private static readonly ILog Log = BridgeLogger.GetLogger(typeof(ColumnNameResolver));
        private readonly X1MCPServiceConnection _connection;

        // table -> (internal name or display name, case-insensitive) -> canonical display name
        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _byTable =
            new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // table -> (internal name or display name, case-insensitive) -> canonical INTERNAL name.
        // Built alongside _byTable from the same schema fetch: sort/displayFields index by internal
        // name (X1's ItemList), the opposite convention from filters (AddTermsByName, display name).
        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _byTableInternal =
            new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public ColumnNameResolver(X1MCPServiceConnection connection)
        {
            _connection = connection;
        }

        /// <summary>
        /// Kicks off the initial cluster-wide cache build in the background; never blocks the
        /// caller. Individual tables missing from this initial pass (e.g. a scanner not yet
        /// finished registering at startup) are picked up lazily by ResolveAsync's own refresh-on-miss.
        /// </summary>
        public void StartBackgroundBuild()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var sources = await _connection.GetDataSourcesInfoAsync(timeoutMs: 15000).ConfigureAwait(false);
                    var tables = sources
                        .SelectMany(s => s.schemas ?? new string[0])
                        .Where(t => !string.IsNullOrEmpty(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase);

                    foreach (var table in tables)
                        await BuildTableAsync(table).ConfigureAwait(false);

                    Log.Info("ColumnNameResolver: built column-name cache for " + _byTable.Count + " table(s).");
                }
                catch (Exception ex)
                {
                    Log.Warn("ColumnNameResolver: initial cache build failed (tables will still resolve lazily on first use): " + ex.Message);
                }
            });
        }

        private async Task BuildTableAsync(string table)
        {
            if (string.IsNullOrEmpty(table))
                return;

            X1FieldInfo[] fields;
            try
            {
                fields = await _connection.GetSchemaFieldsAsync(table, timeoutMs: 15000).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Matches GetSchemaFieldsAsync's own "never throws" contract - a schema refresh
                // failing (host unreachable, etc.) must not blow up the whole search. Deliberately
                // returning here without touching _byTable[table] means a table that was already
                // cached keeps serving its last-known-good map through a transient outage, rather
                // than being wiped and forced to "unresolved" - only a table that was never
                // successfully built has nothing to fall back on.
                Log.Warn("ColumnNameResolver: schema refresh failed for table '" + table + "' - retaining last cached schema, if any: " + ex.Message);
                return;
            }
            if (fields.Length == 0)
                return; // ditto: an empty response leaves any existing cached map for this table untouched

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var internalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fields)
            {
                if (!string.IsNullOrEmpty(f.DisplayName))
                {
                    map[f.DisplayName] = f.DisplayName; // identity: caller already used the correct display name
                    if (!string.IsNullOrEmpty(f.Name))
                        map[f.Name] = f.DisplayName;     // internal name -> display name
                }

                if (string.IsNullOrEmpty(f.Name))
                    continue; // no internal name at all - nothing to map to for the reverse direction

                internalMap[f.Name] = f.Name;                  // identity: caller already used the internal name
                if (!string.IsNullOrEmpty(f.DisplayName))
                    internalMap[f.DisplayName] = f.Name;       // display name -> internal name
            }
            _byTable[table] = map;
            _byTableInternal[table] = internalMap;
        }

        /// <summary>
        /// Resolves <paramref name="column"/> (internal or display name) against <paramref name="table"/>.
        /// On a cache miss, refreshes just that one table once (handles a table or custom field added
        /// since the last build) before giving up. Returns null if still unresolved - callers decide
        /// the fallback (FilterMapper substitutes a general content-field search for filter terms).
        /// </summary>
        public async Task<string> ResolveAsync(string table, string column)
        {
            if (string.IsNullOrEmpty(column))
                return column;

            if (TryLookup(table, column, out var resolved))
                return resolved;

            await BuildTableAsync(table).ConfigureAwait(false);

            if (TryLookup(table, column, out resolved))
                return resolved;

            Log.Warn("ColumnNameResolver: column '" + column + "' not found on table '" + table + "' even after a schema refresh.");
            return null;
        }

        private bool TryLookup(string table, string column, out string resolved)
        {
            resolved = null;
            return _byTable.TryGetValue(table ?? "", out var map) && map.TryGetValue(column, out resolved);
        }

        /// <summary>
        /// Resolves <paramref name="column"/> (internal or display name) against <paramref name="table"/>
        /// to its canonical INTERNAL name - the convention sort/displayFields need (the opposite of
        /// ResolveAsync's display-name output, used by filters). Same cache-miss/refresh-once/give-up
        /// contract as ResolveAsync. Returns null if still unresolved - callers decide the fallback
        /// (FilterMapper falls back to the caller's raw string for sort/displayFields, since there is
        /// no safe universal substitute the way filters have "" for a general content search).
        /// </summary>
        public async Task<string> ResolveInternalAsync(string table, string column)
        {
            if (string.IsNullOrEmpty(column))
                return column;

            if (TryLookupInternal(table, column, out var resolved))
                return resolved;

            await BuildTableAsync(table).ConfigureAwait(false);

            if (TryLookupInternal(table, column, out resolved))
                return resolved;

            Log.Warn("ColumnNameResolver: column '" + column + "' not found (internal-name lookup) on table '" + table + "' even after a schema refresh.");
            return null;
        }

        private bool TryLookupInternal(string table, string column, out string resolved)
        {
            resolved = null;
            return _byTableInternal.TryGetValue(table ?? "", out var map) && map.TryGetValue(column, out resolved);
        }

        /// <summary>
        /// Test-only: seeds a table's resolved-column map directly, bypassing the WCF schema fetch
        /// entirely. As long as a test seeds every column it resolves, the connection passed to the
        /// constructor (which can be null in tests) is never touched. Mirrors BuildTableAsync's own
        /// behavior by adding each display name as an identity entry too, so a test doesn't need to
        /// separately seed "Received" -> "Received" alongside "date_received" -> "Received". Also
        /// derives the reverse (internal-name) map from the same seed dictionary, so one call keeps
        /// both directions consistent, exactly like production BuildTableAsync does from one fetch.
        /// </summary>
        internal void SeedForTest(string table, Dictionary<string, string> nameToDisplayName)
        {
            var map = new Dictionary<string, string>(nameToDisplayName, StringComparer.OrdinalIgnoreCase);
            foreach (var displayName in nameToDisplayName.Values)
                map[displayName] = displayName;
            _byTable[table ?? ""] = map;

            var internalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in nameToDisplayName)
            {
                internalMap[kv.Key] = kv.Key;       // internal name -> itself
                internalMap[kv.Value] = kv.Key;     // display name -> internal name
            }
            _byTableInternal[table ?? ""] = internalMap;
        }
    }
}
