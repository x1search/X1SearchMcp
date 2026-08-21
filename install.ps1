# Copyright (c) 2026 X1 Discovery, Inc.
#
# Licensed under the MIT License (copyright only). See the LICENSE file in
# the repository root for the full license text.
#
# This license does not grant, and shall not be construed as granting, any
# patent rights. See the PATENTS file in the repository root.

#Requires -Version 5.1
<#
.SYNOPSIS
  Installs the X1 Search MCP Bridge for Claude Desktop, Claude Code, claude.ai, and the
  GitHub Copilot app.

.DESCRIPTION
  Copies binaries to the install directory, deploys a default x1mcp.config.json
  (preserving any existing one), and registers the MCP server in the config files
  for the selected client products.

  Registers each product's x1-search entry as "X1McpBridge.exe --proxy" rather than
  the bridge itself: every session now proxies through one shared X1McpGraphQL
  daemon (also installed here, and registered as a per-user logon scheduled task)
  instead of each spawning its own bridge process. This exists because X1ServiceHost
  crashes when 2+ client connections race Connect()/session teardown - one shared
  daemon makes that structurally impossible instead of merely unlikely.

  Target products:
    All       - Claude Desktop, Claude Code AND the GitHub Copilot app (default)
    Desktop   - Claude Desktop app only  (also enables claude.ai web via Desktop relay)
    Code      - Claude Code CLI / IDE extensions only  (~/.claude/settings.json)
    ClaudeAi  - Alias for Desktop (claude.ai web uses Claude Desktop as its local relay)
    Copilot   - GitHub Copilot desktop app / Copilot CLI only  (~/.copilot/mcp-config.json)

  Claude Desktop config : %APPDATA%\Claude\claude_desktop_config.json
  Claude Code config    : %USERPROFILE%\.claude\settings.json
  GitHub Copilot config : %USERPROFILE%\.copilot\mcp-config.json

.PARAMETER InstallDir
  Target folder for the bridge binaries.
  Default: %LOCALAPPDATA%\X1 Discovery\McpBridge

.PARAMETER Target
  Which client product(s) to configure.  All | Desktop | Code | ClaudeAi | Copilot
  Default: All

.PARAMETER SavedContentDir
  Persistent directory where output="save" previews are written.
  Leave empty to use the default: %USERPROFILE%\Documents\X1 Saved
  Set via Group Policy / Intune as env var X1_MCP_SAVED_CONTENT_DIR for fleet rollout.

.PARAMETER Uninstall
  Removes the x1-search entry from the configured product config(s) and
  optionally deletes the install directory.

.EXAMPLE
  .\install.ps1
  .\install.ps1 -Target Desktop
  .\install.ps1 -Target Code
  .\install.ps1 -Target Copilot
  .\install.ps1 -InstallDir "C:\Tools\X1McpBridge"
  .\install.ps1 -SavedContentDir "C:\Users\$env:USERNAME\OneDrive - Acme Corp\X1 Saves"
  .\install.ps1 -Uninstall
  .\install.ps1 -Uninstall -Target Code
  .\install.ps1 -Uninstall -Target Copilot
#>

param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "X1 Discovery\McpBridge"),
    [ValidateSet("All", "Desktop", "Code", "ClaudeAi", "Copilot")]
    [string]$Target = "All",
    [string]$SavedContentDir = "",
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Target flags  (ClaudeAi is treated as Desktop — same config file)
# ---------------------------------------------------------------------------

$doDesktop = $Target -in @("All", "Desktop", "ClaudeAi")
$doCode    = $Target -in @("All", "Code")
$doCopilot = $Target -in @("All", "Copilot")

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "  >> $msg" -ForegroundColor Cyan
}

function Write-OK([string]$msg) {
    Write-Host "     OK: $msg" -ForegroundColor Green
}

function Write-Warn([string]$msg) {
    Write-Host "     WARN: $msg" -ForegroundColor Yellow
}

function Write-Info([string]$msg) {
    Write-Host "     INFO: $msg" -ForegroundColor Gray
}

# Unregisters the Full flavor's shared-daemon logon task. Returns $true if the task is gone
# afterwards (including "was never there"), $false only if it still exists and could not be removed.
#
# Shared by the uninstall path and the Full -> Lean migration rather than duplicated, because the
# two need identical behaviour and the subtlety here is easy to get wrong twice: the task is
# registered from an elevated shell and needs elevation to unregister, so a blanket catch reports an
# access-denied as success and leaves a task that silently relaunches the daemon at next logon,
# pointing into a directory whose contents have changed underneath it. The return value matters -
# the migration must NOT delete the daemon exe if this fails, or the task fails on every logon
# forever.
function Remove-DaemonTask([string]$taskName) {
    $existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if (-not $existingTask) {
        Write-Info "Scheduled task '$taskName' not found (already removed, or never registered)."
        return $true
    }

    try {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
        Write-OK "Removed scheduled task '$taskName'."
        return $true
    }
    catch {
        Write-Warn "Could not remove scheduled task '$taskName': $($_.Exception.Message)"
        Write-Warn "  It is still registered, and will start the daemon again at your next logon."
        if ($_.Exception.Message -match "denied|Access") {
            Write-Warn "  Unregistering it needs an elevated PowerShell session - re-run this"
            Write-Warn "  installer as Administrator to finish removing it."
        }
        return $false
    }
}

# Recursively merge $patch into $base (PSCustomObject).
# Nested objects are merged; leaf values from $patch overwrite $base.
function Merge-Json($base, $patch) {
    foreach ($key in $patch.PSObject.Properties.Name) {
        $pv = $patch.$key
        if ($base.PSObject.Properties[$key] -and
            $base.$key -is [PSCustomObject] -and
            $pv -is [PSCustomObject]) {
            Merge-Json $base.$key $pv
        }
        else {
            $base | Add-Member -Force -NotePropertyName $key -NotePropertyValue $pv
        }
    }
}

# Read a JSON config file, or return an empty object if it doesn't exist.
function Read-JsonConfig([string]$path) {
    if (Test-Path $path) {
        return (Get-Content $path -Raw | ConvertFrom-Json)
    }
    return [PSCustomObject]@{}
}

# Write a PSCustomObject back to a JSON config file (UTF-8, no BOM).
# NOTE: PowerShell 5.1's Set-Content -Encoding UTF8 writes a BOM, which
# Claude Desktop's JSON parser rejects. Use WriteAllText with UTF8NoBOM instead.
function Write-JsonConfig([PSCustomObject]$cfg, [string]$path) {
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }
    $json = $cfg | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($path, $json, [System.Text.UTF8Encoding]::new($false))
}

# Ensure $cfg.permissions.allow contains every rule in $rules (merge, no duplicates).
# Returns the number of rules that were newly added.
function Add-AllowRules([PSCustomObject]$cfg, [string[]]$rules) {
    if (-not $cfg.PSObject.Properties["permissions"]) {
        $cfg | Add-Member -NotePropertyName "permissions" -NotePropertyValue ([PSCustomObject]@{})
    }
    if (-not $cfg.permissions.PSObject.Properties["allow"]) {
        $cfg.permissions | Add-Member -NotePropertyName "allow" -NotePropertyValue @()
    }
    $allow = [System.Collections.Generic.List[string]]::new()
    foreach ($r in @($cfg.permissions.allow)) { [void]$allow.Add([string]$r) }
    $added = 0
    foreach ($r in $rules) {
        if (-not $allow.Contains($r)) { [void]$allow.Add($r); $added++ }
    }
    # Force an array so a single entry doesn't serialize as a bare string.
    $cfg.permissions.allow = @($allow.ToArray())
    return $added
}

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

