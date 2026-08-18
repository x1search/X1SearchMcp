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
  Assembles the X1 Search MCPB desktop extension into installer\x1-search.mcpb.

.DESCRIPTION
  Bundles the runtime payload (X1McpBridge.exe + dependency DLLs + x1mcp.config.json - the same
  Lean payload build-plugin.ps1 stages into cowork-plugin\connector\) into mcpb-package\server\,
  then runs `mcpb pack` to produce the .mcpb file.

  Always Lean, regardless of what flavor build-installer.bat built: bundling the ~160MB net10
  X1McpGraphQL daemon into a public Anthropic directory submission defeats the entire point of the
  Lean/Full split (docs\build-flavors.md). X1McpBridge.exe --proxy already self-elects itself
  ("--host") as the shared relay when no daemon exe is a sibling, so a Lean-only payload needs no
  special-casing beyond simply never staging the daemon here.

  Run build-installer.bat first so the staged binaries exist in installer\.

  Unlike build-plugin.ps1's Cowork-plugin build, a missing `mcpb` CLI here is a hard failure, not a
  skip: there is no valid "half-built" .mcpb the way Lean is a valid, shippable alternative to Full.
  build-installer.bat itself soft-skips this script entirely when `mcpb` isn't on PATH, so that gate
  only matters for someone running this script directly.
#>
$ErrorActionPreference = "Stop"

$root      = $PSScriptRoot
$package   = Join-Path $root "mcpb-package"
$stage     = Join-Path $root "installer"
$manifest  = Join-Path $package "manifest.json"
$payload   = Join-Path $package "server"

if (-not (Test-Path $manifest)) {
    Write-Error "mcpb-package\manifest.json not found. Nothing to pack."
    exit 1
}

if (-not (Test-Path (Join-Path $stage "X1McpBridge.exe"))) {
    Write-Error "Staged binaries not found in '$stage'. Run build-installer.bat first."
    exit 1
}

$mcpb = Get-Command mcpb -ErrorAction SilentlyContinue
if (-not $mcpb) {
    Write-Error "'mcpb' CLI not found on PATH. Install it with: npm install -g @anthropic-ai/mcpb"
    exit 1
}

# --- Stage server/ : exe + runtime DLLs + config, Lean payload only -----------------------------
# Same extension-driven, flavor-blind filter build-plugin.ps1 uses for cowork-plugin\connector\ -
# deliberately re-applied here rather than shared, because this one must NEVER pick up the daemon
# even when build-installer.bat --full staged it into installer\; a name-based exclusion list is
# one more place that could silently start including it, so the filter simply never looks for it.
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Get-ChildItem $stage -File | Where-Object {
    ($_.Extension -in @(".exe", ".dll", ".config") -or $_.Name -eq "x1mcp.config.json") -and
    $_.Name -notlike "X1McpGraphQL*" -and
    $_.Name -notlike "appsettings*"
} | ForEach-Object { Copy-Item $_.FullName -Destination $payload -Force }

if (-not (Test-Path (Join-Path $payload "X1McpBridge.exe"))) {
    Write-Error "X1McpBridge.exe was not staged into '$payload' - check the installer\ contents."
    exit 1
}
if (Test-Path (Join-Path $payload "X1McpGraphQL.exe")) {
    Write-Error "X1McpGraphQL.exe ended up in the MCPB payload - this package must stay Lean-only."
    exit 1
}

# --- Remind about outstanding Legal-owned placeholders -----------------------------------------
$manifestJson = Get-Content $manifest -Raw | ConvertFrom-Json
if (-not $manifestJson.privacy_policies -or $manifestJson.privacy_policies.Count -eq 0) {
    Write-Warning "manifest.json's privacy_policies is empty - fill in before actual Anthropic submission (XS-1664, Legal owned)."
}
if ($manifestJson.license -eq "UNLICENSED") {
    Write-Warning "manifest.json's license is still the 'UNLICENSED' placeholder (XS-1664, Legal owned)."
}

# --- Validate, then pack -------------------------------------------------------------------------
Write-Host ""
Write-Host "Validating manifest..."
& mcpb validate $manifest
if ($LASTEXITCODE -ne 0) {
    Write-Error "mcpb validate failed."
    exit 1
}

$out = Join-Path $stage "x1-search.mcpb"
if (Test-Path $out) { Remove-Item $out -Force }

Write-Host ""
Write-Host "Packing..."
& mcpb pack $package $out
if ($LASTEXITCODE -ne 0) {
    Write-Error "mcpb pack failed."
    exit 1
}

$sizeMB = [math]::Round((Get-Item $out).Length / 1048576, 2)
Write-Host ""
Write-Host "Built MCPB desktop extension: $out ($sizeMB MB)"
