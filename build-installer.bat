@echo off
:: Copyright (c) 2026 X1 Discovery, Inc.
::
:: Licensed under the MIT License (copyright only). See the LICENSE file in
:: the repository root for the full license text.
::
:: This license does not grant, and shall not be construed as granting, any
:: patent rights. See the PATENTS file in the repository root.

setlocal

REM ---------------------------------------------------------------------------
REM build-installer.bat
REM Builds X1McpBridge in Release, then stages a distributable installer package.
REM
REM Usage: build-installer.bat [Release^|Debug] [--lean^|--full]
REM Output: installer\ folder next to this script
REM
REM FLAVORS
REM   Lean (DEFAULT, what customers get)
REM     No X1McpGraphQL daemon, therefore no GraphQL API and NO .NET 10 DEPENDENCY at all. The
REM     shared fan-in relay is served from inside the net4.8 bridge instead ("X1McpBridge.exe
REM     --host", see X1McpBridge\HostMode.cs), on the same port and with the same wire contract, so
REM     --proxy and every registered MCP entry keep working unchanged.
REM     Build requirements: MSBuild + PowerShell only. No .NET SDK needed.
REM
REM   Full (--full, internal/dev)
REM     Additionally publishes and stages the self-contained net10 X1McpGraphQL.exe, which brings
REM     the GraphQL API and the Nitro IDE at http://localhost:5250/graphql.
REM     Build requirements: also needs the .NET 10 SDK on PATH.
REM
REM Why Lean is the default: the default is what ships. Every existing invocation - CI, muscle
REM memory, a bare "build-installer.bat" - silently becomes the customer build, which is the safe
REM direction for an accident. The inverse default means one forgotten flag ships a ~250MB customer
REM package and nothing downstream catches it. The flavor is therefore echoed in this script's
REM banner, in build-info.json, in install.ps1's header and final banner, and in x1_version.
REM
REM One switch sets both effects (no GraphQL, no .NET 10) because they are not two knobs: every
REM GraphQL line in the tree lives in the daemon project, and the daemon IS the .NET 10 dependency.
REM Do not "improve" this into two flags.
REM ---------------------------------------------------------------------------

REM Argument parsing: %1 has historically been CONFIG, so treat it as CONFIG only when it actually
REM names one. Otherwise "build-installer.bat --full" would set CONFIG=--full and fail confusingly
REM later at "msbuild /p:Configuration=--full".
set CONFIG=Release
if /i "%~1"=="Release" set CONFIG=%~1
if /i "%~1"=="Debug"   set CONFIG=%~1

REM Honour a pre-set X1MCP_FLAVOR (so a CI job can pin it once), else default Lean.
if not defined X1MCP_FLAVOR set X1MCP_FLAVOR=Lean
if /i "%X1MCP_FLAVOR%"=="Full" (set FLAVOR=Full) else (set FLAVOR=Lean)

for %%A in (%*) do (
  if /i "%%~A"=="--full" set FLAVOR=Full
  if /i "%%~A"=="/full"  set FLAVOR=Full
  if /i "%%~A"=="--lean" set FLAVOR=Lean
  if /i "%%~A"=="/lean"  set FLAVOR=Lean
)

REM Exported so build-plugin.ps1 / check-plugin-staleness.ps1 inherit it; contained by the setlocal
REM above, so it does not leak back into the caller's shell.
set X1MCP_FLAVOR=%FLAVOR%

echo.
echo  ===================================================
echo   X1 Search MCP Bridge - Build Installer
echo   Configuration: %CONFIG%
echo   Flavor:        %FLAVOR%
if /i "%FLAVOR%"=="Lean" echo                  ^(no GraphQL API, no .NET 10 dependency^)
if /i "%FLAVOR%"=="Full" echo                  ^(GraphQL API + Nitro, bundles the net10 daemon^)
echo  ===================================================
echo.

REM ---------------------------------------------------------------------------
REM Locate MSBuild  (VS 2025 / VS 2022 / VS 2019 full installs + BuildTools)
REM ---------------------------------------------------------------------------

set MSBUILD=
for %%P in (
  "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
  "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
) do (
  if exist %%P (
    set MSBUILD=%%P
    goto :found_msbuild
  )
)

echo  ERROR: MSBuild not found. Install Visual Studio 2019, 2022, or 2025 (any edition including Build Tools).
exit /b 1

:found_msbuild
echo  MSBuild: %MSBUILD%

REM ---------------------------------------------------------------------------
REM Restore NuGet packages
REM ---------------------------------------------------------------------------

set SCRIPT_DIR=%~dp0
set SLN=%SCRIPT_DIR%X1Mcp.sln
set NUGET=%SCRIPT_DIR%nuget.exe

if not exist "%NUGET%" (
  echo.
  echo  Downloading nuget.exe...
  powershell -NoProfile -Command "Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile '%NUGET%'"
  if errorlevel 1 (
    echo  ERROR: Failed to download nuget.exe
    exit /b 1
  )
)

echo.
echo  Restoring NuGet packages...
"%NUGET%" restore "%SLN%" -NonInteractive
if errorlevel 1 (
  echo  ERROR: NuGet restore failed.
  exit /b 1
)

REM ---------------------------------------------------------------------------
REM Build
REM ---------------------------------------------------------------------------

echo.
echo  Building solution (%CONFIG%)...
%MSBUILD% "%SLN%" /p:Configuration=%CONFIG% /p:Platform="Any CPU" /m /nologo /v:minimal
if errorlevel 1 (
  echo  ERROR: Build failed.
  exit /b 1
)
echo  Build succeeded.

REM ---------------------------------------------------------------------------
REM Publish the shared X1McpGraphQL daemon (self-contained, single-file win-x64)
REM FULL FLAVOR ONLY. In Lean the shared relay is "X1McpBridge.exe --host" instead, so nothing
REM below runs and the .NET 10 SDK is not a build requirement at all.
REM
REM Guarded with `goto` rather than by wrapping the block in `if (...)`: the block contains
REM `if exist ... rmdir` and a caret-continued multi-line `dotnet publish`, which is genuinely
REM parse-fragile inside a parenthesised cmd block, and skipping over it leaves the Full path
REM byte-for-byte unchanged - which is what makes "prove Full is unaffected" a two-line diff.
REM ---------------------------------------------------------------------------

if /i not "%FLAVOR%"=="Full" (
  echo.
  echo  Flavor is Lean - skipping the X1McpGraphQL daemon publish.
  echo    The shared relay will be "X1McpBridge.exe --host" ^(net4.8, no .NET 10 dependency^).
  goto :skip_daemon
)

echo.
echo  Publishing X1McpGraphQL daemon (self-contained win-x64)...
set X1MCPGRAPHQL_PROJ=%SCRIPT_DIR%..\X1McpGraphQL\X1McpGraphQL\X1McpGraphQL.csproj
set DAEMON_PUBLISH_OUT=%SCRIPT_DIR%X1McpGraphQL\publish-%CONFIG%

if not exist "%X1MCPGRAPHQL_PROJ%" (
  echo  ERROR: X1McpGraphQL.csproj not found at %X1MCPGRAPHQL_PROJ%
  echo  Expected the X1McpGraphQL project as a sibling of this one ^(..\X1McpGraphQL^).
  echo  Or omit --full to build the Lean flavor, which does not need it.
  exit /b 1
)

REM Checked explicitly so "the .NET SDK isn't installed" reports as that, rather than as a bare
REM errorlevel out of dotnet publish below.
where dotnet >nul 2>nul
if errorlevel 1 (
  echo  ERROR: 'dotnet' was not found on PATH, so the net10 daemon cannot be published.
  echo  Install the .NET 10 SDK, or omit --full to build the Lean flavor, which needs no .NET SDK.
  exit /b 1
)

if exist "%DAEMON_PUBLISH_OUT%" rmdir /s /q "%DAEMON_PUBLISH_OUT%"

dotnet publish "%X1MCPGRAPHQL_PROJ%" -c %CONFIG% -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o "%DAEMON_PUBLISH_OUT%"
if errorlevel 1 (
  echo  ERROR: X1McpGraphQL publish failed.
  exit /b 1
)
echo  X1McpGraphQL daemon published.

:skip_daemon

REM ---------------------------------------------------------------------------
REM Stage installer package
REM ---------------------------------------------------------------------------

set BUILD_OUT=%SCRIPT_DIR%X1McpBridge\bin\%CONFIG%
set STAGE=%SCRIPT_DIR%installer

echo.
echo  Staging installer to: %STAGE%

if exist "%STAGE%" (
  echo  Cleaning previous installer staging...
  rmdir /s /q "%STAGE%"
)
mkdir "%STAGE%"

REM Copy all binaries
echo  Copying binaries...
for %%F in (
  "%BUILD_OUT%\X1McpBridge.exe"
  "%BUILD_OUT%\X1McpBridge.pdb"
  "%BUILD_OUT%\Contracts.dll"
  "%BUILD_OUT%\Contracts.pdb"
  "%BUILD_OUT%\X1.Common.dll"
  "%BUILD_OUT%\X1.Common.pdb"
  "%BUILD_OUT%\X1.Common.dll.config"
  "%BUILD_OUT%\X1.Common.XmlSerializers.dll"
  "%BUILD_OUT%\Newtonsoft.Json.dll"
  "%BUILD_OUT%\ICSharpCode.SharpZipLib.dll"
  "%BUILD_OUT%\HtmlAgilityPack.dll"
  "%BUILD_OUT%\ChilkatDotNet48.dll"
  "%BUILD_OUT%\log4net.dll"
  "%BUILD_OUT%\Microsoft.IdentityModel.dll"
  "%BUILD_OUT%\PLUSManaged.dll"
  "%BUILD_OUT%\PLUSManaged.XmlSerializers.dll"
  "%BUILD_OUT%\Xceed.Compression.dll"
  "%BUILD_OUT%\Xceed.FileSystem.dll"
  "%BUILD_OUT%\Xceed.FileSystem.Windows.dll"
  "%BUILD_OUT%\Xceed.Zip.dll"
) do (
  if exist %%F (
    copy /Y %%F "%STAGE%\" >nul
  ) else (
    echo   WARNING: Not found: %%F
  )
)

REM Copy the shared X1McpGraphQL daemon (self-contained single-file exe + its config).
REM Full flavor only; skipped entirely in Lean rather than allowed to warn, because a warning that
REM fires on every single customer build trains people to ignore warnings.
if /i not "%FLAVOR%"=="Full" goto :skip_daemon_stage

if exist "%DAEMON_PUBLISH_OUT%\X1McpGraphQL.exe" (
  copy /Y "%DAEMON_PUBLISH_OUT%\X1McpGraphQL.exe" "%STAGE%\" >nul
  echo  Copied X1McpGraphQL.exe ^(shared daemon^)
) else (
  REM Hard error, not a warning: a publish that "succeeded" without producing the exe would
  REM otherwise stage a package whose bridge is new and whose daemon is missing or stale - the
  REM install shape that qa-plugin-install-workflow.md records as a known defect, and which
  REM install.ps1 then rejects anyway.
  echo  ERROR: Not found: %DAEMON_PUBLISH_OUT%\X1McpGraphQL.exe
  echo  The publish reported success but produced no daemon exe; refusing to stage a Full package
  echo  without it.
  exit /b 1
)
if exist "%DAEMON_PUBLISH_OUT%\appsettings.json" (
  copy /Y "%DAEMON_PUBLISH_OUT%\appsettings.json" "%STAGE%\" >nul
  echo  Copied appsettings.json ^(daemon config^)
)

:skip_daemon_stage

REM Copy the config (built into bin\Release\ via CopyToOutputDirectory in the csproj)
if exist "%BUILD_OUT%\x1mcp.config.json" (
  copy /Y "%BUILD_OUT%\x1mcp.config.json" "%STAGE%\" >nul
  echo  Copied x1mcp.config.json from build output.
) else (
  echo  NOTE: x1mcp.config.json not found in build output. Installer will create a default.
)

REM Copy the installer script
copy /Y "%SCRIPT_DIR%install.ps1" "%STAGE%\" >nul
echo  Copied install.ps1

REM Copy the /x1 skill folder (installed as a Claude Code user skill)
if exist "%SCRIPT_DIR%skill" (
  xcopy /E /I /Y "%SCRIPT_DIR%skill" "%STAGE%\skill" >nul
  echo  Copied /x1 skill
) else (
  echo  NOTE: skill folder not found; /x1 skill will not be staged.
)

REM ---------------------------------------------------------------------------
REM Lean payload assertion
REM
REM The entire point of the Lean flavor is "no .NET 10". A Lean build that accidentally staged the
REM daemon, or any .NET 5+ runtime marker, is invisible except by total package size - and nobody
REM diffs sizes on every build. So assert it here, where it fails the build, rather than leaving it
REM to a release checklist.
REM ---------------------------------------------------------------------------

if /i "%FLAVOR%"=="Full" goto :skip_lean_assert

echo.
echo  Verifying the Lean package carries no .NET 10 payload...
set LEAN_VIOLATION=
for %%X in (
  "%STAGE%\X1McpGraphQL.exe"
  "%STAGE%\appsettings.json"
  "%STAGE%\appsettings.Development.json"
  "%STAGE%\web.config"
) do if exist %%X (
  echo   ERROR: %%~nxX must not be present in a Lean package.
  set LEAN_VIOLATION=1
)
REM .deps.json / .runtimeconfig.json exist only for .NET Core/5+ assemblies; a net4.8-only payload
REM cannot produce them, so their presence means something non-Framework got staged.
for %%X in ("%STAGE%\*.deps.json" "%STAGE%\*.runtimeconfig.json") do if exist %%X (
  echo   ERROR: %%~nxX indicates a .NET 5+ assembly was staged into a Lean package.
  set LEAN_VIOLATION=1
)
if defined LEAN_VIOLATION (
  echo  ERROR: Lean payload assertion failed - see above.
  exit /b 1
)
echo  Lean package verified: net4.8 only, no .NET 10 dependency.

:skip_lean_assert

REM Build the Cowork plugin (connector + /x1 skill) -> installer\x1-search.plugin
if exist "%SCRIPT_DIR%cowork-plugin\.claude-plugin\plugin.json" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%build-plugin.ps1" -Flavor %FLAVOR%
) else (
  echo  NOTE: cowork-plugin not found; skipping Cowork plugin build.
)