$scriptDir       = $PSScriptRoot
$exeName         = "X1McpBridge.exe"
$exePath         = Join-Path $InstallDir $exeName
$mcpConfigName   = "x1mcp.config.json"
$mcpConfigDest   = Join-Path $InstallDir $mcpConfigName

# Shared relay — every x1-search session (Desktop and Code alike) proxies through ONE relay instead
# of each spawning its own bridge. See ProxyMode.cs / the fan-in architecture plan for why:
# X1ServiceHost crashes when 2+ clients race Connect()/session teardown, so "exactly one bridge
# process, ever" is the actual point.
#
# Which relay depends on the package flavor (see $isFull below, set in Step 1):
#   Full — the bundled self-contained net10 X1McpGraphQL.exe, which spawns its own bridge child and
#          additionally serves the GraphQL API + Nitro IDE.
#   Lean — "X1McpBridge.exe --host" (HostMode.cs), which IS the bridge. No .NET 10 anywhere, which
#          is the whole reason the flavor exists: the daemon is ~160MB of a ~182MB payload.
# Both serve the same contract on the same port, so nothing else in this script - and no registered
# MCP entry - has to care which one it is.
$daemonExeName   = "X1McpGraphQL.exe"
$daemonExePath   = Join-Path $InstallDir $daemonExeName
$daemonTaskName  = "X1McpGraphQL-SharedDaemon"
$relayUrl        = "http://localhost:5250"

# Probed here rather than in Step 1 so the banner can state the flavor before anything else runs -
# every support conversation opens with "which one do you have", and the banner is the first thing in
# a pasted transcript. Step 1 re-reports it with the reasoning.
$srcDaemonExe    = Join-Path $scriptDir $daemonExeName
$isFull          = Test-Path $srcDaemonExe
$flavorName      = if ($isFull) { "Full" } else { "Lean" }

# Claude Desktop
$desktopCfgDir   = Join-Path $env:APPDATA "Claude"
$desktopCfgPath  = Join-Path $desktopCfgDir "claude_desktop_config.json"

# Claude Code  (~/.claude/settings.json)
$codeCfgDir      = Join-Path $env:USERPROFILE ".claude"
$codeCfgPath     = Join-Path $codeCfgDir "settings.json"

# GitHub Copilot. ONE directory serves both the Copilot desktop app and the Copilot CLI, so a
# single target covers both surfaces - there is deliberately no separate -Target CopilotCli.
#
# COPILOT_HOME is Copilot's own documented override for this directory. Honouring it matters more
# than it looks: on a machine that relocates the config dir, ignoring it would produce a
# perfectly successful-looking write to a directory nothing ever reads.
$copilotCfgDir    = if ($env:COPILOT_HOME) { $env:COPILOT_HOME } else { Join-Path $env:USERPROFILE ".copilot" }
$copilotCfgPath   = Join-Path $copilotCfgDir "mcp-config.json"
$copilotSettings  = Join-Path $copilotCfgDir "settings.json"
$copilotAppConfig = Join-Path $copilotCfgDir "config.json"

# /x1 skill — shipped with the connector, installed as a user skill for Claude Code and for
# GitHub Copilot. Source sits next to this script (skill\x1); when run from a staged installer
# the build step copies it alongside, so the same relative path works in both cases.
#
# Copilot loads ~/.copilot/skills/<name>/ natively, which is why the Copilot install copies the
# skill there rather than appending to the skillDirectories array in Copilot's settings.json:
# identical shape to the Claude Code install, no mutation of a user-owned settings array, and
# uninstall is one directory delete instead of a surgical edit to a list the user also hand-edits.
$skillSrcDir     = Join-Path $scriptDir "skill\x1"
$skillDestDir    = Join-Path $codeCfgDir "skills\x1"
$copilotSkillDir = Join-Path $copilotCfgDir "skills\x1"

# Read-only / preview x1-search tools to pre-approve in Claude Code so they stop prompting.
# x1_execute_action is intentionally NOT included - it opens files / launches the browser,
# so it keeps prompting for an explicit OK.
$x1AllowRules = @(
    "mcp__x1-search__x1_search",
    "mcp__x1-search__x1_list_sources",
    "mcp__x1-search__x1_list_actions",
    "mcp__x1-search__x1_get_metadata",
    "mcp__x1-search__x1_get_content",
    "mcp__x1-search__x1_generate_preview"
)

# ---------------------------------------------------------------------------
# Default x1mcp.config.json content
# ---------------------------------------------------------------------------

$defaultX1Config = @'
{
  "defaultTables": ["Files"],
  "autoPreviewTimeoutMs": 10000,
  "prefetchPreviewCount": 3,
  "savedContentDir": "",
  "sources": {
    "Files":        ["name", "path", "size", "modified", "created", "type", "extension", "x1tag", "comments"],
    "Outlook":      ["subject", "from", "to", "date", "date_received", "date_sent", "cc", "bcc", "att", "foldn", "foldp", "path", "size", "importance"],
    "Email":        ["subject", "from", "to", "date", "date_received", "date_sent", "cc", "bcc", "att", "foldn", "foldp", "path", "size", "importance"],
    "Calendar":     ["subject", "starttime", "endtime", "organizer", "location", "recurrence", "cat", "reqattendees", "optattendees", "foldn", "date"],
    "Contact":      ["firstname", "lastname", "email", "company", "workphone", "mobilephone", "jobtitle", "dept", "foldn"],
    "Note":         ["subject", "cat", "created", "modified", "size", "name", "foldn", "foldp"],
    "Task":         ["subject", "from", "to", "owner", "complete", "startdate", "enddate", "duedate", "cat", "name", "date"],
    "Gmail":        ["subject", "from", "to", "date", "date_sent", "cc", "bcc", "att", "labels", "size", "name", "path"],
    "Exchange":     ["subject", "from", "to", "date", "date_received", "date_sent", "cc", "bcc", "att", "foldn", "foldp", "size", "importance", "sender_name"],
    "MSMail":       ["subject", "from", "to", "date", "date_received", "date_sent", "cc", "bcc", "att", "foldn", "foldp", "size", "importance"],
    "MSCalendar":   ["subject", "startdate", "enddate", "organizer", "location", "calendar_name", "cat", "reqattendees", "optattendees", "date"],
    "OneDrive":     ["name", "path", "size", "created", "modified", "type", "created_by", "modified_by", "account_name", "version"],
    "SP365":        ["name", "path", "size", "created", "modified", "type", "title", "description", "site_name", "drive_name", "created_by", "modified_by"],
    "Sharepoint":   ["title", "path", "size", "modified", "type", "author", "description", "site_name", "list_name", "account_name"],
    "GDrive":       ["name", "path", "size", "created", "modified", "type", "created_by", "modified_by", "owned_by", "description", "account_name"],
    "Box":          ["name", "path", "size", "created", "modified", "type", "created_by", "modified_by", "owned_by", "description", "account_name"],
    "Dropbox":      ["name", "path", "size", "created", "modified", "type", "created_by", "modified_by", "account_name"],
    "Slack":        ["message_body", "sender", "sender_email", "conversation_name", "conversation_type", "member_names", "subject", "name", "created"],
    "Teams":        ["message_body", "sender", "sender_email", "channel_display_name", "team_display_name", "chat_topic", "chat_type", "member_names", "subject", "created"],
    "JIRA":         ["summary", "key", "issue_type", "priority", "status", "creator", "reporter", "assignee", "project_name", "created", "modified"],
    "Skype":        ["subject", "from", "to", "chat_name", "msg_type", "body", "created", "modified"],
    "PSTEmail":     ["subject", "from", "to", "date", "date_received", "date_sent", "cc", "bcc", "att", "foldn", "foldp", "size"],
    "PSTCalendar":  ["subject", "startdate", "enddate", "organizer", "recurrence", "reqattendees", "optattendees", "date"],
    "PSTContact":   ["firstname", "lastname", "displayname", "email", "company", "workphone", "mobilephone", "jobtitle", "dept"],
    "PSTNote":      ["subject", "cat", "created", "color"],
    "PSTTask":      ["subject", "duedate", "completeddate", "startdate", "priority", "status", "complete", "date"]
  }
}
'@

