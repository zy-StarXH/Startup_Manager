# Safety model

## What “disable” means

| Source | Real change | Scope |
|---|---|---|
| Registry Run / Startup folder | Windows `StartupApproved` disabled bytes (same mechanism Task Manager uses) | Current user's login |
| Scheduled task | Task Scheduler native `Enabled = false` | Machine / task ACL |
| Service | Windows service start type Auto → Manual | Machine |

No scanned command is ever executed. The app only reads metadata and, when the
user asks, opens Explorer / Task Scheduler / Services for inspection.

## Protection rules

- Never expose a toggle for OS-critical services, driver services, Microsoft
  signed automatic services, or known audio/display/security support services.
- Never expose a toggle for tasks under `\Microsoft\Windows\`.
- Reject protected IDs again inside the service layer, not just in the UI.
- Batch operations skip protected entries and show the real skipped count.
- UI action copy must describe the actual mechanism. Do not claim entries are
  deleted when StartupApproved is used.
- An item locked only because its publisher/path could not be verified may be
  unlocked by an explicit user decision. This exception must never apply to
  critical, driver, security or Microsoft-owned protections. The UI must warn
  that the program is untrusted, keep UAC/backup/verification active, and offer
  "restore system protection". The allowlist is stored in settings and re-applied
  only to items the protection policy marks `CanOverride`.
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
