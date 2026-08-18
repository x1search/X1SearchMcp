// Copyright (c) 2026 X1 Discovery, Inc.
//
// Licensed under the MIT License (copyright only). See the LICENSE file in
// the repository root for the full license text.
//
// This license does not grant, and shall not be construed as granting, any
// patent rights. See the PATENTS file in the repository root.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("X1McpBridge")]
[assembly: AssemblyDescription("Model Context Protocol bridge for X1 Search (WCF)")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("X1McpBridge")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: Guid("a8f3c2d1-4e5b-6a7c-8d9e-0f1a2b3c4d5e")]
// AssemblyVersion/AssemblyFileVersion are NOT declared here. They are generated into
// VersionInfo.g.cs at build time from X1Mcp\version.props (see the GenerateVersionInfo target
// in X1McpBridge.csproj), so the exe and the daemon can never drift apart. Run bump.bat.
[assembly: InternalsVisibleTo("X1McpBridge.Tests")]