# ===========================================================================
# UNINSTALL
# ===========================================================================

if ($Uninstall) {
    Write-Host ""
    Write-Host "  X1 Search MCP Bridge - Uninstall" -ForegroundColor White
    Write-Host "  ===================================" -ForegroundColor White
    Write-Host "  Target: $Target"

    # --- Claude Desktop ---
    if ($doDesktop) {
        Write-Step "Removing x1-search from Claude Desktop config..."
        if (Test-Path $desktopCfgPath) {
            $cfg = Read-JsonConfig $desktopCfgPath
            if ($cfg.PSObject.Properties["mcpServers"] -and
                $cfg.mcpServers.PSObject.Properties["x1-search"]) {
                $cfg.mcpServers.PSObject.Properties.Remove("x1-search")
                Write-JsonConfig $cfg $desktopCfgPath
                Write-OK "Removed x1-search from $desktopCfgPath"
            }
            else {
                Write-Warn "x1-search not found in Claude Desktop config."
            }
        }
        else {
            Write-Warn "Claude Desktop config not found at $desktopCfgPath"
        }
    }

    # --- Claude Code ---
    if ($doCode) {
        Write-Step "Removing x1-search from Claude Code config..."
        if (Test-Path $codeCfgPath) {
            $cfg = Read-JsonConfig $codeCfgPath
            if ($cfg.PSObject.Properties["mcpServers"] -and
                $cfg.mcpServers.PSObject.Properties["x1-search"]) {
                $cfg.mcpServers.PSObject.Properties.Remove("x1-search")
                Write-JsonConfig $cfg $codeCfgPath
                Write-OK "Removed x1-search from $codeCfgPath"
            }
            else {
                Write-Warn "x1-search not found in Claude Code config."
            }
        }
        else {
            Write-Warn "Claude Code config not found at $codeCfgPath"
        }

        if (Test-Path $skillDestDir) {
            Remove-Item $skillDestDir -Recurse -Force
            Write-OK "Removed /x1 skill from $skillDestDir"
        }
    }

    # --- GitHub Copilot ---
    if ($doCopilot) {
        Write-Step "Removing x1-search from GitHub Copilot config..."
        if (Test-Path $copilotCfgPath) {
            $cfg = Read-JsonConfig $copilotCfgPath
            if ($cfg.PSObject.Properties["mcpServers"] -and
                $cfg.mcpServers.PSObject.Properties["x1-search"]) {
                $cfg.mcpServers.PSObject.Properties.Remove("x1-search")
                Write-JsonConfig $cfg $copilotCfgPath
                Write-OK "Removed x1-search from $copilotCfgPath"
            }
            else {
                Write-Warn "x1-search not found in GitHub Copilot config."
            }
        }
        else {
            Write-Warn "GitHub Copilot config not found at $copilotCfgPath"
        }

        if (Test-Path $copilotSkillDir) {
            Remove-Item $copilotSkillDir -Recurse -Force
            Write-OK "Removed /x1 skill from $copilotSkillDir"
        }
    }

    # --- Shared relay: stop it and remove the Full flavor's scheduled task ---
    Write-Step "Removing the shared relay..."

    # Removed unconditionally, in BOTH flavors, forever. The uninstaller a user reaches for is
    # whichever package they still have on disk, so a Lean uninstall must still clean up a task left
    # behind by an earlier Full install - otherwise it orphans a logon task pointing into a directory
    # that is about to be deleted. It costs nothing when the task was never there.
    Remove-DaemonTask $daemonTaskName | Out-Null

    # Stop the bridges as well as the daemon. Stopping only the daemon left bridge processes
    # holding DLLs in $InstallDir, so the delete below failed with access-denied. Bridges from
    # other installs are left alone - they lock their own files, not ours.
    $toStop = @(Get-Process -Name "X1McpGraphQL" -ErrorAction SilentlyContinue)
    $toStop += @(Get-Process -Name "X1McpBridge" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) })

    if ($toStop.Count -gt 0) {
        $toStop | Stop-Process -Force -ErrorAction SilentlyContinue
        # Stop-Process returns before Windows releases the handles.
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline) {
            if (@(Get-Process -Id $toStop.Id -ErrorAction SilentlyContinue).Count -eq 0) { break }
            Start-Sleep -Milliseconds 200
        }
        Write-OK "Stopped $($toStop.Count) running process(es) using '$InstallDir'."
    }

    # --- Optionally delete install directory ---
    if (Test-Path $InstallDir) {
        $answer = Read-Host "  Delete install directory '$InstallDir'? [y/N]"
        if ($answer -match '^[Yy]') {
            # Report the failure instead of dying on it: the rest of the uninstall has already
            # happened, and "delete this folder yourself" is a better outcome than a stack trace.
            try {
                Remove-Item $InstallDir -Recurse -Force -ErrorAction Stop
                Write-OK "Deleted $InstallDir"
            }
            catch {
                Write-Warn "Could not delete '$InstallDir': $($_.Exception.Message)"
                Write-Warn "  Something still holds a file there. Check for X1McpBridge/X1McpGraphQL"
                Write-Warn "  processes, then delete the folder manually."
            }
        }
        else {
            Write-Warn "Install directory kept. You can delete it manually."
        }
    }

    Write-Host ""
    Write-Host "  Uninstall complete." -ForegroundColor Green
    if ($doDesktop) { Write-Host "  Restart Claude Desktop for the change to take effect." }
    if ($doCode)    { Write-Host "  Restart Claude Code for the change to take effect." }
    if ($doCopilot) { Write-Host "  Restart GitHub Copilot for the change to take effect." }
    Write-Host ""
    exit 0
}

# ===========================================================================
# INSTALL
# ===========================================================================

Write-Host ""
Write-Host "  X1 Search MCP Bridge - Installer" -ForegroundColor White
Write-Host "  ===================================" -ForegroundColor White
Write-Host "  Package flavor    : $flavorName$(if ($isFull) { ' (GraphQL API + net10 daemon)' } else { ' (no GraphQL API, no .NET 10)' })"
Write-Host "  Install directory : $InstallDir"
Write-Host "  Target products   : $Target"
if ($doDesktop) { Write-Host "  Desktop config    : $desktopCfgPath" }
if ($doCode)    { Write-Host "  Claude Code config: $codeCfgPath" }
if ($doCopilot) { Write-Host "  Copilot config    : $copilotCfgPath" }
Write-Host ""

# ---------------------------------------------------------------------------
# Step 1 — Verify source exe is present next to this script
# ---------------------------------------------------------------------------

Write-Step "Checking source files..."

$srcExe = Join-Path $scriptDir $exeName
if (-not (Test-Path $srcExe)) {
    Write-Host "  ERROR: $exeName not found alongside install.ps1" -ForegroundColor Red
    Write-Host "  Run build-installer.bat first to build and stage the package." -ForegroundColor Red
    exit 1
}
Write-OK "$exeName found in package."

