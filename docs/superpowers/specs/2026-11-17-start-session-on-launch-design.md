# Launch at Login and Start Streaming on Launch (Desktop Publisher) — Design

Related: hugoalves-dev/flutter_mobile-web_SonicRelay#65 (mobile auto-connect).
Companion: Flutter spec `2026-11-17-auto-connect-paired-publisher-design.md`.

## Intent

Pair with the mobile auto-connect: when the user signs in to the PC, the publisher
starts by itself (in the tray) and is already streaming, so the phone joins it without
anyone touching the PC.

## What exists (verified)

- `IAutoStartService` is only an interface (phase 1 of #32): **no implementation, no
  UI**. Launch at login does not work today.
- `TrayBackgroundPreferenceStore` has `StartMinimized`, but nothing reads it.
- Creating a session and starting audio are two manual actions
  (`PublisherWorkflow.CreateSessionAsync`, `StartAudioAsync`).

## Goals

1. **"Start with the system"** in Settings, off by default.
   - Windows, unpackaged (MSI/portable): HKCU `Software\Microsoft\Windows\CurrentVersion\Run`
     value `SonicRelayPublisher` = `"<exe path>" --minimized`. No elevation.
   - Windows, MSIX (Store): the Run key is virtualized and ignored. Declare a
     `desktop:StartupTask` (`TaskId="SonicRelayPublisher"`, `Enabled="false"`) in
     `AppxManifest.template.xml`. Toggling it in-app needs WinRT (`net10.0-windows`
     TFM), which is out of scope: the in-app switch is replaced by the hint
     "Turn this on in Windows Settings → Apps → Startup". Packaged detection:
     `GetCurrentPackageFullName` P/Invoke returning anything but `APPMODEL_ERROR_NO_PACKAGE`.
   - Linux: `~/.config/autostart/sonicrelay-publisher.desktop` (`Exec=<exe> --minimized`,
     honoring `XDG_CONFIG_HOME`).
   - macOS: option hidden (follow-up).
2. **`--minimized`**: when present, the main window starts hidden in the tray (when the
   tray is available; otherwise minimized).
3. **"Start streaming when the app opens"**, off by default, persisted in a new
   `startup.json` (`PublisherStartupPreferenceStore`, `StartSessionOnLaunch`). When on,
   after device identity is ready and no session is active, the app creates a broadcast
   session, connects signaling and starts audio capture — what the two buttons do.

## Constraint: waiting sessions expire

The backend expires a **waiting** session (no viewer yet) once its code TTL passes
(default 10 min). While option 3 is on:

- 1 min before `CodeExpiresAt`, while the viewer count is 0, call the existing
  `POST /api/sessions/{id}/rotate-code` (new `SessionApiClient.RotateCodeAsync`); the
  displayed code updates and the window extends.
- If the session disappears without the user ending it (signaling `SessionGone` /
  `SessionEnded`), create a new one after a delay with backoff 5 s, 30 s, 2 min, then
  every 5 min; a success resets the backoff. No tight loops.
- If the user clicks **End session**, or unpairs / loses the device identity, nothing is
recreated until the next launch (a reset device must never start capturing by itself).

On Linux an AppImage runs from a temporary mount, so launch-at-login registers the
`APPIMAGE` path when it is set.

## Non-goals

- No change to WebRTC/audio pipeline, signaling or backend.
- Broadcast only (no duplex/screen-share auto start).
- No start when device identity is missing — the normal gated UI shows.
- macOS launch-at-login; in-app MSIX toggle.

## Structure

- `Core/Configuration/PublisherStartupPreferenceStore.cs`.
- `Presentation/Platform/WindowsRunKeyAutoStartService.cs`,
  `LinuxXdgAutoStartService.cs`, `AutoStartServiceFactory.cs` (returns null when
  unsupported/packaged, plus `IsPackaged` for the hint).
- `Presentation/SessionAutoStarter.cs`: subscribes to `PublisherWorkflow.StateChanged`;
  `OnIdentityReadyAsync`, rotation timer, recreate-with-backoff, `NotifyUserEndedSession`.
  Timer and clock injectable.
- `PublisherWorkflow.RotateCodeAsync` + `ISessionApiClient.RotateCodeAsync`.
- `SettingsViewModel`/`SettingsView`: two switches (+ MSIX hint).
- `App.axaml.cs`: `--minimized` handling; after identity init, `SessionAutoStarter`.

## Testing

- Preference store: default false, round-trip, corrupt file → defaults.
- Run-key service against an injectable registry abstraction; XDG service against a
  temp dir (file written/removed, `Exec` quoted).
- Auto starter: off → nothing; on + ready → create + start audio once; rotation only
  while waiting; unexpected end → recreate with backoff schedule; user end → no
  recreate; existing session → nothing.
- Settings VM: switches persist; MSIX shows hint instead of toggle.
