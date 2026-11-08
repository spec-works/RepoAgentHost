[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [ValidateSet('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier,
    [string] $DotNetExecutable = 'dotnet',
    [string] $NuGetSource = 'https://api.nuget.org/v3/index.json'
)

$ErrorActionPreference = 'Stop'
$PackageDirectory = (Resolve-Path $PackageDirectory).Path
$rid = $RuntimeIdentifier
if ($rid -notin @('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64')) {
    throw "Tool smoke checks require Windows or macOS x64 or ARM64; current RID is $rid."
}
$windowsPackage = $rid.StartsWith('win-')
$hostExecutable = if ($windowsPackage) { 'rahcli.exe' } else { 'rahcli' }
$runtimeExecutable = if ($windowsPackage) { 'copilot-runtime.exe' } else { 'copilot-runtime' }
$copilotExecutable = if ($windowsPackage) { 'copilot.exe' } else { 'copilot' }
$runtimeLibrary = if ($windowsPackage) { 'copilot_runtime.dll' } else { 'libcopilot_runtime.dylib' }
if ($IsMacOS) {
    $physicalTemp = & /bin/sh -c 'cd "${TMPDIR:-/tmp}" && pwd -P'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the physical temporary directory.' }
    $env:TMPDIR = $physicalTemp.TrimEnd('/') + '/'
}

$package = Join-Path $PackageDirectory "SpecWorks.RepoAgentHost.Cli.$rid.$Version.nupkg"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $prefix = "tools/any/$rid/"
    foreach ($required in @(
        $hostExecutable, 'rahcli.runtimeconfig.json', 'LICENSE', 'specs.json', 'plugin.json',
        'skills/repo-agent-host-cli/SKILL.md',
        "runtimes/$rid/native/$copilotExecutable",
        "runtimes/$rid/native/$runtimeExecutable",
        "runtimes/$rid/native/$runtimeLibrary",
        "runtimes/$rid/native/plugins/browser-use/.release-target",
        "runtimes/$rid/native/plugins/computer-use/.release-target"
    )) {
        if ($null -eq $archive.GetEntry("$prefix$required")) {
            throw "Package is missing $prefix$required."
        }
    }
} finally {
    $archive.Dispose()
}

# Keep the nested NuGet runtime executable path below Windows process-launch path limits.
$toolDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "rah-$([Guid]::NewGuid().ToString('N').Substring(0, 16))"
New-Item -ItemType Directory -Path $toolDirectory | Out-Null
try {
    & $DotNetExecutable tool install SpecWorks.RepoAgentHost.Cli --version $Version --tool-path $toolDirectory `
        --source $PackageDirectory --source $NuGetSource --arch $rid.Substring(4)
    if ($LASTEXITCODE -ne 0) { throw "Local tool installation failed ($LASTEXITCODE)." }

    $command = Join-Path $toolDirectory $(if ($windowsPackage) { 'rahcli.cmd' } else { 'rahcli' })
    $versionOutput = & $command --version
    if ($LASTEXITCODE -ne 0 -or $versionOutput -ne "RepoAgentHost $($Version.Split('-')[0])") {
        throw "Unexpected tool version: $versionOutput"
    }
    & $command --help
    if ($LASTEXITCODE -ne 0) { throw "Tool help failed ($LASTEXITCODE)." }

    $nativeDirectory = Join-Path 'runtimes' (Join-Path $rid 'native')
    $runtime = Get-ChildItem $toolDirectory -Recurse -Force -File -Filter $runtimeExecutable |
        Where-Object { $_.DirectoryName.EndsWith($nativeDirectory, [StringComparison]::Ordinal) } |
        Select-Object -First 1
    if ($null -eq $runtime) {
        throw "Installed tool is missing the $rid runtime. Use a matching-architecture .NET SDK."
    }
    $installedHost = Join-Path $runtime.Directory.Parent.Parent.Parent.FullName $hostExecutable
    if (!$windowsPackage) {
        foreach ($executable in @($command, $installedHost, $runtime.FullName,
            (Join-Path $runtime.DirectoryName $copilotExecutable))) {
            $mode = [System.IO.File]::GetUnixFileMode($executable)
            if (($mode -band [System.IO.UnixFileMode]::UserExecute) -eq 0) {
                throw "Installed executable is missing execute permission: $executable"
            }
        }
    }
    & $DotNetExecutable run (Join-Path $PSScriptRoot 'verify-runtime.cs') -- `
        $runtime.FullName (Join-Path $toolDirectory 'runtime-state') `
        $installedHost
    if ($LASTEXITCODE -ne 0) { throw "Bundled Copilot runtime handshake failed ($LASTEXITCODE)." }
    Write-Output "Verified installed RepoAgentHost tool and bundled runtime for $rid."
} finally {
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $toolDirectory -Recurse -Force
            break
        } catch [System.UnauthorizedAccessException], [System.IO.IOException] {
            if ($attempt -eq 5) { throw }
            Write-Output "Waiting for temporary runtime file locks to release (attempt $attempt)."
            Start-Sleep -Milliseconds 500
        }
    }
}