# $isFull / $flavorName were probed near the top of this script (see there for why it is a probe and
# not a manifest or a -Flavor parameter). Reported here with the reasoning.
if ($isFull) {
    $flavorName = "Full"
    Write-OK "$daemonExeName found in package (Full flavor: GraphQL API + net10 daemon)."
} else {
    $flavorName = "Lean"
    Write-OK "No $daemonExeName in package (Lean flavor: no GraphQL API, no .NET 10 dependency)."
    Write-Host "         The shared relay will be `"$exeName --host`"." -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# Step 2 — Create install directory
# ---------------------------------------------------------------------------

Write-Step "Creating install directory..."
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir | Out-Null
    Write-OK "Created $InstallDir"
}
else {
    Write-OK "Directory already exists: $InstallDir"
}

# ---------------------------------------------------------------------------
# Step 3 — Copy binaries
#
# Stop the processes holding these binaries first. Windows locks a running .exe, so copying
# over one fails - and with $ErrorActionPreference = "Stop" that aborts the installer partway.
# Because Get-ChildItem yields X1McpBridge.exe before X1McpGraphQL.exe, the failure mode was
# specifically a NEW bridge left beside an OLD daemon, with the run dying before it registered
# the scheduled task. The uninstall path has always stopped the daemon; this path never did.
# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# Step 2b — Migrate a previous Full install to Lean
#
# Installing Lean over Full leaves behind a logon task pointing at a daemon this package no longer
# ships. Left alone, that task restarts the ~160MB net10 daemon at every logon; because the relay is
# shared on a fixed port, it binds 5250 before any Lean host can, and every session is then served by
# the OLD daemon driving the OLD bridge from the OLD directory - silently, since it answers normally.
# That is the failure this whole block exists to prevent, and the reason it must run before the first
# Lean install reaches any machine that has ever had Full.
#
# ORDER IS LOAD-BEARING: unregister the task BEFORE killing the daemon. The task is registered with
# -StartWhenAvailable and an at-logon trigger, so killing the process while its owner is still
# registered invites Task Scheduler to relaunch it mid-install.
# ---------------------------------------------------------------------------

$migrateFromFull = (-not $isFull) -and (
    (Test-Path $daemonExePath) -or
    (Get-ScheduledTask -TaskName $daemonTaskName -ErrorAction SilentlyContinue) -or
    (Get-Process -Name "X1McpGraphQL" -ErrorAction SilentlyContinue)
)
$daemonTaskRemoved = $true

if ($migrateFromFull) {
    Write-Step "Migrating a previous Full install to Lean..."
    $daemonTaskRemoved = Remove-DaemonTask $daemonTaskName

    if (-not $daemonTaskRemoved) {
        # Deliberately do NOT delete the daemon exe in this case. A registered task pointing at a
        # deleted file fails on every logon forever (0x2, file not found), which is strictly worse
        # than an obsolete-but-working task - and leaving the binary keeps the machine in a state a
        # later elevated run can finish cleanly. Everything else still proceeds: refusing to install
        # the new code because a task could not be removed would leave the user with no fix at all.
        Write-Warn "Leaving $daemonExeName in place because its scheduled task could not be removed."
        Write-Warn "  Re-run this installer as Administrator to complete the migration and reclaim ~160 MB."
    }
}

Write-Step "Stopping connector processes that would lock the target binaries..."

# The shared daemon is stopped by name (as uninstall does): only one runs, since it owns port
# 5250. Step 4c restarts the relay from the newly-copied binary, and any session's shim relaunches
# it lazily in the meantime, so stopping it here is safe.
#
# Kept UNCONDITIONAL in both flavors on purpose: this is also the migration hook. A Lean install must
# stop a leftover Full daemon, or that daemon keeps owning port 5250 and keeps serving every session.
$toStop = @(Get-Process -Name "X1McpGraphQL" -ErrorAction SilentlyContinue)

# Bridges are filtered to this install directory on purpose: a bridge belonging to another
# install (e.g. the Cowork plugin's own copy) locks its own exe, not the one we're replacing,
# so killing it would disrupt a live session for no benefit.
$toStop += @(Get-Process -Name "X1McpBridge" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) })

if ($toStop.Count -gt 0) {
    $toStop | Stop-Process -Force -ErrorAction SilentlyContinue
    # Stop-Process returns before Windows releases the file handles; without settling here the
    # copy below can still lose the race and fail with "used by another process".
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-Process -Id $toStop.Id -ErrorAction SilentlyContinue).Count -eq 0) { break }
        Start-Sleep -Milliseconds 200
    }
    Write-OK "Stopped $($toStop.Count) process(es) using '$InstallDir'."
}
else {
    Write-Info "No connector processes to stop."
}

# Verify the relay port is genuinely free rather than trusting Stop-Process to have finished.
# qa-plugin-install-workflow.md instructs a HUMAN to run exactly this check and records that
# skipping it produced two false results during development; doing it here closes that gap.
#
# NOTE the diagnostic asymmetry, which is easy to get wrong: the Lean host uses HttpListener, i.e.
# HTTP.SYS, a kernel driver - so Get-NetTCPConnection reports its OwningProcess as System (pid 4),
# never the bridge. Only a Kestrel-based Full daemon is identifiable that way. So the port check
# answers only "is anything listening", and /health is what identifies the owner (Step 4c probes it).
if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
    $stillListening = Get-NetTCPConnection -LocalPort 5250 -State Listen -ErrorAction SilentlyContinue
    if ($stillListening) {
        $ownerDesc = "unknown"
        try {
            $ownerPid = @($stillListening)[0].OwningProcess
            if ($ownerPid -eq 4) {
                # HTTP.SYS. Ask the relay itself who it is.
                try {
                    $who = (Invoke-WebRequest -Uri "$relayUrl/health" -TimeoutSec 3 -UseBasicParsing).Content | ConvertFrom-Json
                    $ownerDesc = "$($who.component) $($who.version) (pid $($who.pid)) at $($who.exePath)"
                } catch { $ownerDesc = "an HTTP.SYS listener that did not answer /health" }
            }
            else {
                $ownerPath = (Get-CimInstance Win32_Process -Filter "ProcessId=$ownerPid" -ErrorAction SilentlyContinue).ExecutablePath
                $ownerDesc = "pid $ownerPid ($ownerPath)"
            }
        } catch { }

        Write-Host "  ERROR: Something is still listening on port 5250 after stopping this install's processes:" -ForegroundColor Red
        Write-Host "    $ownerDesc" -ForegroundColor Red
        Write-Host "  Sessions would be served by THAT relay, not the one being installed - which looks like a" -ForegroundColor Red
        Write-Host "  successful install whose changes simply never appear. Most likely another install, or the" -ForegroundColor Red
        Write-Host "  Cowork plugin's own connector copy. Stop it and re-run this installer." -ForegroundColor Red
        exit 1
    }
    Write-OK "Relay port 5250 is free."
}
else {
    Write-Warn "Get-NetTCPConnection is unavailable on this host; skipping the port-5250 check."
}

Write-Step "Copying binaries..."

$copyCount = 0
Get-ChildItem -Path $scriptDir -File | Where-Object {
    $_.Extension -in @(".exe", ".dll", ".pdb", ".xml", ".config") -and
    $_.Name -ne "install.ps1"
} | ForEach-Object {
    Copy-Item $_.FullName -Destination $InstallDir -Force
    $copyCount++
}

# X1McpGraphQL.exe's own config isn't caught by the extension filter above (.json, not .config).
# Full-flavor only in practice - a Lean package has no appsettings*.json to match.
Get-ChildItem -Path $scriptDir -Filter "appsettings*.json" -File -ErrorAction SilentlyContinue | ForEach-Object {
    Copy-Item $_.FullName -Destination $InstallDir -Force
    $copyCount++
}

Write-OK "Copied $copyCount binary files to $InstallDir"

# ---------------------------------------------------------------------------
# Step 3b — Delete Full-flavor residue (only now: task gone, processes stopped)
#
# BY EXPLICIT NAME, never by "delete what this package doesn't ship". A set-difference sweep would
# also take x1mcp_stats.json, which belongs to the BRIDGE's CostTracker, not the daemon - i.e. it
# would silently wipe the customer's accumulated cost-savings statistics.
# ---------------------------------------------------------------------------

if ($migrateFromFull) {
    Write-Step "Removing Full-flavor files this package no longer uses..."

    $residue = @()
    if ($daemonTaskRemoved) {
        # Guarded: see the migration note above for why a task we could not unregister blocks this
        # one deletion specifically.
        $residue += $daemonExeName
    }
    $residue += @(
        "appsettings.json",
        "appsettings.Development.json",
        # Written at runtime by the daemon into its own base directory on every start, so nothing has
        # ever owned deleting it.
        "schema.graphql",
        "web.config"
    )

    $reclaimed = 0
    foreach ($name in $residue) {
        $path = Join-Path $InstallDir $name
        if (-not (Test-Path $path)) { continue }
        try {
            $size = (Get-Item $path).Length
            Remove-Item -LiteralPath $path -Force -ErrorAction Stop
            $reclaimed += $size
            Write-OK "Removed $name"
        }
        catch {
            # Warn and continue, matching this script's existing precedent: the install is otherwise
            # complete and correct, and a locked file here means something respawned after the port
            # check above.
            Write-Warn "Could not remove '$name': $($_.Exception.Message)"
        }
    }

    if ($reclaimed -gt 0) {
        Write-OK ("Reclaimed {0:N1} MB of disk space." -f ($reclaimed / 1048576))
    }

    # A customer config naming the now-deleted daemon is warned about, never rewritten: silently
    # editing user config from an installer is a bigger risk than a stale key, and the bridge already
    # falls back to "--host" when a configured daemonExePath does not exist
    # (BridgeConfig.GetRelayLaunchTarget), so the stale value is inert rather than fatal.
    if (Test-Path $mcpConfigDest) {
        try {
            $existingCfg = Get-Content $mcpConfigDest -Raw | ConvertFrom-Json
            foreach ($key in @("daemonExePath", "daemonUrl", "relayMode")) {
                if ($existingCfg.PSObject.Properties.Name -contains $key -and $existingCfg.$key) {
                    Write-Warn "x1mcp.config.json sets `"$key`": `"$($existingCfg.$key)`" - review it; this is a Lean install."
                }
            }
        } catch { }
    }
}

