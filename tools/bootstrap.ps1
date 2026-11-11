$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root '.tools'
$dotnetDir = Join-Path $tools 'dotnet'
$dotnetExe = Join-Path $dotnetDir 'dotnet.exe'
$installScript = Join-Path $tools 'dotnet-install.ps1'

New-Item -ItemType Directory -Force -Path $tools | Out-Null

if (-not (Test-Path $dotnetExe)) {
    Write-Host 'Downloading dotnet-install.ps1...'
    Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installScript
    Write-Host 'Installing .NET 8 SDK into .tools\dotnet (user-scoped, no PATH change)...'
    & $installScript -Channel 8.0 -InstallDir $dotnetDir -NoPath
}

$env:DOTNET_ROOT = $dotnetDir
$env:DOTNET_CLI_HOME = Join-Path $tools 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host ('SDK: ' + (& $dotnetExe --version))
Write-Host ('Root: ' + $env:DOTNET_ROOT)
Write-Host ('NuGet cache: ' + $env:NUGET_PACKAGES)
