---
name: repoyak-cli
description: Host and manage local A2A agents with RepoYak, discover their cards through AI Catalog, and delegate tracked work using a2a. Includes acquiring RepoYak, ai-catalog, and a2a executables. Use for starting or stopping hosted agents, registering knowledge workspaces, discovering local agents, or sending work to RepoYak.
---

# RepoYak CLI

This skill is distributed by the `repoyak` Agent Plugins 1.0 plugin. Loading the plugin
does not install executables, start a host, or grant permission to invoke agents.

RepoYak is a Spec-Works project at the public repository
`spec-works/RepoYak`. Package publication is version-specific and gated by the release workflow;
verify the requested version is available before installation. Source and binary distributions use MIT.

RepoYak hosts agents whose workspaces supply knowledge, instructions, skills, and public
identity. The backing storage is not part of the public agent interface.

| Tool | Responsibility |
|---|---|
| `repoyak` | Start/stop the host and add, list, or remove its agents |
| `ai-catalog` | Discover agents and obtain their card URLs |
| `a2a` | Inspect cards, send messages, and fetch or cancel tracked tasks |

## Acquire the executables

Check existing installations first. Install/upgrade only with the user's permission, using their
approved package manager or release-download method. These instructions are not standing consent.

### RepoYak

The .NET tool package is `SpecWorks.RepoYak.Cli`; its command is `repoyak`.
Installation requires .NET SDK 10 or later. RID packages support Windows and macOS x64 and ARM64
and include the .NET/ASP.NET and Copilot runtimes. Install the manifest package, not its
`.win-x64`, `.win-arm64`, `.osx-x64`, or `.osx-arm64` implementation packages.
Verify the approved version is available from the chosen feed.

If the user supplies a local feed containing all five matching packages:

```powershell
dotnet tool install --global SpecWorks.RepoYak.Cli --version <approved-version> `
  --add-source <approved-package-directory>
repoyak --version
```

After confirming a public version exists, the approved public-feed installation is:

```powershell
dotnet tool install --global SpecWorks.RepoYak.Cli --version <approved-version>
```

Stop the foreground host before `dotnet tool update`; do not overwrite a running installation.
Package installation is not account authentication or permission to start inference.

Use a supplied executable or build from an authorized checkout of `spec-works/RepoYak`.
From that checkout:

```powershell
dotnet publish .\src\RepoYak.Api\RepoYak.Api.csproj `
  -c Release -r win-arm64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true `
  -o .\publish\win-arm64
