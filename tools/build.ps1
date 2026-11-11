param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Test,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Serialize builds so parallel agents do not fight over bin/obj.
$mutex = [System.Threading.Mutex]::new($false, 'Global\StartupManager.BuildLock')
if (-not $mutex.WaitOne([TimeSpan]::FromMinutes(10))) {
    throw 'Timed out waiting for the build lock.'
}
try {
    $env:DOTNET_ROOT = Join-Path $root '.tools\dotnet'
    $env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $root '.nuget\packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'

    $dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    if (-not (Test-Path $dotnet)) { throw 'Local SDK missing. Run tools\bootstrap.ps1 first.' }

    Push-Location $root
    try {
        if (-not $NoRestore) {
            & $dotnet restore StartupManager.sln
            if ($LASTEXITCODE -ne 0) { throw "restore failed ($LASTEXITCODE)" }
        }
        & $dotnet build StartupManager.sln -c $Configuration --no-restore
        if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
        if ($Test) {
            & $dotnet test StartupManager.sln -c $Configuration --no-build
            if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }
        }
    } finally {
        Pop-Location
    }
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
