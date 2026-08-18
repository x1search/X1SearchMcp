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
  Fast local smoke test for X1McpBridge.exe's MCP stdio protocol - no X1 Desktop/Code
  restart, no plugin reload, no relay involved.

.DESCRIPTION
  Launches X1McpBridge.exe directly in plain stdio mode (no args - see Program.cs) and
  drives it through a handful of real JSON-RPC requests over its own stdin/stdout,
  exactly as a real MCP client would, asserting on the responses.

  Deliberately NOT --proxy: that mode is a "dumb pipe" shim (see ProxyMode.cs) that
  forwards to whatever shared relay is already running (lazily launching one if not),
  so testing through it could silently validate a DIFFERENT, already-running build
  instead of the exe you just compiled. Plain mode is the real bridge/WCF
  implementation - the same thing the daemon's own McpStdioBridgeClient spawns - so
  it's the one that actually exercises the binary under test.

  This catches things an in-process unit test (McpServerProtocolTests.cs) can't: real
  process startup, real stdio framing, a real line-by-line round trip through
  Console.In/Out. It is NOT a replacement for run-tests.bat's unit suite or a live
  functional pass through the real client - it's a fast first gate to run right after
  a build, before looping in Claude Desktop/Code at all.

  Checks performed:
    1. initialize                 - protocolVersion + serverInfo present
    2. tools/list                 - exactly 17 tools; x1_search schema mentions
                                     multi-table fan-out (byTable) and not the old
                                     "one table only" wording
    3. tools/call x1_version      - well-formed; version matches this exe's own file
                                     version (catches "forgot to rebuild")
    4. tools/call x1_list_sources - well-formed, no JSON-RPC error
    5. tools/call x1_search       - OPTIONAL: needs a live X1ServiceHost. A failure
                                     here is a WARNING, not a FAILURE - this script
                                     validates the build, not live X1 connectivity.

.PARAMETER ExePath
  Path to the X1McpBridge.exe to test. Defaults to the local Release build.

.PARAMETER TimeoutMs
  How long to wait for each response line before giving up. Default 15000 - generous
  enough to cover the x1_search check's own 10000ms tool-level timeout with headroom.

.EXAMPLE
  .\smoke-test-stdio.ps1
.EXAMPLE
  .\smoke-test-stdio.ps1 -ExePath "C:\Users\Stewart Robinson\AppData\Local\X1 Discovery\McpBridge\X1McpBridge.exe"
#>
param(
    [string]$ExePath = (Join-Path $PSScriptRoot "X1McpBridge\bin\Release\X1McpBridge.exe"),
    [int]$TimeoutMs = 15000
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExePath)) {
    Write-Host "ERROR: exe not found at '$ExePath'. Build first (build.bat / run-tests.bat)." -ForegroundColor Red
    exit 1
}

$fileVersion = (Get-Item $ExePath).VersionInfo.FileVersion
Write-Host "Testing: $ExePath"
Write-Host "Version: $fileVersion"
Write-Host ""

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $ExePath
$psi.WorkingDirectory = Split-Path $ExePath -Parent
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$proc = [System.Diagnostics.Process]::Start($psi)
Start-Sleep -Milliseconds 300
if ($proc.HasExited) {
    $stderr = $proc.StandardError.ReadToEnd()
    Write-Host "ERROR: process exited immediately (code $($proc.ExitCode))." -ForegroundColor Red
    if ($stderr) { Write-Host $stderr -ForegroundColor Red }
    exit 1
}

$script:nextId = 1
$script:failures = 0
$script:warnings = 0

function Send-Request {
    param([string]$Method, [hashtable]$Params = @{})
    $req = @{ jsonrpc = "2.0"; id = $script:nextId; method = $Method; params = $Params }
    $script:nextId++
    $json = ($req | ConvertTo-Json -Depth 10 -Compress)
    $proc.StandardInput.WriteLine($json)
    $proc.StandardInput.Flush()

    $task = $proc.StandardOutput.ReadLineAsync()
    if (-not $task.Wait($TimeoutMs)) {
        throw "No response within ${TimeoutMs}ms for method '$Method'"
    }
    $line = $task.Result
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw "Empty/closed response stream for method '$Method'"
    }
    return ($line | ConvertFrom-Json)
}

