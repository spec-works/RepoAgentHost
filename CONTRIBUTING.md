# Contributing to RepoYak

RepoYak is a Spec-Works project, hosted publicly at `spec-works/RepoYak`.
The product, CLI, package, and repository use the RepoYak name. Package publication is gated
separately from source changes by the release workflow.

## Specification-first changes

Read [the host contract](docs/host-contract.md) and [specs.json](specs.json) before changing
behaviour. Use the underlying A2A, AI Catalog, and Agent Skills specifications rather than
introducing a competing protocol. Update the host contract and consumer skill when observable
behaviour changes. Describe intentional deviations and limitations explicitly.

## Local checks

With the .NET 10 SDK, from the repository root:

```powershell
dotnet test .\RepoYak.sln
git diff --check
```

Tests use temporary workspaces and fake inference runtimes. Never require private credentials
or a live Copilot account in normal CI. For live checks, use isolated workspaces and ports;
do not rotate real users' sessions or terminate unrelated processes.

See the README for publishing a self-contained executable. Preserve per-agent session/task
isolation, lazy runtime startup, explicit session rotation, and HTTP+JSON streaming.

For NuGet-tool changes, also follow [tool packaging and release](docs/nuget-tool.md) and run the
disposable installation/runtime handshake check. Keep Windows x64 and ARM64 assets in their
respective RID packages; packaging success alone does not prove an installed command works.

## Contributions and licensing

Submit changes through a pull request with the requested behaviour, relevant specification links,
compatibility implications, and verification evidence. Contributions are under the [MIT license](LICENSE).
Do not commit workspace knowledge, private ledger state, credentials, local session transcripts,
or generated binary bundles.

## Reporting security issues

For a suspected vulnerability, contact the repository owner privately rather than disclosing
secrets or exploit details in a public issue. No dedicated security-reporting address or supported
version policy is established yet. Loopback binding is not an authentication or sandbox guarantee.
