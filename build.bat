:: Copyright (c) 2026 X1 Discovery, Inc.
::
:: Licensed under the MIT License (copyright only). See the LICENSE file in
:: the repository root for the full license text.
::
:: This license does not grant, and shall not be construed as granting, any
:: patent rights. See the PATENTS file in the repository root.


:: -restore with RestorePackagesConfig pulls packages.config dependencies
:: (log4net, Newtonsoft.Json) into packages\ before building.
"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\bin\MSBuild.exe" X1Mcp.sln -restore /p:RestorePackagesConfig=true /p:Configuration=Release