function Check {
    param([bool]$Condition, [string]$Label, [switch]$Warn)
    if ($Condition) {
        Write-Host "  [PASS] $Label" -ForegroundColor Green
    }
    elseif ($Warn) {
        Write-Host "  [WARN] $Label" -ForegroundColor Yellow
        $script:warnings++
    }
    else {
        Write-Host "  [FAIL] $Label" -ForegroundColor Red
        $script:failures++
    }
}

try {
    Write-Host "1. initialize"
    $r = Send-Request -Method "initialize"
    Check ($r.result.protocolVersion -eq "2024-11-05") "protocolVersion is 2024-11-05"
    Check ($r.result.serverInfo.name -eq "x1-mcp-bridge") "serverInfo.name is x1-mcp-bridge"
    Check ($r.result.serverInfo.version -eq $fileVersion) "serverInfo.version matches file version ($fileVersion, got $($r.result.serverInfo.version))"

    Write-Host "2. tools/list"
    $r = Send-Request -Method "tools/list"
    $tools = @($r.result.tools)
    Check ($tools.Count -eq 17) "exactly 17 tools returned (got $($tools.Count))"
    $searchTool = $tools | Where-Object { $_.name -eq "x1_search" }
    Check ($null -ne $searchTool) "x1_search tool present"
    if ($searchTool) {
        $tablesDesc = $searchTool.inputSchema.properties.tables.description
        Check ($tablesDesc -match "byTable") "tables description mentions multi-table fan-out (byTable)"
        Check ($tablesDesc -notmatch "Do NOT pass multiple tables") "tables description free of stale single-table wording"
    }

    Write-Host "3. tools/call x1_version"
    $r = Send-Request -Method "tools/call" -Params @{ name = "x1_version"; arguments = @{} }
    Check ($r.result.isError -eq $false) "isError is false"
    $versionInfo = $r.result.content[0].text | ConvertFrom-Json
    Check ($versionInfo.component -eq "X1McpBridge") "component is X1McpBridge"
    Check ($versionInfo.version -eq $fileVersion) "reported version matches file version ($fileVersion, got $($versionInfo.version)) - if this fails, you forgot to rebuild"

    Write-Host "4. tools/call x1_list_sources"
    $r = Send-Request -Method "tools/call" -Params @{ name = "x1_list_sources"; arguments = @{} }
    Check ($null -eq $r.error) "no JSON-RPC error"
    $sourcesInfo = $r.result.content[0].text | ConvertFrom-Json
    Check ($null -ne $sourcesInfo.sources) "sources array present"

    Write-Host "5. tools/call x1_search on Files (optional - needs live X1ServiceHost)"
    try {
        $r = Send-Request -Method "tools/call" -Params @{
            name      = "x1_search"
            arguments = @{ tables = @("Files"); query = "*"; limit = 1; timeoutMs = 10000 }
        }
        if ($r.error) {
            Check $false "x1_search returned a JSON-RPC error: $($r.error.message)" -Warn
        }
        else {
            $searchResult = $r.result.content[0].text | ConvertFrom-Json
            Check ($null -ne $searchResult.results) "x1_search returned a well-formed result" -Warn
        }
    }
    catch {
        Check $false "x1_search did not respond in time (is X1ServiceHost running?): $($_.Exception.Message)" -Warn
    }
}
finally {
    try { $proc.StandardInput.Close() } catch {}
    if (-not $proc.WaitForExit(3000)) {
        try { $proc.Kill() } catch {}
    }
}

Write-Host ""
if ($script:failures -eq 0) {
    Write-Host "SMOKE TEST PASSED ($($script:warnings) warning(s))" -ForegroundColor Green
    exit 0
}
else {
    Write-Host "SMOKE TEST FAILED ($($script:failures) failure(s), $($script:warnings) warning(s))" -ForegroundColor Red
    exit 1
}
