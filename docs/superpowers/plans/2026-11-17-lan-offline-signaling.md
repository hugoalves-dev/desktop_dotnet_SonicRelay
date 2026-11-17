# LAN / Offline Signaling (Desktop) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Desktop publisher host authenticated local signaling, pair phones offline and stream WebRTC/Opus P2P on the LAN, with `auto / lanOnly / cloudOnly` connectivity and the effective path shown in the UI.

**Architecture:** A new `SonicRelay.Windows.Lan` project holds identity, pairing, trust, the Kestrel host, the signaling hub (backend-role emulation) and mDNS. Presentation composes an `ISessionHost` (Cloud, Lan or Dual) whose `ISessionApiClient` + `ISignalingClient` feed the unchanged `PublisherWorkflow`/`WebRtcPublisher`. ICE policy becomes per viewer, from its signaling origin.

**Tech Stack:** .NET 10, Avalonia 11, ASP.NET Core Kestrel (`FrameworkReference Microsoft.AspNetCore.App`), SIPSorcery, xUnit 2.9, QRCoder, a DNS-SD library chosen in Task 9.

**Spec:** `docs/superpowers/specs/2026-11-17-lan-offline-signaling-design.md`

## Global Constraints

- `connectivityMode` literals: `"auto"`, `"lanOnly"`, `"cloudOnly"`; default `"auto"`; stored in `connectivity.json` beside `preferences.json` (never sent to the backend).
- LAN-signaled peers: `iceServers` empty, `iceTransportPolicy all`, in every mode. Cloud peers: unchanged policy.
- `lanOnly`: zero calls to RelayControl (API, signaling, ICE servers, device bootstrap).
- Identity: ECDSA P-256 self-signed, 5-year validity; pin = base64url(SHA-256(cert DER)).
- Pairing window: 80-bit secret, TTL 120 s, single use, invalid after 5 failed attempts, constant-time compare.
- Manual code: 16 Crockford base32 chars `XXXX-XXXX-XXXX-XXXX` = 40-bit pin prefix ‖ 40-bit secret.
- Credential: 256-bit random base64url; Desktop stores SHA-256 only.
- Host limits: 16 connections, 64 KiB messages, 10 s handshake, 8 KiB headers; 5 failed auth/pair per IP per minute → 429 for 60 s.
- Routes: `POST /lan/v1/pair`, `GET /lan/v1/signaling` (WebSocket); header `Authorization: SonicRelay-Lan <credential>` + `X-SonicRelay-Device`. Everything else 404.
- Close codes: `4401` bad credential, `4403` revoked, `4409` no live session/full, `4410` session ended.
- mDNS: announce `_sonicrelay._tcp`, browse `_sonicrelay-viewer._tcp`, TXT only `v=1`, `id=<6 hex>`.
- Non-admin: no firewall edits, no elevation, no service.
- Secrets/codes/credentials never logged; pins logged truncated to 8 chars.

## Review Focus

1. A replayed or concurrently double-submitted pairing secret → exactly one success, the other `pairing_invalid` (Task 3 test `Redeem_IsAtomic_UnderConcurrency`).
2. A revoked device with a live socket → socket closed with 4403 immediately (Task 6 test `Revoke_ClosesLiveSocket`).
3. A viewer addressing another viewer (`to` ≠ publisher) → dropped, never forwarded (Task 6 test `ViewerToViewer_IsDropped`).
4. API unreachable in `auto` → session still created as LAN-only and labelled so, not an error (Task 7 test `Auto_ApiDown_FallsBackToLanOnly`).
5. Port in use when starting the host → `lan.bind_failed` state, app keeps working in cloud (Task 5 test `Start_PortInUse_ReportsBindFailed`).

---

### Task 1: Connectivity mode + per-viewer ICE policy

**Files:**
- Create: `src/SonicRelay.Windows.Core/Configuration/ConnectivityModes.cs`, `src/SonicRelay.Windows.Core/Configuration/ConnectivityPreferenceStore.cs`
- Create: `src/SonicRelay.Windows.WebRtc/PeerIcePolicy.cs`
- Modify: `src/SonicRelay.Windows.WebRtc/SipSorceryPeerConnectionFactory.cs`
- Test: `tests/SonicRelay.Windows.Core.Tests/ConnectivityPreferenceStoreTests.cs`, `tests/SonicRelay.Windows.WebRtc.Tests/SipSorceryPeerConnectionFactoryTests.cs`

