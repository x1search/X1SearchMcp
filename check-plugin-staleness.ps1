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
  Reports whether the currently-installed x1-search Cowork plugin was built from
  this repo's current HEAD, or is stale relative to newer commits.

.DESCRIPTION
  Compares build-info.json (stamped by build-plugin.ps1) inside the deployed
  plugin's connector/ folder against `git rev-parse HEAD` of this repo, and flags
  whether any source path that feeds the plugin (X1McpBridge/, cowork-plugin/,
  skill/, install.ps1, build-installer.bat, and in the Full flavor ../X1McpGraphQL)
  changed since that build.

  This is the check that would have caught XS-1610 shipping stale: the fix
  landed in source on 2026-07-12, but the deployed plugin binary was last
  built 2026-07-11 and nobody rebuilt/redeployed until a user hit the bug.

.PARAMETER InstalledPayloadDir
  Path to the deployed plugin's connector/ folder. Defaults to auto-discovering
  <marketplace>/x1-search/connector under the current user's Claude Code plugins
  dir, falling back to the legacy bin/ name for installs that predate the rename.

.PARAMETER Flavor
  Which flavor's source paths to diff. Defaults to the flavor of the DEPLOYED plugin, read from its
  own build-info.json - staleness is a property of the deployed artifact, not of whatever is being
  built right now. Packages predating the flavor stamp are treated as Full, which is what they were.
  Pass this explicitly only to override that inference.
#>
param(
    [string]$InstalledPayloadDir,

    [ValidateSet("Lean", "Full")]
    [string]$Flavor
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Fail($msg) {
    Write-Warning $msg
    exit 1
}

if (-not $InstalledPayloadDir) {
    $marketplaceRoot = Join-Path $env:USERPROFILE ".claude\plugins\marketplaces"
    # "bin" is the pre-rename layout; still discovered so this reports honestly against a
    # plugin installed before the payload moved off the reserved bin/ name.
    $candidates = @(Get-ChildItem -Path $marketplaceRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { $mp = $_.FullName; @("connector", "bin") | ForEach-Object { Join-Path $mp "x1-search\$_" } } |
        Where-Object { Test-Path $_ })

    if ($candidates.Count -eq 0) {
        Fail "No installed x1-search plugin found under '$marketplaceRoot'. Pass -InstalledPayloadDir explicitly."
    }
    if ($candidates.Count -gt 1) {
        Write-Warning "Multiple installed copies found; checking the first:`n$($candidates -join "`n")"
    }
    $InstalledPayloadDir = $candidates[0]
}

$buildInfoPath = Join-Path $InstalledPayloadDir "build-info.json"
if (-not (Test-Path $buildInfoPath)) {
    Fail "No build-info.json in '$InstalledPayloadDir' - this plugin predates staleness stamping (rebuilt with an older build-plugin.ps1) and can't be verified. Rebuild with build-installer.bat and redeploy."
}

$buildInfo = Get-Content $buildInfoPath -Raw | ConvertFrom-Json
$currentCommit = (git -C $root rev-parse HEAD 2>$null).Trim()

# Infer the flavor from the deployed package unless overridden. A package with no flavor stamp
# predates the split, and everything before the split was Full.
$stampedFlavor = if ($buildInfo.PSObject.Properties.Name -contains "flavor" -and
                     $buildInfo.flavor -in @("Lean", "Full")) { $buildInfo.flavor } else { $null }
$flavorSource = if ($Flavor) { "passed in" }
                elseif ($stampedFlavor) { "from the deployed package" }
                else { "assumed - package predates the flavor stamp" }
if (-not $Flavor) {
    $Flavor = if ($stampedFlavor) { $stampedFlavor } else { "Full" }
}

# Source paths that actually feed the package. install.ps1 and build-installer.bat were previously
# absent from this list even though they ship in / define the package, so a change to either reported
# UP TO DATE. ../X1McpGraphQL only feeds a Full package.
$feedPaths = @("X1McpBridge", "cowork-plugin", "skill", "install.ps1", "build-installer.bat")
if ($Flavor -eq "Full") { $feedPaths += "../X1McpGraphQL" }

Write-Host ""
Write-Host "Installed plugin payload: $InstalledPayloadDir"
Write-Host "  Built from commit:   $($buildInfo.sourceCommit)"
Write-Host "  Built at (UTC):      $($buildInfo.builtAtUtc)"
Write-Host "  Flavor:              $Flavor ($flavorSource)"
if ($Flavor -and $stampedFlavor -and $Flavor -ne $stampedFlavor) {
    Write-Warning "  The flavor passed in ($Flavor) differs from the deployed package's own stamp ($stampedFlavor) - the diff below may cover the wrong source paths."
}
if ($buildInfo.PSObject.Properties.Name -contains "daemonIncluded") {
    Write-Host "  Bundles net10 daemon: $($buildInfo.daemonIncluded)"
    if ($stampedFlavor -eq "Lean" -and $buildInfo.daemonIncluded) {
        Write-Warning "  This Lean package contains X1McpGraphQL.exe - the .NET 10 dependency Lean exists to remove."
    }
}
if ($buildInfo.sourceDirty) {
    Write-Warning "  This build was made from a DIRTY working tree - it may contain uncommitted changes not reflected by its commit hash."
}

if (-not $buildInfo.sourceCommit) {
    Write-Warning "  Status: UNKNOWN - build-info.json has no recorded commit (git was unavailable at build time)."
    exit 2
}

if ($buildInfo.sourceCommit -eq $currentCommit) {
    Write-Host "  Status: UP TO DATE (matches current HEAD $currentCommit)" -ForegroundColor Green
    exit 0
}

Write-Host "  Current HEAD:        $currentCommit"

$changed = $null
try {
    $changed = git -C $root diff --name-only "$($buildInfo.sourceCommit)" $currentCommit -- $feedPaths 2>$null
} catch {
    $changed = $null
}

if ($null -eq $changed) {
    Write-Warning "  Status: UNKNOWN - couldn't diff against $($buildInfo.sourceCommit) (commit not found locally, e.g. rebased away). Rebuild to be safe."
    exit 2
} elseif ($changed) {
    Write-Warning "  Status: STALE - relevant source changed since this build:"
    ($changed -split "`n") | Where-Object { $_ } | ForEach-Object { Write-Warning "    $_" }
    Write-Warning "  Rebuild with build-installer.bat, then redeploy the new X1McpBridge.exe to '$InstalledPayloadDir'."
    exit 2
} else {
    Write-Host "  Status: behind HEAD, but no plugin-relevant source changed - safe to leave as is." -ForegroundColor Yellow
    exit 0
}