.\publish\win-arm64\repoyak.exe list
```

Building requires the .NET 10 SDK; the self-contained executable needs no separate .NET runtime.
Choose the destination RID, for example `win-x64`, rather than assuming Windows means ARM64.
On macOS, publish with `-r osx-arm64` (Apple Silicon) or `-r osx-x64` (Intel), omit
the single-file options, and invoke `repoyak` without `.exe`. Keep the entire publish directory.
Use physical workspace paths: symlinked ancestors, including `/var` and `/tmp` on macOS,
are rejected. For tests, set `TMPDIR` to its physical path with `pwd -P`.
Agent execution requires an authenticated GitHub Copilot runtime and appropriate subscription.
Report authentication failures; do not pass tokens in CLI arguments or workspace files.

Use the full executable path or the user's normal PATH setup. Examples below assume `repoyak`
resolves to the supplied binary.

### AI Catalog CLI: `ai-catalog`

Check `ai-catalog --version` and `ai-catalog --help`.

Windows:

```powershell
winget install --id AgentCard.AICatalog --exact
```

Open a new terminal if PATH has not refreshed.

macOS/Linux:

```bash
brew tap Agent-Card/ai-catalog-cli https://github.com/Agent-Card/ai-catalog-cli
brew trust Agent-Card/ai-catalog-cli
brew install ai-catalog
```

Alternative with Rust: `cargo install ai-catalog-cli`. The crate is `ai-catalog-cli`; the
executable is **`ai-catalog`**.

Prebuilt archives: <https://github.com/Agent-Card/ai-catalog-cli/releases/latest>.
Select the OS/architecture, verify against the accompanying SHA-256 checksum, extract, and put
`ai-catalog` on PATH. Use the release's actual asset names; do not guess a version or filename.

### A2A CLI: `a2a`

Check `a2a version` and `a2a help`.

Windows:

```powershell
winget install --id a2aproject.a2acli --exact
```

macOS/Linux:

```bash
brew tap a2aproject/a2a-cli https://github.com/a2aproject/a2a-cli
brew install a2a
```

Prebuilt archives: <https://github.com/a2aproject/a2a-cli/releases/latest>.
Select the platform, extract, and put `a2a` on PATH.

Alternative with Go: `go install github.com/a2aproject/a2a-cli@latest`. This installs **`a2a-cli`**;
rename it to `a2a` or consistently invoke that actual filename.

Use installed command help as the authority for exact syntax. Do not substitute an older,
unrelated A2A client or invent flags when a command fails.

## Run the host: no Windows Service

Start a normal foreground host in one terminal:

```powershell
repoyak start
```

`start` restores saved registrations, or starts with an empty catalog on first run. An optional
`repoyak start "C:\agents\portfolio"` seeds a new registry. The single-path form remains an
alias for that seeded form. Subsequent starts reload saved registrations without resurrecting
agents previously removed. With no initial path there is no root/default A2A agent: discover through
`/catalog` and use `/agents/{id}`; never assume the first catalog entry is a default agent.

Leave the terminal running. Ctrl+C or `repoyak stop` shuts down the host and all its agents while
preserving registrations and saved sessions. `repoyak start --background` detaches the host on
Windows and appends logs to `host.log` in its state directory. `serve` remains a foreground alias.
On macOS, use foreground `start`; `--background` is not supported. No launch agent is installed.
Do not register a
Windows Service, scheduled task, or auto-start mechanism unless explicitly requested. From an
agent harness, use its supported long-lived process/terminal mechanism and verify
`http://127.0.0.1:5080/healthz` responds before claiming readiness.

In another terminal:

```powershell
repoyak add "C:\agents\ai-catalog"
repoyak add "C:\other\portfolio" --id another-portfolio
repoyak add "C:\agents\assistant" --id assistant --persona margo
repoyak list
repoyak remove another-portfolio
```

`add` registers an agent in an **already running host**. `remove` targets an **agent ID**,
cancels/joins its outstanding A2A work, and unregisters it.
It does not delete the workspace or saved session. Obtain specific approval before removing an
agent that may have active work. Re-registering the same ID and workspace resumes its session.

Registration starts no Copilot process and loads no session. RepoYak lazily starts **one shared
Copilot runtime** on first work (or `/new-session`), then creates/resumes distinct per-agent sessions.
Each request targets its agent's session object with that workspace's configuration; the runtime has
no global "current agent" to switch. Different agents can work concurrently without sharing history.
Stopping one agent disposes its session, not the shared runtime or another agent's session.
Loaded sessions remain resident until stopped, rotated, or host shutdown; idle eviction and runtime
pools are not implemented. A shared runtime failure affects all agents; report errors rather than
assuming automatic failover or starting replacement conversations.

`list` returns JSON with `id`, `workspacePath`, `status`, `error`, and `cardUrl`. Failed agents
remain visible for diagnosis but are excluded from discovery; fix the error, then remove and
re-add to retry. If a management command times out after connecting, its outcome is unknown:
run `list` before retrying rather than creating duplicate registrations.

### IDs and configuration

Use `--persona <installed-custom-agent-name>` to select an optional persona independently of
the friendly ID. `--id` and `--persona` may appear in either order. The name is persisted and
reported by `list`; absent means the default agent. SDK-native discovery resolves installed
agents from the workspace's agent roots and the host user's `~\.copilot\agents` directory.
No definition is copied into the workspace.

Because runtime startup is lazy, persona availability is checked on first work. An unknown or
disabled name fails explicitly; do not claim registration success verifies persona availability.
Selection is session-scoped, reapplied on resume and `/new-session`, and never changes peer agents.
Remove/re-add the same ID and workspace to change the persona; saved history is preserved.
Only send `/new-session` on explicit user instruction when fresh history is wanted.

