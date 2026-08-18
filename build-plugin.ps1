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
  Assembles the X1 Search Cowork plugin (connector binaries + /x1 skill) into
  installer\x1-search.plugin.

.DESCRIPTION
  Bundles the runtime payload (X1McpBridge.exe + dependency DLLs + x1mcp.config.json, and in the
  Full flavor also the shared X1McpGraphQL daemon exe + its appsettings.json - see the fan-in plan
  for why every session proxies through one shared relay instead of each spawning its own bridge)
  and the canonical /x1 skill into a self-contained Cowork plugin, then zips it with forward-slash
  entry names (required by the .plugin/zip format).

  Run build-installer.bat first so the staged binaries exist in installer\.

.PARAMETER Flavor
  Which flavor build-installer.bat produced. Lean (the default, and what customers get) has no
  X1McpGraphQL daemon and therefore no .NET 10 dependency; the shared relay is
  "X1McpBridge.exe --host" instead. Full additionally carries the net10 daemon and its GraphQL API.

  This is passed in rather than inferred so the value is self-documenting in the build log, but note
  the payload copy below needs no flavor logic at all: it is extension-driven, so the daemon simply
  isn't there to copy in Lean. The parameter is used for the dirty-check pathspec and the stamp.
#>
param(
    [ValidateSet("Lean", "Full")]
    [string]$Flavor = $(if ($env:X1MCP_FLAVOR -in @("Lean", "Full")) { $env:X1MCP_FLAVOR } else { "Lean" })
)
$ErrorActionPreference = "Stop"

$root     = $PSScriptRoot
$plugin   = Join-Path $root "cowork-plugin"
$stage    = Join-Path $root "installer"
$skillSrc = Join-Path $root "skill\x1"

if (-not (Test-Path (Join-Path $stage "X1McpBridge.exe"))) {
    Write-Error "Staged binaries not found in '$stage'. Run build-installer.bat first."
    exit 1
}

# --- Stage connector/ : exe + runtime DLLs + config (exclude install.ps1 and the skill subdir) ---
# Includes X1McpBridge.exe (run with --proxy, per cowork-plugin/.mcp.json) and, in the Full flavor
# only, the shared X1McpGraphQL.exe daemon it lazily launches - appsettings*.json is that daemon's
# config, analogous to x1mcp.config.json for the bridge.
#
# The filter below is deliberately extension-driven and flavor-blind: in Lean the daemon was never
# staged, so nothing needs to know not to copy it. That property is worth keeping - a name-based
# exclusion list is one more place to forget to update.
#
# This folder must NOT be named bin/: a top-level bin/ in a plugin is auto-added to PATH by the
# CLI, and claude.ai-hosted plugins are rejected at upload for containing one (that PATH
# injection isn't visible on the admin approval surface). The entry point is declared via
# mcpServers in .mcp.json instead, which is the sanctioned mechanism. Nothing resolves this
# name at runtime - the bridge finds X1McpGraphQL.exe and x1mcp.config.json as siblings of its
# own exe - so the name only has to stay in sync with .mcp.json and check-plugin-staleness.ps1.
$payload = Join-Path $plugin "connector"
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Get-ChildItem $stage -File | Where-Object {
    $_.Extension -in @(".exe", ".dll", ".config") -or
    $_.Name -eq "x1mcp.config.json" -or
    $_.Name -like "appsettings*.json"
} | ForEach-Object { Copy-Item $_.FullName -Destination $payload -Force }

# --- Stage skills/x1 from the canonical skill source (single source of truth) ---
$skills = Join-Path $plugin "skills"
if (Test-Path $skills) { Remove-Item $skills -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $skills "x1") -Force | Out-Null
Copy-Item (Join-Path $skillSrc "*") -Destination (Join-Path $skills "x1") -Recurse -Force

# --- Stamp the package with its source provenance (staleness guard) ---
# A stale packaged binary once shipped silently for two days after the fix it needed
# had already landed in source (XS-1610) - build-plugin.ps1 had copied it once and
# nothing ever flagged that source moved on without a rebuild. Stamping the commit
# this package was built from lets check-plugin-staleness.ps1 catch that drift by
# comparing this file against the deployed plugin's own copy of it.
try   { $sourceCommit = (git -C $root rev-parse HEAD 2>$null).Trim() } catch { $sourceCommit = $null }

# Paths whose source actually feeds this package. install.ps1 and build-installer.bat are included
# because they ship in / define the package yet were previously outside this pathspec entirely -
# which meant a change to either reported "UP TO DATE" against a deployed plugin built before it.
#
# ../X1McpGraphQL is Full-only, and excluding it in Lean is correctness rather than tidiness: the
# daemon's source contributes nothing to a Lean package, and anyone who deletes that directory to
# prove a Lean build needs no .NET 10 would otherwise see 47 deletions turn sourceDirty permanently
# true - which qa-plugin-install-workflow.md treats as release-blocking.
$dirtyPaths = @("X1McpBridge", "cowork-plugin", "skill", "install.ps1", "build-installer.bat")
if ($Flavor -eq "Full") { $dirtyPaths += "../X1McpGraphQL" }
$dirtyFiles = git -C $root status --porcelain -- $dirtyPaths 2>$null

$buildInfo = [ordered]@{
    sourceCommit = $sourceCommit
    sourceDirty  = [bool]$dirtyFiles
    builtAtUtc   = [DateTime]::UtcNow.ToString("o")
    # flavor = declared intent; daemonIncluded = what is actually in the payload. Recording both
    # means a package where they disagree is self-evident instead of needing to be deduced from size.
    flavor         = $Flavor
    daemonIncluded = [bool](Test-Path (Join-Path $payload "X1McpGraphQL.exe"))
}
$buildInfo | ConvertTo-Json | Set-Content -Path (Join-Path $payload "build-info.json") -Encoding utf8

if ($buildInfo.flavor -eq "Lean" -and $buildInfo.daemonIncluded) {
    Write-Error "Lean plugin payload contains X1McpGraphQL.exe - the .NET 10 dependency this flavor exists to remove."
    exit 1
}
if ($buildInfo.flavor -eq "Full" -and -not $buildInfo.daemonIncluded) {
    Write-Error "Full plugin payload is missing X1McpGraphQL.exe - sessions would have no GraphQL surface."
    exit 1
}

# --- Zip to .plugin with forward-slash entry names ---
Add-Type -AssemblyName System.IO.Compression
$out = Join-Path $stage "x1-search.plugin"
if (Test-Path $out) { [System.IO.File]::Delete($out) }
$fs = [System.IO.File]::Open($out, [System.IO.FileMode]::Create)
$zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $baseLen = $plugin.Length + 1
    foreach ($f in (Get-ChildItem $plugin -Recurse -File -Force)) {
        $rel = ($f.FullName.Substring($baseLen)) -replace '\\', '/'
        $entry = $zip.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
        $es = $entry.Open()
        $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
        $es.Write($bytes, 0, $bytes.Length)
        $es.Dispose()
    }
}
finally {
    $zip.Dispose(); $fs.Dispose()
}

$sizeMB = [math]::Round((Get-Item $out).Length / 1048576, 2)
Write-Host ("Built Cowork plugin [$Flavor]: " + $out + " ($sizeMB MB)")
