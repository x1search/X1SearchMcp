@echo off
:: Copyright (c) 2026 X1 Discovery, Inc.
::
:: Licensed under the MIT License (copyright only). See the LICENSE file in
:: the repository root for the full license text.
::
:: This license does not grant, and shall not be construed as granting, any
:: patent rights. See the PATENTS file in the repository root.

:: ============================================================
:: bump.bat - Bump the connector version in version.props
::
:: Usage:
::   bump.bat            Increment the revision (1.0.0.1 -> 1.0.0.2)
::   bump.bat 1.1.0.0    Set an explicit version
::
:: Thin wrapper over bump.ps1, which holds the logic - same split as
:: build-installer.bat / build-plugin.ps1.
:: ============================================================

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bump.ps1" %*
exit /b %errorlevel%