The persona does not grant access to private Margo memory, app session history, or external tools
such as Work IQ. Workspace and tool restrictions still apply. Do not promise those capabilities
merely because `--persona margo` is configured.

- The folder name becomes a lowercase hyphen-separated ID: `AI Catalog` -> `ai-catalog`.
  `--id` overrides it. IDs are 1-64 lowercase ASCII letters/digits separated by single hyphens.
- Duplicate IDs and workspace paths are rejected, not silently renamed.
- Management uses a same-OS-user named pipe, not an HTTP administration API.
- `REPOSITORY_AGENT_PORT` selects the host (default `5080`); set the same value in both terminals.
- `REPOAGENT_STATE_DIRECTORY` overrides saved state; default:
  `%LOCALAPPDATA%\AgentDock\hosts\port-5080`.
- Optional `REPOAGENT_ALLOWED_ROOTS` restricts registration to semicolon-separated absolute roots.
  Without it, the trusted same-user CLI may register any supported local workspace.
- `REPOSITORY_AGENT_MODEL` selects the inference model when starting the host.

Set configuration before starting the host:

```powershell
$env:REPOSITORY_AGENT_PORT = "5081"
$env:REPOAGENT_ALLOWED_ROOTS = "C:\agents;D:\knowledge"
repoyak start
```

Set the same port in the control-command terminal. Current workspaces must be local Git
repositories; URL cloning is not implemented. Paths through junctions/symbolic links are rejected.

### Named catalogs and explicit configuration

One host exposes multiple catalogs at `/catalogs/{name}` on the same port. `/catalog` aliases
`/catalogs/default`. Agents remain host-wide at `/agents/{id}` and can appear in several catalogs
without creating separate runtimes, sessions, or task stores. Membership is not access control.

All commands accept `--config <json-path>` and `--port <port>`. The JSON file persists host ID,
port, agent registrations, and catalog memberships. Startup creates it if absent. An explicit port
overrides the file and is persisted; otherwise the file overrides the port environment variable.
Runtime state and logs use `<config-path>.state`. Always pass the same config path when managing
that host. Legacy defaults remain supported; configurations without memberships migrate to `default`.
Stop before manually editing the file; restart loads edits. Selecting a different config does not
automatically move saved sessions.

```powershell
repoyak start --config "D:\agents\host.json" --port 5081 --background
repoyak add "D:\github\microsoft\work-iq" --id work-iq-support --catalog support --config "D:\agents\host.json"
repoyak add "D:\github\microsoft\work-iq" --id work-iq-support --catalog engineering --config "D:\agents\host.json"
repoyak list --catalog support --config "D:\agents\host.json"
```

`add` with the same workspace/ID and an explicit catalog adds membership idempotently, without
changing persona or history. New agents without `--catalog` join `default`.
`remove <id> --catalog <name>` removes only membership, leaving the agent callable even if no
catalog references it. `remove <id>` unregisters it globally. `list` returns all agents including
their catalog memberships. Catalog names follow the agent-ID rules; adding creates catalogs and
empty catalogs are retained. Independent processes still require different ports.

## Discover agents through AI Catalog

```powershell
ai-catalog catalog add repoyak-local http://127.0.0.1:5080/catalog
ai-catalog search portfolio --json
ai-catalog catalog update repoyak-local
```

Catalog clients cache documents; refresh after starting/stopping agents. If the name is already
registered, update it rather than treating a failed `catalog add` as a host outage.

Entries have a stable `identifier` URN including host identity and agent ID, type
`application/a2a-agent-card+json`, `url` pointing to the agent card, and descriptive metadata/tags.
Get the actual identifier from search; do not invent it from the folder name.

```powershell
ai-catalog show "<identifier-from-search>" --scope repoyak-local --json
```

Pass the entry's `url` to `a2a card get`. Cards live at
`/agents/{id}/.well-known/agent-card.json`; they advertise HTTP+JSON at `/agents/{id}` and
JSON-RPC compatibility at `/agents/{id}/a2a`. Prefer actual card
interfaces over constructing URLs. **HTTP+JSON is the preferred binding**; the A2A CLI calls it
`rest`. Select `--transport rest` rather than defaulting to JSON-RPC.
The friendly `/agents/{id}` URL also serves the card for clients that interpret a URL with a path
as a direct card URL rather than appending the well-known discovery path.

