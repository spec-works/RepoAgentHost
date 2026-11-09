param(
    [string]$RepositoryPath,
    [ValidateSet("serve", "start", "stop", "add", "remove", "list")]
    [string]$Command = "start",
    [string]$AgentId,
    [string]$Persona,
    [string]$Config,
    [int]$Port,
    [string]$Catalog,
    [switch]$Background
)

$ErrorActionPreference = "Stop"

$resolvedRepositoryPath = if ($RepositoryPath) { [System.IO.Path]::GetFullPath($RepositoryPath) } else { $null }
$projectPath = Join-Path $PSScriptRoot "src\RepoYak.Api\RepoYak.Api.csproj"
if ($Persona -and $Command -ne "add") {
    throw "-Persona applies to -Command add."
}
if ($Catalog -and $Command -notin @("add", "remove", "list")) {
    throw "-Catalog applies to -Command add, remove, or list."
}
if ($Background -and $Command -ne "start") {
    throw "-Background applies to -Command start."
}
if ($AgentId -and $Command -notin @("add", "remove")) {
    throw "-AgentId applies to -Command add or remove."
}
if ($resolvedRepositoryPath -and $Command -notin @("start", "serve", "add")) {
    throw "-RepositoryPath applies to -Command start, serve, or add."
}

$cliArguments = @(switch ($Command) {
    "serve" {
        if ($resolvedRepositoryPath) { @("serve", $resolvedRepositoryPath) }
        else { @("serve") }
    }
    "start" {
        $startArguments = @("start")
        if ($resolvedRepositoryPath) { $startArguments += $resolvedRepositoryPath }
        if ($Background) { $startArguments += "--background" }
        $startArguments
    }
    "add" {
        if (-not $resolvedRepositoryPath) { throw "Pass -RepositoryPath when registering an agent." }
        $startArguments = @("add", $resolvedRepositoryPath)
        if ($AgentId) { $startArguments += @("--id", $AgentId) }
        if ($Persona) { $startArguments += @("--persona", $Persona) }
        $startArguments
    }
    "stop" { @("stop") }
    "remove" {
        if (-not $AgentId) { throw "Pass -AgentId when removing an agent." }
        @("remove", $AgentId)
    }
    "list" { @("list") }
})

if ($Config) { $cliArguments += @("--config", [System.IO.Path]::GetFullPath($Config)) }
if ($PSBoundParameters.ContainsKey("Port")) { $cliArguments += @("--port", "$Port") }
if ($Catalog) { $cliArguments += @("--catalog", $Catalog) }

dotnet run --no-launch-profile --project $projectPath -- @cliArguments
exit $LASTEXITCODE
