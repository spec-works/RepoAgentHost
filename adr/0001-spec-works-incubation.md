# ADR 0001: Spec-Works incubation without repository transfer

## Status

Superseding amendment on 2026-11-08: the product is RepoYak under Spec-Works branding.
The solution is `RepoYak.sln`, projects/namespaces are `RepoYak.Api` and `RepoYak.Api.Tests`,
the executable/command is `repoyak`, the package is `SpecWorks.RepoYak.Cli`, the plugin is
`repoyak`, and the consumer skill is `repoyak-cli`. The GitHub repository is renamed to
`spec-works/RepoYak`; local checkout paths are unchanged. Existing state paths,
management pipe names, catalog URNs, environment variables, and saved session references
remain compatible. No `rahcli` command alias is shipped. The renamed package requires a
separate install, not an update of `SpecWorks.RepoAgentHost.Cli`. Package publication and
remote repository renaming are separate actions, now authorized alongside release
`0.2.0-preview.4`. Earlier amendments remain historical context.

Superseding amendment on 2026-11-04: the repository is now hosted at
`spec-works/RepoAgentHost`. The solution is `RepoAgentHost.sln`, the source and test projects
are `RepoAgentHost.Api` and `RepoAgentHost.Api.Tests`, and their C# namespaces match those names.
This completes the deferred internal project rename. The executable remains `rahcli` and the
package remains `SpecWorks.RepoAgentHost.Cli`. Local checkout paths are unchanged. Legacy
state paths, management pipe prefix, catalog URNs, and configuration names remain unchanged
to preserve existing registrations, session continuity, and discovery identifiers. Earlier
ownership, naming, and staging decisions below are retained as historical context.

Superseding amendment on 2026-11-01: at the user's request the repository was made public, then
renamed to `darrelmiller/RepoAgentHost`. The product is RepoAgentHost (formerly AgentDock), CLI
`rahcli`, package `SpecWorks.RepoAgentHost.Cli`, plugin `repo-agent-host`, and skill
`repo-agent-host-cli`. Organisation transfer remains unperformed. The original decisions below
describe their historical context; private visibility and the earlier product/remote names no
longer apply. Legacy state paths, pipe prefix, catalog URNs, and configuration names remain
unchanged to preserve existing registrations, session continuity, and discovery identifiers.

Accepted on 2026-11-01 for affiliation, ownership, and licensing.
Publication and full packaging alignment remain deferred.

Subsequently amended on 2026-11-01: the user requested a NuGet-tool mechanism and selected
Windows x64/ARM64 first. `SpecWorks.AgentDock.Cli` now has platform-specific self-contained
packaging and a manually triggered, gated publication workflow. Public publication is still deferred.

Amended on 2026-11-01 at the user's request: the GitHub repository is now
`darrelmiller/agentdock`, formerly `darrelmiller/portfolioagent`. Ownership and private visibility
are unchanged. The earlier URL/name retention decision is superseded only for the remote
repository name; local checkout and installed executable paths are retained.

## Context

AgentDock implements a local multi-agent host over A2A, AI Catalog, Agent Skills, and the
GitHub Copilot SDK. It fits Spec-Works' specification-first interoperability goals, but the
repository is private and still named `darrelmiller/portfolioagent`. The user chose to establish
Spec-Works affiliation while retaining that ownership for now, and explicitly approved MIT.

## Decision

Keep the current repository owner, visibility, and runtime/session state unchanged.
Use the renamed `darrelmiller/agentdock` repository URL.
Use AgentDock as the public project/product name, MIT licensing with the current owner's
copyright, and a root RFC 9264-style `specs.json` linkset. Document the implemented host contract
and add normal build/test CI.

Do not publish a registry entry, release, or package as a side effect of affiliation.
Spec-Works' part conventions are the target, not a claim that every requirement is already met.

### Staging exceptions

- Keep the existing solution, `PortfolioAgent` internal namespaces, and `src`/`tests` paths
  during incubation. `SpecWorks.AgentDock` namespace naming and a PascalCase repository
  name was initially deferred; the user subsequently chose lowercase `agentdock` for the remote.
  Internal namespace and checkout-path migration remain deferred.
- Target .NET 10 for this executable host, matching the SDK integration. The multi-target
  library convention does not establish that older frameworks can run this host.
- Continue the standalone executable alongside the .NET tool. Tool packaging uses a manifest and
  matching Windows RID packages; installation/runtime checks gate publishing. No NuGet.org
  publication is established by preparing this mechanism.
- Source Link, library XML documentation, and DocFX publication are deferred
  until there is a distributable package/documentation plan. These are not represented as complete.
- Current regression tests establish implementation behaviour, not protocol-wide conformance.

## Consequences

The project has clear specification and contribution entry points without exposing private
source or disturbing saved agents. Repo transfer remains possible later, independently of
affiliation. Before public publication, resolve the staging exceptions, review private content,
confirm transport conformance and platform support, and choose registry/release/package ownership.

## References

- [Spec-Works conventions](https://github.com/spec-works/spec-works.github.io/blob/main/docs/specification/conventions.md)
- [Spec-Works profile](https://github.com/spec-works/.github/blob/main/profile/README.md)
- [Host contract](../docs/host-contract.md)
- [Specification linkset](../specs.json)