**Interfaces:**
- Produces: `ConnectivityModes { Auto, LanOnly, CloudOnly; bool IsValid(string?) }`; `ConnectivityPreferenceStore(string? path = null) { string Mode; Task SetModeAsync(string); int? FixedLanPort; Task SetFixedLanPortAsync(int?); int? LastLanPort; Task SetLastLanPortAsync(int) ; event Action? Changed }`.
- Produces: `enum SignalingOrigin { Cloud, Lan }`; `PeerIcePolicy(bool UseConfiguredServers, bool ForceRelay)` with `static PeerIcePolicy For(SignalingOrigin origin, bool forceRelay)` (Lan → `(false,false)`).
- Produces: factory ctor parameter `Func<string, PeerIcePolicy>? icePolicy = null` (default: Cloud policy from `forceRelay`); when `UseConfiguredServers` is false the provider is **not called**.

- [ ] Step 1: tests — `Defaults_ToAuto`, `Persists_AndReloads`, `InvalidMode_Throws`, `CorruptFile_FallsBackToAuto`, `FixedPort_OutOfRange_Throws` (1024–65535).
- [ ] Step 2: tests — `LanViewer_GetsNoIceServers_AndProviderNotCalled`, `CloudViewer_KeepsProviderServers`, `CloudViewer_ForceRelay_UsesRelayPolicy`.
- [ ] Step 3: run `dotnet test tests/SonicRelay.Windows.Core.Tests tests/SonicRelay.Windows.WebRtc.Tests` → new tests FAIL (types missing).
- [ ] Step 4: implement (store mirrors `RelayPreferenceStore` JSON pattern, own file `connectivity.json`).
- [ ] Step 5: run the same command → PASS. Commit `feat(lan): connectivity mode and per-viewer ICE policy (#75)`.

### Task 2: LAN project, identity and endpoint resolver

**Files:**
- Create: `src/SonicRelay.Windows.Lan/SonicRelay.Windows.Lan.csproj` (refs Core, Signaling, ApiClient; `FrameworkReference Microsoft.AspNetCore.App`), add to `SonicRelay.Windows.slnx`.
- Create: `src/SonicRelay.Windows.Lan/Identity/LanIdentity.cs`, `LanIdentityStore.cs`, `src/SonicRelay.Windows.Lan/Network/LanEndpointResolver.cs`
- Create: `tests/SonicRelay.Windows.Lan.Tests/` (xunit 2.9.3, same package versions as other test projects) with `LanIdentityStoreTests.cs`, `LanEndpointResolverTests.cs`.

**Interfaces:**
- Produces: `LanIdentity { X509Certificate2 Certificate; Guid PublisherDeviceId; string Pin; string Hint /*6 hex of sha256(deviceId)*/ }`; `static string LanIdentity.ComputePin(byte[] der)`.
- Produces: `LanIdentityStore(ITokenProtector protector, string? directory = null) { Task<LanIdentity> LoadOrCreateAsync(CancellationToken); Task<LanIdentity> ResetAsync(CancellationToken) }` — persists PFX bytes protected by `ITokenProtector`.
- Produces: `LanEndpointResolver(Func<IEnumerable<NetworkInterface>>? source = null) { IReadOnlyList<string> Resolve(int port, string hostName) }` — order Ethernet, Wi-Fi, others, tunnel/VPN last; excludes loopback/link-local/down; IPv6 bracketed; appends `<host>.local:<port>`; max 4.

- [ ] Step 1: tests — `Creates_P256_FiveYearCert`, `Reload_ReturnsSamePinAndDeviceId`, `Reset_ChangesPin`, `Pin_IsBase64UrlSha256OfDer` (known vector), `Resolver_OrdersAndCaps`, `Resolver_SkipsLoopbackAndLinkLocal`, `Resolver_BracketsIpv6`.
- [ ] Step 2: run `dotnet test tests/SonicRelay.Windows.Lan.Tests` → FAIL.
- [ ] Step 3: implement (`CertificateRequest` + `ECDsa.Create(ECCurve.NamedCurves.nistP256)`; resolver takes an abstraction over interface data so tests feed fakes).
- [ ] Step 4: run → PASS. Commit `feat(lan): publisher identity and endpoint resolver (#75)`.

