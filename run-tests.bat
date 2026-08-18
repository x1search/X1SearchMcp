@echo off
:: Copyright (c) 2026 X1 Discovery, Inc.
::
:: Licensed under the MIT License (copyright only). See the LICENSE file in
:: the repository root for the full license text.
::
:: This license does not grant, and shall not be construed as granting, any
:: patent rights. See the PATENTS file in the repository root.

setlocal EnableDelayedExpansion

:: ============================================================
:: run-tests.bat — Build and run X1McpBridge unit tests
:: Usage: run-tests.bat [Release|Debug]
:: Exit code: 0 = all passed, non-zero = build or test failure
:: ============================================================

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Release

:: ── Locate MSBuild ───────────────────────────────────────────────────────────
:: Paths are stored UNQUOTED and quoted only where expanded. Storing the quotes in the
:: variable instead makes `if "%MSBUILD%"==""` expand to `if ""C:\Program Files (x86)\..."",
:: which cmd fails to parse ("Files was unexpected at this time") - and starting MSBUILD off
:: at a hardcoded default also made both not-found checks below unreachable.
set "MSBUILD="

for %%p in (
  "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\bin\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
) do (
  if exist %%p set "MSBUILD=%%~p"
)
:: Take the full path `where` resolved rather than the bare name: `where` searches the current
:: directory, but when NoDefaultCurrentDirectoryInExePath=1 cmd refuses to *execute* from it,
:: so a bare name can pass the lookup here and then fail to run.
if not defined MSBUILD (
  for /f "delims=" %%n in ('where MSBuild.exe 2^>nul') do if not defined MSBUILD set "MSBUILD=%%n"
)
if not defined MSBUILD (
  echo ERROR: MSBuild.exe not found. Install Visual Studio Build Tools 2022.
  exit /b 1
)
echo [build] MSBuild: %MSBUILD%

:: ── Locate or download nuget.exe ─────────────────────────────────────────────
:: Same full-path rule as MSBuild above - store what `where` resolved, not the bare name.
set "NUGET="
for /f "delims=" %%n in ('where nuget.exe 2^>nul') do if not defined NUGET set "NUGET=%%n"
if not defined NUGET if exist "%~dp0nuget.exe" set "NUGET=%~dp0nuget.exe"
if not defined NUGET (
  echo [nuget] nuget.exe not found — downloading...
  powershell -NoProfile -Command ^
    "Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile '%~dp0nuget.exe'"
  if not exist "%~dp0nuget.exe" (
    echo ERROR: Failed to download nuget.exe
    exit /b 1
  )
  set "NUGET=%~dp0nuget.exe"
)
echo [nuget] nuget: %NUGET%

:: ── Restore packages ─────────────────────────────────────────────────────────
echo.
echo [nuget] Restoring packages...
"%NUGET%" restore "%~dp0X1Mcp.sln" -NonInteractive
if %errorlevel% neq 0 (
  echo ERROR: nuget restore failed
  exit /b 1
)

:: ── Build solution ───────────────────────────────────────────────────────────
echo.
echo [build] Building X1Mcp.sln (%CONFIG%)...
"%MSBUILD%" "%~dp0X1Mcp.sln" /p:Configuration=%CONFIG% /v:minimal /nologo
if %errorlevel% neq 0 (
  echo ERROR: Build failed
  exit /b 1
)

:: ── Locate NUnit console runner ───────────────────────────────────────────────
:: `for /r` with a bare filename (no wildcard) yields <dir>\nunit3-console.exe for every
:: directory it walks, existing or not - so the `if exist` is what makes this a real search
:: rather than "whatever directory happened to be last".
set "NUNIT="
for /r "%~dp0packages" %%f in (nunit3-console.exe) do (
  if exist "%%f" set "NUNIT=%%f"
)
if not defined NUNIT (
  echo ERROR: nunit3-console.exe not found under packages\
  echo        Run: nuget install NUnit.ConsoleRunner -OutputDirectory packages
  exit /b 1
)
echo [test] Runner: %NUNIT%

:: ── Run tests ────────────────────────────────────────────────────────────────
set TEST_DLL=%~dp0X1McpBridge.Tests\bin\%CONFIG%\X1McpBridge.Tests.dll
if not exist "%TEST_DLL%" (
  echo ERROR: Test assembly not found: %TEST_DLL%
  exit /b 1
)

echo.
echo [test] Running tests (%CONFIG%)...
"%NUNIT%" "%TEST_DLL%" ^
  --result="%~dp0TestResults.xml;format=nunit3" ^
  --labels=Before ^
  --timeout=30000
set TEST_EXIT=%errorlevel%

echo.
if %TEST_EXIT% == 0 (
  echo [test] All tests PASSED.
) else (
  echo [test] One or more tests FAILED. See TestResults.xml for details.
)

exit /b %TEST_EXIT%
