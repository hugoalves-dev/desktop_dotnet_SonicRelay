# LAN offline — foundation for issue #75

> Status: partial implementation. This branch **does not enable LAN streaming**.
> Do not display a working LAN mode or bypass publisher authentication until the
> authenticated listener and Mobile protocol have been implemented and tested.

## ICE policy

`SipSorceryPeerConnectionFactory` accepts an optional `useLanOnly` predicate.
The predicate must be supplied by a **trusted, established session transport**
(not from mDNS results or remote user input). With LAN-only enabled:

- No request is made to the ICE server provider, including RelayControl.
- The RTCPeerConnection has an empty `iceServers` list even if static
  STUN/TURN servers were supplied.
- ICE transport policy is `all`, not `relay`, to allow host candidates.

This is a prerequisite, **not** a complete host-candidate-only guarantee: the
future LAN signaling layer must additionally validate outgoing and incoming ICE
candidates (e.g. reject `typ srflx` and `typ relay`) and verify that no
external ICE discovery traffic occurs in an offline integration test.

## Integration required before shipping

1. Define and implement authenticated TLS signaling with pinned peer identities,
   an expiring one-time offline pairing flow, encrypted trust storage and
   revocation. mDNS must never be treated as authentication.
2. Implement mDNS/DNS-SD publishing and peer discovery. Support manual or QR
   bootstrap for routed subnets where multicast is unavailable.
3. Split Cloud session creation and signaling from the LAN transport. Drive
   `useLanOnly` from the established transport's immutable session policy.
4. Add Auto / LAN only / Cloud only selection and actual-transport diagnostics.
   Only allow Cloud fallback **before** establishing a session.
5. Implement the matching protocol on Android/iOS and test offline
   Desktop ↔ Mobile audio streaming, disconnection and authorization failures.
6. Validate interface selection, firewall errors, non-admin behavior and
   no unauthorized media transmission.

The existing Relay preference (`automatic`, `forceRelay`,
`disableFallback`) controls TURN policy *inside Cloud mode* and must not be
repurposed as the transport selector.
