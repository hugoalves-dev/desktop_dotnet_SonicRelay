# Non-admin checklist

Use this checklist for every feature, dependency, packaging, and release decision. All mandatory items must remain true for normal installation, configuration, upgrades, and runtime.

## Required guardrails

- [ ] no mandatory admin-required installer;
- [ ] no mandatory Windows service;
- [ ] no custom audio driver;
- [ ] no kernel-mode component;
- [ ] no mandatory inbound local firewall port;
- [ ] no write access to Program Files for runtime data;
- [ ] no write access to HKLM registry for runtime configuration;
- [ ] no machine-wide dependency required for normal usage;
- [ ] app data must go to user-scoped folders;
- [ ] network communication must be outbound-only for API/signaling/WebRTC/TURN/STUN;
- [ ] any dependency requiring elevation must be rejected or documented as incompatible.

## Implementation review

- [ ] Distribution is unpackaged and self-contained, per-user, or portable where practical.
- [ ] Configuration, tokens, logs, caches, and updates use `%LOCALAPPDATA%`, `%APPDATA%`, or another user-owned location.
- [ ] The application does not attempt to install global runtime dependencies or change firewall rules.
- [ ] The selected audio-capture path works through user-mode Windows APIs available to a standard user.
- [ ] Installation and the primary publish flow have been tested using a standard, non-elevated Windows account.

## Inbound local listener (issue #75)

- The LAN listener (Kestrel) binds as the current user on an automatic or user-chosen port ≥ 1024;
  no URL ACL, certificate store binding, service or elevation.
- SonicRelay never creates firewall rules. The OS prompt on first listen is the user's choice;
  managed networks use a fixed port and an administrator-owned rule ([LAN mode](lan-mode.md)).
- It is off in `cloudOnly`, limited to 16 connections, and stops with the app.

## Known risks to validate

- **WinUI 3 packaging:** MSIX deployment policy or certificate requirements can be restricted on managed machines. Do not make machine-wide MSIX installation or sideloading policy changes a prerequisite; retain an unpackaged or per-user path.
- **Windows App SDK:** Framework-dependent deployment can introduce a runtime that is absent and cannot be installed globally by the user. Prefer self-contained deployment, or prove that the required runtime can be provisioned per user without elevation.
- **Audio capture:** WASAPI loopback normally operates in user mode, but protected content, device policy, remote sessions, and endpoint-specific behavior can limit capture. Do not solve those limitations with a custom driver or elevated component; document unsupported environments instead.

If a future change cannot satisfy an item, it is incompatible with the product constraint until a non-admin alternative is designed and validated.
