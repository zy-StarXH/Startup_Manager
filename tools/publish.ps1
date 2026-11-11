# Builds a self-contained win-x64 folder for StartupManager + the elevated helper.
# No installer tooling is downloaded. Web assets and the app icon are included.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Output,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# In an isolated .agents/<lane> copy, fall back to the main tree's SDK/caches.
$mainRoot = Split-Path -Parent (Split-Path -Parent $root)
$sdkCandidates = @(
    (Join-Path $root '.tools\dotnet\dotnet.exe'),
    (Join-Path $mainRoot '.tools\dotnet\dotnet.exe')
)
$dotnet = $sdkCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $dotnet) {
    throw 'Local .NET SDK not found. Run tools\bootstrap.ps1 first.'
}

$toolsDir = Split-Path -Parent (Split-Path -Parent $dotnet)
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_HOME = Join-Path $toolsDir 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if (Test-Path -LiteralPath (Join-Path $toolsDir '..\.nuget\packages')) {
    $env:NUGET_PACKAGES = (Resolve-Path -LiteralPath (Join-Path $toolsDir '..\.nuget\packages')).Path
}
elseif (Test-Path -LiteralPath (Join-Path $mainRoot '.nuget\packages')) {
    $env:NUGET_PACKAGES = Join-Path $mainRoot '.nuget\packages'
}

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $root 'artifacts\publish\StartupManager'
}
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (Test-Path -LiteralPath $Output) {
    Remove-Item -LiteralPath $Output -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Output | Out-Null

$appProject = Join-Path $root 'src\StartupManager.App\StartupManager.App.csproj'
$elevatedProject = Join-Path $root 'src\StartupManager.Elevated\StartupManager.Elevated.csproj'

Write-Host "Publishing app ($Configuration, self-contained win-x64)..."
& $dotnet publish $appProject -c $Configuration -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE -ne 0) { throw "App publish failed ($LASTEXITCODE)." }

Write-Host 'Publishing elevated helper...'
& $dotnet publish $elevatedProject -c $Configuration -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE -ne 0) { throw "Elevated helper publish failed ($LASTEXITCODE)." }

$webIndex = Join-Path $Output 'web\index.html'
if (-not (Test-Path -LiteralPath $webIndex)) {
    throw "Web assets were not copied to $webIndex."
}
if (-not (Test-Path -LiteralPath (Join-Path $Output 'StartupManager.Elevated.exe'))) {
    throw 'StartupManager.Elevated.exe is missing from the publish output.'
}

$icon = Join-Path $root 'src\StartupManager.App\Assets\StartupManager.ico'
if (Test-Path -LiteralPath $icon) {
    Copy-Item -LiteralPath $icon -Destination (Join-Path $Output 'StartupManager.ico') -Force
}

Write-Host ("Published to {0}" -f $Output)
Write-Host ("  StartupManager.exe: {0}" -f (Test-Path -LiteralPath (Join-Path $Output 'StartupManager.exe')))
Write-Host ("  Elevated helper:     {0}" -f (Test-Path -LiteralPath (Join-Path $Output 'StartupManager.Elevated.exe')))
Write-Host ("  web\index.html:      {0}" -f (Test-Path -LiteralPath $webIndex))

if ($Zip) {
    $zipPath = $Output.TrimEnd('\') + '.zip'
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host ("Zipped: {0}" -f $zipPath)
}
