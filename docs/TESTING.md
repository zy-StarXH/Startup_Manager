# Testing and acceptance plan

2026-11-18 / v1.3.0: Release candidate passes with **230 tests passed and 1 skipped**
(224 Core + 6 App). New regression cases cover the cached snapshot used by
mutations (a toggle must not rescan), in-place state updates after a verified
write, a batch unlock/relock with one settings write and no extra scans,
per-item reporting when a Windows policy lock refuses the batch, and scoped
verification that re-reads only the source that owns a restored backup.

Performance measurements on this machine (Release, real system, 173-176 items):
the full scan dropped from 4.5-8.8 s to 2.3-3.2 s (Task Scheduler COM scanning is
now the only slow provider), the MSIX provider from 2.2-3.1 s to 0.14-0.5 s, and
a real registry toggle through the native host from several seconds to
**230-330 ms** (click to updated state; measured on both Debug and Release
builds, disable and enable). A cold toggle with no cached scan (the
elevated-helper path) fell from a full scan to 38 ms for a registry item because
only the target's provider is scanned. Running the published one-shot helper
directly against a temporary HKCU Run value (process start + scoped scan + backup
+ apply + verify) took 414 ms to disable and 562 ms to enable; the response
reported success and the approval bytes changed to disabled/enabled as expected.
The temporary value, approval, backups and IPC files were removed afterwards.

`node tools/qa-protection.mjs` (demo): 37 in-memory UI checks, including the new
bulk unlock/relock dialog, per-item permission state and switch enablement;
screenshot `_qa/protection-1.3.0/demo-bulk-protection.png` inspected.
`node tools/qa-native-toggle.mjs` (native CDP on port 9233): creates a temporary
HKCU Run value, toggles it off/on through the WPF host, asserts both complete
under 3 s, and cleans the value/approval plus its backups afterwards.
`node tools/qa-protection.mjs --native` still covers the per-item unlock dialog.

2026-11-17 / v1.2.0: Release build passes with 225 tests passed and 1 skipped.
Ten new regression cases cover tool-protected system tasks/services/Run/folder
entries: default lock, explicit unlock, settings persistence, fresh elevated-manager
scan, UAC precheck, backup ordering, verification, relock, recovery while locked,
synthetic disabled-service permissions, Windows policy and safety-check refusal,
stale permission revocation and rollback after an OS access denial. All startup
mutations in these cases use in-memory providers; real system services are untouched.

`node tools/qa-protection.mjs --native`: 10 native UI checks, with real protected
task confirmation opened and canceled. `node tools/qa-protection.mjs`: 20 in-memory
demo UI checks for cancel/unlock/disable/restore/relock; screenshots inspected.
The v1.1.2 icon transparency/UI checks also pass on the 1.2.0 candidate.

2026-11-17 / v1.1.2: Release build passes with 215 tests passed and 1 skipped.
Six icon regression cases exercise native HBITMAP-to-PNG conversion: transparent
and antialiased pixels, premultiplied colors, genuine black artwork, row orientation,
non-square dimensions, legacy mask fallback, invalid handles and unavailable files.
The app test project links the production IconService source and never starts the WPF app.

`node tools/qa-icons.mjs` checks a running native host on local CDP port 9233:
image pixels retain transparency, real-icon containers have no added shadow/border,
images use contain without rounded clipping, and startup detail renders correctly.
The verified snapshot contained 29 startup icons (28 with transparent pixels), 93 service
icons and 28 task icons; there were no JavaScript errors. PNG screenshots were inspected.
The script only reads startup configuration and temporarily adds a preview DOM element.

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
