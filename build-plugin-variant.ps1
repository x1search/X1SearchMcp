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
  XS-1677: assembles a second, differently-identified "X1 Search 2" Cowork plugin into
  installer\x1-search-2.plugin, for the XS-1647 internal deployment-test rehearsal.

.DESCRIPTION
  Copies the already-built cowork-plugin\ tree (connector binaries + /x1 skill, staged by
  build-plugin.ps1) into a scratch directory outside the repo, renames its plugin identity so
  it installs as a wholly separate plugin alongside the real x1-search plugin rather than
  "updating" it, then zips it the same way build-plugin.ps1 does.

  Deliberately does not touch build-plugin.ps1, cowork-plugin\.claude-plugin\plugin.json,
  cowork-plugin\.mcp.json, or version.props - this is a one-off rehearsal artifact, not a
  permanent second product, and none of the real release pipeline (XS-1627/1628/1646/1648)
  should be able to see or be affected by it.

  Built from the exact same binaries/version as the real plugin (no version bump here) - two
  --proxy shims that disagree on version make RelayHealth.Decide evict and relaunch the shared
  relay on every session; building identical binaries under a different plugin identity avoids
  that churn and is also the more faithful test, since XS-1647 is rehearsing deployment
  mechanics, not new connector code. The two installs are told apart afterward by install path
  (x1_version's runningBridges[].path), which is all the coexistence check needs.

  Run build-installer.bat then build-plugin.ps1 first so cowork-plugin\connector\ is fresh.
#>
$ErrorActionPreference = "Stop"

# PowerShell 5.1's `Set-Content -Encoding utf8` always writes a UTF-8 BOM (there is no
# utf8NoBOM alias before PS 6), and a leading BOM is a corrupt-manifest error to Claude Code's
# CLI plugin loader (Node's JSON.parse does not strip it) - found by actually running
# `claude plugin install` against this script's output, not by reading the code. plugin.json and
# .mcp.json are the two files this script rewrites via ConvertTo-Json, so both need this instead.
function Set-JsonNoBom($Path, $Content) {
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

$root       = $PSScriptRoot
$plugin     = Join-Path $root "cowork-plugin"
$stage      = Join-Path $root "installer"
$buildInfo  = Join-Path $plugin "connector\build-info.json"
$variantDir = Join-Path $env:TEMP "x1-search-2-plugin-build"

if (-not (Test-Path $buildInfo)) {
    Write-Error "cowork-plugin\connector\build-info.json not found. Run build-installer.bat then build-plugin.ps1 first."
    exit 1
}

# --- Stage a copy of the real plugin into a scratch dir outside the repo ------------------------
if (Test-Path $variantDir) { Remove-Item $variantDir -Recurse -Force }
New-Item -ItemType Directory -Path $variantDir -Force | Out-Null
# $env:TEMP resolves to an 8.3 short-name path on this machine (e.g. STEWAR~1), but
# Get-ChildItem below returns long-form FullNames - re-resolve to the long form now so the
# $baseLen substring math against $variantDir.Length further down actually lines up.
$variantDir = (Get-Item $variantDir).FullName
Copy-Item (Join-Path $plugin "*") -Destination $variantDir -Recurse -Force

# --- Rename the plugin identity so it installs side by side, not as an update -------------------
$manifestPath = Join-Path $variantDir ".claude-plugin\plugin.json"
$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest.name = "x1-search-2"
$manifest.description = "[Internal test build] " + $manifest.description
Set-JsonNoBom $manifestPath ($manifest | ConvertTo-Json)

# Rename the mcpServers key too (command/args unchanged) - purely so tool ids, /mcp listings, and
# log/diagnostic output are unambiguous between the two plugins during the rehearsal (the plugin
# name already namespaces the tools, so this isn't required for coexistence itself).
$mcpJsonPath = Join-Path $variantDir ".mcp.json"
$mcpJson = Get-Content $mcpJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
$serverConfig = $mcpJson.mcpServers.'x1-search'
$mcpJson.mcpServers | Add-Member -NotePropertyName "x1-search-2" -NotePropertyValue $serverConfig
$mcpJson.mcpServers.PSObject.Properties.Remove('x1-search')
Set-JsonNoBom $mcpJsonPath ($mcpJson | ConvertTo-Json -Depth 10)

Write-Host "Variant identity: $($manifest.name) (version $($manifest.version), unchanged from the real plugin)"

# --- Zip to .plugin with forward-slash entry names (same approach as build-plugin.ps1) ----------
Add-Type -AssemblyName System.IO.Compression
$out = Join-Path $stage "x1-search-2.plugin"
if (Test-Path $out) { [System.IO.File]::Delete($out) }
$fs = [System.IO.File]::Open($out, [System.IO.FileMode]::Create)
$zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $baseLen = $variantDir.Length + 1
    foreach ($f in (Get-ChildItem $variantDir -Recurse -File -Force)) {
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
Write-Host ("Built X1 Search 2 rehearsal plugin: " + $out + " ($sizeMB MB)")
Write-Host ""
Write-Host "Next steps under XS-1647 (not this script):"
Write-Host "  - Local/admin-console upload: upload $out the same way x1-search.plugin is uploaded today."
Write-Host "  - CLI install (verified against Claude Code 2.1.200): 'claude plugin marketplace add' needs a"
Write-Host "    DIRECTORY containing .claude-plugin/marketplace.json (schema: name/version/description/owner"
Write-Host "    plus a plugins[] array of {name,version,source}) - it will NOT accept this .plugin zip, nor a"
Write-Host "    bare unzipped plugin directory, directly. Unzip $out into <marketplace-dir>/x1-search-2/,"
Write-Host "    add a marketplace.json alongside it pointing source at './x1-search-2', then:"
Write-Host "      claude plugin marketplace add <marketplace-dir>"
Write-Host "      claude plugin install x1-search-2@<marketplace-name>"
Write-Host "    No enable-time permission warning was printed by either command (matches XS-1590's CLI-bypass"
Write-Host "    finding). A session already running before the install does NOT pick up the new plugin's"
Write-Host "    tools - requires a full Claude quit/relaunch, same as XS-1648's how-to guide step 3 says."
Write-Host "  - Coexistence check: call x1_version through both plugins; confirm two distinct runningBridges[].path entries, mismatch:false, no relaunch thrashing in X1McpBridge.log."
