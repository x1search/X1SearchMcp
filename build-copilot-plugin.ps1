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
  Assembles the X1 Search GitHub Copilot plugin (connector binaries + /x1 skill) into
  installer\copilot-plugin\ and installer\x1-search-copilot.plugin.

.DESCRIPTION
  Same payload and the same canonical sources as build-plugin.ps1 - only the manifest directory and
  the MCP entry shape differ (Copilot needs "type": "local" and accepts a "tools" filter). Kept as a
  separate script rather than a -Client switch on build-plugin.ps1 because the Cowork plugin is the
  shipping product: a shared script means every Copilot-only change is one edit away from breaking it.

  Run build-installer.bat first so the staged binaries exist in installer\.

  TWO outputs, because Copilot never unpacks an archive - it only ever consumes a DIRECTORY:

    installer\copilot-plugin\          - the installable artifact. `copilot plugin install <abs
                                         path to this directory>` works (and auto-enables the
                                         plugin); `copilot --plugin-dir <this directory>` runs it
                                         without installing. A RELATIVE path is rejected as an
                                         invalid spec, so always hand it an absolute one.
    installer\x1-search-copilot.plugin - the same tree zipped, for distribution/archival and to
                                         match the Cowork plugin's artifact shape. Copilot cannot
                                         install it directly (it reads a zip as a repo and reports
                                         "No plugin.json found"), so unzip it first.

  Two behaviours worth knowing before changing any of this:
    - A plugin's MCP servers only start when the plugin is ENABLED (settings.json -> enabledPlugins).
      `copilot plugin install` sets that itself; a --plugin-dir mount does NOT, and the symptom is a
      plugin whose skills load fine while its tools are simply absent.
    - Copilot warns that direct installs (local paths, repos, URLs) are deprecated in favour of
      plugin@marketplace. The directory route works today; a marketplace entry is the durable one.
  See copilot-plugin\README.md for the full set of verified behaviours.

.PARAMETER Flavor
  Which flavor build-installer.bat produced. Lean (the default, and what customers get) has no
  X1McpGraphQL daemon and therefore no .NET 10 dependency; the shared relay is
  "X1McpBridge.exe --host". Full additionally carries the net10 daemon and its GraphQL API.

  Passed in rather than inferred so the value is self-documenting in the build log. As in
  build-plugin.ps1 the payload copy needs no flavor logic - it is extension-driven, so in Lean the
  daemon simply isn't there to copy. The parameter is used for the dirty-check pathspec and the stamp.
#>
param(
    [ValidateSet("Lean", "Full")]
    [string]$Flavor = $(if ($env:X1MCP_FLAVOR -in @("Lean", "Full")) { $env:X1MCP_FLAVOR } else { "Lean" })
)
$ErrorActionPreference = "Stop"

$root     = $PSScriptRoot
$plugin   = Join-Path $root "copilot-plugin"
$stage    = Join-Path $root "installer"
$skillSrc = Join-Path $root "skill\x1"

if (-not (Test-Path (Join-Path $stage "X1McpBridge.exe"))) {
    Write-Error "Staged binaries not found in '$stage'. Run build-installer.bat first."
    exit 1
}

# --- Stage connector/ : exe + runtime DLLs + config (exclude install.ps1 and the skill subdir) ---
# Extension-driven and flavor-blind, exactly as in build-plugin.ps1: in Lean the daemon was never
# staged, so nothing here needs to know not to copy it. Also NOT named bin/ - see the README.
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

# --- Assert the MCP entry still says --proxy -------------------------------------------------
# .mcp.json is hand-maintained, and a bare "X1McpBridge.exe" here is the single most damaging
# edit anyone could make to this file: the plugin would work in casual testing and then crash
# X1ServiceHost as soon as a second client connected (the Connect()/teardown race the shared relay
# exists to prevent - docs/build-flavors.md). Cheap to assert, expensive to discover in the field.
$mcpJsonPath = Join-Path $plugin ".mcp.json"
$mcpJson = Get-Content $mcpJsonPath -Raw | ConvertFrom-Json
$entry = $mcpJson.mcpServers."x1-search"
if (@($entry.args) -notcontains "--proxy") {
    Write-Error "$mcpJsonPath does not register x1-search with --proxy. Every client must proxy through the one shared relay; see docs/build-flavors.md."
    exit 1
}
if ($entry.command -notlike '*${CLAUDE_PLUGIN_ROOT}*') {
    Write-Error "$mcpJsonPath must locate the connector via `${CLAUDE_PLUGIN_ROOT} - an absolute path would only work on the machine that built it."
    exit 1
}
if ($entry.type -ne "local") {
    Write-Error "$mcpJsonPath must set `"type`": `"local`" - Copilot needs an explicit transport type for a stdio child process."
    exit 1
}

# --- Stamp the package with its source provenance (staleness guard) ---
# Same rationale as build-plugin.ps1: a stale packaged binary once shipped silently for two days
# after the fix it needed had already landed in source (XS-1610).
try   { $sourceCommit = (git -C $root rev-parse HEAD 2>$null).Trim() } catch { $sourceCommit = $null }

# copilot-plugin rather than cowork-plugin; everything else feeds both packages identically.
$dirtyPaths = @("X1McpBridge", "copilot-plugin", "skill", "install.ps1", "build-installer.bat")
if ($Flavor -eq "Full") { $dirtyPaths += "../X1McpGraphQL" }
$dirtyFiles = git -C $root status --porcelain -- $dirtyPaths 2>$null

$buildInfo = [ordered]@{
    sourceCommit   = $sourceCommit
    sourceDirty    = [bool]$dirtyFiles
    builtAtUtc     = [DateTime]::UtcNow.ToString("o")
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

# --- Output 1: the unzipped directory, which is what --plugin-dir needs ---
$outDir = Join-Path $stage "copilot-plugin"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
Copy-Item (Join-Path $plugin "*") -Destination $outDir -Recurse -Force
# Copy-Item's wildcard skips dot-directories, so .claude-plugin/ has to be named explicitly - and
# without its manifest the directory is not a plugin at all, just a folder Copilot ignores.
Copy-Item (Join-Path $plugin ".claude-plugin") -Destination $outDir -Recurse -Force
Copy-Item (Join-Path $plugin ".mcp.json") -Destination $outDir -Force

foreach ($required in @(".claude-plugin\plugin.json", ".mcp.json", "connector\X1McpBridge.exe", "skills\x1\SKILL.md")) {
    if (-not (Test-Path (Join-Path $outDir $required))) {
        Write-Error "Staged Copilot plugin is missing '$required' - the dot-file copy above did not do what it looks like it does."
        exit 1
    }
}

# --- Output 2: the same tree zipped, with forward-slash entry names ---
Add-Type -AssemblyName System.IO.Compression
$out = Join-Path $stage "x1-search-copilot.plugin"
if (Test-Path $out) { [System.IO.File]::Delete($out) }
$fs = [System.IO.File]::Open($out, [System.IO.FileMode]::Create)
$zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $baseLen = $outDir.Length + 1
    foreach ($f in (Get-ChildItem $outDir -Recurse -File -Force)) {
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
Write-Host ("Built Copilot plugin [$Flavor]: " + $outDir + " (directory, for --plugin-dir)")
Write-Host ("Built Copilot plugin [$Flavor]: " + $out + " ($sizeMB MB, zipped)")
