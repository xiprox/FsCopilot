# 04 · Transport

    Purpose:  How traffic states and ATC audio travel, why the relay had to change to carry
              them, and what the relay costs at scale.
    Depends:  01-design.md §Wire format, §Encoding and transport; 03-implementation-plan.md
              §Transport (superseded here).
    Status:   Built and verified on 2026-09-08 (log of that date). Amended by pointer only.

## The failure that forced this

Traffic states and audio frames go `Delivery.Unreliable`: each carries its own sequence number
and the receiver decides what is stale. Over the relay, none of them arrived, and the server
answered every one with a reliable `PROTOCOL_ERROR` — at ~54 audio frames a second per peer,
to upstream's production box.

The cause is in LiteNetLib 1.3.1 itself, read out of the assembly rather than the docs:

```
NetConstants.ChannelTypeCount = 4
DeliveryMethod: ReliableUnordered=0 Sequenced=1 ReliableOrdered=2 ReliableSequenced=3 Unreliable=4
header(PacketProperty.Unreliable) = 1 byte      property only
header(PacketProperty.Channeled)  = 4 bytes     property, channel, sequence
NetPeer._channels[]             the four channeled methods, indexed channel * 4 + method
NetPeer._unreliableChannel[]    a separate path; no channel number exists for it
```

`Unreliable = 4` is outside the channeled range: it never touches `_channels`, and there is no
byte on the wire for a channel. Every unreliable packet arrives as channel 0 whatever was
asked. The relay told control from data **by channel number** — 0 control, 1 data — so every
unreliable packet was read as a control message with an unknown type.

The one-machine bed never saw it. Two instances on one machine punch through to each other,
`HybridNetwork` sends every packet on both transports, and the relay copies simply vanished
while the direct copies carried the test. The log entry of 2026-09-06 that records "a quarter
of state batches dropped over the relay" was measured on that bed; the shared Sequenced
ordering it describes is real on the direct path too, and that is where it was measured.

## The relay protocol, version 2

**Every frame starts with one byte, the frame type: `0` control, `1` data.** The relay reads it
and nothing else. The frame type is the relay's own header field, which is where a frame type
belongs; a transport flag — channel number, delivery method — describes how a packet travels,
not what it is, and routing on one is one coincidence away from breaking again. (Routing on
`method == Unreliable` was considered: it needs no wire change and works today, because no
control message is unreliable. It was rejected for being the same kind of rule that failed.)
Cost: one byte per packet.

