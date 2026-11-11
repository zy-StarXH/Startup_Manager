# Testing and acceptance plan

## Automated

### Unit tests
- StartupApproved binary encode/decode and key mapping.
- Stable item IDs for all four sources.
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
- Read-only service/task enumeration.

### UI
- Headless Chrome against `web/index.html` in demo mode.
- Initial loading, state, search, tabs, sort, detail, protected row, backup restore,
  settings, bulk confirmation, error and empty states.
- 1440x1000 and 1024x768 screenshots; sidebar rail at <=900; no duplicated titlebar
  inside the WPF host.
- Native shell: custom titlebar buttons, DPI 200%, WebView2 bridge round-trip.

## Manual UAC acceptance (required once)

1. Launch the portable app normally (no UAC).
2. Confirm ordinary Run/Startup-folder toggles work without elevation.
3. Find a safe third-party automatic service (for example TeamViewer, if installed).
4. Toggle it off; approve the single UAC prompt.
5. Confirm the service start type is now Manual and the UI shows verified state.
6. Restore from the restore center; approve UAC; confirm the original start type is back.
7. Cancel a UAC prompt deliberately; confirm the item state is unchanged and the UI reports cancel.
8. Never test this against Microsoft/driver/security services.

## Acceptance checklist

- [x] Scans real registry Run, startup folders, startup-triggered tasks, and automatic services.
- [x] Registry/folder toggles change StartupApproved and are reflected as real disabled state.
- [ ] Tasks use native Enabled; services use native StartType. Implemented and unit/mock tested;
      a real privileged toggle still needs the manual UAC pass below.
- [x] Every mutation has a backup; restore works; failed verification rolls back.
      (Real registry backup/restore verified end-to-end; service/task backup paths use the same engine and tests.)
- [x] Protected OS/driver/security items cannot be changed through the service API.
- [ ] UAC is on-demand, batched, and cancellation is harmless. Implemented; cancellation was
      tested with fakes, not against a real secure-desktop prompt.
- [x] No simulated impact seconds or other invented metrics remain.
- [x] UI matches the approved prototype and all controls are real.
- [x] Portable build launches on Windows 11 x64 with WebView2 available.
- [x] No console errors, no unhandled exceptions in the tested flows; logs contain no secrets.

Environment note: the automated session was not elevated. Task Scheduler denied creating a
temporary root task. Computer-use validation exercised the native window, real search/detail,
and a real HKCU startup entry disable/restore. A harmless UAC prompt was then triggered and
confirmed to live on the Windows secure desktop, which computer use cannot capture or operate;
the request was canceled by stopping its caller, with no elevated command executed. That is why
the two privileged rows remain manual acceptance items. Do not change UAC security settings
just to automate this test.
