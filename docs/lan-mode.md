# Local network (LAN) mode

SonicRelay Desktop can stream to a paired phone on the local network **without internet,
RelayControl, STUN or TURN** (issue #75). Protocol: RelayControl
[`docs/protocol/lan-signaling-v1.md`](https://github.com/vitorhugo-dotnet/dotnet_RelayControl/blob/main/docs/protocol/lan-signaling-v1.md);
design: [`superpowers/specs/2026-11-17-lan-offline-signaling-design.md`](superpowers/specs/2026-11-17-lan-offline-signaling-design.md).

## Connectivity modes (Settings → Connectivity)

| Mode | What happens |
| --- | --- |
| `auto` (default) | The local listener runs, and sessions are also created on RelayControl when it answers within 5 s. Paired phones on the network connect locally; others go through RelayControl. If RelayControl is down, the session is local-only (code `LAN`). |
| `lanOnly` | Only the local listener. SonicRelay never contacts RelayControl, STUN or TURN — not even to bootstrap the cloud device identity. |
| `cloudOnly` | Today's behaviour. The local listener and mDNS are off. Direct P2P is still preferred by ICE; `forceRelay` is independent. |

The mode applies to the **next** session; a live session never moves between transports.

## Connection priority

1. **Same subnet:** the phone finds the desktop through mDNS/DNS-SD (`_sonicrelay._tcp`).
2. **Different, routed subnets** (e.g. `192.168.1.20/24` ↔ `192.168.2.10/24`): multicast does not
   cross routers, so pair with the **QR code** or type an **address + local pairing code**.
   An mDNS reflector on the router makes discovery automatic; operators can also publish
   unicast DNS-SD records (below). Once paired, the phone remembers the addresses.
3. **RelayControl signaling + direct media:** in `auto`, when no local path works before the
   session starts. Audio still flows P2P whenever an IP/UDP path exists.
4. **TURN** only when no direct candidate pair works and the relay policy allows it.

Signaling over RelayControl cannot bypass a missing route or a firewall that blocks UDP between
the devices — that needs TURN or a network change.

## Pairing offline

Open **Pairing → Pair over the local network → Create local pairing code**. The single-use code
lasts **120 s**; five wrong attempts close it. The QR carries the desktop's addresses, its
certificate pin and the secret, plus the cloud challenge when one is open (one QR for both). The
typed code `XXXX-XXXX-XXXX-XXXX` carries a 40-bit pin prefix and a 40-bit secret.

Identity and trust:

- The desktop has a self-signed ECDSA P-256 certificate; phones **pin** its SHA-256. mDNS and IP
  addresses are never trusted on their own.
- Each pairing gives the phone a fresh 256-bit credential; the desktop stores only its hash.
- **Revoke** a phone on the pairing page (its socket closes with `4403`). **Reset LAN identity**
  (Settings) revokes every phone and takes effect after a restart.
- Revoking in the cloud does not reach the desktop while it is offline; revoke locally too.
- On Linux and macOS there is no secure storage yet, so LAN identity and pairings last for one run.

## Firewall and permissions (non-admin)

SonicRelay never edits the firewall. The first time the listener starts, Windows (Defender
Firewall) or macOS may ask whether to allow incoming connections — allow it for **private**
networks. In managed environments, set a **fixed port** in Settings and ask the administrator for
one inbound TCP rule for that port plus UDP for WebRTC (host candidates use ephemeral ports).

## Unicast DNS-SD (optional, for network operators)

To make routed subnets discoverable without QR, publish in the search domain:

```text
_sonicrelay._tcp.<domain>.          PTR  SonicRelay-<hint>._sonicrelay._tcp.<domain>.
SonicRelay-<hint>._sonicrelay._tcp.<domain>.  SRV  0 0 <port> <desktop-host>.<domain>.
SonicRelay-<hint>._sonicrelay._tcp.<domain>.  TXT  "v=1" "id=<hint>"
```

iOS browses such domains natively; Android and the desktop rely on QR/manual entry.

## Troubleshooting by diagnostic code

| Code | Meaning | What to do |
| --- | --- | --- |
| `lan.bind_failed` | The listener could not open its port. | Pick another fixed port or leave it automatic. |
| `lan.unreachable` | The phone could not open TCP to any address. | Check that both devices have a route to each other, the VLAN ACLs, and the desktop firewall prompt. |
| `lan.isolation_suspected` | The phone sees the desktop via mDNS but cannot connect. | Wi-Fi client/AP isolation: use a network without isolation, or `auto` for RelayControl + TURN. |
| `lan.pin_mismatch` | A different certificate answered. | Not the paired desktop (or its identity was reset): pair again. |
| `lan.pairing_expired` / `lan.pairing_invalid` | The code timed out, was used, or was mistyped. | Create a new local pairing code. |
| `lan.revoked` | The desktop revoked this phone. | Pair again. |
| `ice.no_direct_route` | Signaling worked but no UDP path between the devices. | A firewall/ACL is blocking media; LAN cannot bypass it. |
| `cloud.unavailable` | RelayControl did not answer. | Local sessions keep working; cloud returns automatically. |

The dashboard's **Path** row shows what the audio actually used: `LAN · P2P direct`,
`RelayControl signaling · P2P direct` or `RelayControl signaling · TURN relay` — only after ICE
connects.

## Manual verification matrix (required before closing #75)

| Scenario | Expected |
| --- | --- |
| Same subnet, internet unplugged, `lanOnly` | Discovery, pairing, audio; Path `LAN · P2P direct`; a packet capture shows no traffic to RelayControl/STUN/TURN. |
| `192.168.1.20/24` ↔ `192.168.2.10/24` routed, mDNS blocked | Pair via QR or address + code; audio with Path `LAN · P2P direct`, no TURN. |
| Same, but UDP blocked between subnets | `ice.no_direct_route`, never shown as P2P. |
| No route at all | `lan.unreachable`; in `auto` the phone falls back to RelayControl before the session starts. |
| AP isolation | `lan.isolation_suspected`, bounded retries, no loop. |
| Revoke while streaming | Phone disconnected with `lan.revoked`. |
| Wi-Fi drop / IP change | Reconnect within 30 s grace or an explicit error. |
