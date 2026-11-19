# Launch at Login and Start Streaming on Launch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the publisher start at sign-in (minimized) and start a broadcast session + audio by itself, keeping the waiting session alive.

**Architecture:** OS-specific `IAutoStartService` implementations chosen by a factory; a `SessionAutoStarter` drives `PublisherWorkflow` from its `StateChanged` events with injectable timers; Settings exposes two switches.

**Tech Stack:** .NET 10, Avalonia, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-17-start-session-on-launch-design.md`

## Global Constraints

- Both options off by default.
- Run key: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `SonicRelayPublisher`, data `"<exe>" --minimized`.
- XDG file: `$XDG_CONFIG_HOME|~/.config` + `/autostart/sonicrelay-publisher.desktop`.
- MSIX: `StartupTask TaskId="SonicRelayPublisher" Enabled="false"`; in-app hint `Turn this on in Windows Settings → Apps → Startup.`
- Rotation margin 1 min before `CodeExpiresAt`, only when `ViewerCount == 0`.
- Recreate backoff: 5 s, 30 s, 2 min, then 5 min; reset on success.
- User End session → no recreate until next launch.

## Review Focus

- Session created but `StartAudioAsync` fails (no capture device) → session stays, error shown, no recreate loop. Test in Task 4.
- Identity init fails at startup (backend down) → auto starter never runs; nothing thrown. Test in Task 4.
- Exe path with spaces → Run value and XDG `Exec` quoted. Test in Task 2.
- Rotate-code returns 409 (session ended meanwhile) → treated as gone → recreate path. Test in Task 4.
- `--minimized` without a tray → window minimized, not hidden. Test in Task 5 (resolver function).

---

### Task 1: Rotate code API + workflow

**Files:** Modify `src/SonicRelay.Windows.ApiClient/Sessions/SessionContracts.cs`, `SessionApiClient.cs`, `src/SonicRelay.Windows.Presentation/PublisherWorkflow.cs`, `PublisherSnapshot.cs` (add `DateTimeOffset? CodeExpiresAt`). Tests: ApiClient + `PublisherWorkflowTests`.

**Interfaces:** `Task<StreamSessionResponse> ISessionApiClient.RotateCodeAsync(Guid sessionId, CancellationToken ct = default)` → `POST api/sessions/{id}/rotate-code`; `Task PublisherWorkflow.RotateCodeAsync(CancellationToken ct = default)` updates `SessionCode` and `CodeExpiresAt`; `CreateSessionAsync` also sets `CodeExpiresAt`.

- [ ] Failing tests (request path/verb; workflow updates code & expiry; no session → validation error). Implement. Pass. Commit.

### Task 2: Auto-start services

**Files:** Create in `src/SonicRelay.Windows.Presentation/Platform/`: `IRegistryRunKey.cs` (+ `WindowsRegistryRunKey`), `WindowsRunKeyAutoStartService.cs`, `LinuxXdgAutoStartService.cs`, `PackageIdentity.cs` (`static bool IsPackaged()` via `GetCurrentPackageFullName`, false off Windows), `AutoStartServiceFactory.cs` (`static IAutoStartService? Create(string exePath)` → Windows unpackaged / Linux / null). Tests in Presentation.Tests.

- [ ] Failing tests: Run key set/remove/IsEnabled with fake registry, quoted value; XDG writes file with `Exec="<exe>" --minimized`, `X-GNOME-Autostart-enabled=true`, removes on disable, honors `XDG_CONFIG_HOME` (injected). Implement. Pass. Commit.

### Task 3: Startup preference store + MSIX manifest

**Files:** Create `src/SonicRelay.Windows.Core/Configuration/PublisherStartupPreferenceStore.cs` (`startup.json`, `bool StartSessionOnLaunch`, `Task SetStartSessionOnLaunchAsync(bool)`), modify `packaging/windows/msix/AppxManifest.template.xml`. Tests in Core.Tests (+ structure test if manifest is validated there).

- [ ] Failing tests: default false, round-trip, corrupt file → false. Implement + manifest extension. Pass. Commit.

### Task 4: SessionAutoStarter

**Files:** Create `src/SonicRelay.Windows.Presentation/SessionAutoStarter.cs`; test `SessionAutoStarterTests.cs`.

**Interfaces:** `SessionAutoStarter(IPublisherSessionControl workflow, Func<bool> enabled, TimeProvider time)`; `Task OnIdentityReadyAsync()`; `void NotifyUserEndedSession()`; `IDisposable`. `IPublisherSessionControl` (new, implemented by `PublisherWorkflow`): `PublisherSnapshot State`, `event Action<PublisherSnapshot> StateChanged`, `CreateSessionAsync`, `StartAudioAsync`, `RotateCodeAsync`.

- [ ] Failing tests with `FakeTimeProvider`: disabled → nothing; enabled + authenticated + no session → create then start audio once; existing session → nothing; not authenticated → nothing; rotation at expiry−1 min only while ViewerCount 0; SessionId becomes null without user end → recreate after 5 s, failures back off 30 s/2 min/5 min, success resets; user end → no recreate; start-audio failure → no recreate.
- [ ] Implement. Pass. Commit.

### Task 5: Settings UI, `--minimized`, wiring

**Files:** Modify `SettingsViewModel.cs`, `Controls/SettingsView.axaml`, `App.axaml.cs`, `ViewModels/MainWindowViewModel.cs` (End-session command calls `NotifyUserEndedSession`), `Program.cs`/startup args. Tests in Desktop.Tests.

- [ ] Failing tests: settings VM exposes `LaunchAtLogin` (bound to service), `StartSessionOnLaunch` (store), `ShowStartupHint` when packaged; `StartupWindowMode.Resolve(args, trayAvailable)` → Hidden/Minimized/Normal.
- [ ] Implement. Pass. Commit.

### Task 6: Verify

- [ ] `dotnet build SonicRelay.Windows.slnx` and `dotnet test` for Core, ApiClient, Presentation, Desktop test projects → pass.
