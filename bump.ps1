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
  Bumps the connector version in version.props.

.DESCRIPTION
  version.props is the only place the version lives. X1McpBridge.exe and X1McpGraphQL.exe both
  read it at build time, so they cannot disagree - which is the point: the version exists to
  answer "which binary am I talking to", and two writers would defeat it.

  The number identifies a connector RELEASE, not a pair of files: the Lean flavor (the customer
  default) ships only X1McpBridge.exe, with no net10 daemon at all. What must remain true either
  way is that binaries from one release never report different versions - ProxyMode's relay-identity
  check and x1_version both rely on it.

  Invoked via bump.bat. The logic lives here rather than in the .bat because extracting and
  rewriting an XML element in cmd means for/f token splitting, which silently produced the
  element NAME instead of its value when this was first written.

.PARAMETER Version
  Explicit four-part version to set (e.g. 1.1.0.0). Omit to increment the revision.
#>
param(
    [Parameter(Position = 0)]
    [string]$Version
)

$ErrorActionPreference = "Stop"

# Read/write via the .NET APIs, not Get-Content/Set-Content. In Windows PowerShell 5.1
# Get-Content -Raw decodes a BOM-less UTF-8 file as CP1252 - which turned plugin.json's em-dash
# into "â€”" - and Set-Content -Encoding utf8 writes a BOM these files did not have. Both of
# those corrupt a file this script is only supposed to change one number in.
function Read-TextFile([string]$Path) {
    return [System.IO.File]::ReadAllText($Path)   # decodes UTF-8, BOM optional
}
function Write-TextFile([string]$Path, [string]$Text) {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $utf8NoBom)
}

$props = Join-Path $PSScriptRoot "version.props"
if (-not (Test-Path $props)) {
    Write-Error "version.props not found next to bump.ps1 (expected '$props')."
    exit 1
}

$raw = Read-TextFile $props

$match = [regex]::Match($raw, '<X1McpVersion>\s*([^<\s]+)\s*</X1McpVersion>')
if (-not $match.Success) {
    Write-Error "No <X1McpVersion> element found in version.props."
    exit 1
}
$current = $match.Groups[1].Value

$fourPart = '^\d+\.\d+\.\d+\.\d+$'

if ($PSBoundParameters.ContainsKey('Version') -and $Version) {
    if ($Version -notmatch $fourPart) {
        Write-Error "'$Version' is not a four-part version (e.g. 1.2.3.4)."
        exit 1
    }
    $new = $Version
}
else {
    if ($current -notmatch $fourPart) {
        Write-Error "Current version '$current' is not a four-part version - cannot increment it. Pass an explicit version instead."
        exit 1
    }
    $p = $current.Split('.')
    $new = "{0}.{1}.{2}.{3}" -f $p[0], $p[1], $p[2], ([int]$p[3] + 1)
}

if ($new -eq $current) {
    Write-Host ""
    Write-Host "  Version already $current - nothing to do." -ForegroundColor Yellow
    Write-Host ""
    exit 0
}

# Targeted replace rather than [xml] round-tripping: that would reformat the file and drop the
# comment explaining why this version is deliberately not synced to X1 Search's.
$updated = $raw -replace '<X1McpVersion>\s*[^<\s]+\s*</X1McpVersion>', "<X1McpVersion>$new</X1McpVersion>"
Write-TextFile $props $updated

# Read it back: a bad write here silently ships two exes stamped with a version that never
# existed, which is worse than not versioning at all.
$verify = [regex]::Match((Read-TextFile $props), '<X1McpVersion>\s*([^<\s]+)\s*</X1McpVersion>')
if (-not $verify.Success -or $verify.Groups[1].Value -ne $new) {
    Write-Error "Write-back verification failed: version.props does not now contain '$new'."
    exit 1
}

