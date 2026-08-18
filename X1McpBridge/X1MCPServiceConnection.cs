// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System;
using System.Collections.Generic;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Threading.Tasks;
using X1.Service;

namespace X1.McpBridge
{
    [CallbackBehavior(ConcurrencyMode = ConcurrencyMode.Multiple, UseSynchronizationContext = false)]
    internal sealed class X1MCPServiceCallbacks : IX1MCPServiceCallbacks
    {
        // XS-1573: one-way OnGetDataSourcesInfoFinished carries no correlation id;
        // serialize with a FIFO queue matching the ExpectDataSourcesInfo / OnGetDataSourcesInfoFinished pair.
        private readonly object _sync = new object();
        private readonly Queue<TaskCompletionSource<ConfiguredDataSourceInfo[]>> _dataSourcesQueue =
            new Queue<TaskCompletionSource<ConfiguredDataSourceInfo[]>>();
        private readonly Action _onShutdown;

        // XS-1701: onShutdown is optional (default null) so existing direct-construction call
        // sites (X1ServiceContractWireTests, which only exercise OnGetDataSourcesInfoFinished)
        // keep compiling unchanged; only X1MCPServiceConnection's own constructor supplies a
        // real handler.
        public X1MCPServiceCallbacks(Action onShutdown = null)
        {
            _onShutdown = onShutdown;
        }

        public TaskCompletionSource<ConfiguredDataSourceInfo[]> ExpectDataSourcesInfo()
        {
            var tcs = new TaskCompletionSource<ConfiguredDataSourceInfo[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) _dataSourcesQueue.Enqueue(tcs);
            return tcs;
        }

        public void CancelDataSourcesWait(TaskCompletionSource<ConfiguredDataSourceInfo[]> tcs)
        {
            lock (_sync)
            {
                if (_dataSourcesQueue.Count == 0) return;
                var kept = new Queue<TaskCompletionSource<ConfiguredDataSourceInfo[]>>(_dataSourcesQueue.Count);
                foreach (var t in _dataSourcesQueue)
                    if (!ReferenceEquals(t, tcs)) kept.Enqueue(t);
                _dataSourcesQueue.Clear();
                foreach (var t in kept) _dataSourcesQueue.Enqueue(t);
            }
        }

        public void OnGetDataSourcesInfoFinished(ConfiguredDataSourceInfo[] dataSourcesInfo)
        {
            TaskCompletionSource<ConfiguredDataSourceInfo[]> tcs;
            lock (_sync)
            {
                if (_dataSourcesQueue.Count == 0) return;
                tcs = _dataSourcesQueue.Dequeue();
            }
            tcs.TrySetResult(dataSourcesInfo ?? new ConfiguredDataSourceInfo[0]);
        }

        // XS-1698/XS-1701: X1ServiceHost -> connector notification that it is shutting down
        // gracefully. One-way, no correlation id needed - just forward to whoever constructed us.
        public void OnShutdown() => _onShutdown?.Invoke();
    }

    /// <summary>
    /// XS-1671: X1MCPService.Connect() returns the literal string "Unlicensed" (instead of a
    /// version string) when the account isn't entitled to MCP connector use
    /// (LicenseManager.Instance.IsPaidPluginEnabled(Plugin.MCP) is false). This exception makes
    /// that a distinct, catchable condition instead of silently falling through as a normal
    /// connected session.
    /// </summary>
    internal sealed class X1McpUnlicensedException : Exception
    {
        public X1McpUnlicensedException()
            : base(BridgeConstants.NotLicensedForMcp)
        {
        }
    }

    /// <summary>
    /// Lightweight IX1MCPService client for status (index / host).
    /// XS-1609: dedicated MCP-only WCF service, served at its own endpoint address, so this
    /// connection no longer shares X1ServiceHost's single-client slot with the desktop UI's
    /// own IX1Service connection.
    /// </summary>
    internal sealed class X1MCPServiceConnection : IDisposable
    {
        private static readonly log4net.ILog Log = BridgeLogger.GetLogger(typeof(X1MCPServiceConnection));
        private readonly X1MCPServiceCallbacks _callbacks;
        private readonly object _connectLock = new object();
        private DuplexChannelFactory<IX1MCPService> _factory;
        private IX1MCPService _channel;
        private bool _connected;
        private volatile bool _unlicensed;
        // Not volatile - bool? isn't a valid volatile field type. Guarded by _connectLock instead
        // (see IsFullSuiteLicensed/ResetChannel), which EnsureConnectedChannel already takes.
        private bool? _fullSuiteLicensed;
        private readonly object _dataSourcesInFlightLock = new object();
        private Task<ConfiguredDataSourceInfo[]> _dataSourcesInFlight;