### Task 3: Pairing service, QR v2 and manual code

**Files:**
- Create: `src/SonicRelay.Windows.Lan/Pairing/LanPairingService.cs`, `LanPairingWindow.cs`, `LanPairingPayload.cs`, `ManualPairingCode.cs`, `CrockfordBase32.cs`
- Test: `tests/SonicRelay.Windows.Lan.Tests/LanPairingServiceTests.cs`, `ManualPairingCodeTests.cs`, `LanPairingPayloadTests.cs`

**Interfaces:**
- Produces: `LanPairingWindow(Guid PairingId, byte[] Secret /*10 bytes*/, DateTimeOffset ExpiresAt)`; `string ManualCode` (formatted).
- Produces: `LanPairingService(LanIdentity identity, TimeProvider time) { LanPairingWindow Open(); void Close(); LanRedeemResult Redeem(Guid? pairingId, byte[]? secret, string? manualCode) }`; `enum LanRedeemResult { Accepted, Invalid, Expired, Exhausted }`. Accepts either (pid+secret) or a manual code matching the open window. Atomic: one `Accepted` per window, then closed.
- Produces: `ManualPairingCode.Encode(string pin, byte[] secret) -> string`, `ManualPairingCode.TryDecode(string code, out byte[] pinPrefix5, out byte[] secret5)`; Crockford decoding is case-insensitive, ignores `-`, maps `O→0`, `I/L→1`.
- Produces: `LanPairingPayload.BuildQrJson(CloudChallenge? cloud, LanPairingWindow? lan, LanIdentity id, string name, IReadOnlyList<string> endpoints) -> string` matching spec §6.3 (`v:2`, keys `cloud{challengeId,code}`, `lan{pid,sec,pin,did,name,ep}`); null blocks omitted.

- [ ] Step 1: tests — `Redeem_ValidSecret_AcceptsOnce`, `Redeem_Replay_Invalid`, `Redeem_AfterTtl120s_Expired` (FakeTimeProvider), `Redeem_FiveFailures_Exhausted`, `Redeem_IsAtomic_UnderConcurrency` (50 parallel valid redeems → exactly 1 Accepted), `ManualCode_RoundTrip`, `ManualCode_Format_Is4x4`, `ManualCode_Decoding_IsLenient`, `Qr_OmitsNullBlocks`, `Qr_MatchesSpecShape`.
- [ ] Step 2: run → FAIL. Step 3: implement (secret compare via `CryptographicOperations.FixedTimeEquals`; lock around window state). Step 4: run → PASS.
- [ ] Step 5: commit `feat(lan): offline pairing windows, QR v2 and manual code (#75)`.

### Task 4: Trust store

**Files:**
- Create: `src/SonicRelay.Windows.Lan/Trust/LanTrustStore.cs`, `LanTrustedDevice.cs`
- Test: `tests/SonicRelay.Windows.Lan.Tests/LanTrustStoreTests.cs`

**Interfaces:**
- Produces: `LanTrustedDevice(string ViewerDeviceId, string Name, string Platform, string CredentialSha256, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt, bool Revoked)`.
- Produces: `LanTrustStore(ITokenProtector protector, string? path = null) { Task<string> AddAsync(string viewerDeviceId, string name, string platform) /*returns new credential, replaces previous for same id*/; LanTrustedDevice? Authenticate(string viewerDeviceId, string credential) /*null if unknown, wrong or revoked*/; Task RevokeAsync(string viewerDeviceId); IReadOnlyList<LanTrustedDevice> Devices; Task TouchAsync(string viewerDeviceId); event Action<string>? Revoked }`.

- [ ] Step 1: tests — `Add_ReturnsCredential_StoresOnlyHash` (file bytes never contain the credential), `Authenticate_RightCredential`, `Authenticate_Wrong_ReturnsNull`, `Revoked_ReturnsNull_AndRaisesEvent`, `Repair_ReplacesOldCredential`, `Persists_Protected`.
- [ ] Step 2–4: fail → implement → pass. Step 5: commit `feat(lan): local trust store with revocation (#75)`.

### Task 5: Kestrel LAN host (pairing endpoint + authenticated WebSocket)

