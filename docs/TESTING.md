# Testing and acceptance plan

2026-11-16 / v1.1.1: Release build passes with 209 tests passed and 1 skipped.
New regression cases cover isolated MSIX task states, malformed data, manifest
defaults, policy locks, common-folder restore, repeated restore, enable rollback
and recreated-file conflicts. Published WPF/WebView2 checks confirm ChatGPT's
disabled package task and backup, the packaged-app source tab, and Tailscale's
common-folder entry after it was moved to quarantine. No UAC was triggered.
Reboot acceptance remains manual.

## Automated

### Unit tests
- StartupApproved binary encode/decode and key mapping.
- Stable item IDs for all five sources (registry, folder, task, service, packagedApp).
- Command-line/shortcut target parsing.
- Backup store round-trip, path traversal rejection, corrupt-file handling, retention.
- Transaction rollback when verification fails.
- Elevation pre-check: admin-required batch returns `RequiresElevation` with zero changes.
- Protected-item rejection even if the UI bypasses it.
- Task trigger classification, task scope, service metadata parsing.
- Protection policy: TeamViewer service manageable; NVIDIA/Realtek/Microsoft core services protected;
  Microsoft task protected; third-party logon task manageable.
- Bridge serialization: camelCase enums, no private metadata, no StateJson to the web UI.

### Safe integration tests (no elevation)
- HKCU Run fixture: unique value `StartupManagerTest_<guid>` pointing to `cmd.exe /c exit`.
  Never execute it. Capture → disable → verify StartupApproved 0x03 → enable → verify →
  delete the Run value and approval value in `finally`.
- Scheduled task fixture: unique current-user task with a logon trigger; scan, disable, verify,
  enable, delete in `finally`. If creation is denied, skip with a reason; do not elevate.
- Startup folder parsing against temporary injected directories (do not touch the real Startup folder).
- Quarantine round-trip: move, rescan, restore, repeated restore, enable rollback, and recreated-file conflict.
- Packaged StartupTask fixtures under an isolated HKCU test key: state defaults, state 0/1/2, state 3/4 lock, malformed data, and restore.
- Read-only service/task enumeration.

### UI
- Headless Chrome against `web/index.html` in demo mode.
- Initial loading, state, search, tabs, sort, detail, protected row, backup restore,
  settings, bulk confirmation, error and empty states.
- 1440x1000 and 1024x768 screenshots; sidebar rail at <=900; no duplicated titlebar
  inside the WPF host.
- Native shell: custom titlebar buttons, DPI 200%, WebView2 bridge round-trip.
- Native icon extraction through the shell image factory: startup rows should show
  real icons; services without an extractable image (for example packaged MSIX
  services) should keep the letter-tile fallback. Verified against both Debug and
  Release builds; the UI version label must read 1.1.1.

## Manual UAC acceptance (required once)

1. Launch the portable app normally (no UAC).
2. Confirm current-user Run and current-user Startup-folder toggles work without elevation; common-folder changes require UAC.
3. Find a safe third-party automatic service (for example TeamViewer, if installed).
4. Toggle it off; approve the single UAC prompt.
5. Confirm the service start type is now Manual and the UI shows verified state.
6. Restore from the restore center; approve UAC; confirm the original start type is back.
7. Cancel a UAC prompt deliberately; confirm the item state is unchanged and the UI reports cancel.
8. Never test this against Microsoft/driver/security services.

## Acceptance checklist

- [x] Scans real registry Run, startup folders, MSIX StartupTasks, startup-triggered tasks, and automatic services.
- [x] Registry toggles change StartupApproved; startup-folder toggles move the file to recoverable storage and write StartupApproved.
- [x] Packaged StartupTasks are managed separately from packaged services; policy states remain locked.
- [x] Packaged services use the registry Start fallback with real backup and verification.
      ChatGPT (`CodexSandboxService.OpenAI.Codex`) was disabled after a real UAC approval;
      SCM and the registry both report DemandStart, and an active backup exists.
- [ ] Tasks use native Enabled; ordinary services use native StartType. Implemented and
      unit/mock tested; a real privileged ordinary-service/task toggle still needs manual acceptance.
- [x] Every mutation has a backup; restore works; failed verification rolls back.
      (Real registry and packaged-service paths verified; folder quarantine has isolated round-trip/rollback tests; task/service paths share the engine.)
- [x] Protected OS/driver/security items cannot be changed through the service API.
- [ ] UAC is on-demand, batched, and cancellation is harmless. A real secure-desktop prompt
      was approved for the packaged-service fallback; cancellation was tested with fakes,
      not against a real secure-desktop prompt.
- [x] No simulated impact seconds or other invented metrics remain.
- [x] UI matches the approved prototype and all controls are real.
- [x] Portable build launches on Windows 11 x64 with WebView2 available.
- [x] No console errors, no unhandled exceptions in the tested flows; logs contain no secrets.

Environment note: the automated session was not elevated. Task Scheduler denied creating a
temporary root task. Computer-use validation exercised the native window, real search/detail,
and a real HKCU startup entry disable/restore. A harmless UAC prompt was then triggered and
confirmed to live on the Windows secure desktop, which computer use cannot capture or operate;
that request was canceled without an elevated command. Later, the packaged-service fallback
for ChatGPT was approved manually and verified: SCM and the service registry both report
Manual, with an active backup. The v1.1.1 release UI check also confirmed ChatGPT's packaged
StartupTask and Tailscale's quarantined common-folder entry without changing either one.
Ordinary SCM service/task toggles, real-folder UAC changes, and deliberate UAC cancellation
still need manual acceptance. Do not change UAC security settings just to automate this test.