        // XS-1701: onShutdown fires when X1 announces a clean shutdown (XS-1698's OnShutdown
        // callback). Optional so the existing parameterless construction (production, and every
        // test that doesn't care about shutdown handling) keeps working unchanged.
        public X1MCPServiceConnection(Action onShutdown = null)
        {
            _callbacks = new X1MCPServiceCallbacks(onShutdown);
        }

        /// <summary>
        /// True once Connect() has come back with the "Unlicensed" sentinel. Cheap check with no
        /// WCF round-trip — callers can short-circuit tool calls immediately instead of each one
        /// independently timing out against a session that will never work.
        /// </summary>
        public bool IsUnlicensed => _unlicensed;

        public IX1MCPService GetChannel()
        {
            // If the channel has faulted or been closed, tear it down so we reconnect cleanly.
            if (_channel is ICommunicationObject existing &&
                (existing.State == CommunicationState.Faulted ||
                 existing.State == CommunicationState.Closed))
            {
                existing.Abort();
                _channel = null;
                try { _factory?.Abort(); } catch { }
                _factory = null;
                // The old channel is gone, so the "Connect" call we made against it no longer
                // holds — EnsureConnected must re-issue Connect() on the freshly created channel.
                _connected = false;
            }

            if (_channel != null)
                return _channel;

            Binding binding;
            EndpointAddress address;
            string userName = Environment.UserName;
            string remoteHost = Environment.GetEnvironmentVariable("X1_MCP_SERVICE_HOST") ?? "";

            if (!string.IsNullOrEmpty(remoteHost))
            {
                binding = X1McpWcfBindingFactory.CreateTcpBinding(useEncryption: true, usePortSharing: false);
                string endpoint = X1MCPWcfUtils.MCPServiceEndpointName(userName, remoteHost);
                address = X1McpWcfBindingFactory.CreateEndpointAddressForCurrentUser(endpoint);
            }
            else if (RegistrySettings.ReadInteger("UseNetTcpBinding", 0) == 1)
            {
                int port = RegistrySettings.ReadInteger("SearchManagerNetTcpBindingPort", 0);
                if (port == 0)
                    throw new InvalidOperationException("Registry UseNetTcpBinding=1 but SearchManagerNetTcpBindingPort is not set.");
                binding = X1McpWcfBindingFactory.CreateTcpBinding(useEncryption: true, usePortSharing: false);
                string endpoint = X1MCPWcfUtils.MCPServiceEndpointName(userName, netTcpPort: port);
                address = new EndpointAddress(endpoint);
            }
            else
            {
                binding = X1McpWcfBindingFactory.CreateNamedPipeBinding(useEncryption: false);
                string endpoint = X1MCPWcfUtils.MCPServiceEndpointName(userName);
                address = new EndpointAddress(endpoint);
            }

            var ctx = new InstanceContext(_callbacks);
            _factory = new DuplexChannelFactory<IX1MCPService>(ctx, binding, address);
            _channel = _factory.CreateChannel();
            return _channel;
        }

        /// <summary>
        /// XS-1583-SESSION-LIFECYCLE: per guidance from Jogy/Dominik, IX1Service.Connect() has
        /// significant overhead and should be called once when the bridge starts up and reused
        /// for every subsequent call, rather than connect-per-request-disconnect. Idempotent —
        /// safe to call repeatedly; only re-issues Connect() if the channel isn't already
        /// connected (including after a fault/reconnect, via GetChannel()'s reset of
        /// <see cref="_connected"/>).
        /// </summary>
        public IX1MCPService EnsureConnectedChannel()
        {
            lock (_connectLock)
            {
                var ch = GetChannel();
                if (!_connected)
                {
                    string result = ch.Connect();
                    _connected = true;
                    _unlicensed = result == "Unlicensed";
                }
                if (_unlicensed)
                    throw new X1McpUnlicensedException();
                return ch;
            }
        }

        /// <summary>Explicit connect at bridge startup — see EnsureConnectedChannel.</summary>
        public void Connect() => EnsureConnectedChannel();

        /// <summary>
        /// Graceful disconnect at bridge shutdown. Best-effort: swallows failures since the
        /// service host may already be gone (e.g. it crashed) by the time we get here.
        /// </summary>
        public void Disconnect()
        {
            lock (_connectLock)
            {
                if (_connected && _channel != null)
                {
                    try { _channel.Disconnect(shutdown: false); }
                    catch (Exception ex) { Log.Debug("Disconnect: " + ex.Message); }
                }
                _connected = false;
            }
            Dispose();
        }

