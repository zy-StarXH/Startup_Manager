# StartupManager contract (frozen for parallel work)

This file is the interface between the C# host and the web UI, and between the
core service and its providers. Do not change names, shapes, or value casing
without the lead agent's review.

## 1. Domain values

`StartupSource` (JSON lowercase): `registry`, `folder`, `task`, `service`

`StartupScope` (JSON camelCase): `currentUser`, `allUsers`, `system`

`BackupStatus`: `active`, `restored`, `failed`

`Mechanism`:
- `startupApproved` — Windows StartupApproved enable/disable state
- `scheduledTask` — Task Scheduler `Enabled` flag
- `serviceStartType` — Windows service start type

Stable item id (`StartupItem.Id`):
- Registry: `reg-` + SHA-256 hex of `hive|view|keyPath|valueName`
- Startup folder: `dir-` + SHA-256 hex of `scope|fullPath`
- Scheduled task: `tsk-` + SHA-256 hex of `fullTaskPath`
- Service: `svc-` + SHA-256 hex of lowercase service name

Hashes are lowercase hex. IDs must be stable across scans and are the only
identifiers accepted by change/elevation APIs.

## 2. `StartupItem` JSON

```json
{
  "id": "reg-…",
  "name": "微信",
  "publisher": "腾讯科技（深圳）有限公司",
  "source": "registry",
  "scope": "currentUser",
  "enabled": true,
  "protected": false,
  "protectionReason": null,
  "canOverride": false,
  "userAllowed": false,
  "isPackagedService": false,
  "requiresAdmin": false,
  "managedByApp": false,
  "location": "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
  "subLocation": "WeChat",
  "command": "\"C:\\Program Files\\Tencent\\WeChat\\WeChat.exe\"",
  "targetPath": "C:\\Program Files\\Tencent\\WeChat\\WeChat.exe",
  "mechanism": "startupApproved",
  "lastModified": "2026-11-11T12:00:00+08:00",
  "icon": null
}
```

Notes:
- `command` is display-only and must never be executed.
- `requiresAdmin` means the change is expected to need UAC.
- `protected` rows must not be changed by the UI.
- `managedByApp` is true for disabled rows that only exist in our backup store
  (for example a service moved from Auto to Manual).
- `icon` is an optional `data:image/png;base64,...`; the UI falls back to a
  colored letter tile.
- Metadata is `[JsonIgnore]` and never reaches the web UI.

## 3. `BackupSummary` JSON

```json
{
  "id": "8f4d…",
  "itemId": "svc-…",
  "source": "service",
  "scope": "system",
  "requiresAdmin": true,
  "displayName": "TeamViewer 服务",
  "location": "服务：TeamViewer",
  "command": "\"C:\\Program Files\\TeamViewer\\TeamViewer_Service.exe\"",
  "createdAt": "2026-11-11T12:00:00+08:00",
  "restoredAt": null,
  "status": "active",
  "summary": "启动类型：自动",
  "error": null
}
```

`StateJson` is provider-private and is never sent over the bridge.

## 4. Web → host messages

All messages are sent through `window.chrome.webview.postMessage` and include a
unique `requestId` where a response is expected.

```json
{ "type": "ready" }
{ "type": "command", "requestId": "r-1", "command": "refresh" }
{ "type": "command", "requestId": "r-2", "command": "setEnabled", "itemIds": ["svc-…"], "enabled": false }
{ "type": "command", "requestId": "r-3", "command": "restoreBackup", "backupId": "8f4d…" }
{ "type": "command", "requestId": "r-4", "command": "saveSettings", "settings": { … } }
{ "type": "command", "requestId": "r-4b", "command": "setUserOverride", "itemId": "svc-…", "allowed": true }
{ "type": "command", "requestId": "r-5", "command": "openItemLocation", "itemId": "reg-…" }
{ "type": "command", "requestId": "r-6", "command": "openBackupFolder" }
{ "type": "command", "requestId": "r-7", "command": "openLogFolder" }
{ "type": "command", "requestId": "r-8", "command": "copyLocation", "itemId": "tsk-…" }
```