A client says which protocol it speaks with `v=2` in its connect token; none means 1. The relay
serves both — v1 by the old channel rule, v2 by the frame byte — refuses a newer version at
connect (`VERSION_UNSUPPORTED`), and refuses to link the two (`VERSION_MISMATCH`). That last
case can never actually pass the schema check, since a v1 client is an upstream build with a
different packet table; the code says so and the reason is here. Unknown control types are
logged and never answered — a reply per received packet turns a confused client into an
amplifier, and that is exactly what the old relay did to an unreliable stream. A failed
forward to one peer (an unreliable packet over that link's MTU, a socket gone) is caught per
peer; before, the exception escaped `OnNetworkReceive` and aborted the rest of that poll's
events for every session on the box.

The client has **no fallback**: against a v1 relay its framed `ConnectIntent` is read as
control type 0, answered with one bare `PROTOCOL_ERROR`, which the client drops as frame type
255, and no link forms. Measured: one error per intent, no storm, `NO LINK` after the 15 s
timeout. So the fork's default relay must be one built from this tree: `Program.RelayHost`
is `fscrelay.ihsan.dev`, deployed the same day from `ahead` `af557a1` (see
`record/self-hosted-relay/`), and `--relay host` overrides it. `--no-direct` skips the direct
attempt on every link, which is the only way two instances on one machine exercise the relay
at all; both instances need it.

## Channels are ordering domains

With the frame type carrying the framing, channel numbers go back to meaning what they mean to
LiteNetLib: a (channel, method) pair is an ordering domain. ReliableOrdered on one channel
waits for nothing on another; a Sequenced channel keeps one "newest so far" of its own.
`Transport.Map` is the single place a `Delivery` becomes a channel and a method, on both
transports:

| `Delivery` | Channel | Method | Carries |
| --- | --- | --- | --- |
| (relay control) | 0 | ReliableOrdered | ConnectIntent, LinkReady, LinkClosed, Error |
| `Reliable` | 1 | ReliableOrdered | cockpit `Update`, `SetMaster`, interactions, `PeerTags`, ping |
| `Sequenced` | 2 | Sequenced | the user aircraft's physics |
| `Bulk` | 3 | ReliableOrdered | `ShareHost`, `TrafficIdentity`, `TrafficRemove` |
| `Unreliable` | — | Unreliable | `TrafficStates`, `AtcFrame` — no channel exists |

**The rule for sharing a domain: two streams share one only if neither is hurt by waiting
behind the other.** ReliableOrdered is head-of-line blocking by definition, and a peer joining
gets every traffic identity at once — a hundred-odd packets, at least two round trips through
a 64-packet window — which on one shared reliable channel held every cockpit `Update` behind
it. That is what `Bulk` is for. `ShareHost` rides with the identities rather than on the
session channel because the receiver only accepts identities from the host it knows and it
learns the host from `ShareHost`; the two must stay in wire order, and only a shared ordered
channel guarantees it. A claim raised during an identity burst waits behind the burst: rare,
brief, and the price of the ordering.

Channels are named for ordering needs, never for features. The next feature picks a
`Delivery`; it does not get a channel. Per-feature channels (traffic, ATC) were considered:
their unreliable streams have no channel anyway, so a "traffic channel" would carry only the
identities, and the naming invites the next feature to ask for the next number. More channels
cost nothing in LiteNetLib — a channel object is made lazily per peer per (channel, method),
a queue and a few integers, up to 255 — so this is a naming decision, not a budget.

## Delivery per stream

**Sequenced is correct only when packet N+1 fully supersedes packet N.** The user aircraft's
physics is that: one object, full state, newest wins. Nothing else in traffic-atc is:

- **Traffic state batches** partition the objects between them. Batch N+1 carries different
  aircraft from batch N; Sequenced would drop N's aircraft because N+1's arrived first — not
  lossy, wrong. Unreliable, with `Seq` for gap and duplicate accounting and ordering **per
  object** by sample time on the receiver, which no transport mode can express.
- **Audio frames**: a late frame is still playable; the jitter buffer and PLC are better than
  any transport policy. Unreliable.
- **Identities, removes, hosting claims**: must arrive, in order. `Bulk`.

This is the layering the receiver was already built for — replay window, host clock offset,
per-object playout — so the transport offers best-effort delivery and the application owns
ordering. `Unreliable` is the correct choice under that layering, not a bandwidth workaround.

**The one unreliable loss that shows is an object's last sample.** A lost sample of a moving
object is replaced by the next poll inside the playout buffer. A lost "stopped here" leaves
the receiver extrapolating 600 ms past the stop and holding there until the 5 s heartbeat.
So `TrafficGate` re-sends the sample that ends a run of motion on the next two polls,
unchanged and marked quiet: a receiver that has it drops the copy as the same instant
(`TrafficInterpolator.Push` refuses a sample at or before the newest), one that lost it gets
the stop where it happened. Targeted redundancy where the application knows which packet
matters, instead of reliability for a stream that does not want it.

## The MTU floor

The batches were sized against "LiteNetLib's initial 508-byte MTU". That number is from an
older LiteNetLib; 1.3.1 has `NetConstants.InitialMtu = 1024`, `PossibleMtu = [1024, 1164,
1392, 1404, 1424, 1432]`, and the client logs `mtu 1024` on every connect. An unreliable packet
over the link's current MTU is not fragmented but thrown away with an exception, and a relayed
packet crosses two links that discover their MTU separately, so the budget is **the floor,
never the discovered value**: `Transport.MtuFloor = 1024`, `MaxUnreliablePayload = 1023`.
`TrafficStates.MaxPerPacket` is derived from it — 23 states of 43 bytes behind a 19-byte
header — instead of the hand-typed 10. Half the packet rate, and packet count, not bytes, is
what a relay pays for. `AtcFrame.MaxOpusBytes` derives from the same constant; at 24 kbps a
frame is ~60 bytes, so that limit is headroom only.

## What the relay costs at scale

Fan-out is already the right shape — a client sends once, the relay sends n−1 copies — but
this feature is a step change in what the relay carries. Per four-seat session, from the
numbers in 01-design:

```
ingress:  physics 4 × 2.3 KB/s + traffic 1.8 + audio 3 (while talking)  ≈ 14 KB/s
egress:   ingress × 3                                                    ≈ 42 KB/s  (~340 kbps)
```

Traffic plus ATC roughly **doubles** a session's relay egress against physics alone; a
hundred concurrent sessions is ~45 Mbps sustained. Levers, none of them built here:

- **Packet rate.** Done above; the biggest single win available.
- **Per-peer rate limit and link cap.** The relay forwards anything at any rate: one in,
  n−1 out is an amplifier proportional to session size. A token bucket per peer and a cap on
  links per peer belong on the box before the fork's users are pointed at it by default. This
  is a property of the relay, not of traffic-atc.
- **Interest filtering.** The first byte of every data payload is the packet id. A peer could
  tell the relay which ids not to forward to it; the ATC host would send once and the relay
  would drop it for peers with receive off, instead of every peer receiving and discarding.
  The relay stays application-agnostic.
- **Server threading.** The relay is `PollEvents()` on a 10 ms `Task.Delay` loop — one thread,
  10 ms granularity, up to 10 ms added to every relayed packet, half an audio frame.
  `UnsyncedEvents` and `UseNativeSockets` are the levers, at the cost of making the peer
  tables thread-safe.

## Verified

2026-09-08, a headless probe (`RelayNetwork` × 2 on one machine, a relay built from this tree
on localhost, a hundred 600-byte packets of each `Delivery` from A to B):

```
Reliable   100/100   Bulk 100/100   Sequenced 100/100   Unreliable 100/100   reordered 0
server: CONNECT v=2 ×2, LINK UP, no CONTROL ?, no FRAME ?, no PROTOCOL_ERROR
```

Same probe against a relay built from `main` (upstream's v1): `CONNECT` without `v`, one
`PROTOCOL_ERROR` per intent, the client drops each as frame type 255, `NO LINK` at the 15 s
timeout, no crash on either side.

Same probe against the deployed `fscrelay.ihsan.dev` (167.99.157.219, NYC), from this machine
over the internet: `mtu 1024` at connect, 100/100 of every delivery, 0 reordered, 8.5 s
end to end including the 15 s-capped connect.

The two-instance bed against a live sim (MSFS 2024, FSLTL, BeyondATC), both instances on
`--no-direct` through the deployed relay, A hosting and B receiving with `--traffic-shadow 80`,
2026-09-08 17:25–17:27:

```
A  host: 92→95 objects, polls 11–15 ms avg, sent 16–17 % of samples through the gate
B  receiving: 92→95 objects, 0 failed, 0 fallback, 0 packet gaps, 0 duplicates,
   ~16 000 pose writes per 30 s, three consecutive windows
B  [Atc] Receiving from A, playing on the selected device; A capturing BeyondATC
```

That is the Unreliable state path and the Bulk identity path through `fscrelay.ihsan.dev`
with a 150–175 ms relay RTT, and no direct link (`transport: relay` on both cards). Not yet
run: the stop-redundancy under injected loss.