# ---------------------------------------------------------------------------
# Step 4 — Deploy x1mcp.config.json (preserve existing)
# ---------------------------------------------------------------------------

Write-Step "Deploying x1mcp.config.json..."

if (Test-Path $mcpConfigDest) {
    Write-Warn "Existing x1mcp.config.json preserved (not overwritten)."
    Write-Info "Delete it and re-run to reset to defaults."
}
else {
    $srcConfig = Join-Path $scriptDir $mcpConfigName
    if (Test-Path $srcConfig) {
        Copy-Item $srcConfig -Destination $mcpConfigDest -Force
        Write-OK "Copied x1mcp.config.json from package."
    }
    else {
        [System.IO.File]::WriteAllText($mcpConfigDest, $defaultX1Config, [System.Text.UTF8Encoding]::new($false))
        Write-OK "Created default x1mcp.config.json"
    }
}

# ---------------------------------------------------------------------------
# Step 4b — Patch savedContentDir if -SavedContentDir was supplied
# ---------------------------------------------------------------------------

if ($SavedContentDir -ne "") {
    Write-Step "Patching savedContentDir in x1mcp.config.json..."
    try {
        $cfgText = [System.IO.File]::ReadAllText($mcpConfigDest)
        $cfgObj  = [Newtonsoft.Json.Linq.JObject]::Parse($cfgText)
        $cfgObj["savedContentDir"] = $SavedContentDir
        [System.IO.File]::WriteAllText($mcpConfigDest, $cfgObj.ToString(), [System.Text.UTF8Encoding]::new($false))
        Write-OK "savedContentDir set to: $SavedContentDir"
    }
    catch {
        # Newtonsoft not available here; fall back to a simple string replace on the empty-string value
        $patched = $cfgText -replace '"savedContentDir"\s*:\s*""', ('"savedContentDir": "' + ($SavedContentDir -replace '\\','\\') + '"')
        [System.IO.File]::WriteAllText($mcpConfigDest, $patched, [System.Text.UTF8Encoding]::new($false))
        Write-OK "savedContentDir set to: $SavedContentDir (text patch)"
    }
}

# ---------------------------------------------------------------------------
# Step 4c — Start the shared relay, and in the Full flavor register it as a
# per-user logon task so it's already warm at next logon. Warm-start is an
# optimization, not a dependency: each session's shim (this same exe, run with
# --proxy) lazily starts the relay itself on first use, so failures here are
# non-fatal.
#
# Lean deliberately registers NO scheduled task. Recorded as a decision, not an
# omission:
#   • the warm-start argument is far weaker - a net4.8 HttpListener starts in
#     milliseconds, against the ~160MB single-file daemon's self-extract;
#   • registering one needs elevation (see the catch below);
#   • it would put a permanent background process on a customer machine that the
#     customer never asked for;
#   • and it is one more thing a later flavor change has to migrate away from.
# If warm-start is ever wanted for Lean it MUST use a different task name -
# never $daemonTaskName, which the migration below deletes by definition.
# ---------------------------------------------------------------------------