# --- Keep the derived manifests in step ----------------------------------------------------
# A manifest's version must be semver (MAJOR.MINOR.PATCH) - a four-part string is not valid there -
# so the connector's REVISION becomes the manifest's PATCH: 1.0.0.7 -> 1.0.7.
#
# This is not cosmetic. Claude Code and GitHub Copilot both pin a plugin to whatever version string
# its manifest declares, and only ship users an update when that string changes - so a manifest left
# at a fixed version means republishing silently delivers nothing.
#
# ANY NEW MANIFEST MUST BE REGISTERED IN $derivedManifests BELOW, or it will never update for users
# and nothing will say so.
$np = $new.Split('.')

# BUILD has no slot in the 3-part mapping. Bumping only the revision (the normal path) never trips
# this; an explicit version that moves BUILD would silently collide with, or even go backwards from,
# an already-published version - so say so rather than quietly emit a number that means something
# different.
if ($np[2] -ne '0') {
    Write-Warning "Connector version '$new' has a non-zero BUILD ($($np[2])), which the manifests'"
    Write-Warning "  MAJOR.MINOR.PATCH mapping cannot represent - they all map on REVISION alone."
    Write-Warning "  Every one of them will read $($np[0]).$($np[1]).$($np[3]); check that this still moves forwards."
}

$pluginVersion = "{0}.{1}.{2}" -f $np[0], $np[1], $np[3]

# One writer for the whole mapping, called once per manifest. Three copies of this block is how two
# of them would eventually disagree about what REVISION maps to - and the third would be the one
# nobody noticed had quietly stopped updating.
function Set-JsonVersion([string]$Path, [string]$NewVersion, [string]$Label) {
    $rawJson = Read-TextFile $Path

    # Targeted replace rather than ConvertTo-Json, which would reformat the whole manifest.
    $updatedJson = $rawJson -replace '("version"\s*:\s*")[^"]*(")', "`${1}$NewVersion`${2}"
    if ($updatedJson -eq $rawJson) {
        Write-Error "Could not update '$Path': no \"version\" field matched."
        exit 1
    }
    Write-TextFile $Path $updatedJson

    # Read it back for the same reason version.props is: a bad write here ships a package stamped
    # with a version that never existed, which is worse than not versioning it at all.
    $verifyJson = [regex]::Match((Read-TextFile $Path), '"version"\s*:\s*"([^"]*)"')
    if (-not $verifyJson.Success -or $verifyJson.Groups[1].Value -ne $NewVersion) {
        Write-Error "Write-back verification failed: $Label does not now contain '$NewVersion'."
        exit 1
    }
}

$derivedManifests = [ordered]@{
    "cowork-plugin\.claude-plugin\plugin.json"  = "Cowork / Claude Code plugin"
    "copilot-plugin\.claude-plugin\plugin.json" = "GitHub Copilot plugin"
    "mcpb-package\manifest.json"                = "MCPB desktop extension"
}

$updatedManifests = [ordered]@{}
foreach ($rel in $derivedManifests.Keys) {
    $manifestPath = Join-Path $PSScriptRoot $rel
    if (Test-Path $manifestPath) {
        Set-JsonVersion $manifestPath $pluginVersion $derivedManifests[$rel]
        $updatedManifests[$rel] = $derivedManifests[$rel]
    }
    else {
        Write-Warning "$rel not found - its version was not updated."
    }
}

Write-Host ""
Write-Host "  Version bumped: $current -> $new" -ForegroundColor Green
foreach ($rel in $updatedManifests.Keys) {
    Write-Host ("  {0,-43} {1}  ({2})" -f $rel, $pluginVersion, $updatedManifests[$rel]) -ForegroundColor Green
}
if ($updatedManifests.Count -gt 0) {
    Write-Host "  semver mapping: the connector's REVISION is each manifest's PATCH." -ForegroundColor Green
}
Write-Host ""
Write-Host "  Rebuild for this to reach the binaries:"
Write-Host "    build.bat  then  build-installer.bat"
Write-Host ""
exit 0
