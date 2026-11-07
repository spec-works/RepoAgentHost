# RepoAgentHost

**A Spec-Works project for hosting workspace-grounded agents through standard discovery and A2A.**

RepoAgentHost is currently incubated in the public
[spec-works/RepoAgentHost repository](https://github.com/spec-works/RepoAgentHost).
The repository is hosted by the `spec-works` organisation. Formerly called AgentDock,
the project is now RepoAgentHost and its CLI is
`rahcli`. Local checkout paths remain unchanged.

The [host contract](docs/host-contract.md) documents RepoAgentHost-specific behaviour.
[specs.json](specs.json) links the underlying specifications in the Spec-Works linkset convention.
See [contributing](CONTRIBUTING.md) and the [MIT license](LICENSE).

This is an **incubating host/tool**, not a published conformant Spec-Works part.
[ADR 0001](adr/0001-spec-works-incubation.md) records the affiliation and staging exceptions.

A local multi-agent A2A host backed by the GitHub Copilot SDK for .NET. Each configured workspace
supplies an agent's knowledge, instructions, skills, and descriptive identity. Each agent owns a
persistent Copilot session and a separate A2A task store. Workspaces currently use local Git
repositories, but their public agent cards do not expose that implementation detail.

The host source repository and the grounding repository are separate. No portfolio content is
copied into this repository.

## Install as a .NET tool

The repository builds `SpecWorks.RepoAgentHost.Cli`, invoked as `rahcli`.
The first tool packages target **Windows x64 and ARM64**. A small manifest package selects the
matching RID package, which includes both the .NET/ASP.NET runtimes and the SDK's Copilot runtime
assets. Installation requires the **.NET 10 SDK or later** for platform-specific tool support;
no separate .NET runtime or Copilot executable is required after installation.

**Not published to NuGet.org yet.** Once a version is published:

```powershell
dotnet tool install --global SpecWorks.RepoAgentHost.Cli
rahcli start
```

To build and install from an authorized local checkout:

```powershell
dotnet pack .\src\RepoAgentHost.Api\RepoAgentHost.Api.csproj -c Release `
  -p:Version=0.2.0-preview.1 --output .\artifacts\packages
dotnet tool install --global SpecWorks.RepoAgentHost.Cli --version 0.2.0-preview.1 `
  --add-source .\artifacts\packages
rahcli --version
rahcli start
```

Keep all three generated packages together: the manifest plus `.win-x64` and `.win-arm64`
packages. Do not install the RID packages directly. See [tool packaging and release](docs/nuget-tool.md)
for the local verification command and the gated publication workflow.

## Requirements

- .NET 10 SDK to build from source or install the NuGet tool
- A GitHub Copilot subscription and a Copilot SDK runtime authenticated for your account
- The `a2a` CLI for A2A discovery and smoke tests

The application binds only to `127.0.0.1`. Its A2A endpoint is unauthenticated. Tool permission
requests are approved automatically so the headless agent can edit repository files and run local
commands as the service account. Any local process that can reach the endpoint can therefore make
those changes. Use only with a trusted local caller and repository; do not expose it beyond loopback
without adding authentication and an approval mechanism.

## Run locally

From the repository root, `run.ps1` restores the persisted agent registrations:

```powershell
.\run.ps1
```

Pass another local Git repository path directly when needed:

```powershell
.\run.ps1 -RepositoryPath "D:\github\some-repository"
```

An initial repository path is optional when starting the host. It seeds a new registry only;
otherwise `start` restores saved registrations, or creates an empty registry on first run.
Workspace paths are not read from an environment variable. The folder name supplies the friendly agent ID:
`Portfolio` becomes `portfolio`, `AI Catalog` becomes `ai-catalog`.

### Host and agent lifetimes

The host is an ordinary application. **No Windows Service, installation, or service
manager is involved.** `start` runs in the foreground by default. Use `start --background` on
Windows to detach it; logs are appended to `host.log` in the host state directory. `serve` remains
a foreground alias. Use other CLI invocations to manage agents or shut down the host:

```powershell
# Terminal 1: start the host; Ctrl+C shuts down the host and all its agents.
.\publish\win-arm64\rahcli.exe start

# Terminal 2: register another workspace and start its agent in the running host.
.\publish\win-arm64\rahcli.exe add "D:\github\darrelmiller\ai-catalog"
.\publish\win-arm64\rahcli.exe add "D:\github\another\portfolio" --id another-portfolio
.\publish\win-arm64\rahcli.exe add "D:\github\darrelmiller\margo" --id assistant --persona margo
.\publish\win-arm64\rahcli.exe list
.\publish\win-arm64\rahcli.exe remove another-portfolio
.\publish\win-arm64\rahcli.exe stop
```

`start` and `stop` control the **host**. `stop` shuts down all agents but preserves their registrations.
`add` and `remove` manage **agents** in an already running host. `remove` cancels and joins
outstanding A2A work before unregistering the agent. It does not delete workspace
files or saved session state. Re-registering the same ID and workspace resumes its saved session.
Other agents continue serving while registrations change.

Registration does not start a Copilot CLI process or load a conversation. The host owns **one shared
Copilot runtime**, started lazily on the first work request or explicit `/new-session`. Concurrent
first requests are serialized only for runtime initialization, so they cannot spawn duplicate
processes. Each agent creates/resumes its own session with its own workspace, skills, configuration,
and event subscription; requests target that session object, never a global "current session".
Different agents can execute concurrently on the shared runtime.

Stopping an agent disposes only its session, not the shared runtime or other agents' sessions.
The shared runtime is disposed when the host shuts down. Loaded sessions currently stay resident
until their agent stops, `/new-session` replaces them, or the host shuts down; idle eviction and a
multi-runtime pool are not implemented. A runtime failure affects all agents sharing it and is
reported; there is no silent session replacement or automatic failover.

Equivalent development commands:

```powershell
.\run.ps1 -Command start -Background
.\run.ps1 -Command add -RepositoryPath "D:\github\darrelmiller\ai-catalog"
.\run.ps1 -Command add -RepositoryPath "D:\github\another\portfolio" -AgentId another-portfolio
.\run.ps1 -Command list
.\run.ps1 -Command remove -AgentId another-portfolio
.\run.ps1 -Command stop
```

IDs are 1-64 lowercase ASCII letters/digits separated by single hyphens. Automatic IDs use the
folder name, not a README heading that may change. Duplicate IDs and duplicate workspace paths are
rejected; supply `--id` to distinguish repositories with the same folder name. An ID is not a
Copilot session ID.

Registrations persist under `%LOCALAPPDATA%\AgentDock\hosts\port-5080\agents.json` by default.
The legacy `AgentDock` state directory, management pipe prefix, and catalog URNs are intentionally
retained for compatibility. Renaming does not move saved sessions or create new agent identities.
Existing `REPOSITORY_AGENT_PORT` and `REPOAGENT_*` configuration names remain supported.
The initial path seeds a new registry only; on subsequent host starts the saved registrations are
restored, including the absence of agents previously removed. Agents that fail to start are shown
by `list` with their error, but are excluded from discovery. Remove and re-add to retry them.

Without an explicit initial path, there is no default agent or root A2A alias. Discover agents
through `/catalog` and use their `/agents/{id}` URLs. Passing an initial path retains the legacy
root aliases for that workspace; the host never guesses a default from the catalog.

CLI control uses a local named pipe restricted to the same OS user as the host. There is **no
HTTP administration API** or management token. An optional `REPOAGENT_ALLOWED_ROOTS` setting
(semicolon-separated absolute paths) restricts which workspaces may be registered. Without it,
the same-user CLI may register any supported local workspace. Paths through junctions or symbolic
links are rejected.

The A2A agent card does not expose its workspace provider. To set its public name and description,
add `.agent/agent-profile.json` to the workspace:

```json
{
  "name": "Portfolio",
  "description": "A private knowledge base for project decisions and connections."
}
```

If that file is absent, the name comes from the root `README.md` level-one heading and the
description from its first prose paragraph. If neither source provides a value, generic defaults
are used. The runtime supplies the A2A endpoint and protocol fields.

By default, the server listens on `http://127.0.0.1:5080`. Override the port with
`REPOSITORY_AGENT_PORT` and the model with `REPOSITORY_AGENT_MODEL`. If no model is configured,
the Copilot runtime selects its default model.

Use the same `REPOSITORY_AGENT_PORT` for host and CLI commands. Separate ports address separate
hosts. `REPOAGENT_STATE_DIRECTORY` overrides the host's registry and session-state directory;
one process may own a registry directory at a time.

### Named catalogs and a configurable registry

One host can expose multiple catalogs on the same port. Agents are registered once at
`/agents/{id}`; catalogs reference those shared agents and do not create additional sessions.
`/catalogs/{name}` serves a named AI Catalog document. `/catalog` remains an alias for
`/catalogs/default`. Catalog names follow the same lowercase hyphen-separated rules as agent IDs.

Use `--config` on every command to select a JSON configuration file. `start` creates it if
missing; `add` and `remove` persist agent registrations and memberships there. `--port` overrides
the file's port and is saved by the host on startup. Otherwise the file's port takes precedence
over `REPOSITORY_AGENT_PORT`, with `5080` as the fallback.

```powershell
rahcli start --config "D:\agents\host.json" --port 5081 --background
rahcli add "D:\github\microsoft\work-iq" --id work-iq-support --catalog support --config "D:\agents\host.json"
rahcli add "D:\github\microsoft\work-iq" --id work-iq-support --catalog engineering --config "D:\agents\host.json"
rahcli list --catalog support --config "D:\agents\host.json"
rahcli remove work-iq-support --catalog support --config "D:\agents\host.json"
rahcli stop --config "D:\agents\host.json"
```

Both catalogs advertise the same card URL and stable agent identifier. Repeating `add` with the
same workspace/ID and `--catalog` adds membership without replacing its runtime or history;
it cannot change the agent's workspace or persona. Without `--catalog`, new registrations join
`default`, and duplicate registrations remain errors.

`remove <id> --catalog <name>` removes only membership. Even after the last membership is removed,
the agent stays registered and callable at its known URL. `remove <id>` unregisters it everywhere.
`list` shows all agents with their catalog names; `list --catalog <name>` filters membership.
Named catalogs are created by `add --catalog` and remain available when empty. Unknown catalogs
return an error, and failed agents are not advertised.

The generated configuration contains `hostId`, `port`, `agents`, and `catalogs` (catalog names
mapped to arrays of agent IDs). Existing configurations without `catalogs` are migrated in place
to `default`, preserving host and agent identities. Stop the host before manually editing the
file; edits are loaded on restart, not watched live.

With explicit `--config`, runtime state and background logs live in `<config-path>.state`, and a
`<config-path>.lock` file prevents concurrent ownership. Without `--config`, legacy state paths
remain unchanged. Selecting a different configuration starts a separate deployment; existing
session files are not automatically moved to its state directory. Independent host processes
still require different ports. The management pipe includes the selected config identity so a
command cannot accidentally manage a different configuration on the same port.

The development wrapper accepts `-Config`, `-Port`, and `-Catalog`, for example:

```powershell
.\run.ps1 -Command start -Config "D:\agents\host.json" -Port 5081 -Background
.\run.ps1 -Command list -Config "D:\agents\host.json" -Catalog support
```

## Publish a Windows ARM64 binary

On the Windows ARM64 development machine, publish a self-contained executable:

```powershell
dotnet publish .\src\RepoAgentHost.Api\RepoAgentHost.Api.csproj `
  -c Release -r win-arm64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true `
  -o .\publish\win-arm64
```

Run the published executable to restore the registry:

```powershell
.\publish\win-arm64\rahcli.exe start
```

The agent scans text files when it receives a question, so changes to bundle documents are picked up
without restarting. Skill directories are discovered when a Copilot session is created; restart the
agent to refresh changed or added skills.

## Talk to it with A2A CLI

```powershell
a2a card get http://127.0.0.1:5080
a2a send -a http://127.0.0.1:5080 --transport rest --stream "What projects are active?"

# Each agent has independent discovery, task management, and message endpoints.
a2a card get http://127.0.0.1:5080/agents/portfolio
a2a send -a http://127.0.0.1:5080/agents/portfolio --transport rest --stream -o jsonl "What projects are active?"
```

Health check: `http://127.0.0.1:5080/healthz`

Work responses contain `task.id` and result artifacts, rather than a plain message. Use task
operations against the **same agent URL** that created the task. The original root discovery and
`/a2a` URLs remain aliases for the initial workspace when it is registered; its card advertises
the friendly-ID endpoint.

HTTP+JSON is the preferred card interface, at `/agents/{id}`. JSON-RPC remains available at
`/agents/{id}/a2a` for existing clients. All agents support SSE streaming. Use `--transport rest
--stream` with `a2a send`; `-o jsonl` is useful for processing individual stream events. Submitted,
Working, public assistant intent/message updates, result artifacts, and Completed are streamed
as A2A events. Assistant updates are Working status events with a text message, not separate
tasks. Reasoning and raw tool arguments/results are not forwarded. Final text is delivered as a
result artifact; streaming does not yet emit token-by-token artifact deltas.

### AI Catalog discovery

`GET http://127.0.0.1:5080/catalog` returns `application/ai-catalog+json` with entries of type
`application/a2a-agent-card+json`, pointing to each available agent's card. Entries are generated
from the live registry, not a separately maintained list. Catalog identifiers include a durable
host identity plus the friendly agent ID, so IDs from separate hosts do not collide.

```powershell
ai-catalog catalog add local-agents http://127.0.0.1:5080/catalog
ai-catalog search portfolio --json
ai-catalog catalog update local-agents
```

Catalog clients cache documents; refresh after starting or stopping agents.

### Custom agent personas

Register an installed custom-agent name with `--persona`; it is independent of the friendly ID
and the workspace's public card:

```powershell
.\publish\win-arm64\rahcli.exe add "D:\knowledge\assistant" --id assistant --persona margo
.\run.ps1 -Command add -RepositoryPath "D:\knowledge\assistant" -AgentId assistant -Persona margo
```

`list` includes the persisted `persona` name. Existing registrations without it still run the
default agent. The SDK resolves the name from available custom agents, including workspace agent
roots and the host user's `~\.copilot\agents` directory. RepoAgentHost uses the SDK's native
session-scoped selection API rather than parsing agent definitions itself. Persona selection is
validated on first work, not during registration, to preserve lazy runtime startup. An unavailable
or disabled persona fails the request; it never silently falls back to the default agent.

Selection is reapplied before work on created/resumed sessions and when `/new-session` creates
a replacement. It affects only that agent's session, not other sessions on the shared runtime.
To change a registered persona, remove and re-add the same ID/workspace with the new option
(or omit it to return to the default). This preserves the saved conversation; request `/new-session`
explicitly if you want fresh history.

Selecting Margo does not import this app's conversation, grant private-memory filesystem access,
or configure Work IQ. Existing workspace/tool boundaries and disabled hook/MCP discovery still
apply. RepoAgentHost does not copy persona definitions into the workspace or expose private persona
paths in public cards.

### Consumer skill

[The RepoAgentHost CLI skill](skills/repo-agent-host-cli/SKILL.md) teaches consumers how to acquire
the `ai-catalog` and `a2a` CLIs, run and manage RepoAgentHost, discover cards, and track tasks.
The repository root is an [Agent Plugins 1.0](https://agent-plugins.org/specification) plugin
named `repo-agent-host`, with a root `plugin.json` and a discoverable `skills/repo-agent-host-cli/SKILL.md`.
Install the repository or the generated plugin ZIP using a compatible client's plugin installer.
See [plugin distribution and installation](docs/plugin.md). Installation syntax is client-specific;
the standard does not define a universal install command.

Alternatively, install just the skill in a directory supported by your agent harness:

```powershell
$destination = Join-Path $HOME ".agents\skills\repo-agent-host-cli"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item "skills\repo-agent-host-cli\SKILL.md" (Join-Path $destination "SKILL.md")
```

Use your harness's documented skill root if different. The skill does not install executables
automatically. Publishing also copies it alongside the executable as
`skills\repo-agent-host-cli\SKILL.md`.

## Repository discovery and safety

- The configured path must be a Git repository. The agent scans supported text formats, skips
  common build, dependency, and Git directories, and does not follow directory junctions or
  symlinks outside normal traversal.
- Skills are found by locating `SKILL.md` files and passing their skill roots to the Copilot SDK.
  Repository instructions and local custom agents are loaded, but automatic configuration discovery
  is disabled so repository-provided MCP servers and hooks are not started.
- Tool calls run under the current Windows account. The agent is instructed to preserve unrelated
  changes and not commit, push, change remotes, or perform destructive actions unless requested.
- The A2A endpoint has no authentication yet. Loopback binding prevents remote network access, not
  access by other processes running on the same machine.
- Responses should cite repository-relative source paths and distinguish current facts from dated
  or exploratory material.
- Agents are instructed to check repository freshness and use `git pull --ff-only` on a clean
  branch with a configured upstream. Local changes, divergence, missing upstream, and update
  failures must be reported rather than bypassed. External service calls are not categorically
  prohibited by the host prompt; credential and private-data protections still apply.
- One Copilot session is shared by all work for each agent workspace, regardless of the caller's A2A
  context ID. Turns are serialized. Different callers share history; this is not caller isolation.
- Different agents have independent sessions, turn gates, and task stores, and can work concurrently.
  This separates their conversation history, not their OS permissions. They run under the same
  account with the same automatically approved tool permissions; it is not a sandbox.
- Each work message creates an A2A task with Submitted, Working, and Completed states and a result
  artifact. Messages targeting an existing nonterminal task continue that task. A2A context IDs remain
  correlation handles, not Copilot session selectors. The SDK handles task errors and cancellation.
- Send `/new-session` to explicitly rotate the agent's session. Empty messages and this control
  operation return messages, not tasks. Status enquiries should use A2A `task get`, not a work message.
- The active Copilot session ID is stored in the host state directory under `sessions`, scoped to
  the friendly agent ID and canonical workspace path, and resumed on the next work request after
  restart. Reusing an ID for a different workspace does not reuse its old session. Existing
  single-agent session state is imported if available. Resume failures are reported rather than
  replaced with a fresh session. Only one running agent may own a workspace at a time, even across
  hosts.
- A2A task records currently use the SDK's in-memory store and do not survive restart, even though
  the Copilot session does. Task completion means execution finished, not that the caller accepted
  the outcome. Interactive input/approval bridging is not implemented yet.
- Retrieved repository content is sent to the configured Copilot runtime for inference. Do not point
  the agent at data that is not permitted to leave the machine through that inference service.