**Files:**
- Create: `src/SonicRelay.Windows.Lan/Hosting/LanSignalingHost.cs`, `LanHostOptions.cs`, `LanHostState.cs`, `AttemptLimiter.cs`
- Test: `tests/SonicRelay.Windows.Lan.Tests/LanSignalingHostTests.cs` (real TLS on `127.0.0.1`, client uses `HttpClientHandler.ServerCertificateCustomValidationCallback` pin check and `ClientWebSocket` with `RemoteCertificateValidationCallback`).

**Interfaces:**
- Consumes: Tasks 2–4.
- Produces: `LanSignalingHost(LanIdentity identity, LanPairingService pairing, LanTrustStore trust, ILanConnectionSink sink, LanHostOptions options, DiagnosticLog? log = null) { Task StartAsync(int? port, CancellationToken); Task StopAsync(); int? Port; LanHostState State; string? Error; event Action? StateChanged }`; `enum LanHostState { Stopped, Listening, BindFailed }`.
- Produces: `interface ILanConnectionSink { Task AcceptAsync(LanPeerConnection connection, CancellationToken ct); }` and `LanPeerConnection { string ViewerDeviceId; WebSocket Socket; IPEndPoint Remote }`.
- Pair request JSON `{ pid?, sec?, code?, viewerDeviceId, viewerName, platform }` → 200 `{ credential, publisherDeviceId, publisherName, pin }`; 400 `pairing_invalid`, 410 `pairing_expired`, 429 rate limited.

- [ ] Step 1: tests — `Pair_ValidSecret_ReturnsCredential`, `Pair_Replay_Returns400`, `Pair_RateLimit_After5Failures_Returns429`, `Signaling_ValidCredential_ReachesSink`, `Signaling_BadCredential_Closes4401`, `Signaling_Revoked_Closes4403`, `UnknownPath_404`, `WrongPin_ClientRejectsHandshake`, `Start_PortInUse_ReportsBindFailed`, `Stop_ClosesListener`.
- [ ] Step 2–4: fail → implement (`WebApplication.CreateSlimBuilder`, `ConfigureKestrel` limits, `ListenAnyIP(port, o => o.UseHttps(cert))`, `app.UseWebSockets(new() { KeepAliveInterval = 15s })`) → pass.
- [ ] Step 5: commit `feat(lan): Kestrel TLS host for pairing and signaling (#75)`.

### Task 6: LAN signaling hub + LAN session API (backend-role emulation)

**Files:**
- Create: `src/SonicRelay.Windows.Lan/Sessions/LanSignalingHub.cs` (implements `ISignalingClient`, `ILanConnectionSink`), `LanSessionApiClient.cs` (implements `ISessionApiClient`)
- Test: `tests/SonicRelay.Windows.Lan.Tests/LanSignalingHubTests.cs`, `LanSessionApiClientTests.cs` (hub tested with an in-memory `WebSocket` pair via `WebSocket.CreateFromStream` over a duplex pipe).

**Interfaces:**
- Consumes: `ISignalingClient`, `ISignalingMessageHandler`, `SignalingMessageEnvelope`, `SignalingMessageTypes` (existing).
- Produces: `LanSignalingHub(IEnumerable<ISignalingMessageHandler> handlers, LanTrustStore trust, TimeProvider time, int maxViewers = 15)`; `ConnectAsync(sessionId)` marks the hub live (State → Connected) without any network; `CloseAsync` sends `session.ended` + close `4410` to all. `IReadOnlyCollection<string> ConnectedViewers`.
- Produces: `LanSessionApiClient(LanSignalingHub hub)`: `CreateSessionAsync` returns a new GUID session, code `"LAN"`, echoes mode; participants/permissions kept in memory; `SetAudioPermissionAsync` updates and broadcasts `participant.capabilities`.
- Behaviour pins: on `viewer.ready` → handlers get `session.joined` with `from = viewerDeviceId`; capabilities broadcast with `canSendAudio=false` initially; viewer messages get `from` stamped by the hub (client value ignored); viewer `to` other than the publisher → dropped; socket drop → `participant.disconnected`, re-auth within 30 s → `participant.reconnected`, after 30 s → `session.left`; no live session → close `4409`; `ping` answered with `pong`.