        /// <summary>
        /// XS-1678: true if this connection is entitled to the full data-source suite; false means
        /// it's restricted to Files-only. Cached for the connection's lifetime (reset alongside
        /// <see cref="_connected"/> in <see cref="ResetChannel"/>) rather than re-checked every
        /// call, since the tier can't change mid-connection.
        ///
        /// Fails CLOSED (false) if the RPC itself fails - the opposite of GetDataSourcesInfoAsync/
        /// GetSchemaFieldsAsync's "fail open with an empty result" convention, deliberately: those
        /// failures are cosmetic, but failing open here would let an arbitrary-file extract/export
        /// call proceed and hang for its full timeout on a transient hiccup, reproducing exactly
        /// the bad UX XS-1678 exists to fix. A cached false self-heals on the next reconnect.
        /// </summary>
        public bool IsFullSuiteLicensed()
        {
            lock (_connectLock)
            {
                if (_fullSuiteLicensed.HasValue)
                    return _fullSuiteLicensed.Value;

                bool result;
                try
                {
                    result = EnsureConnectedChannel().IsLicensed();
                }
                catch (X1McpUnlicensedException)
                {
                    throw; // whole-plugin gate (XS-1671), not the tier gate - let it propagate as-is.
                }
                catch (Exception ex)
                {
                    Log.Warn("IsFullSuiteLicensed: IsLicensed() failed, treating as files-only tier: " + ex.Message);
                    return false;
                }
                _fullSuiteLicensed = result;
                return result;
            }
        }

        public string ConnectAndGetHostStatus()
        {
            var ch = EnsureConnectedChannel();
            return ch.GetX1ServiceHostStatus();
        }

        /// <summary>
        /// XS-1685: reports which MCP client is connected, for the MCP Options tab to display.
        /// One-way and best-effort - callers (McpServer.HandleInitialize) already wrap this in
        /// their own try/catch, but this method also never assumes the channel is already up.
        /// </summary>
        public void ReportClientInfo(string name, string version)
        {
            var ch = EnsureConnectedChannel();
            ch.ReportClientInfo(name, version);
        }

        /// <summary>
        /// Forcibly tears down the current channel/factory so the next GetChannel() call
        /// reconnects from scratch, and clears _connected so EnsureConnectedChannel re-issues
        /// Connect() on the fresh channel.
        ///
        /// GetChannel()'s own Faulted/Closed check is blind to the failure mode this exists for:
        /// a one-way call whose callback never arrives (GetDataSourcesInfoAsync) or a synchronous
        /// call that hangs (GetSchemaFieldsAsync) doesn't necessarily transition WCF's channel
        /// state to Faulted - it just silently stops delivering results, so .State keeps reading
        /// "Opened" even though the channel is effectively dead. Without an explicit reset here,
        /// every subsequent call hits the identical dead channel until some unrelated later event
        /// happens to fault it for real - observed in production logs recovering only after
        /// several minutes, by luck rather than design. A client-side timeout or a thrown
        /// exception on either call is itself strong enough evidence of a dead channel to reset
        /// proactively, at the cost of one extra reconnect on the (rare, and no worse than the
        /// empty-result outcome already returned) case where it was merely transient slowness.
        /// </summary>
        internal void ResetChannel()
        {
            if (_channel is ICommunicationObject co)
            {
                try { co.Abort(); }
                catch { /* already dead - that's the point */ }
            }
            _channel = null;
            try { _factory?.Abort(); }
            catch { /* ignore */ }
            _factory = null;
            _connected = false;
            // XS-1678: re-check tier on the next IsFullSuiteLicensed() call after a forced
            // reconnect rather than trusting a value cached against the now-abandoned channel.
            _fullSuiteLicensed = null;
        }

        /// <summary>
        /// XS-1573: fetch the configured data sources (accounts, indexed-item counts,
        /// last-scan timestamp, isScanning flag). Returns an empty array on timeout —
        /// never throws, except XS-1671's <see cref="X1McpUnlicensedException"/>, which callers
        /// should check for via <see cref="IsUnlicensed"/> before calling this at all (see
        /// McpServer.CallToolInner's centralized check).
        ///
        /// XS-1672 testing found three independent, uncoordinated callers of this method
        /// (McpServer's x1_list_sources handler, TableSchemaResolver, ColumnNameResolver — each
        /// building/refreshing its own cache) routinely overlap in practice (e.g. all three fire
        /// during bridge startup). GetDataSourcesInfo/OnGetDataSourcesInfoFinished is a one-way
        /// request paired with a single-slot FIFO callback queue (see X1MCPServiceCallbacks) with
        /// no per-request correlation id — it was never designed to have more than one outstanding
        /// call at a time. Concurrent calls were also observed tripping a server-side race in
        /// X1MCPServerAPICalls' shared per-day stats file (concurrent writers hit
        /// "the process cannot access the file... being used by another process"), which throws
        /// before the server ever reaches OnGetDataSourcesInfoFinished — so the request is silently
        /// dropped and every caller hangs to its own timeout. Coalescing onto a single in-flight
        /// task, rather than issuing one WCF call per caller, avoids both the wrong-answer risk on
        /// the FIFO queue and the concurrent-write collision on the server.
        /// </summary>
        public Task<ConfiguredDataSourceInfo[]> GetDataSourcesInfoAsync(int timeoutMs)
        {
            lock (_dataSourcesInFlightLock)
            {
                if (_dataSourcesInFlight != null && !_dataSourcesInFlight.IsCompleted)
                    return _dataSourcesInFlight;

                var task = GetDataSourcesInfoCoreAsync(timeoutMs);
                _dataSourcesInFlight = task;
                // Free the slot once this request finishes, but only if nobody has since taken
                // it over (can't happen today since replacement only ever happens under this
                // same lock above, but the reference check keeps this correct if that changes).
                task.ContinueWith(_ =>
                {
                    lock (_dataSourcesInFlightLock)
                    {
                        if (ReferenceEquals(_dataSourcesInFlight, task))
                            _dataSourcesInFlight = null;
                    }
                }, TaskContinuationOptions.ExecuteSynchronously);
                return task;
            }
        }