RepoYak deliberately uses loopback HTTP. Some catalog clients restrict pulling HTTP artifacts:
report a policy rejection and use the A2A client's supported discovery operation for this trusted
local endpoint. Never disable transport security for a remote service to force a pull.

## Send bounded work and retain task IDs

```powershell
$agent = "http://127.0.0.1:5080/agents/portfolio"
a2a card get $agent
$reply = a2a send -a $agent --transport rest --context-id "caller-topic" -o json `
  "Summarize current project priorities and cite workspace evidence." | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "The A2A request failed." }
$taskId = $reply.task.id
```

Work returns a **Task**: ID in **`task.id`**, state in `task.status.state`, and results in
`task.artifacts[].parts[].text`. Do not look for `message.taskId` or treat every response as a
plain Message. Verify the task state before claiming success; an ID alone is not completion.

Retain the task ID, caller context ID, and agent/card URL together. Use the **same agent URL**:

```powershell
a2a task get -a $agent --transport rest $taskId -o json
a2a task list -a $agent --transport rest -o json
a2a task cancel -a $agent --transport rest $taskId
```

For long work, `a2a send --async` returns identifiers without waiting; fetch the task afterwards.
Use installed help for polling flags. **Prefer streaming for interactive work**:

```powershell
a2a send -a $agent --transport rest --stream --context-id "caller-topic" -o jsonl `
  "Review the workspace documents and report progress."
```

This uses HTTP+JSON with server-sent events, not JSON-RPC. JSONL contains individual stream events,
not one `$reply` object: retain task IDs from task/status/artifact events and inspect the terminal
status. Submitted and Working arrive first; public assistant intent and messages arrive as Working
status updates with text in `statusUpdate.status.message.parts[].text`. Result artifacts and
Completed follow. Do not count a status message as a new task or a completed result. Reasoning
events and raw tool data are not exposed. Token-by-token result artifact deltas are not implemented.
For a single final response object, omit `--stream` and use `-o json` as above.

To continue an existing nonterminal task:

```powershell
a2a send -a $agent --transport rest --stream --task-id $taskId --context-id "caller-topic" "Here is the clarification."
```

A revision after completion is a **new task**, optionally in the same caller context. Do not reuse
a terminal task ID. Input-required/approval bridging is not implemented by this host; do not
promise it based solely on the CLI's capabilities.

## Sessions are separate from tasks

Each agent keeps one persistent Copilot session across callers and A2A contexts, with serialized
turns. Different agents have separate sessions and task stores. Changing a caller context does
**not** reset receiving history.

Explicitly start fresh history for one agent:

```powershell
a2a send -a $agent --transport rest "/new-session"
```

This control and empty requests return plain Messages, not Tasks. Rotate only on an explicit user
request: it discards active conversational continuity. Saved sessions resume across host restart,
but A2A task records are in-memory and disappear on restart or unregistering. Save results
externally when durable accountability is needed. Completion means execution finished, not that
the caller accepted the outcome.

## Safety

A2A is loopback-only but unauthenticated. Local callers can invoke automatically approved editing
and command tools under the host account. Separate agents are not filesystem sandboxes or
caller-security boundaries; all callers of one agent share its history. Use trusted callers and
workspaces; do not expose the host remotely without appropriate security.

Send bounded outcomes, not ambient authorization. Never put secrets in requests or profiles.
Workspace content goes to the configured Copilot inference service; use only approved data.
Runtime discovery of supported Copilot configuration is enabled, including MCP server definitions;
explicit host settings take precedence, with no host-imposed server-name exclusions. Discovered
servers may launch local commands or contact external services under the host account; trust the
repository and configured servers. Their tool calls are automatically approved without per-call
confirmation. Authentication and managed policies still apply. Repository
MCP configuration requires Copilot folder trust; registration does not bypass it. Repository file
hooks remain disabled. Restart the host to apply configuration changes to loaded sessions.

Installation references:
<https://github.com/Agent-Card/ai-catalog-cli#install> and
<https://github.com/a2aproject/a2a-cli#installation>.