- [ ] Step 1: tests — `ViewerReady_EmitsSessionJoined`, `FromIsStampedByHub`, `ViewerToViewer_IsDropped`, `PublisherOffer_RoutedToViewer`, `Drop_Then_Reconnect_Within30s_EmitsReconnected`, `Drop_After30s_EmitsLeft`, `NoSession_Closes4409`, `Close_SendsSessionEnded_And4410`, `Revoke_ClosesLiveSocket`, `Permission_BroadcastsCapabilities`, `MaxViewers_Closes4409`.
- [ ] Step 2–4: fail → implement → pass. Step 5: commit `feat(lan): signaling hub emulating backend session roles (#75)`.

### Task 7: Session hosts and runtime composition

**Files:**
- Create: `src/SonicRelay.Windows.Presentation/Sessions/ISessionHost.cs`, `CloudSessionHost.cs`, `LanSessionHost.cs`, `DualSessionHost.cs`, `RoutingSignalingClient.cs`, `DualSessionApiClient.cs`, `SessionHostSelector.cs`
- Modify: `src/SonicRelay.Windows.Presentation/PublisherRuntime.cs` (compose by mode), `PublisherWorkflow.cs` (add `InitializeLocalIdentityAsync(Guid publisherDeviceId, CancellationToken)`), `PublisherSnapshot.cs` (add `SessionHostKind? HostKind`, `bool CloudAvailable`)
- Test: `tests/SonicRelay.Windows.Presentation.Tests/SessionHostTests.cs`, `PublisherWorkflowTests.cs` (add)

**Interfaces:**
- Produces: `enum SessionHostKind { Cloud, Lan, Dual }`; `interface ISessionHost { SessionHostKind Kind; ISessionApiClient Sessions; ISignalingClient Signaling; SignalingOrigin OriginOf(string viewerId); }`.
- Produces: `RoutingSignalingClient(ISignalingClient cloud, LanSignalingHub lan)`: `ConnectAsync` connects both (cloud failure → LAN only + `CloudFailed` event), `SendAsync` routes by `To` to the origin recorded from inbound `from`, broadcasts (no `To`) to both; `State` = Connected if either is.
- Produces: `SessionHostSelector.SelectAsync(string mode, Func<CancellationToken, Task<bool>> apiReachable)` → `Cloud` for cloudOnly, `Lan` for lanOnly, `Dual` for auto with API, `Lan` for auto without API.
- Runtime: `PublisherRuntime` gets `ConnectivityPreferenceStore`, `LanServices` (identity, pairing, trust, host, hub); `SipSorceryPeerConnectionFactory` gets `icePolicy: viewerId => PeerIcePolicy.For(host.OriginOf(viewerId), relay.ForceRelay)`; host start/stop follows the mode (`Changed` event). Mode changes apply to the **next** session; an active session is not migrated.

- [ ] Step 1: tests — `Selector_Matrix` (4 cases), `Auto_ApiDown_FallsBackToLanOnly`, `LanOnly_NeverCallsCloudApiOrIceProvider` (strict fakes throw), `Routing_ToLanViewer_GoesToLan`, `Routing_Broadcast_GoesToBoth`, `Workflow_LocalIdentity_AllowsCreateSession`, existing `PublisherWorkflowTests` still green.
- [ ] Step 2–4: fail → implement → `dotnet test tests/SonicRelay.Windows.Presentation.Tests` PASS.
- [ ] Step 5: commit `feat(lan): cloud/LAN/dual session hosts and composition (#75)`.

### Task 8: Effective path detection

**Files:**
- Modify: `src/SonicRelay.Windows.WebRtc/SipSorceryPeerConnection.cs` (expose nominated pair candidate types), `WebRtcPublisherDiagnostics` (add per-viewer `ConnectionPath`)
- Create: `src/SonicRelay.Windows.WebRtc/ConnectionPath.cs`
- Test: `tests/SonicRelay.Windows.WebRtc.Tests/ConnectionPathTests.cs`

**Interfaces:**
- Produces: `enum ConnectionPath { Pending, LanDirect, CloudDirect, CloudTurnRelay, Failed }`; `static ConnectionPath ConnectionPathResolver.Resolve(SignalingOrigin origin, RTCIceConnectionState state, RTCIceCandidateType? local, RTCIceCandidateType? remote)` — Pending until `connected`; any `relay` → `CloudTurnRelay`; LAN origin + failed → `Failed` (diagnostic `ice.no_direct_route`).