if ($isFull) {
    Write-Step "Registering the shared X1McpGraphQL daemon..."

    try {
        $action    = New-ScheduledTaskAction -Execute $daemonExePath -WorkingDirectory $InstallDir
        $trigger   = New-ScheduledTaskTrigger -AtLogOn
        $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
        $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero)
        Register-ScheduledTask -TaskName $daemonTaskName -Action $action -Trigger $trigger `
            -Principal $principal -Settings $settings -Force | Out-Null
        Write-OK "Registered scheduled task '$daemonTaskName' (starts the daemon at logon)."
    }
    catch {
        Write-Warn "Could not register the scheduled task: $($_.Exception.Message)"
        if ($_.Exception.Message -match "denied") {
            Write-Warn "Registering a new scheduled task requires an elevated PowerShell session, even for a"
            Write-Warn "  task that only runs as you - re-run this installer as Administrator to enable daemon"
            Write-Warn "  auto-start at logon."
        }
        Write-Warn "Not fatal either way - each session's connector starts the relay itself on first use if it isn't already running."
    }
}
else {
    Write-Step "Shared relay (Lean flavor)..."
    Write-Info "No scheduled task is registered: the relay starts on demand, in milliseconds, when a session first needs it."
}

# Step 3 stopped the relay, but a session's shim may have lazily relaunched one while this script
# ran. Restart it rather than reporting "already running" and leaving it: that process may predate
# the binaries just copied, and because the relay is shared on port 5250 it would go on serving the
# old code to every session - silently, since a stale relay answers normally and differs only in
# behaviour.
$relayRunning = @(Get-Process -Name "X1McpGraphQL" -ErrorAction SilentlyContinue)
$relayRunning += @(Get-Process -Name "X1McpBridge" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) })
if ($relayRunning.Count -gt 0) {
    # Ask it to stop cleanly first: Stop-Process would drop the WCF connection mid-call, and in Lean
    # the relay IS the bridge, so that is a user-visible failed tool call rather than a restartable
    # child. POST /shutdown drains in-flight work before disconnecting.
    try { Invoke-WebRequest -Uri "$relayUrl/shutdown" -Method POST -Body "{}" -ContentType "application/json" -TimeoutSec 5 -UseBasicParsing | Out-Null } catch { }
    Start-Sleep -Milliseconds 500
    $relayRunning | Stop-Process -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        if (@($relayRunning | ForEach-Object { Get-Process -Id $_.Id -ErrorAction SilentlyContinue }).Count -eq 0) { break }
        Start-Sleep -Milliseconds 200
    }
    Write-Info "Stopped a relay that started while installing; restarting it from the new binary."
}

try {
    if ($isFull) {
        Start-Process -FilePath $daemonExePath -WorkingDirectory $InstallDir -WindowStyle Hidden
    } else {
        Start-Process -FilePath $exePath -ArgumentList "--host" -WorkingDirectory $InstallDir -WindowStyle Hidden
    }
    Write-OK "Started the shared relay."
}
catch {
    Write-Warn "Could not start the relay now: $($_.Exception.Message)"
    Write-Warn "It will start automatically the next time a Claude session needs it."
}

# Health probe: makes the install self-verifying. Confirms that the thing now answering on the shared
# port is the thing this installer just put there - which is exactly the confusion that has already
# produced false results during development, where a leftover relay from another install kept serving
# every session while looking perfectly healthy.
$probeDeadline = (Get-Date).AddSeconds(20)
$probed = $null
while ((Get-Date) -lt $probeDeadline) {
    try {
        $probed = (Invoke-WebRequest -Uri "$relayUrl/health" -TimeoutSec 3 -UseBasicParsing).Content | ConvertFrom-Json
        break
    } catch { Start-Sleep -Milliseconds 500 }
}
if ($probed) {
    $probedComponent = if ($probed.PSObject.Properties.Name -contains "component" -and $probed.component) { $probed.component } else { "unknown (pre-flavor build)" }
    Write-OK "Relay answering on $relayUrl - component=$probedComponent version=$($probed.version)"

    $expectedComponent = if ($isFull) { "X1McpGraphQL" } else { "X1McpBridge" }
    if ($probed.PSObject.Properties.Name -contains "component" -and $probed.component -and $probed.component -ne $expectedComponent) {
        Write-Warn "The relay answering is $($probed.component), but this $flavorName install expects $expectedComponent."
        Write-Warn "  Something else on this machine owns port 5250 - most likely another install or the Cowork plugin's"
        Write-Warn "  own copy. Sessions will be served by THAT relay, not the one just installed."
        if ($probed.PSObject.Properties.Name -contains "exePath" -and $probed.exePath) {
            Write-Warn "  It is running from: $($probed.exePath)"
        }
    }
}
else {
    Write-Warn "The relay did not answer on $relayUrl within 20s. It will be started on demand by the first session that needs it."
}

# The Cowork plugin is installed and updated independently of this standalone install, and nothing
# owns its copy - so a Lean standalone upgrade leaves any plugin-resident daemon untouched (correctly:
# it belongs to the plugin). But that daemon's --proxy will grab port 5250 on the next session, and
# then the Lean host never serves: an install that looks entirely successful whose changes simply
# never appear. Detect and say so, since the two halves should ship as one release.
if (-not $isFull) {
    $pluginDaemons = @()
    foreach ($root in @(
        (Join-Path $env:USERPROFILE ".claude\plugins\marketplaces"),
        (Join-Path $env:APPDATA "Claude\plugins")
    )) {
        if (Test-Path $root) {
            $pluginDaemons += @(Get-ChildItem -Path $root -Recurse -Filter $daemonExeName -File -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty FullName)
        }
    }
    if ($pluginDaemons.Count -gt 0) {
        Write-Warn "A net10 daemon still exists in a separately-installed plugin:"
        $pluginDaemons | ForEach-Object { Write-Warn "    $_" }
        Write-Warn "  That copy is not managed by this installer. Its --proxy shim will start it and claim port"
        Write-Warn "  5250 before this Lean relay can, so sessions would keep using the daemon and the .NET 10"
        Write-Warn "  dependency would remain on this machine. Update or remove that plugin to complete the switch."
    }
}

# Build the shared MCP server entry used in all product configs
$mcpEntry = [PSCustomObject]@{
    command = $exePath
    args    = @("--proxy")
}

# GitHub Copilot's entry carries two extra keys, and Copilot is the first target whose entry shape
# differs at all - hence a second object rather than mutating the shared one:
#   type  - Copilot's own "/mcp add" writes "local" for a stdio child process. Its docs also accept
#           "stdio"; "local" is used here because it is what the app itself produces, which is the
#           shape most likely to keep loading across Copilot updates.
#   tools - the tool filter. "*" is every tool, i.e. the same surface every other target gets. An
#           absent filter also means "all", but writing it explicitly matches what the app produces
#           and means adding a tool needs no change here.
# args stays @("--proxy") like every other target, and that is not cosmetic - see the one-relay
# invariant in docs/build-flavors.md.
$copilotMcpEntry = [PSCustomObject]@{
    type    = "local"
    command = $exePath
    args    = @("--proxy")
    tools   = @("*")
}

# ---------------------------------------------------------------------------
# Step 5 — Claude Desktop
# ---------------------------------------------------------------------------

if ($doDesktop) {
    Write-Step "Configuring Claude Desktop..."

    $cfg = Read-JsonConfig $desktopCfgPath

    if (-not $cfg.PSObject.Properties["mcpServers"]) {
        $cfg | Add-Member -NotePropertyName "mcpServers" -NotePropertyValue ([PSCustomObject]@{})
    }

    if ($cfg.mcpServers.PSObject.Properties["x1-search"]) {
        $existing = $cfg.mcpServers."x1-search".command
        if ($existing -ne $exePath) {
            Write-Warn "Replacing existing x1-search path:"
            Write-Warn "  was: $existing"
            Write-Warn "  now: $exePath"
        }
    }

    $cfg.mcpServers | Add-Member -Force -NotePropertyName "x1-search" -NotePropertyValue $mcpEntry
    Write-JsonConfig $cfg $desktopCfgPath
    Write-OK "Updated $desktopCfgPath"

    Write-Info "claude.ai web: once Claude Desktop is running with this config,"
    Write-Info "  the x1-search tool is also available in claude.ai via Desktop relay."
}

# ---------------------------------------------------------------------------
# Step 6 — Claude Code  (~/.claude/settings.json)
# ---------------------------------------------------------------------------

if ($doCode) {
    Write-Step "Configuring Claude Code..."

    $cfg = Read-JsonConfig $codeCfgPath

    if (-not $cfg.PSObject.Properties["mcpServers"]) {
        $cfg | Add-Member -NotePropertyName "mcpServers" -NotePropertyValue ([PSCustomObject]@{})
    }

    if ($cfg.mcpServers.PSObject.Properties["x1-search"]) {
        $existing = $cfg.mcpServers."x1-search".command
        if ($existing -ne $exePath) {
            Write-Warn "Replacing existing x1-search path:"
            Write-Warn "  was: $existing"
            Write-Warn "  now: $exePath"
        }
    }

    $cfg.mcpServers | Add-Member -Force -NotePropertyName "x1-search" -NotePropertyValue $mcpEntry

    # Pre-approve the read-only / preview x1-search tools so they don't prompt every call.
    $addedRules = Add-AllowRules $cfg $x1AllowRules

    Write-JsonConfig $cfg $codeCfgPath
    Write-OK "Updated $codeCfgPath"
    if ($addedRules -gt 0) {
        Write-OK "Added $addedRules x1-search tool(s) to permissions.allow (no more prompts for search/preview)."
    }
    else {
        Write-Info "x1-search read-only tools already in permissions.allow."
    }
    Write-Info "x1_execute_action (open/show_in_folder/open_url) still prompts by design - it launches files/the browser."

    # Install the /x1 skill (Claude Code user skill). It teaches Claude to drive the connector
    # efficiently (search, preview, attachments, token-cheap path-not-bytes retrieval).
    if (Test-Path $skillSrcDir) {
        if (Test-Path $skillDestDir) { Remove-Item $skillDestDir -Recurse -Force }
        New-Item -ItemType Directory -Path $skillDestDir -Force | Out-Null
        Copy-Item (Join-Path $skillSrcDir '*') -Destination $skillDestDir -Recurse -Force
        Write-OK "Installed /x1 skill to $skillDestDir"
    }
    else {
        Write-Warn "/x1 skill source not found at $skillSrcDir (skipping skill install)."
    }

    Write-Info "This covers: Claude Code CLI, Claude Code desktop app, VS Code extension,"
    Write-Info "  JetBrains extension, and claude.ai/code."
}

# ---------------------------------------------------------------------------
# Step 6b — GitHub Copilot  (~/.copilot/mcp-config.json)
#
# Registered by merging the config file, not by shelling out to "copilot mcp add". That command
# exists, but it needs the Copilot CLI on PATH - a separate npm install from the desktop app - so
# it would make this the only target whose registration depends on a third-party executable being
# present. Merging the JSON is the same mechanism every other target here uses, and it works when
# only the desktop app is installed.
#
# Deliberately NOT gated on "is Copilot installed": -Target All writes this unconditionally, so a
# machine that installs Copilot later already has a correct registration waiting. The detection
# probe at the end of this block only decides what to TELL the user.
# ---------------------------------------------------------------------------

if ($doCopilot) {
    Write-Step "Configuring GitHub Copilot..."

    $cfg = Read-JsonConfig $copilotCfgPath

    if (-not $cfg.PSObject.Properties["mcpServers"]) {
        $cfg | Add-Member -NotePropertyName "mcpServers" -NotePropertyValue ([PSCustomObject]@{})
    }

    if ($cfg.mcpServers.PSObject.Properties["x1-search"]) {
        # Property presence is checked before every read: Set-StrictMode -Version Latest turns a
        # reference to a missing property into a terminating error, and this entry may have been
        # hand-written or produced by Copilot's own /mcp add, so no key is guaranteed to be there.
        $existingEntry = $cfg.mcpServers."x1-search"

        $existingCmd = $null
        if ($existingEntry.PSObject.Properties["command"]) { $existingCmd = $existingEntry.command }
        if ($existingCmd -ne $exePath) {
            Write-Warn "Replacing existing x1-search path:"
            Write-Warn "  was: $existingCmd"
            Write-Warn "  now: $exePath"
        }

        # Called out separately from the path change, because an entry added by hand through
        # "/mcp add" registers the bridge with no args at all - and that difference is not
        # cosmetic. Without --proxy the client spawns a bridge that owns its own WCF connection to
        # X1ServiceHost instead of proxying to the shared relay, which is precisely the
        # Connect()/teardown race that crashes X1ServiceHost. Correcting it silently would hide the
        # single most likely cause of "Copilot keeps killing my X1 service".
        $existingArgs = @()
        if ($existingEntry.PSObject.Properties["args"]) { $existingArgs = @($existingEntry.args) }
        if ($existingArgs -notcontains "--proxy") {
            Write-Warn "The existing x1-search entry did not use --proxy (args: [$($existingArgs -join ', ')])."
            Write-Warn "  That registration spawns its own bridge instead of sharing the one relay - the race"
            Write-Warn "  that crashes X1ServiceHost. Corrected to --proxy."
        }
    }

    $cfg.mcpServers | Add-Member -Force -NotePropertyName "x1-search" -NotePropertyValue $copilotMcpEntry
    Write-JsonConfig $cfg $copilotCfgPath
    Write-OK "Updated $copilotCfgPath"

    # Install the /x1 skill as a Copilot user skill. Same source and the same remove-then-copy as
    # the Claude Code install above; only the destination differs.
    if (Test-Path $skillSrcDir) {
        if (Test-Path $copilotSkillDir) { Remove-Item $copilotSkillDir -Recurse -Force }
        New-Item -ItemType Directory -Path $copilotSkillDir -Force | Out-Null
        Copy-Item (Join-Path $skillSrcDir '*') -Destination $copilotSkillDir -Recurse -Force
        Write-OK "Installed /x1 skill to $copilotSkillDir"
    }
    else {
        Write-Warn "/x1 skill source not found at $skillSrcDir (skipping skill install)."
    }

    # Copilot has no analogue of Claude Code's permissions.allow, so there is nothing here to
    # pre-approve: its saved approvals live in permissions-config.json, which is auto-managed AND
    # keyed by absolute project path, meaning there is no machine-wide slot an installer could
    # seed. Stated rather than left to be discovered as "why does Copilot keep prompting when
    # Claude Code doesn't".
    Write-Info "Copilot prompts on first use of each tool - pick its 'always allow' option, or start"
    Write-Info "  it with --allow-tool 'x1-search(*)'. There is no global allowlist to pre-seed:"
    Write-Info "  Copilot saves approvals per project directory, not per machine."

    # --- Advisory: a skillDirectories entry left over from a manual "/skill add" ---
    #
    # Warned about, never rewritten - the same precedent as the Lean-migration warning about a
    # customer x1mcp.config.json earlier in this script. skillDirectories is a user-owned array in
    # a user-editable file, and an entry there may point at a skill tree the user maintains
    # themselves. But if it also provides x1, Copilot now loads the same skill twice, and that
    # duplicate is invisible until "/skills list" shows two.
    if (Test-Path $copilotSettings) {
        try {
            $cs = Read-JsonConfig $copilotSettings
            if ($cs.PSObject.Properties["skillDirectories"]) {
                foreach ($dir in @($cs.skillDirectories)) {
                    $d = [string]$dir
                    if ($d -and (Test-Path (Join-Path $d "x1\SKILL.md"))) {
                        Write-Warn "settings.json lists skillDirectories entry '$d', which also provides the x1 skill."
                        Write-Warn "  Copilot would now load /x1 twice. Remove that entry from:"
                        Write-Warn "    $copilotSettings"
                        Write-Warn "  Left alone on purpose - this installer does not edit your settings array."
                    }
                }
            }
        }
        catch {
            Write-Info "Could not read $copilotSettings to check for duplicate skill directories: $($_.Exception.Message)"
        }
    }

    # --- Advisory: the plugin route and this route both register x1-search ---
    #
    # The Copilot plugin ships its own x1-search server entry and its own copy of the skill, so a
    # machine with both ends up with two registrations under one name. Both use --proxy, so the
    # shared relay keeps the WCF invariant either way and nothing crashes - but the duplicate is
    # confusing, and is exactly the kind of thing diagnosed weeks later. Mirrors the
    # plugin-resident-daemon warning in the relay step above.
    if (Test-Path $copilotAppConfig) {
        try {
            $cc = Read-JsonConfig $copilotAppConfig
            if ($cc.PSObject.Properties["installedPlugins"]) {
                $x1Plugins = @(@($cc.installedPlugins) | Where-Object {
                    $_ -and $_.PSObject.Properties["name"] -and $_.name -eq "x1-search"
                })
                if ($x1Plugins.Count -gt 0) {
                    Write-Warn "The x1-search Copilot PLUGIN is also installed. It registers its own x1-search"
                    Write-Warn "  server and its own /x1 skill, so both are now present twice. Pick one route:"
                    Write-Warn "  either 'copilot plugin uninstall x1-search', or re-run this installer with"
                    Write-Warn "  -Target Desktop / -Target Code instead of All."
                }
            }
        }
        catch {
            Write-Info "Could not read $copilotAppConfig to check for a conflicting plugin: $($_.Exception.Message)"
        }
    }

    # --- Detection: informational only (see the block comment above for why it does not gate) ---
    $copilotUninstallKey = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GitHub Copilot"
    if ((Test-Path $copilotUninstallKey) -or (Test-Path $copilotCfgDir)) {
        Write-Info "Covers the GitHub Copilot desktop app AND the Copilot CLI - they share this config."
    }
    else {
        Write-Info "GitHub Copilot was not detected on this machine. The config has been written anyway,"
        Write-Info "  so x1-search is already registered if Copilot is installed later."
    }

    # Licensing, stated here because the failure is remote and looks nothing like a connector
    # fault: a free Copilot account is not entitled to the CLI/app agent surface, so the session
    # dies during start-up - "not authorized to use this Copilot feature", or a 421 after a seat
    # change - before x1-search is loaded at all. Without this line every such support conversation
    # starts by investigating the bridge.
    Write-Info "Requires a PAID Copilot license: Copilot Free cannot run the CLI/app agent surface,"
    Write-Info "  and an org-provided seat also needs that org's Copilot CLI policy enabled."
    Write-Info "  See docs/UserManual.md section 13 if a session fails before any x1 tool runs."

    # No restart is offered here, unlike Claude Desktop. Copilot runs long-lived parallel agent
    # sessions in their own git worktrees; killing it mid-session discards work that no config file
    # can reconstruct. Advisory only, on purpose.
    Write-Info "Restart GitHub Copilot to pick up the new server."
}

# ---------------------------------------------------------------------------
# Step 7 — Check X1ServiceHost (advisory)
# ---------------------------------------------------------------------------

Write-Step "Checking X1ServiceHost..."
$x1proc = Get-Process -Name "X1ServiceHost" -ErrorAction SilentlyContinue
if ($x1proc) {
    Write-OK "X1ServiceHost is running (PID $($x1proc.Id))."
}
else {
    Write-Warn "X1ServiceHost is not currently running."
    Write-Warn "The MCP bridge requires X1ServiceHost to be running when Claude connects."
}

# ---------------------------------------------------------------------------
# Step 8 — Offer to restart Claude Desktop
# ---------------------------------------------------------------------------

if ($doDesktop) {
    Write-Step "Checking Claude Desktop process..."
    # Get-Process -Name "claude" matches by image name alone, which collides with Claude Code
    # CLI sessions (also named claude.exe, e.g. a session running *this very installer*) and
    # with this app's own Electron helper subprocesses (renderer/gpu/utility/crashpad - same exe
    # path, distinguished only by a --type=... arg). Filtering on CommandLine via CIM is what
    # correctly isolates just the actual Desktop app process(es), so this step can't kill or
    # restart the wrong thing - a prior version of this block did `Stop-Process -Name "claude"`,
    # which silently killed every Code CLI session on the machine too.
    $claudeMainProcs = Get-CimInstance Win32_Process -Filter "Name = 'claude.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -notmatch 'claude-code' -and $_.CommandLine -notmatch '--type=' }
    if ($claudeMainProcs) {
        Write-Host ""
        $answer = Read-Host "  Claude Desktop is running. Restart it now to load the new config? [Y/n]"
        if ($answer -notmatch '^[Nn]') {
            Write-Step "Restarting Claude Desktop..."
            $claudeExe = ($claudeMainProcs | Select-Object -First 1).ExecutablePath
            foreach ($proc in $claudeMainProcs) {
                Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
            }
            Start-Sleep -Seconds 2
            Start-Process $claudeExe
            Write-OK "Claude Desktop restarted."
        }
        else {
            Write-Warn "Please restart Claude Desktop manually for changes to take effect."
        }
    }
    else {
        Write-Warn "Claude Desktop is not running. Start it to activate the MCP bridge."
    }
}

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------

Write-Host ""
Write-Host "  =============================================" -ForegroundColor Green
Write-Host "  Installation complete!" -ForegroundColor Green
Write-Host "  =============================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Bridge installed to  : $InstallDir"
Write-Host "  X1 config file       : $mcpConfigDest"
if ($SavedContentDir -ne "") {
    Write-Host "  Saved content dir    : $SavedContentDir"
}
else {
    $defaultSaveDir = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "X1 Saved"
    Write-Host "  Saved content dir    : $defaultSaveDir (default; set -SavedContentDir to override)"
}
if ($doDesktop) {
    Write-Host "  Claude Desktop config: $desktopCfgPath"
}
if ($doCode) {
    Write-Host "  Claude Code config   : $codeCfgPath"
    if (Test-Path $skillDestDir) {
        Write-Host "  /x1 skill installed  : $skillDestDir"
    }
}
if ($doCopilot) {
    Write-Host "  Copilot config       : $copilotCfgPath"
    if (Test-Path $copilotSkillDir) {
        Write-Host "  /x1 skill installed  : $copilotSkillDir"
    }
}
Write-Host ""
Write-Host "  Package flavor  : $flavorName"
Write-Host "  MCP server name : x1-search"
Write-Host "  Executable      : $exePath --proxy"
if ($isFull) {
    Write-Host "  Shared relay    : $daemonExePath ($relayUrl)"
    Write-Host "  GraphQL API     : $relayUrl/graphql  (Nitro IDE)"
} else {
    Write-Host "  Shared relay    : $exePath --host ($relayUrl)"
    Write-Host "                    net4.8 in-bridge relay - no GraphQL API, no .NET 10 dependency."
}
if (($doCode -and (Test-Path $skillDestDir)) -or ($doCopilot -and (Test-Path $copilotSkillDir))) {
    Write-Host "  Skill           : /x1  (run /x1 in Claude Code or GitHub Copilot, or just ask to"
    Write-Host "                    find/preview your content)"
}
Write-Host ""
if ($isFull) {
    Write-Host "  Every registered client proxies through the one shared relay above - see" -ForegroundColor Gray
    Write-Host "    scheduled task '$daemonTaskName' for its logon auto-start." -ForegroundColor Gray
} else {
    Write-Host "  Every registered client proxies through the one shared relay above. It starts on" -ForegroundColor Gray
    Write-Host "    demand when a session first needs it; there is no scheduled task." -ForegroundColor Gray
}
Write-Host ""
Write-Host "  Edit x1mcp.config.json to change which data sources are searched by default."
Write-Host ""

if ($doDesktop -and $doCode) {
    Write-Host "  claude.ai web: open Claude Desktop and enable 'Use with claude.ai' in" -ForegroundColor Gray
    Write-Host "    Desktop settings to make x1-search available on claude.ai." -ForegroundColor Gray
    Write-Host ""
}
