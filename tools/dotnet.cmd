@echo off
setlocal
set "ROOT=%~dp0.."
set "DOTNET_ROOT=%ROOT%\.tools\dotnet"
set "DOTNET_CLI_HOME=%ROOT%\.tools\dotnet-home"
set "NUGET_PACKAGES=%ROOT%\.nuget\packages"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
if not exist "%DOTNET_ROOT%\dotnet.exe" (
  echo [ERROR] Local .NET SDK is missing. Run tools\bootstrap.ps1 first. 1>&2
  exit /b 1
)
"%DOTNET_ROOT%\dotnet.exe" %*
endlocal
