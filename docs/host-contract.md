# RepoAgentHost host contract

RepoAgentHost is a Spec-Works project providing an agent host over workspace knowledge, skills,
instructions, and the GitHub Copilot SDK. This document describes the current 0.2 implementation,
not an independent replacement for the underlying protocol specifications.

## Specification boundaries

[A2A 1.0](https://a2a-protocol.org/v1.0.0/specification/) defines agent cards, messages, tasks,
artifacts, and streaming. [AI Catalog](https://ai-catalog.io/spec/) defines the discovery document.
[Agent Skills](https://agentskills.io/specification) defines workspace skill artifacts.
[specs.json](../specs.json) is the project's specification linkset.

RepoAgentHost-specific contracts are registration, runtime ownership, workspace binding, and
receiving-session continuity. Git is the current workspace provider, not the public agent identity.

## Registration and host lifetime

`rahcli start` restores the persisted registry or initializes an empty one. An optional workspace
argument seeds a new registry only. The process runs in the foreground by default; `--background`
detaches it on Windows and writes `host.log` in the state directory. No OS service is installed.
`serve` remains a foreground alias. `stop` shuts down the host and all agents without removing
their saved registrations or sessions.

`add <repository-path> [--id <id>] [--persona <name>]`, `list`, `remove <id>`, and `stop` control the running host
through a same-user named pipe. Registrations persist independently of process lifetime.
The workspace basename supplies a default lowercase hyphen-separated ID. Duplicate IDs or workspace
paths are rejected. Explicit IDs contain 1-64 lowercase ASCII letters/digits separated by single
hyphens. A persona name is optional and selects a native SDK custom agent; availability is checked
before inference on first work. Missing personas fail explicitly.

`remove` cancels and joins outstanding A2A work, unregisters the agent, and disposes its session.
It does not delete workspace files or saved session state. Restart does not restore removed
registrations. Failed registrations are visible to management but excluded from the public catalog.

Agents are host-wide and catalogs are membership lists, not runtime owners. `add ... --catalog <name>`
creates a catalog or adds the matching existing agent to it without replacing its session.
`remove <id> --catalog <name>` removes only membership; the agent remains registered even with no
memberships. `remove <id>` unregisters it from all catalogs. `list --catalog <name>` filters agents.
Without a catalog option, new registrations join `default` and `list` returns all agents.

All commands accept `--config <json-path>` and `--port <port>`. The config persists host identity,
port, agents, and catalog memberships. Startup creates a missing file; legacy registrations migrate
to `default`. The host exclusively locks the config and updates it atomically for management changes.
Manual edits require a restart. An explicit config stores runtime state in `<config-path>.state`;
without one the legacy state directory remains in use. Management targets both port and config
identity. Background startup propagates those choices to its child process.
Windows retains the legacy pipe name. On macOS, the account, port, and config identity are
hashed into a compact same-user pipe name to fit Unix-domain socket path limits in the
default temporary directory; this does not change the management commands or trust boundary.

## Discovery and transport

| Surface | Contract |
|---|---|
| `GET /catalog` | Default catalog alias |
| `GET /catalogs/{name}` | Live `application/ai-catalog+json` listing ready members; unknown names return 404 |
| `GET /agents/{id}` | Direct agent card |
| `GET /agents/{id}/.well-known/agent-card.json` | Agent card discovery |
| `/agents/{id}` | Preferred HTTP+JSON A2A binding, including SSE |
| `/agents/{id}/a2a` | JSON-RPC compatibility binding |
| `GET /healthz` | Host responsiveness, not inference or persona readiness |

Catalog entries point to agent cards using `application/a2a-agent-card+json`. Identifiers include
the persisted host identity and agent ID. Cards take descriptive identity from workspace
`.agent/agent-profile.json`, then README fallback. Descriptive identity, registration ID, selected
persona, and session ID are distinct.
An agent in several catalogs has the same identifier, card URL, receiving session, and task store
in each. Membership is discovery configuration, not an authorization boundary.

Root A2A aliases exist only when an initial workspace is explicitly provided to `start` or `serve`.
Without one, callers discover through the catalog; no default is inferred from list order.

## Message, task, and session semantics

Each work message creates a tracked task, or continues an addressed nonterminal task. Task stores
are isolated per agent. A caller context ID is a correlation handle, not a receiving-session
selector. A completed-task revision is new work, not a continuation of a terminal task.

Execution emits Submitted, Working, public assistant updates as Working status messages, a final
result artifact, and Completed. Completion means execution finished, not caller acceptance.
Reasoning and raw tool data are not forwarded. Token-by-token artifact deltas and interactive
input/approval bridging are not implemented.

Each agent owns one receiving Copilot session with serialized turns. Separate agents can run
concurrently on a single host-owned runtime. The runtime starts lazily on first work or explicit
`/new-session`; registration starts no Copilot process. Calls target the agent's session object,
not a process-wide current-session selector. Persona selection is session-scoped and reapplied
before work and rotation.

`/new-session` explicitly creates fresh continuity and returns a Message. Empty requests also
return Messages. Session IDs persist and are resumed; resume failures do not silently create new
conversations. A2A task records are currently in-memory and do not survive restart or unregistering.
Loaded sessions have no idle eviction; shared-runtime failure affects all hosted agents.

## Trust boundary

The server binds to loopback. A2A is unauthenticated and editing/command permissions are automatically
approved under the host account. Agent/session separation is not an OS sandbox. All callers of
one agent share its history. Use only trusted local callers and workspaces.

Optional allowed roots restrict registration. Paths through junctions/symlinks are rejected.
Repository MCP/hook auto-discovery is disabled. Selecting a persona does not grant access to app
conversation history, private memory, or external services. Retrieved workspace material goes to
the configured inference runtime; only approved content should be hosted.

## Verification and publication

The host prompt instructs agents to check repository freshness before relying on content and
permits `git pull --ff-only` when the working tree is clean and the current branch has an upstream.
It does not authorize forced synchronization, branch switching, merges, rebases, or hiding local
changes. Update blockers and failures must be reported. This is agent guidance, not an automatic
host startup pull or an enforced sandbox. External service calls are not categorically prohibited
by the host prompt; credentials and private data remain protected.

The test suite covers registry persistence, ID/CLI validation, per-agent task isolation, HTTP+JSON
streaming, progress events, cancellation, and optional personas. Tests do not claim full A2A
conformance or validate every model/runtime capability. Real runtime checks require an authenticated
Copilot account and disposable workspaces; normal CI does not invoke inference.

The source repository is public at
[spec-works/RepoAgentHost](https://github.com/spec-works/RepoAgentHost). Hosting under Spec-Works
does not imply a published registry entry or public release.

[NuGet-tool packaging](nuget-tool.md) builds `SpecWorks.RepoAgentHost.Cli` with command `rahcli`,
using self-contained Windows and macOS x64/ARM64 packages and a .NET 10 manifest. Disposable installation,
runtime handshake, and host HTTP checks gate the manually requested publication job. The
mechanism does not establish a NuGet.org release. macOS runs in the foreground; `--background`
remains Windows-only. macOS packaging includes the matching SDK-bundled Copilot runtime and
requires installed-tool verification on the advertised architecture. It does not install a
launch agent, grant macOS privacy permissions, or notarize a public binary distribution.
