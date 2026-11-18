# Safety model

## What “disable” means

| Source | Real change | Scope |
|---|---|---|
| Registry Run | Windows `StartupApproved` disabled bytes | Current user's login |
| Startup folder | Move original file into recoverable storage, plus StartupApproved | User folder: current user; common folder: all users, requires UAC |
| MSIX StartupTask | Windows per-user startup task state, with exact value backup/restore | Current user's login; policy-controlled tasks remain locked |
| Scheduled task | Task Scheduler native `Enabled = false` | Machine / task ACL |
| Service | Windows service start type Auto → Manual | Machine |

Manual services may still be started by callers or triggers. Their desktop apps
may have independent login entries. Never describe Manual as preventing all starts.
Files still present in Startup are shown as active entries with a migration note
when legacy StartupApproved state is disabled. Restores must not overwrite a
recreated file or move files outside the configured Startup directories.

No scanned command is ever executed. The app only reads metadata and, when the
user asks, opens Explorer / Task Scheduler / Services for inspection.

## Protection rules

- Default-lock system services, driver-related services, Microsoft tasks and
  security startup entries. Since v1.2.0, the user's explicit per-item decision
  may unlock any tool-owned protection category, including critical/system items.
- Unlocking changes only the tool allowlist, not the Windows startup state.
  Show the original protection reason and consequences before confirmation.
  Do not infer that a protected component is advertising or unnecessary.
- Reject protected IDs again inside the service layer, not just in the UI.
- Batch operations skip protected entries and show the real skipped count.
  Batch unlock/relock applies the same per-item validation and persists one
  allowlist update; policy-locked entries are reported as skipped.
- A mutation may reuse a scan that is at most two minutes old to resolve its
  targets and protection flags (v1.3.0). Providers still re-read the live OS
  state for every write and verify the result, the elevated helper performs its
  own fresh scan, and a full rescan runs when the snapshot is missing or older
  than the window. Never reuse a snapshot for a write without provider-level
  live reads and verification.
- UI action copy must describe the actual mechanism. Do not claim entries are
  deleted when StartupApproved is used.
- Per-item permissions persist in `userAllowedItemIds`. Backend unlock requests
  validate fresh scan/policy state. A new elevated helper reads the same list.
  UAC, backups, verification, error reporting and rollback remain active.
- Offer "restore tool protection" without altering startup state. Backup-only
  disabled items also respect current permissions; the recovery center can
  still restore an existing backup after the entry is relocked.
- Windows policy-controlled MSIX states 3/4 and failed safety evaluation are
  not tool-owned locks and cannot be overridden. Never change ACLs, ownership,
  group policy, or escalate beyond the existing UAC flow to force a denied write.
- MSIX packaged services deny SCM `SERVICE_CHANGE_CONFIG` to administrators by
  design. The packaged-service registry `Start` value fallback is allowed only
  after the user has explicitly allowed management, requires UAC, is backed up
  and verified, and is labeled in the UI as taking effect after reboot. The
  running service is never stopped. If the registry write fails, report it; do
  not claim success.

## Transaction

Every change is: capture → persist backup → apply → verify → mark verified.
Failure path: restore captured state, verify the restore, record an error.
A missing or unreadable backup blocks mutation rather than proceeding.

## UAC

- Main app is `asInvoker`.
- Only a typed request is passed to `StartupManager.Elevated.exe`; no arbitrary
  scripts, files to execute, or free-form shell commands.
- The helper rescans and resolves IDs itself. It validates the request path,
  owner, expiry, nonce, and operation type.
- One UAC prompt per batch, not per item.
- Canceling UAC must leave the machine unchanged.

## Development rules

- Agents must not trigger UAC.
- Agents may create temporary HKCU Run test values and temporary scheduled tasks
  with unique names, and must clean them up in a `finally` block.
- Do not change real services during automated development or tests.
- Do not create, delete, or modify Windows security software, drivers, or
  system-owned tasks.
