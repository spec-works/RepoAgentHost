# NuGet tool packaging and release

## Package contract

- Package ID: `SpecWorks.RepoAgentHost.Cli`
- Command: `rahcli`
- Installer: .NET SDK 10 or later
- Platforms: `win-x64`, `win-arm64`, `osx-x64`, and `osx-arm64`
- Deployment: self-contained, including ASP.NET Core and the SDK-bundled Copilot runtime

`dotnet pack` uses .NET 10's platform-specific tool packaging: one manifest NuGet package points
to four RID-specific packages of the same version. There is no platform-agnostic fallback: a
Windows Copilot runtime must not be selected on another OS. Keep every generated package in the
same feed. The .NET installer selects the matching implementation.

The SDK's version-pinned, checksum-verified build targets acquire and register Copilot assets
for each RID. Tool packaging preserves that publish layout, including hidden plugin marker files,
the consumer skill, MIT license, and specification linkset. Builds need NuGet.org and the SDK's
configured runtime download source; installed execution does not download a replacement runtime.

## Local build and verification

From the repository root in PowerShell:

```powershell
dotnet test .\RepoAgentHost.sln -c Release
dotnet pack .\src\RepoAgentHost.Api\RepoAgentHost.Api.csproj -c Release `
  -p:Version=0.2.0-preview.1 --output .\artifacts\packages
.\scripts\verify-tool.ps1 -PackageDirectory .\artifacts\packages -Version 0.2.0-preview.1
```

The verification script checks the matching package's required assets, installs the manifest
into a disposable tool directory, and exercises `rahcli --version` and `--help`. It then
starts the installed Copilot runtime through the SDK and checks a RPC ping without creating a
session, running tools, or invoking a model. It uses isolated runtime state and disables logged-in
user authentication. It does not alter the real RepoAgentHost host, registrations, or conversations.
It also launches the installed host on a temporary loopback port with a new registry, checks health
and catalog HTTP responses, and terminates that helper process.
It also checks same-user CLI registration, listing, removal, shutdown, and persistence after
restarting the host, without starting an agent inference session. Temporary installation/state
files are cleaned up.

On macOS, use a physical temporary directory: `/var` is a symlink to `/private/var`, and the
host deliberately rejects workspace paths through symlinks. From the repository root:

```bash
export TMPDIR="$(cd "${TMPDIR:-/tmp}" && pwd -P)/"
dotnet test RepoAgentHost.sln -c Release
dotnet pack src/RepoAgentHost.Api/RepoAgentHost.Api.csproj -c Release \
  -p:Version=0.2.0-preview.1 --output artifacts/packages
pwsh -File scripts/verify-tool.ps1 \
  -PackageDirectory artifacts/packages -Version 0.2.0-preview.1
```

Use a native ARM64 SDK for `osx-arm64` or a native x64 SDK for `osx-x64`. The verifier checks
the installed Mac executable and shared-library names, plugin marker files, and executable
permissions before exercising the runtime and host. macOS `start` is foreground-only; use a
second terminal for management commands. No launch agent or system service is installed.

An optional `-RuntimeIdentifier` declares one of the four packages expected by the check.
Use a matching-architecture SDK: .NET 10's manifest selection can follow the installer architecture
even when `--arch` is supplied. For example, on Windows ARM64 with an x64 SDK installed:

```powershell
.\scripts\verify-tool.ps1 -PackageDirectory .\artifacts\packages -Version 0.2.0-preview.1 `
  -RuntimeIdentifier win-x64 -DotNetExecutable "C:\Program Files\dotnet\x64\dotnet.exe"
```

That x64 verification runs under Windows emulation; it is not a native x64 CI result.
NuGet.org remains available during local installation for the .NET installer's apphost package.
If that endpoint is unavailable, `-NuGetSource <approved-feed>` selects an alternative feed for
that dependency; the RepoAgentHost packages still come from `-PackageDirectory`.

## Gated GitHub workflow

`.github/workflows/nuget-tool.yml` is manually dispatched with a required version and an optional
`publish` boolean, defaulting to false:

1. Test and pack all four implementations.
2. Install and verify packages on native Windows and macOS x64 and ARM64 runners.
3. Upload the five `.nupkg` files as the `nuget-tool` artifact.
4. Only when explicitly requested, enter the `nuget` environment and push RID packages first,
   then the manifest, to NuGet.org.

Packing runs on macOS to preserve Unix executable permissions in the Mac packages.
Windows packages are cross-built there and still require native Windows verification.

The workflow must be committed/pushed before it is available in Actions; preparing it locally is
not a completed CI or publication run. A failed smoke check blocks publishing. Partial publication
is possible if a later push fails; inspect NuGet before retrying. `--skip-duplicate` permits
retrying already-published versions but cannot replace immutable packages.

### One-time publisher setup

- Confirm access to the `SpecWorks.RepoAgentHost.Cli` package ID and RID-suffixed IDs with the chosen
  NuGet.org account. Project affiliation does not establish NuGet prefix ownership.
- Create the repository's `nuget` environment with required reviewers and restrict allowed branches
  to the intended release branch. Repository plan/visibility may limit environment protection;
  confirm those controls are available rather than assuming they work.
- Configure environment secret `NUGET_API_KEY` with the smallest package push scope covering the
  five IDs. Never put its value in source, instructions, or command examples.
- Confirm the repository can run `windows-11-arm` hosted jobs; if unavailable, use an approved
  native Windows ARM64 runner rather than deleting the verification gate.
- Confirm native Mac x64 and ARM64 runners are available. Cross-compilation or Rosetta execution
  alone does not verify native support. Direct downloadable Mac distributions additionally need
  an appropriate signing/notarization strategy before public release.
- Review bundled dependencies' redistribution terms and private-source/metadata exposure before
  approving the first public package.

No feed credential, public package, visibility change, automatic tag-triggered publication, or
release upload is created by this mechanism.

## Updating an installed tool

After publication, stop the host before updating its installed files:

```powershell
rahcli stop
dotnet tool update --global SpecWorks.RepoAgentHost.Cli
```

`rahcli stop` shuts down the host and preserves registrations. Ctrl+C also stops a foreground
`start` process. `rahcli remove <id>` unregisters just that agent. NuGet-tool and self-contained executable installations share the
normal host state, so never run two instances on the same port/state directory.
