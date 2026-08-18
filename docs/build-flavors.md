# Build flavors: Lean and Full

## Why

The connector shipped two exes: `X1McpBridge.exe` (net4.8, 240 KB) and `X1McpGraphQL.exe` (net10,
self-contained single-file, **168.5 MB**). The daemon was ~88% of the customer payload, and it exists
purely as a *transport* — a fan-in relay so that many Claude sessions share one WCF connection. On top
of that it served a GraphQL API and Nitro IDE that nothing in production ever called.

So customers were downloading ~160 MB for a relay plus a developer tool.

## What changed

Two flavors from one source tree, selected at build time by a single switch.

| | **Lean** (default) | **Full** (`--full`) |
|---|---|---|
| GraphQL API + Nitro IDE | no | yes, `http://localhost:5250/graphql` |
| .NET 10 dependency | **none** | yes (inside the self-contained exe) |
| Shared relay | `X1McpBridge.exe --host` | `X1McpGraphQL.exe` |
| Processes at rest | 1 | 2 (daemon + its bridge child) |
| `installer\` | **28.2 MB** | 252 MB |
| `x1-search.plugin` | **7.1 MB** | 69.8 MB |
| Install directory | **21.0 MB** | 181.7 MB |
| Build needs | MSBuild + PowerShell | also the .NET 10 SDK |

All figures measured at one commit. The Lean install directory is 21 MB of which
`ChilkatDotNet48.dll` alone is 14.1 MB — the only size lever left, and out of scope here.

## One switch, because it is one decision

"No GraphQL" and "no .NET 10" are not two knobs. Every GraphQL line in the tree lives in the daemon
project, and the daemon *is* the .NET 10 dependency. Compiling GraphQL out of the daemon and still
shipping it saves only ~31 MB of ~161 MB (19%) — the ASP.NET Core runtime is the bulk. The whole win
comes from not shipping the daemon at all.

Do not "improve" the switch into two flags.

```bat
build-installer.bat            :: Lean  — the customer package
build-installer.bat --full     :: Full  — internal/dev, with GraphQL + Nitro
```

Lean is the default because **the default is what ships**. Every existing invocation — CI, muscle
memory, a bare `build-installer.bat` — silently becomes the customer build, which is the safe
direction for an accident. The inverse means one forgotten flag ships a 252 MB customer package and
nothing downstream catches it. The counter-risk (a developer gets Lean by accident and thinks GraphQL
broke) is mitigated by printing the flavor in five places: the build banner, the Lean payload
assertion, `build-info.json`, `install.ps1`'s header and final banner, and `x1_version`.

## How Lean replaces the relay

`X1Mcp/X1McpBridge/HostMode.cs` serves the *same* contract on the *same* port — `GET /health` and
`POST /graphql/mcp` (MCP JSON-RPC), via `HttpListener` — dispatching into
`McpServer.ProcessMessage`, the same entry point `RunStdio` uses. So `ProxyMode`,
`cowork-plugin/.mcp.json`, and every already-registered MCP entry are unchanged, and the two packages
are drop-in interchangeable in either direction.

Unlike the daemon, this process *is* the bridge — one fewer process, one fewer hop, and the relay and
the WCF owner become the same thing.

### The invariant that must not break

`X1ServiceHost.exe` crashes and silently cold-restarts when 2+ clients race `Connect()`/session
teardown (`X1ConcurrencyWorkaround.cs`). So: **exactly one WCF-owning process, ever, launched
detached.** Detached because Cowork tears down per-task sandboxes and a relay owned by a session's
process tree dies with it.

Three mechanisms, in order of authority:

1. **The port bind.** HTTP.SYS and Kestrel *do* arbitrate against each other in both directions on
   the same loopback port — verified, not assumed — so only one relay of either flavor can hold 5250.
2. **A shared named mutex, `X1McpGraphQL-SingleInstance`.** Both flavors take the same name *on
   purpose*, so a Lean host and a Full daemon are mutually exclusive rather than merely each-unique.
   The name reads wrong in a Lean install and is still correct: its job is cross-flavor exclusion,
   not self-description. **Do not rename it.**
3. **The `/health` identity handshake** — `RelayHealth.Decide`. A bare 200 OK cannot distinguish the
   right relay from a leftover of another version, another flavor, or another Windows user, and
   adopting the wrong one is silent.

### Serialization — the subtle one

`X1ConcurrencyWorkaround.RunSerialized` wraps only `CallTool`. `resources/read` → `ReadResource` →
`ConnectAndGetHostStatus()` reaches WCF *outside* it. What actually prevents overlapping calls in
stdio mode is `RunStdio`'s single-threaded read loop — and an `HttpListener` has no such loop.

So `HostMode` funnels every request through one `BlockingCollection` and one dedicated thread. A
single thread rather than a semaphore, because it also keeps thread *identity* stable: the duplex WCF
callbacks and the native deps (`PLUSManaged`, `ChilkatDotNet48`) have unverified thread affinity, and
one long-lived thread is closer to stdio's proven behaviour.

It must **not** be `RunSerialized` as an outer gate — that is a non-reentrant `SemaphoreSlim(1,1)`
and would deadlock as soon as `CallTool` re-entered it. `HostModeDispatchTests` pins both properties.

### Two silent traps in the wire contract

`ProxyMode` never inspects the HTTP status code, and treats an `event-stream` content type as SSE:

* **Every response on every path must be JSON.** An `HttpListener` default HTML 404 reaches
  `JObject.Parse`, throws, and is reported to the user as "the relay is unavailable" after a
  pointless relaunch.
* **`Content-Type` must never contain `event-stream`.** Otherwise `ParseSseLastMessage` finds no
  `data:` line in plain JSON, returns null, nothing is written back, and **the client hangs forever**
  on a reply that was actually produced.

Both are covered by real HTTP round trips in `HostModeTests`.

## Full → Lean migration

Installing Lean over Full leaves residue that will silently defeat it. The logon task
`X1McpGraphQL-SharedDaemon` restarts the 160 MB daemon at every logon; it binds 5250 before any Lean
host can; and every session is then served by the *old* daemon driving the *old* bridge from the *old*
directory — silently, because it answers normally.

`install.ps1` handles this, and the ordering is load-bearing:

1. Unregister the task **before** killing the daemon — it has `-StartWhenAvailable` and an at-logon
   trigger, so killing the process while its owner is registered invites Task Scheduler to relaunch
   it mid-install.
2. Stop the relay (unconditionally, in both flavors — this is the migration hook).
3. Verify port 5250 is actually free, and **fail the install** if it isn't.
4. Copy binaries, then delete residue **by explicit name**: `X1McpGraphQL.exe`, `appsettings*.json`,
   `schema.graphql`, `web.config`. Never by set-difference — that would also take
   `x1mcp_stats.json`, which belongs to the *bridge's* CostTracker, i.e. the customer's accumulated
   cost-savings statistics.
5. Report reclaimed bytes, start the Lean relay, and probe `/health` to prove what is now serving.

**The non-elevated case:** the task can only have been created from an elevated shell, so a
non-elevated upgrade cannot remove it. If it can't, the installer **does not delete the daemon exe** —
a registered task pointing at a deleted file fails on every logon forever, which is worse than an
obsolete-but-working task, and leaving the binary keeps the machine in a state a later elevated run
can finish. Everything else still proceeds.

**The dangerous case it can only warn about:** the Cowork plugin is installed and updated
independently, and nothing owns its copy. A Lean *standalone* upgrade correctly leaves a
plugin-resident daemon alone — but that daemon's `--proxy` will claim 5250 on the next session, and
the Lean host never serves. **The Lean plugin and the Lean standalone installer should ship as one
release.** The installer detects and reports this rather than pretending otherwise.

## Diagnostic asymmetry worth knowing

A Lean relay uses `HttpListener`, i.e. HTTP.SYS, a kernel driver — so
`Get-NetTCPConnection -LocalPort 5250` reports its owner as **`System` (pid 4)**, never the bridge.
Only a Kestrel-based Full daemon is identifiable that way. Identify a Lean relay by asking it:

```powershell
(Invoke-WebRequest http://localhost:5250/health -UseBasicParsing).Content
```

## Lifecycle & upgrades (XS-1692)

The relay (either flavor) is deliberately kept running after every Claude session ends — see "The
invariant that must not break" above. The cost is that it holds its own binary and DLLs locked for
as long as it's resident, so an upgrade that tries to replace those files fails unless the
resident process is stopped first.

**The standalone installer already defends against this**, and has since it landed under XS-1651:
`install.ps1` stops any relay scoped to its own install directory before copying, verifies port
5250 is actually free afterwards (failing the install if not), copies the new binaries, then asks
the relay to shut down gracefully over `POST /shutdown` — draining in-flight work first, unlike
`Stop-Process` — restarts it from the new binary, and probes `/health` to confirm what's now
serving. See `install.ps1`'s own comments at each of those steps for the mechanics.

**The Cowork/marketplace plugin has no equivalent.** Its manifest
(`cowork-plugin/.claude-plugin/plugin.json`) is a bare descriptor with no pre-update/post-update
hook, and the plugin manager's update mechanism has no comparable stop-before-replace step. This is
already documented from the QA side — `docs/qa-plugin-install-workflow.md` Step 2 is a *manual*
"kill every connector process" instruction, done by a human before every test cycle, precisely
because nothing automated does it for this flavor's install path.

Two mitigations exist for the plugin gap, both partial:

1. **`X1McpConnectorIdleShutdown`** (`HostMode.cs`, `RegistrySettings.cs`) — the Lean host shuts
   itself down after a configurable period (default 3600s / 1 hour) with no dispatched request,
   via the same `/shutdown` path the installer uses. This bounds how long a resident relay can hold
   the lock after real use stops, but doesn't coordinate with the moment an update actually runs —
   an upgrade seconds after last use still hits the same failure. See `docs/UserManual.md` §4.6 for
   the customer-facing description and the registry value's exact semantics.
2. **Manual documentation** — `cowork-plugin/README.md` and `docs/UserManual.md` §3 tell a plugin
   user to quit Claude and stop `X1McpBridge.exe` themselves before updating.

The properly correct fix — the plugin manager running a pre-update stop step, mirroring what
`install.ps1` already does — needs a lifecycle hook Claude Code's plugin system doesn't currently
expose. That's a platform ask tracked outside this repo, not something XS-1692 can deliver alone;
see `docs/XS-1692-plan.md` for the full discussion and decision record.

## Files

| Concern | Where |
|---|---|
| Flavor switch, Lean payload assertion | `build-installer.bat` |
| GraphQL conditional compilation | `X1McpGraphQL/X1McpGraphQL.csproj` (`X1McpFlavor`), `Program.cs` (`#if X1MCP_GRAPHQL`) |
| Lean relay | `X1McpBridge/HostMode.cs`, `HostSingleInstanceGuard.cs` |
| Relay identity + adopt/evict decision | `X1McpBridge/RelayHealth.cs`, `ProxyMode.cs`, `X1McpGraphQL/RelayHealthBody.cs` |
| Flavor + launch-target resolution | `X1McpBridge/BridgeConfig.cs` (`GetRelayMode`, `GetRelayLaunchTarget`) |
| Machine-wide process scan | `X1McpBridge/RelayProcessScanner.cs` |
| Flavor probe, migration | `install.ps1` |
| Flavor stamp, dirty-check scope | `build-plugin.ps1`, `check-plugin-staleness.ps1` |
| Idle self-shutdown | `X1McpBridge/HostMode.cs` (`CheckIdle`), `X1McpBridge/RegistrySettings.cs` |