- [ ] Step 1: tests — table of origin × state × candidate types incl. `NotConnected_IsPending_NeverP2P`. Step 2–4: fail → implement → pass. Step 5: commit `feat(lan): report the effective connection path (#75)`.

### Task 9: mDNS/DNS-SD announce and browse

**Files:**
- Create: `src/SonicRelay.Windows.Lan/Discovery/ILanServiceAdvertiser.cs`, `LanDiscoveryService.cs`, `NearbyViewer.cs`, implementation file named after the chosen library.
- Test: `tests/SonicRelay.Windows.Lan.Tests/LanDiscoveryServiceTests.cs` (fake advertiser).

**Interfaces:**
- Produces: `interface ILanServiceAdvertiser { Task AnnounceAsync(string instance, int port, IReadOnlyDictionary<string,string> txt, CancellationToken); Task StopAsync(); IObservable/ event Action<DiscoveredService> ServiceFound/ServiceLost; Task BrowseAsync(string serviceType, CancellationToken) }`.
- Produces: `LanDiscoveryService { Task StartAsync(LanIdentity id, int port); Task StopAsync(); IReadOnlyList<NearbyViewer> Nearby; event Action? NearbyChanged }`; instance `SonicRelay-<hint>`, TXT exactly `{ v=1, id=<hint> }`.

- [ ] Step 1 (spike, ≤1 h): evaluate candidate packages (e.g. `Makaretu.Dns.Multicast.New`, `Zeroconf`) against spec §6.6 criteria; record the choice and reason in `docs/lan-mode.md`. If none fits, implement the advertiser on `UdpClient` + RFC 6762 records.
- [ ] Step 2: tests — `Announce_TxtHasOnlyVersionAndHint`, `Nearby_MarksPairedByHint`, `Stop_StopsAnnouncing`. Step 3–4: fail → implement → pass.
- [ ] Step 5: commit `feat(lan): mDNS/DNS-SD announce and nearby viewers (#75)`.

### Task 10: UI, diagnostics and docs

**Files:**
- Modify: `src/SonicRelay.Windows.Desktop/ViewModels/SettingsViewModel.cs`, `Controls/SettingsView.axaml` (Connectivity section: mode, LAN port, paired LAN devices with Revoke, Reset LAN identity, nearby devices)
- Modify: `src/SonicRelay.Windows.Presentation/Pairing/PairingViewModel.cs`, `Controls/PairingView.axaml` (QR v2, manual code, endpoints, 120 s countdown, works without cloud)
- Modify: `ViewModels/DashboardShellViewModel.cs`, `MainWindowViewModel.cs` (pairing gate bypass in lanOnly / API down; host status line; per-viewer path label)
- Modify: `src/SonicRelay.Windows.Core/Diagnostics/DiagnosticRedactor.cs` (redact `credential`, `sec`, `code`, `SonicRelay-Lan` header values)
- Create: `docs/lan-mode.md` (usage, firewall help for Windows/macOS/Linux, routed-subnet QR/manual, unicast DNS-SD records for operators, troubleshooting by diagnostic code, manual test matrix); Modify `docs/architecture.md` (Lan project), `docs/non-admin-checklist.md` (inbound listener), `README.md` (link)
- Test: `tests/SonicRelay.Windows.Desktop.Tests/SettingsViewModelTests.cs`, `PairingViewModelTests.cs`, `MainWindowViewModelStateTests.cs`, `DiagnosticRedactorTests.cs`

- [ ] Step 1: tests — `Settings_ModeChange_Persists`, `Settings_Revoke_CallsTrustStore`, `Pairing_LanOnly_ShowsQrWithoutCloudBlock`, `Pairing_Countdown_ExpiresWindow`, `Gate_LanOnly_DoesNotRequireCloudIdentity`, `Dashboard_PathLabel_PendingUntilConnected`, `Redactor_RemovesLanSecrets`.
- [ ] Step 2–4: fail → implement → `dotnet test` (affected test projects) PASS; `dotnet build SonicRelay.Windows.slnx -c Release` clean.
- [ ] Step 5: commit `feat(lan): connectivity UI, LAN pairing page, diagnostics and docs (#75)`.