        private async Task<ConfiguredDataSourceInfo[]> GetDataSourcesInfoCoreAsync(int timeoutMs)
        {
            IX1MCPService ch;
            try
            {
                ch = EnsureConnectedChannel();
            }
            catch (X1McpUnlicensedException)
            {
                // Not a transport fault - don't tear down the channel for a business-rule state.
                throw;
            }
            catch
            {
                ResetChannel();
                return new ConfiguredDataSourceInfo[0];
            }

            var tcs = _callbacks.ExpectDataSourcesInfo();
            try { ch.GetDataSourcesInfo(); }
            catch
            {
                _callbacks.CancelDataSourcesWait(tcs);
                ResetChannel();
                return new ConfiguredDataSourceInfo[0];
            }
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed == tcs.Task && tcs.Task.Status == TaskStatus.RanToCompletion)
                return tcs.Task.Result;
            _callbacks.CancelDataSourcesWait(tcs);
            Log.Warn("GetDataSourcesInfoAsync: timed out after " + timeoutMs + "ms waiting for OnGetDataSourcesInfoFinished — resetting channel");
            ResetChannel();
            return new ConfiguredDataSourceInfo[0];
        }

        /// <summary>
        /// Lists the field/column definitions for a table (or PST sub-schema, e.g. PSTEmail).
        /// Unlike GetDataSourcesInfo, GetSchemaFields is a synchronous (non-oneway) call with
        /// no callback — it blocks until the WCF response arrives, so we just guard it with a
        /// Task.Run + timeout race rather than the TCS/callback pattern used elsewhere in this
        /// file. Returns an empty array on timeout or error — never throws, except XS-1671's
        /// <see cref="X1McpUnlicensedException"/> (see <see cref="GetDataSourcesInfoAsync"/>).
        /// </summary>
        public async Task<X1FieldInfo[]> GetSchemaFieldsAsync(string table, int timeoutMs)
        {
            IX1MCPService ch;
            try
            {
                ch = EnsureConnectedChannel();
            }
            catch (X1McpUnlicensedException)
            {
                // Not a transport fault - don't tear down the channel for a business-rule state.
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("GetSchemaFieldsAsync: connect failed for table=" + table + ": " + ex.Message);
                ResetChannel();
                return new X1FieldInfo[0];
            }

            var work = Task.Run(() =>
            {
                try
                {
                    var result = ch.GetSchemaFields(table);
                    if (result == null)
                        Log.Debug("GetSchemaFieldsAsync: service returned null for table=" + table + " (no scanner schema matched that name)");
                    return result ?? new X1FieldInfo[0];
                }
                catch (Exception ex)
                {
                    Log.Warn("GetSchemaFieldsAsync: GetSchemaFields threw for table=" + table + ": " + ex);
                    ResetChannel();
                    return new X1FieldInfo[0];
                }
            });
            var completed = await Task.WhenAny(work, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed != work)
            {
                Log.Warn("GetSchemaFieldsAsync: timed out after " + timeoutMs + "ms for table=" + table + " — resetting channel");
                ResetChannel();
                return new X1FieldInfo[0];
            }
            return await work.ConfigureAwait(false);
        }

        public void Dispose()
        {
            _connected = false;
            try
            {
                if (_channel is ICommunicationObject co)
                {
                    try
                    {
                        co.Close(TimeSpan.FromSeconds(2));
                    }
                    catch
                    {
                        co.Abort();
                    }
                }
            }
            catch
            {
                // ignore
            }

            _channel = null;
            try
            {
                _factory?.Close(TimeSpan.FromSeconds(2));
            }
            catch
            {
                _factory?.Abort();
            }
            _factory = null;
        }
    }
}