REM Build the MCPB desktop extension -> installer\x1-search.mcpb (Lean payload always, regardless
REM of %FLAVOR% - see build-mcpb.ps1 header for why). Soft-skipped if the mcpb CLI isn't installed,
REM since a missing dev-machine tool must not block the Cowork plugin / installer build.
if exist "%SCRIPT_DIR%mcpb-package\manifest.json" (
  where mcpb >nul 2>nul
  if errorlevel 1 (
    echo  NOTE: 'mcpb' CLI not found on PATH ^(npm install -g @anthropic-ai/mcpb^); skipping .mcpb build.
  ) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%build-mcpb.ps1"
  )
) else (
  echo  NOTE: mcpb-package not found; skipping MCPB build.
)

REM ---------------------------------------------------------------------------
REM Done
REM ---------------------------------------------------------------------------

echo.
echo  Checking whether the currently-deployed plugin is up to date...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%check-plugin-staleness.ps1" -Flavor %FLAVOR%

echo.
echo  ===================================================
echo   Installer package staged to:
echo   %STAGE%
echo.
echo   Flavor: %FLAVOR%
if /i "%FLAVOR%"=="Lean" (
  echo     No GraphQL API and no .NET 10 dependency. The shared relay is
  echo     "X1McpBridge.exe --host". Build needs only MSBuild + PowerShell.
) else (
  echo     GraphQL API + Nitro IDE at http://localhost:5250/graphql, via the
  echo     bundled net10 X1McpGraphQL.exe. Build needed the .NET 10 SDK.
)
echo.
echo   To install for all Claude products (Desktop + Claude Code):
echo     powershell -ExecutionPolicy Bypass -File "%STAGE%\install.ps1"
echo.
echo   To install for Claude Desktop only:
echo     powershell -ExecutionPolicy Bypass -File "%STAGE%\install.ps1" -Target Desktop
echo.
echo   To install for Claude Code (CLI) only:
echo     powershell -ExecutionPolicy Bypass -File "%STAGE%\install.ps1" -Target Code
echo.
echo   To install to a custom directory:
echo     powershell -ExecutionPolicy Bypass -File "%STAGE%\install.ps1" -InstallDir "C:\MyPath"
echo  ===================================================
echo.

endlocal
exit /b 0
