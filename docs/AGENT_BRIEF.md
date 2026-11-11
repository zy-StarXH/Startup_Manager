# Parallel workstream brief

This project is built by a lead agent plus parallel subagents. Every subagent
gets an isolated copy of the scaffold under `.agents/<lane>/` and works there.
The lead reviews and copies the owned files back into the main tree.

## Environment

- Windows 11 x64; user is not running elevated; UAC is available but must NOT be
  triggered by agents.
- .NET SDK 8.0.428 is installed locally at the main tree's `.tools/dotnet/dotnet.exe`.
- Each workstream has an isolated copy under `.agents/<lane>/`. Use the main SDK
  and NuGet cache explicitly from a PowerShell prompt:

```powershell
$env:DOTNET_ROOT='D:\Zcode\projects\自启动管理\.tools\dotnet'
$env:NUGET_PACKAGES='D:\Zcode\projects\自启动管理\.nuget\packages'
$env:DOTNET_CLI_HOME='D:\Zcode\projects\自启动管理\.tools\dotnet-home'
& "$env:DOTNET_ROOT\dotnet.exe" build 'D:\Zcode\projects\自启动管理\.agents\<lane>\StartupManager.sln'
```

- Do not use `tools\build.ps1` inside a copy (it points at a copy-local SDK).
  For tests, replace `build` with `test` on the copy's solution.
- Do not install global tools and do not change the machine PATH.
- NuGet cache is under the main tree's `.nuget/packages` on D:.
- Node.js 22 and Chrome are available for web tests.
- The approved visual reference is the repository's `prototype.html`.
- The bridge and provider contract is `docs/CONTRACT.md`. Read it before coding.

## File ownership

Only edit files in your assigned area. The contract files and scaffold files are
read-only to subagents. Do not rename projects, interfaces, or the bridge fields.

| Lane | Owned area |
|---|---|
| A | `src/StartupManager.Core/Providers/Registry*.cs`, `Providers/StartupFolder*.cs`, `Providers/RegistryFolder/` and matching tests |
| B | `src/StartupManager.Core/Providers/ScheduledTask*.cs`, `Providers/Service*.cs`, `Protection/` and matching tests |
| C | `src/StartupManager.Core/Backup/`, `Services/`, `Elevation/`, `src/StartupManager.Elevated/`, matching tests |
| D | `web/` only |
| E | `src/StartupManager.App/` and `tools/` (except files owned by lead) |

No subagent may edit `src/StartupManager.Core/Models/`, `JsonDefaults.cs`,
`Interfaces/`, `docs/CONTRACT.md`, or `Directory.Build.props`. If a contract
change is genuinely required, report it to the lead instead of editing it.

## Verification rules

- Use xUnit for C# tests or the existing headless Chrome approach for web tests.
- Safe integration fixtures: unique HKCU Run values and temporary user scheduled
  tasks. Clean up in `finally`. Never modify real services.
- Never execute a command discovered from startup data.
- Do not use UAC. If a feature requires it, implement the code and test only the
  non-elevated half or a mock.
- Report tests run, what passed/failed, and any integration assumptions.

## Output

Return a short report:
1. Files created/modified in your lane.
2. How to build/test your part.
3. Tests and results.
4. Known limitations or contract mismatches.
5. Anything the lead must wire manually.
