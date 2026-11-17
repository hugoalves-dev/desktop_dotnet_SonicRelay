# LAN / offline signaling with prioritized connectivity — design (issue #75)

Status: proposed · Issue: [desktop_dotnet_SonicRelay#75](https://github.com/hugoalves-dev/desktop_dotnet_SonicRelay/issues/75)
(including the owner's "ordem de prioridade" comment) · Architecture origin: RelayControl #40.

## 1. Intent

A SonicRelay Desktop publisher and a SonicRelay Mobile viewer that can reach each other over IP
must be able to stream WebRTC/Opus audio **without internet, the RelayControl API, PostgreSQL,
STUN or TURN**. When local signaling is not possible but the internet is, RelayControl carries the
signaling while the audio still flows **peer-to-peer** whenever a direct IP/UDP path exists. TURN
stays the last resort. The user must always see which path was actually used and why a path failed.

Why: today a dead internet link or API outage prevents a session between two devices sitting on the
same desk. Discovery, signaling and media are tangled into one cloud-only flow.

## 2. Goals, non-goals, anti-goals

**Goals**

1. Connectivity priority (owner comment): (1) same-subnet LAN via mDNS/DNS-SD; (2) routable
   different subnets via QR / manual `IP:port` / hostname bootstrap (DNS-SD reflector or unicast
   when the infrastructure provides it); (3) RelayControl signaling + direct ICE; (4) TURN.
2. Three independent decisions, modelled separately: **discovery**, **signaling transport**,
   **media/ICE policy**.
3. `Auto / LAN only / Cloud only` connectivity mode on both clients, independent of the existing
   relay mode (`automatic / forceRelay / disableFallback`).
4. Offline first pairing via QR or typed code, with cryptographic identity, an authenticated TLS
   channel, single-use temporary secrets and a persisted, locally revocable trust.
5. Mutual presence: the Desktop is discoverable by and can see Mobile devices, and vice versa,
   while both apps are active. Identity is only confirmed after pairing.
6. Effective path + actionable diagnostics on both UIs (no route, firewall, client isolation,
   expired/invalid pairing, certificate mismatch, API unavailable).
7. Cloud flow unchanged by default; no regression.

**Non-goals** (from the issue): a local copy of the API/DB, coturn on the LAN, multicast across
isolated VLANs, transparent LAN↔Cloud migration of a live media session, LAN on Flutter Web,
writing a custom unicast DNS-SD server.

**Anti-goals** (it fails even if it "works" when): a device receives audio because it merely
appeared on the network; the API bearer token is reused as a peer credential; any custom
cryptography; the Desktop needs elevation, a service, or edits the firewall; `LAN only` touches
RelayControl/STUN/TURN; the UI claims "P2P LAN" when media actually went through TURN or failed;
the client loops forever on an AP-isolated network; subnets are scanned.

## 3. Constraints

- Desktop is non-admin (docs/non-admin-checklist.md). `HttpListener` over HTTPS on Windows needs
  `netsh` URL ACL/cert binding (admin) → rejected. The MSIX declares `runFullTrust` only, so no
  AppContainer network capability is required; macOS is not sandboxed, so no entitlement change.
  First listen triggers the OS firewall prompt; we document it and never script firewall rules.
- mDNS is link-local (RFC 6762); it never crosses routers by itself. DNS-SD over unicast DNS
  (RFC 6763) works only when the network operator publishes records.
- Flutter mobile: iOS needs `NSLocalNetworkUsageDescription` + `NSBonjourServices`; Android needs
  `CHANGE_WIFI_MULTICAST_STATE` for multicast. Flutter Web is excluded.
- The cloud backend today is not a dumb relay: it emits `session.joined/left`, `participant.*`
  broadcasts and enforces who may publish audio. The LAN host must play that role.

## 4. Approaches considered (decided with the owner on 2026-11-17)

| Decision | Chosen | Rejected and why |
| --- | --- | --- |
| Desktop local server | **Embedded Kestrel** (ASP.NET Core) with an in-memory TLS certificate, WebSockets and connection/size limits. Non-admin on any port. | `TcpListener + SslStream + WebSocket.CreateFromStream`: smaller package but hand-written HTTP upgrade parsing = attack surface. `HttpListener`: needs admin for HTTPS on Windows. |
| Peer authentication | **Server-pinned TLS + per-device LAN credential** issued at pairing (256-bit random, stored hashed on Desktop, secure storage on Mobile, revocable). | Mutual TLS: Mobile would have to generate/persist an X.509 client cert in Dart — fragile on Android/iOS. API bearer reuse: explicitly forbidden. |
| Where host logic lives | **`ISessionHost` abstraction** (`CloudSessionHost`, `LanSessionHost`, composed by `DualSessionHost`) with a routing `ISignalingClient`. `PublisherWorkflow` and `WebRtcPublisher` keep their contracts. | A fake REST API inside the Desktop: duplicates the domain (anti-goal in #40). Four separate WebRTC session types per priority level: the comment explicitly rejects it. |

## 5. Model: three separate decisions

```text
 Discovery (where is the peer?)          Signaling transport (who relays SDP/ICE?)     Media / ICE policy
 ─────────────────────────────           ─────────────────────────────────────────     ─────────────────────
 mDNS/DNS-SD (same L2, or reflector)     LAN host on the Desktop (TLS, pinned)         LAN-signaled: iceServers [] (host candidates)
 QR / manual IP:port / hostname          RelayControl WebSocket (existing)             Cloud-signaled: existing STUN/TURN policy,
 last-known endpoints of a trusted peer                                                 forceRelay honoured independently
 iOS Bonjour unicast domains (OS-level)
```

### 5.1 Connectivity mode

New literal set, shared string-for-string by both clients (not by the backend; it is never sent):

```text
connectivityMode = "auto" | "lanOnly" | "cloudOnly"     default: "auto"
```

| Mode | Desktop | Mobile |
| --- | --- | --- |
| `auto` | LAN host running **and** cloud session when the API is reachable (dual-homed session). If the API is unreachable, the session is LAN-only and the UI says so. | For a trusted publisher reachable on the LAN (discovered or last-known endpoint), try LAN first; on failure **before the session is established** (see 5.4) fall back to the existing cloud join. |
| `lanOnly` | LAN host only. No API/STUN/TURN calls at all, including the device-identity bootstrap. | LAN only; explicit error if unreachable. No API/STUN/TURN calls. |
| `cloudOnly` | Today's behaviour; LAN host and mDNS stopped. | Today's behaviour. Direct P2P still allowed by ICE; `forceRelay` independent. |

### 5.2 Media / ICE policy

- A peer whose signaling arrived over the LAN host uses `iceServers: []` and
  `iceTransportPolicy: all` in **every** mode. Host candidates cover same-subnet and routed-subnet
  cases; no internet dependency is introduced into a LAN session.
- A peer signaled through RelayControl keeps today's policy (`BackendIceServersProvider`, relay
  mode, `forceRelay`), so direct host/srflx pairs are still preferred by ICE and TURN is used only
  when no direct pair succeeds (or when `forceRelay` is set).
- Desktop: `SipSorceryPeerConnectionFactory` gains a per-viewer policy
  (`Func<string viewerId, PeerIcePolicy>`), derived from the viewer's signaling origin.

### 5.3 Effective path

After ICE connects, both sides read the nominated candidate pair and report:

| Signaling | Selected pair | Label |
| --- | --- | --- |
| LAN | host ↔ host | `LAN · P2P direct` |
| RelayControl | host/srflx/prflx (no relay) | `RelayControl signaling · P2P direct` |
| RelayControl | any `relay` candidate | `RelayControl signaling · TURN relay` |

Never label a session P2P before ICE has reached `connected`.

### 5.4 "Before the session is established"

The Mobile commits to a transport only when **signaling is up and ICE reaches `connected`**. In
`auto`, a LAN attempt is abandoned and the cloud join starts when any of these happens first:
no LAN endpoint (discovered or remembered), TCP/TLS connect fails on every endpoint (3 s each,
endpoints tried in parallel), the LAN host rejects the device (it can then never succeed), or ICE
does not reach `connected` within 15 s. Once connected, a later drop reconnects **on the same
transport** (bounded backoff) and never migrates the media to the other transport.

## 6. Desktop design

### 6.1 Identity

`LanIdentity`: a long-lived self-signed **ECDSA P-256** certificate (CN = `SonicRelay <short id>`,
5-year validity) generated on first use and stored through the existing `ITokenProtector`
(DPAPI on Windows, Secret Service on Linux, Keychain on macOS) in the user-scoped app data folder.
**Pin = SHA-256 of the certificate DER**, base64url (whole certificate, not the SPKI, so Dart can
verify it without ASN.1 parsing). Regenerating the identity (Settings → "Reset LAN identity")
invalidates every pairing by design. `publisherDeviceId` is a random GUID persisted with it.

### 6.2 LAN host (Kestrel)

- Runs only while `connectivityMode ∈ {auto, lanOnly}`; started/stopped live on mode change and on
  app exit. Binds `IPAddress.IPv6Any` (dual-stack) on a **dynamic port by default** or a user-set
  fixed port (so an admin can create one narrow firewall rule). The chosen port is persisted and
  reused across runs to keep remembered endpoints valid.
- TLS 1.2+ with `LanIdentity`. Limits: 16 concurrent connections, 64 KiB message size, 10 s
  handshake/upgrade timeout, request headers 8 KiB, idle keepalive via the existing ping/pong.
- Routes (protocol v1, canonical spec lives in RelayControl `docs/protocol/lan-signaling-v1.md`):
  - `POST /lan/v1/pair` → exchange a pairing secret for a device credential.
  - `GET /lan/v1/signaling` → WebSocket; `Authorization: SonicRelay-Lan <credential>` +
    `X-SonicRelay-Device: <viewerDeviceId>`.
  - Any other path → 404. No unauthenticated info endpoint.
- Rate limiting per remote IP: 5 failed pairing/auth attempts per minute, then 429 for 60 s.

### 6.3 Pairing (offline)

`LanPairingService` opens a **pairing window** when the user opens the Pairing page:
`pairingId` (GUID), 80-bit `secret`, TTL **120 s**, **single use**, invalidated after 5 failed
attempts. Comparisons are constant-time (`CryptographicOperations.FixedTimeEquals`).

QR payload v2 carries cloud and LAN blocks; either may be absent depending on the mode and API
reachability. The Flutter parser keeps accepting today's exact 2-key cloud payload.

```json
{
  "v": 2,
  "cloud": { "challengeId": "…guid…", "code": "…" },
  "lan": {
    "pid": "…guid…", "sec": "<base64url 80-bit>", "pin": "<base64url sha256(cert DER)>",
    "did": "<publisherDeviceId>", "name": "DESKTOP-01",
    "ep": ["192.168.1.20:47321", "[fd00::20]:47321", "desktop-01.local:47321"]
  }
}
```

**Manual code** (no camera, routed subnet): the user types the `IP:port` or hostname shown on the
Desktop plus a 16-character Crockford base32 code `XXXX-XXXX-XXXX-XXXX` = 40-bit pin prefix ‖
40-bit secret. The Mobile accepts the server certificate only if its pin starts with that prefix.
Within a 120 s single-use window this resists MITM (forging a cert with a matching 40-bit prefix
in time is impractical for a LAN attacker); documented as weaker than the full-pin QR.

Pair request (over pinned TLS):
`{ pid, sec | code, viewerDeviceId, viewerName, platform }` →
`{ credential, publisherDeviceId, publisherName, pin }` (credential = 256-bit random, base64url).
Replays fail because the secret is consumed atomically on the first valid use.

### 6.4 Trust store

`LanTrustStore` (protected JSON): per viewer `{ viewerDeviceId, name, platform, credentialSha256,
pairedAt, lastSeenAt, revoked }`. Settings lists paired devices with **Revoke**; revocation closes
any live socket from that device (`4403`). Documented limit: revoking a device in the cloud does
not reach the Desktop's LAN trust while the devices are offline; LAN revocation is local and immediate.

### 6.5 Session host seam

```text
PublisherWorkflow ──► ISessionHost ──┬─ CloudSessionHost  (wraps ISessionApiClient + SignalingClient; today's code)
                       │             ├─ LanSessionHost    (local session state + LanSignalingHub)
                       │             └─ DualSessionHost   (auto: cloud when reachable + LAN always)
                       └─ .Signaling : ISignalingClient ──► WebRtcPublisher (unchanged)
```

- `ISessionHost`: `CreateSessionAsync(mode)`, `EndSessionAsync`, `GetParticipantsAsync`,
  `SetAudioPermissionAsync`, `Signaling`, `Kind` (Cloud | Lan | Dual) and `ViewerOrigin(viewerId)`.
  `PublisherWorkflow` swaps `ISessionApiClient` + `ISignalingClient` for `ISessionHost`; behaviour
  for cloud is byte-identical (covered by the existing workflow tests).
- `LanSignalingHub` turns accepted sockets into `ISignalingClient` events and **emulates the
  backend roles**: after auth + `viewer.ready` it emits `session.joined` with the viewer's stable
  device id as participant id; it is the authority for `participant.capabilities` (viewers start
  with `canSendAudio=false`; the publisher grants it in duplex), re-broadcasts
  `participant.audio_state_changed`, routes `to`-addressed messages, enforces that a viewer can only
  address the publisher, applies a 30 s reconnect grace (`participant.disconnected/reconnected`),
  and sends `session.ended` on stop. Close codes: `4401` bad credential, `4403` revoked, `4409`
  no live session / full, `4410` session ended.
- `DualSessionHost` uses the cloud session id as the LAN session id when present (one session,
  two doors), otherwise a local GUID. Its routing signaling client sends each `to`-addressed message
  through the transport the viewer joined on and broadcasts to both.

### 6.6 Discovery (mDNS/DNS-SD)

- Desktop announces `_sonicrelay._tcp` on all up, non-loopback interfaces. Instance
  `SonicRelay-<6 hex of sha256(publisherDeviceId)>`, SRV port = LAN host port,
  TXT `v=1`, `id=<same 6-hex hint>`. No name, credentials or pin in TXT. The full identity is
  only checked through the pinned TLS handshake.
- Desktop browses `_sonicrelay-viewer._tcp` and shows "nearby devices": paired ones as online
  (by TXT `id` hint, then confirmed when they connect), unknown ones as "not paired".
- Library: a maintained .NET mDNS/DNS-SD package chosen in a time-boxed spike (Stage D, first
  task). Criteria: announce + browse, multi-interface, IPv4/IPv6, no admin, MIT/Apache license,
  works on Windows/Linux/macOS. If none qualifies, fall back to a minimal announcer on top of
  `UdpClient` multicast with RFC 6762 record encoding from a vetted DNS library.
- Unicast DNS-SD / reflectors: reflectors work transparently. Unicast DNS-SD is documented for
  operators (PTR/SRV/TXT records). iOS Bonjour browses wide-area domains natively; Android and
  Desktop rely on QR/manual for routed subnets in this iteration.

### 6.7 Endpoints and multiple interfaces

`LanEndpointResolver` lists up, non-loopback, non-link-local IPv4/IPv6 unicast addresses
(Ethernet first, Wi-Fi, then others; VPN/tunnel adapters last) plus `<hostname>.local`. It caps
the list at 4 for the QR. Kestrel listens on any address, so no interface is wrongly excluded.
SIPSorcery gathers host candidates on all interfaces (unchanged).

### 6.8 UI (Avalonia)

- Settings: "Connectivity" section with `Auto / LAN only / Cloud only`, LAN port (Auto or fixed),
  paired LAN devices with Revoke, "Reset LAN identity", firewall help link.
- Pairing page: one QR (v2) plus the manual code and endpoints; countdown for the 120 s window.
- Dashboard: "Connection path" per viewer (5.3) and a LAN host status line (listening on port N /
  bind failed / disabled by mode).

### 6.9 Diagnostics catalogue (both apps)

| Code | When | Message direction |
| --- | --- | --- |
| `lan.bind_failed` | port in use / denied | choose another port |
| `lan.unreachable` | TCP connect timeout on every endpoint | no route, wrong subnet/VLAN ACL, firewall blocking inbound on the Desktop |
| `lan.isolation_suspected` | publisher seen via mDNS but TCP unreachable | Wi-Fi client/AP isolation |
| `lan.pin_mismatch` | TLS cert ≠ pin | not the paired Desktop; re-pair |
| `lan.pairing_expired` / `lan.pairing_invalid` | TTL elapsed / bad or reused secret | open pairing again |
| `lan.revoked` | 4403 | device was removed on the Desktop |
| `ice.no_direct_route` | ICE failed with no relay allowed | signaling worked but UDP between peers is blocked: firewall/ACL/isolation; LAN cannot bypass it |
| `cloud.unavailable` | API unreachable in auto/cloud | offline; LAN still possible |

Retries are bounded (max 5 attempts with backoff, then a terminal error state with a manual
"Retry"). No subnet scanning ever.

### 6.10 Logging and secrets

Pairing secrets, codes and credentials never appear in logs (`DiagnosticRedactor` patterns added).
Pins are logged truncated to 8 characters. SDP/ICE payload redaction stays as is.

### 6.11 Startup gates

Today the Desktop shell gates the dashboard on the cloud device identity/pairing. In `lanOnly`
(and in `auto` when the API is unreachable) that gate must not block: the LAN host, LAN pairing
and LAN sessions work without a cloud device identity. Cloud features stay disabled with
`cloud.unavailable` until the API answers.

## 7. Mobile (Flutter) design

- Startup gates (first-run server setup, cloud pairing gate) must not block `lanOnly`: a user with
  no reachable server can pair over the LAN and listen.

- `ConnectivityModeStorage` (`auto` default) + Settings UI; LAN UI hidden on web (`kIsWeb`).
- `LanPublisherStore` (flutter_secure_storage): trusted publishers
  `{ publisherDeviceId, name, pin, credential, lastEndpoints[], lastSeenAt }`, delete = forget.
- `PinnedHttpClientFactory`: `HttpClient(context: SecurityContext(withTrustedRoots: false))`
  with `badCertificateCallback` that accepts **only** `sha256(cert.der) == pin` (or prefix for the
  manual code). The same client is passed to `WebSocket.connect(customClient:)`.
- `LanDiscoveryService` (package `bonsoir`): browse `_sonicrelay._tcp`, broadcast
  `_sonicrelay-viewer._tcp` while the app is in the foreground. Android: add
  `CHANGE_WIFI_MULTICAST_STATE`; iOS: `NSLocalNetworkUsageDescription`, `NSBonjourServices`.
- Pairing: the QR scanner parses payload v2 (cloud + lan blocks, legacy cloud payload still
  accepted). A "Pair over local network" form takes `host:port` + manual code.
- `SessionConnector` decides the transport per 5.1/5.4: LAN signaling goes through the existing
  `WebSocketClient` (new LAN connector + headers provider), cloud uses today's path. The
  `WebRtcReceiverService` gets `iceServers: []` for LAN-signaled sessions.
- Reconnect on Wi-Fi drop / IP change: re-resolve via mDNS, then remembered endpoints, bounded
  backoff, then a terminal error (5.4, 6.9). Effective path and diagnostics are shown in the
  listener page `IceStatePanel`.

## 8. RelayControl

No runtime change. Add `docs/adr/0009-lan-offline-signaling.md` (the #40 decision record that was
never committed) and the canonical `docs/protocol/lan-signaling-v1.md` (routes, payloads, QR v2,
manual code, close codes, mDNS records, security properties), referenced by both clients.

## 9. Security summary

- Discovery ≠ authentication: TXT has only a version and a non-identifying 6-hex hint.
- Confidentiality/authenticity: TLS with a pinned certificate; no custom crypto.
- Pairing: single-use 80-bit secret, 120 s TTL, 5-attempt cap, per-IP rate limit, constant-time
  compare → brute force and replay rejected.
- Authorization: only trusted, non-revoked credentials reach the signaling hub; only those peers
  get an offer, so no audio flows to an unpaired device. DTLS-SRTP protects the media as today.
- Credential at rest: SHA-256 hash on the Desktop; secure storage on the Mobile.
- Listener: off in `cloudOnly`, bounded connections/sizes/timeouts, closed on exit.

## 10. Verification

- **Automated (CI):** pairing service (TTL, single use, attempt cap, constant time), trust store,
  QR v2 / manual code encode-decode, LAN hub host emulation (join/leave/capabilities/routing/close
  codes), `DualSessionHost` routing, per-viewer ICE policy (`iceServers` empty for LAN), mode
  matrix (no API/ICE-provider calls in `lanOnly`), in-process Kestrel integration with real TLS
  and a pinned `ClientWebSocket` (good pin, wrong pin, revoked, replayed secret). Flutter: payload
  parsing, pin verification callback, connector fallback state machine, ICE config for LAN,
  bounded reconnect.
- **Manual matrix (must be executed on real devices before #75 closes; not doable in CI):**
  same subnet offline; `192.168.1.20/24` ↔ `192.168.2.10/24` routed with mDNS blocked (QR/manual,
  path shows `LAN · P2P direct`, no TURN); negative case without route/UDP (explicit
  `lan.unreachable` / `ice.no_direct_route`, never "P2P"); AP isolation (no loop); Auto fallback
  to RelayControl with P2P media; `lanOnly` with packet capture showing zero API/STUN/TURN traffic;
  revoke while connected; Wi-Fi drop and IP change.

## 11. Delivery

One PR per repository; the commits follow the stages of the implementation plans:

- RelayControl: ADR 0009 + protocol spec.
- Desktop (`docs/superpowers/plans/2026-11-17-lan-offline-signaling.md`): A connectivity mode +
  per-viewer ICE policy · B identity, trust store, pairing, Kestrel host · C `ISessionHost`,
  `LanSignalingHub`, `DualSessionHost` · D mDNS · E UI, diagnostics, docs.
- Flutter (`docs/superpowers/plans/2026-11-17-lan-offline-signaling.md`): A mode + ICE ·
  B trusted publishers, pinned client, pairing payload v2 · C session connector + fallback ·
  D mDNS · E UI, diagnostics, docs.

## 12. Left to the builder

The .NET mDNS library choice (6.6 spike), exact widget/XAML layout, default backoff constants
within the stated bounds, and file/class placement inside the existing project boundaries.