Allowed commands: `refresh`, `setEnabled`, `restoreBackup`, `saveSettings`,
`setUserOverride`, `openItemLocation`, `openBackupFolder`, `openLogFolder`,
`copyLocation`. Unknown commands and malformed IDs must be rejected.
`setUserOverride` is accepted only for protected items whose `canOverride` is
true, or to remove an existing user override; hard protections are rejected.

`setEnabled` semantics:
- Empty or unknown ids are rejected.
- If any target is protected, reject the whole request.
- If any target requires elevation and the host is not elevated, the host
  launches one elevated helper request for the whole batch.
- On success, the host sends a fresh state and then a command result. The web UI
  must not optimistically render the new state as final; it should show a
  pending state until the host responds.

## 5. Host → web messages

```json
{
  "type": "state",
  "snapshot": {
    "items": [ … StartupItem … ],
    "scannedAt": "2026-11-11T12:00:00+08:00",
    "warnings": [],
    "isElevated": false,
    "enabledCount": 8,
    "disabledCount": 4,
    "protectedCount": 2,
    "adminCount": 2
  },
  "backups": [ … BackupSummary … ],
  "settings": { … AppSettings … },
  "system": {
    "userName": "Zy",
    "machineName": "…",
    "backupDirectory": "C:\\Users\\Zy\\AppData\\Local\\StartupManager\\Backups",
    "logDirectory": "C:\\Users\\Zy\\AppData\\Local\\StartupManager\\Logs"
  }
}
```

Other host messages:

```json
{ "type": "scanStarted" }
{ "type": "scanFinished", "ok": true, "message": null }
{ "type": "commandResult", "requestId": "r-2", "ok": true, "message": "已关闭 1 项", "requiresElevation": false, "elevationCanceled": false }
{ "type": "error", "requestId": "r-2", "message": "UAC 已取消，未做任何更改。" }
{ "type": "settings", "settings": { … } }
```

After any successful mutation the host must reload the snapshot and backups and
send a full `state` message before returning the command result. The UI can
then render verified state.

## 6. `AppSettings` JSON

```json
{
  "includeRegistry": true,
  "includeStartupFolders": true,
  "includeScheduledTasks": true,
  "includeServices": true,
  "startWithWindows": false,
  "minimizeToTray": true,
  "backupRetentionDays": 30,
  "uiFontScale": 1.0,
  "userAllowedItemIds": []
}
```

`uiFontScale` is a UI display scale (0.8–1.5) applied through the host's
WebView2 ZoomFactor. The web settings UI offers 0.9 / 1.0 / 1.1 / 1.25.

## 7. C# interfaces (see src/StartupManager.Core)

- `IStartupProvider.ScanAsync`
- `IStartupChangeProvider.CaptureStateAsync`
- `IStartupChangeProvider.ApplyEnabledAsync`
- `IStartupChangeProvider.VerifyEnabledAsync`
- `IStartupChangeProvider.RestoreStateAsync`
- `IProtectionPolicy.Evaluate`
- `IBackupStore`
- `IStartupManager` (facade used by host/helper)
- `IElevationCoordinator`

Provider implementations:
- `StartupManager.Core.Providers.RegistryStartupProvider`
- `StartupManager.Core.Providers.StartupFolderProvider`
- `StartupManager.Core.Providers.ScheduledTaskProvider`
- `StartupManager.Core.Providers.ServiceStartupProvider`

The composition root in the app will wire:
`IStartupManager` = `StartupManagerService` with the four providers,
`StartupProtectionPolicy`, `JsonBackupStore`, and `AppPaths`.

## 8. Change and backup invariants

1. Capture a provider state snapshot before mutating.
2. Persist the backup before applying.
3. Apply the smallest possible OS change.
4. Re-read the OS state; return `Verified=true` only when it matches.
5. On verification failure, restore the captured state and return an error.
6. The UI never gets a success result for an unverified change.
7. Backups are stored under `%LocalAppData%\StartupManager\Backups` with an
   ACL limited to the current user; state JSON is never returned to the web UI.
8. StartupApproved is the only mechanism for registry/startup-folder toggles in
   v1. The original Run value/shortcut stays in place; enabling restores the
   original approval bytes.
9. Services change `StartType` to Manual; tasks set native `Enabled=false`.
   Do not stop running services in v1.
10. Protected items must be rejected by the service even if the UI is bypassed.
