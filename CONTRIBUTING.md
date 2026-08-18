# Contributing to X1 Search MCP Bridge

Thanks for your interest in improving this project.

## Before you start

For anything beyond a small fix (a new tool, a change to the wire protocol, a new build flavor),
please open an issue first to discuss the approach. It saves everyone rework.

## Development setup

- Windows 10/11, MSBuild (Visual Studio 2022 Build Tools or full VS), PowerShell.
- `build.bat` / `build.sh` build the solution in Release via MSBuild.
- `build-installer.bat` restores packages, builds, and stages the `installer/` folder (see the
  [README](README.md) for the Lean vs. Full flavor split).
- `run-tests.bat` runs the `X1McpBridge.Tests` suite (NUnit).
- To exercise the MCP server directly without Claude Desktop, see `smoke-test-stdio.ps1`.

Building and testing the bridge itself does not require a running X1 Search index. Exercising it
end-to-end does require X1 Desktop installed with a completed index scan, and `X1ServiceHost`
running under the same Windows user account — see the README's Requirements section.

## Making changes

1. Fork the repo and create a branch off `main`.
2. Keep changes focused — a bug fix shouldn't carry an unrelated refactor.
3. Add or update tests in `X1McpBridge.Tests` for behavior changes.
4. Run `run-tests.bat` and make sure the solution still builds via `build.bat`.
5. Open a pull request describing what changed and why.

## Reporting bugs

Open a GitHub issue with:
- What you expected vs. what happened.
- Steps to reproduce.
- The connector version (`x1_version` tool output, or `X1McpBridge.exe --version`).
- Whether you're on the Lean or Full build flavor.

For security vulnerabilities, please follow [SECURITY.md](SECURITY.md) instead of opening a
public issue.

## Code of Conduct

This project follows the [Code of Conduct](CODE_OF_CONDUCT.md). By participating, you're
expected to uphold it.
