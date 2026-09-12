# Pre-PR review findings

    Purpose:    Everything the adversarial review of `ahead-pointer-forwarding` found, one
                section each, to be worked through before the branch becomes a PR.
    Depends on: 11-fsc-implementation-plan
    Decides:    nothing yet — each section records a decision as it is made

Reviewed 2026-09-11 against `main..ahead-pointer-forwarding` at `7b71df0`. Like
[06-open-questions](06-open-questions.md) this file is a register and **is updated in
place**: the status line of each section changes, the reasoning under it grows, and evidence
goes in [build/log.md](build/log.md) with a pointer back here.

What the review did *not* find is worth a line too: the build is clean, every panel file
passes `node --check`, the fail-open overlay design holds, the port rotation and the
quit-vs-crash goodbye behave as documented. The findings cluster in one place — the session
state machine in `Coordinator` and the replay side of `pointer.js` — and that is the part
the record said had never met a peer. Corrected the same day: a three-way session on
2026-09-08 had in fact run, and its app log was read afterwards — see "First multi-machine
session" in [build/log.md](build/log.md). It exercised the live path only (70 replays on
the display units, none missed) and none of the outage paths, so the findings below were
still found by reading, not by flying; but R07 was then observed in that log, and R08 was
not, on this side.

Status values: **OPEN** · **DECIDED** (fix chosen, not built) · **FIXED** (on the branch, log
entry named) · **WONTFIX** (reason given) · **DEFERRED** (to what, and why it can wait).

Ordering is by severity as judged at review time. Re-order freely as understanding changes.

> **Walkthrough complete, 2026-09-11.** Every section was talked through and decided the
> same day; R17 was found on the way. Decisions that changed the design beyond a local fix,
> in the order they should be built because later ones lean on earlier ones:
>
> 1. R08 — one `PointerEvent` type, one stream, on the wire and in `PanelServer`.
> 2. R03/R04 — `PointerAck`; history is "everything after the last ack"; a fresh session
>    resends nothing.
> 3. R01/R10 — `Peer` gains a connected flag; connecting is a real state driven by pending
>    peers and a join in flight; failed joins take control back.
> 4. R02 — the disconnect carries "left"; the amber overlay names the way out.
> 5. R06/R07/R17/R09 — the replay queue with `GapMs`, the `replaying` overlay, hit-test
>    pass-through, the `_replaying` counter gone, the app-side pending queue gone.
> 6. R05, R12, R13, R15 — local fixes with no dependencies.
> 7. R11, R14, R16 — before push: profile off the branch (after the salvage diff), the
>    wire-shape comment block, the `pointer:` documentation line.
>
> Each section's status flips to FIXED with a log entry named as it lands.

> **Plan review, same day.** The seventeen decisions were then read as one system, looking
> for contradictions and holes between them. Four were found and are corrected in place;
> the section each belongs to carries the text.
>
> - R03 contradicted R04: "restart handled for free" assumed a restarted peer could be
>   recognised, but peer id and session id are both per app run. Fixed by acks carrying the
>   acker's session, per-acker last-ack, and resend after the minimum ack on recovery.
> - R06's `GapMs` as an absolute wait would have added up to a second of latency to every
>   live gesture. Fixed: wait the *remaining* gap.
> - R10's connected flag would have been misreported by the hybrid merge, which prefers
>   direct over relay by peer id. Fixed: prefer connected first.
> - R10's "join in flight" had no home the Coordinator could see. Fixed: on `INetwork`.
>
> Pinned down on the way: the relay's `PEER_LEFT` / `PEER_DISCONNECTED` split (R02);
> quitting sends "left" (R02); replaying-overlay precedence and the one in-sim check the
> pass-through needs (R06); `Route` drops and counts (R09); `GapMs` is per key, capture
> side (R06). Accepted limits are written under R03 and R05. One item deferred outside
> pointer sync: the remaining pilot should become master when the master leaves (R02).

> **Built, 2026-09-12.** Seven commits on `ahead-pointer-forwarding`, in the order above:
> `149b90d` R08 · `257ec0e` R03/R04/R15 · `93fc60e` R01/R10 · `00a2ddc` R02 · `9f2a2f1`
> R06/R07/R09/R17 · `242e7d1` R05/R12/R13 · `833ea67` R14. R16 is a documentation line in
> the profiles repo. Two are not closed: R11's file stays on the branch until the user has
> read the salvage list (20 variables only the branch's copy names), and R12's rejection
> waits on one sim session. The app builds clean at every commit and every panel file
> passes `node --check`. What was settled while building, and the probe that checks the
> replay queue, are in "The review register built" in [build/log.md](build/log.md).

---

## Blockers

### R01 · A failed or rejected join locks the panels for five minutes
**Status:** FIXED 2026-09-12, `93fc60e` (with R10), log "The review register built" · **Severity:** blocker · **Where:** `Coordinator.cs` OnLink, `P2PNetwork.cs` Peers projection, `MainViewModel.cs` JoinCommand

> **Amended by R10, same day:** pending peers are not filtered out of `Peers` after all.
> They stay in the list with a `Connected` flag on `Peer` (false while LiteNetLib reports
> `Outgoing`), because R10 needs them as the host's "handshake in progress" signal. The UI
> hides unconnected peers, so every effect listed below still holds. The Coordinator treats
> connected as live and unconnected as connecting.

**Decision:** change the one filter in `P2PNetwork`'s `Peers` projection from
`ConnectionState.Any` to `ConnectionState.Connected`. The relay side needs nothing: the relay
server sends `LinkReady` to both sides only after it has checked the target is connected and
the schemas match, so a relay peer in the list is always a real link. Separately, have
`JoinCommand`'s `Failed`/`Rejected` paths call `TakeControl()`, so a joiner whose connect
failed is not left as a slave with nobody to follow.

Effect on existing behaviour, checked against every reader of `Peers` (there are three, all
in the app; nothing on the wire changes):

- *Connections list.* Today a peer still handshaking shows for up to 5 s as "Unknown", ping
  0, then becomes real or vanishes. After: appears only once connected. The Leave button is
  enabled off this list, so it stops lighting up during a handshake that may still fail.
- *Connect/disconnect sounds.* Today a failed direct attempt plays connect then disconnect
  with nobody connected, and the normal relay fallback plays connect, disconnect, connect.
  After: a failed attempt is silent; a relay fallback plays one connect. Pre-existing
  annoyance, removed.
- *Leaving.* Today the peer lingers in a shutting-down state until the other side
  acknowledges or a timeout runs out, so the list and the sound lag by seconds. After: the
  list empties the moment Leave is pressed.
- *Coordinator.* Gets what it needs: live means a real link.

The Hybrid "already connected" check and auto-connect query LiteNetLib directly with their
own filters and are untouched. The accepting side of a direct connection is placed in
`Connected` the moment it accepts, so it gains no delay.

---

*Original finding:*

Session state is derived from `Peers.Count > 0`. `P2PNetwork` builds that list with
`GetPeersNonAlloc(peers, ConnectionState.Any)`, and in LiteNetLib 1.3.1 `Any` is 46:
`Outgoing | Connected | ShutdownRequested | EndPointChange`. Checked by reflection on the
package DLL, not assumed.

So a NAT-punch attempt that times out, or a peer that rejects us on schema mismatch, shows
up as a tagged peer for a few seconds and then disappears. `Join()` has already made us slave
before `Connect` is called. The Coordinator sees none → live → degraded and applies the
amber lock, with its five-minute timer, to a pilot who never had a session. If the relay
fallback then succeeds the "recovered" branch also resends the entire history (see R03, R04).

**Fix direction:** derive liveness from *connected* peers only. Either filter the projection
in `P2PNetwork` to `ConnectionState.Connected` (which also improves the connections list in
the UI, which today shows peers still handshaking) or expose a connected flag on `Peer` and
filter in the Coordinator. Check `RelayNetwork` has the same property: its peers are added on
`LinkReady`, which should already mean connected — confirm.

**Also:** `JoinCommand`'s failure path leaves `MasterSwitch` as slave. Pre-existing, but now
it has a visible cost. Consider `TakeControl()` on `Failed`/`Rejected`.

---

### R02 · The master leaving on purpose locks the slave
**Status:** FIXED 2026-09-12, `00a2ddc`, log "The review register built"; the master-leaves-then-who-is-master item stays deferred · **Severity:** blocker · **Where:** `P2PNetwork.cs` Disconnect/OnPeerDisconnected, `RelayNetwork.cs` OnLinkClosed, `INetwork.PeerLeft`, `Coordinator.cs` OnLink, `overlay.js`

**Decision:** carry the reason in the disconnect itself, so the peer learns "left" rather
than "lost" and ends the session instead of degrading. Not a packet sent before
`Disconnect()` — the transport does not promise queued packets go out ahead of the
disconnect.

- *Direct path.* LiteNetLib 1.3.1 has `NetManager.DisconnectAll(byte[], int, int)`; the
  peer receives it in `DisconnectInfo.AdditionalData` with `Reason ==
  RemoteConnectionClose`. Confirmed by reflection on the package DLL. `P2PNetwork.Disconnect`
  sends a small "left" payload; `OnPeerDisconnected` reads it.
- *Relay path.* Already works: on `DisconnectIntent` the relay server sends the other side
  `LinkClosed` with code `PEER_LEFT`, and `OnLinkClosed` receives the code today and ignores
  it. The server sends `PEER_DISCONNECTED` when a peer times out at the relay, so the map
  is exact: `PEER_LEFT` and `LEFT_ALL` mean left; `PEER_DISCONNECTED`, and a direct-path
  close with no payload, mean degraded.
- *Quitting sends "left" too.* The app's exit path calls `Disconnect()`, so a quit carries
  the payload and the peer ends its session rather than waiting five minutes for an app
  that is gone. A crash or kill never reaches that path. Right on both counts.
- *Deferred, not pointer sync.* When the master leaves, the remaining pilot is now unlocked
  but still a slave, with their sim in slave control mode and nobody feeding it — upstream
  behaviour today. The user's view: the remaining peer should become master. Work for
  another time; noted here so it is not mistaken for a pointer-sync gap.
- *Surface.* `INetwork` gains a way to say *why* a peer went — the shape is an
  implementation choice (a `PeerLeft` stream, or a reason on the peer-list change).
  `Coordinator` treats "left" as `EndSession()` and everything else as the existing
  degraded path.
- *Copy.* The amber overlay names the way out regardless: "Take control to keep flying, or
  wait for the connection to return." A crash on the other side still lands here, and the
  pilot should not need to know the lock rule to escape it.

Rejected alternative: treat every peer loss as session over and drop degraded entirely.
Simpler, but it discards the locked-slave gap replay that R03/R04 are about.

---

*Original finding:*

`Leave` calls `net.Disconnect()`, `masterSwitch.TakeControl()`, `coordinator.EndSession()` —
all local. The other side sees a peer drop, indistinguishable from a crash, and goes
degraded. If it is the slave it is locked amber for five minutes.

The escapes exist — Take Control clears the lock because the policy is "degraded *and*
slave" — but the overlay text tells the pilot none of them. "Looks like there's an issue
with your connection" is also simply wrong in this case.

**Fix direction:** a session-end signal that reaches the peer before the socket drops.
LiteNetLib has `DisconnectAll(byte[] data, int start, int count)` and the peer sees it in
`DisconnectInfo.AdditionalData`; the relay has a `LinkClosed` control frame with a `code`
field already. Either way `INetwork` needs a way to say *why* the peer went. A packet sent
just before `Disconnect()` is not enough — queued reliable packets are not guaranteed to
flush ahead of the disconnect.

Whatever is chosen, the amber overlay copy should name the way out: "Take control to keep
flying, or wait for the connection to return."

---

### R03 · History resend replays presses the peer already applied
**Status:** FIXED 2026-09-12, `257ec0e`, log "The review register built" — the outage-start snapshot of ackers was not built, see the log for why it is the same set · **Severity:** blocker · **Where:** `Pointer.cs` PointerAck, `Coordinator.cs` SendPointer, OnAck, SendAcks, ResendHistory

**Decision:** the receiver acknowledges, and the sender resends from the last acknowledgement.

- A new packet, `PointerAck(Session, Seq)`: "I have your session up to Seq". Sent by the
  receiver every few seconds, only when its `_lastSeq` for that session has moved. One more
  type in a schema that this PR already changes (R14), so no extra compatibility cost.
- The sender keeps, per peer session of its own, everything after the last acked Seq. That
  *is* the history — no live ring, no 60 s age, no separate accumulation mode. A hard cap
  stays as a safety bound (the existing 2000, since ~60 B presses and ≤2.4 KB drags make
  that well under a megabyte).
- On recovery the sender resends everything after the last ack. The receiver's own dedupe
  stays as the second line for the kept-running case.
- A restarted receiver is caught up, but not "for free" as first written — see the plan
  review at the top. Both the peer id and the session id are per app run, so a restarted
  peer is a new peer in every respect. Acks therefore carry the acker's own session id, the
  sender keeps a last-ack *per acker*, and on the degraded → live transition it resends
  everything after the **minimum** ack among the ackers it knew when the outage began. A
  restarted peer arriving during our degraded state is then caught up exactly. A genuinely
  new third person gets a replay they did not need, which dedupe cannot help — accepted, on
  R09's reasoning that a fresh cockpit is desynced anyway. Ackers are forgotten at
  `EndSession`.
- Bounded residue: up to one ack interval of events can double-apply on a restarted peer.
- If the *sender's* app restarts, its history is gone. A slave locked during that outage
  gets only what the sender's panels queued in `channel.js` while the app was down (30 s,
  200 events at most). Accepted.

Corrects the original finding below on one point: the live ring is *not* useless. The
direct transport takes 15 s (`DisconnectTimeout`) to declare a peer dead, and everything
sent in that window is dropped from the transport's retry queue when it does. The ring is
what covered that window, and recovery does resend it. What the ring could not do is know
how much of itself the peer had already applied — the ack is that knowledge.

Rejected: resend only the last ~20 s before the drop was noticed (detection lag plus
margin). Bounds the damage on a restarted peer but is still guesswork.

Ack semantics: "received by the app", not "applied by the panel". App → panel is loopback
with a per-key pending queue; the gap between the two is R09's problem, not this one.

---

*Original finding:*

On recovery from degraded, `ResendHistory(includeAll: true)` sends everything held — which
includes the pre-outage live ring of up to 50 events per key. Dedupe by `(Session, Seq)`
covers this **only if the peer's app kept running**. If their app restarted, its `_lastSeq`
is empty, every pre-outage press is accepted, and up to 50 per key actuate a second time.

The plan says accumulation is "every event from the degradation instant". The code switches
the cap to 2000 but never drops what was already in the ring, so the ring rides along.

**Fix direction:** on the live → degraded transition, record the outage start (an index per
key, or a timestamp) and have the recovered resend begin there. The live ring then only ever
serves R04's case — and R04 argues that case does not exist.

---

### R04 · A fresh session replays the last 60 s of solo input onto the new peer
**Status:** FIXED 2026-09-12, `257ec0e` (with R03), log "The review register built" · **Severity:** blocker · **Where:** `Coordinator.cs` OnLink, SendPointer

**Decision:** a fresh session resends nothing. With R03's ack model there is nothing to
resend anyway — a peer that has never acked this session has no "after the last ack" — but
the rule is stated on its own so it survives if R03's shape changes: recovery replays,
first contact does not. Nothing is recorded while `_session == None` either; there is
nobody to replay it to.

The detection-lag race the 60 s window was written for is real (see R03's correction) and
is covered by the ack model, not by this branch.

---

*Original finding:*

`OnPeersChanged` is fed by `DistinctUntilChanged()` on "any peers", so every drop passes
through degraded, and `recovered` is true on every return. The `includeAll: false` branch
therefore only runs when `_hadPeer` is false: first connection of the app run, or the first
after `EndSession`. Its sole effect is to push up to 60 s of the local pilot's *solo*
presses — captured and recorded while no session existed — into a panel that never saw them
and whose state they do not describe.

The "reconnect race" the 60 s window was written for cannot reach this branch. The race
that does exist — sender's link stays up while the receiver considers it dropped — is not
covered by either branch, because the sender's `Peers` never changes.

**Fix direction:** do not record while `_session == None`, or clear history on the none →
live transition, so a fresh session starts clean. Then decide whether the live ring is worth
keeping at all, or whether the only history that matters is the degraded accumulation.

---

## Significant

### R05 · Capture is document-wide, not instrument-wide
**Status:** FIXED 2026-09-12, `242e7d1`, log "The review register built" — plus a `locked` stat for presses on our own overlay, so they do not inflate `outside` · **Severity:** significant · **Where:** `pointer.js` `_inside`, `_onDown`, `_onUp`

**Decision:** capture stays on `document` (it must — the panel's handlers may stop
propagation and the sim delivers to the deepest node), but a gesture is forwarded only if
its `mousedown` target is inside the instrument: `this._instrument.contains(ev.target)`.
Fall back to the normalised point being inside `[0,1]` with a small tolerance only when
there is no element to test. A `mouseup` with no pending down is dropped unless it passes
the same test. Count rejections in a new `outside` stat so a document where this fires a
lot is visible in the reports.

Whether the A220's opted documents actually hold more than one instrument was not checked;
it does not change the fix. Accepted limit: a drag that starts inside the instrument and
leaves it is still forwarded with path points outside the rect — this bounds the press,
not the path.

---

*Original finding:*

Listeners sit on `document` in the capture phase — correct, because the panel's own handlers
may `stopPropagation` and the sim delivers to the deepest node. But nothing rejects a point
outside the instrument's rect. `_normalise` happily produces `nx = 1.7`.

In a multi-instrument document, a click on the *neighbouring* instrument is forwarded under
the opted key. On the other side `elementFromPoint` resolves it onto that neighbour, which
is still on the element-name path and also receives the `Interact` — so it actuates twice.
The blocking overlay only covers the owner's rect, so the lock does not prevent this either.

The v1 "one agent per document" note is aware that a second instrument cannot *own* pointer
mode; it does not cover the owner capturing the second instrument's input.

**Fix direction:** in `_onDown` (and `_onUp` when there is no pending down), drop the event
unless `this._instrument.contains(ev.target)`, or unless the normalised point is inside
`[0,1]` on both axes with a small tolerance. The "slightly outside" allowance in the packet
doc is for edge presses on the owner, not for other instruments.

---

### R06 · Replayed gestures run concurrently, not in order
**Status:** FIXED 2026-09-12, `9f2a2f1`, log "The review register built"; probe p11 checks parts 1, 3 and 4 off-sim; part 5's in-sim check is still owed · **Severity:** significant · **Where:** `pointer.js` replay, `_pump`, `_run`, `_replayPress`, `_replayDrag`; `Pointer.cs` GapMs

**Decision:** five parts.

1. *A replay queue.* Gestures replay one at a time; the next starts when the previous has
   finished, timers included. The per-gesture deadman stays, so a stalled gesture releases
   the queue rather than jamming it.
2. *Gestures are never compressed.* A hold is not idle time, it is the gesture: hold-to-reset,
   hold-for-secondary-function, hold-to-repeat are read off the duration, and shortening it
   replays a long-press as a tap — the wrong action, not a faster one. Drag timing is mostly
   not semantic, but drags are rare in a backlog and compressing them buys almost nothing.
3. *The gap between gestures is carried on the wire and preserved, capped at 1 s.* Each
   event gains a `GapMs` (ushort, ms since the previous gesture's end on the capturing side).
   The queue waits the *remaining* gap before starting the next gesture: `GapMs` minus the
   time since the previous gesture finished replaying here, floored at zero — never the
   absolute value, or every live gesture would arrive with up to a second of added latency
   for a gap that already elapsed on the sender. Live traffic therefore waits nothing; a
   resend burst waits the full capped gap. Computed on the capture side, per key, since each
   panel runs its own queue. This is the part that matters: a panel that loads something after a click (a dropdown) needs the time the pilot
   gave it, and the pilot's own pacing is the only honest source of that number. Only idle
   time longer than a second is shortened, so an outage does not take its own length to
   replay. A fixed settle delay was considered and rejected: it has to be conservative
   because the slowest post-click load is unknown, so a backlog drains at 1 s per tap. There
   is no cheaper source — a resend burst arrives all at once, so arrival time tells the panel
   nothing. Two bytes on a schema that is breaking anyway (R14).
4. *A blocking `replaying` overlay while the queue is non-empty.* Real input is kept off the
   panel for the whole replay, so the local pilot can neither race it nor produce a
   divergence nobody can see. A synchronous tap with an empty queue shows nothing. Same
   fail-open properties as the other locks: a DOM node, removed when the queue drains,
   removed by the deadman if it does not. This dissolves most of R07. Precedence: it shows
   only when no other lock is up. In practice no events arrive during connecting or
   degraded, so the two never compete, but the rule is explicit.
5. *Hit-test pass-through.* Replay finds its target with `elementFromPoint`, and a blocking
   overlay is the topmost element, so the lookup would hit the overlay. For the duration of
   each lookup the overlay is set to ignore hit-testing, then restored, in a try/finally.
   The three steps are one synchronous function and the page is single-threaded, so no real
   click can be delivered in between — from the pilot's side the overlay never opens. The
   synthetic events themselves are dispatched straight at the found element and never
   hit-test at all. See R17: this was a latent bug for every existing lock state. Assumes
   Coherent applies the style change synchronously before `elementFromPoint`, as Chrome
   does — one in-sim check, with removing and reinserting the node as the fallback.

---

*Original finding:*

A held press schedules its up with `setTimeout(finish, hold)`; a drag schedules every step
against *now*. A burst — history resend after reconnect, or two quick real gestures — fires
every `mousedown` in one tick, with the ups and the drag paths interleaving afterwards. The
panel sees two buttons down at once, a mousedown mid-drag, and so on.

The plan's invariant, "ordered replay reconstructs sync exactly", holds only for instant taps
(hold ≤ 40 ms), which go through synchronously.

**Fix direction:** a serial replay queue. Each gesture is a promise-like unit; the next starts
when the previous releases. The existing deadman stays per gesture. Consider compressing
holds and drag timing when the queue is deep — a resend of 200 events should not take the
sum of their recorded durations to drain.

---

### R07 · Real input is silently dropped during replay windows
**Status:** FIXED 2026-09-12, `9f2a2f1` (with R06), log "The review register built" · **Severity:** significant · **Where:** `pointer.js` `_isOurs`

**Decision:** drop the `_replaying` counter as a capture guard. `selfEmit` and `isTrusted`
are the guards; the counter caught nothing they do not.

- The unknown the counter was hedging is already answered in the record: Q02 probed
  `isTrusted` in the cockpit, real input arrives trusted, and the only untrusted events seen
  were `VCockpit.js`'s own synthetic mouseenter/mouseleave at (0,0). See "Q02: isTrusted is
  a usable discriminator" in [build/log.md](build/log.md).
- R06's `replaying` overlay closes the window the counter was covering from the other side:
  while a gesture is replaying the pilot's real click reaches neither the panel nor the
  capture, so "panel acts, no capture" cannot happen. A synchronous tap has no window at all.
- A queue-busy state remains, for the overlay only.
- The `ignored` stat is split: `echo` for events rejected by the two guards, and anything
  rejected for another reason gets its own name, so a future discrepancy shows up in the
  reports instead of hiding in them.

**Observed** in the 2026-09-08 session log: ignored 142 against replayed 70. Each replay
fires a synthetic down and up, so a clean run reads exactly 2:1; the two extra are real
inputs swallowed by the counter. See "First multi-machine session" in
[build/log.md](build/log.md).

---

*Original finding:*

`_replaying > 0` suppresses capture of the local pilot's *trusted* events for the whole
replay window: up to 1.5 s for a held press, up to ~8 s plus pauses for a drag. The panel
still acts on those events locally, so the two cockpits diverge, and the `ignored` stat
counts them alongside genuine echoes so the loss is invisible in the stats reports.

The counter was added as the third guard in case `selfEmit` is lost and `isTrusted` is
absent in Coherent. Whether `isTrusted` exists there is answerable in one probe.

**Fix direction:** probe `isTrusted` on a real click in the cockpit. If it is present, the
counter is redundant and can go, or be demoted to a stat. If absent, keep the counter but
make `_isOurs` require `selfEmit` *or* the counter *and* a synthetic signature (e.g. the
exact coordinates of the in-flight replay), so a trusted event at a different point is
still captured. Either way, split `ignored` into `echo` and `suppressed`.

---

### R08 · Press and drag streams can reorder past the dedupe
**Status:** FIXED 2026-09-12, `149b90d`, log "The review register built" · **Severity:** significant · **Where:** `Pointer.cs` PointerEvent, `PanelServer.cs` Events, `Coordinator.cs`

**Decision:** one packet type instead of two. A single `PointerEvent` with a kind byte
(press | drag), the press fields always present, the path empty for a press. One type is one
`Subject`, one `ObserveOn` hop, and order preserved end to end: wire, network layer,
Coordinator, socket to the panel (the per-socket `SemaphoreSlim` releases async waiters
FIFO). The same unification on the capture side — one subject in `PanelServer` rather than
`_presses` and `_drags` — makes `Seq` assignment follow capture order, which closes the
ordering half of R15. R03's ack then acknowledges one sequence, and R06's `GapMs` lives in
one place. The wire ends up matching what the panel channel already does (`k: 'press' |
'drag'` on one stream).

Rejected: merging the two streams in the Coordinator (the hop is inside the network layer,
before any merge) and a tolerant "not seen before" dedupe (stops the drop, still delivers
the pair in the wrong order into R06's queue).

The user believes this was observed in a real session. The log signature would be a
`[Pointer] 1 events from the peer never arrived` warning with no later arrival, since the
reordered lower `Seq` is dropped silently after the higher one raised the gap warning.
**Checked** against this machine's log of the 2026-09-08 session: no such warning in any
run, so not observed on this side. The drop is logged on the receiver; the peers' logs from
that evening would settle it. The decision stands on the mechanism regardless.

---

*Original finding:*

Each packet type's `Stream<T>()` is its own `Subject` with its own
`.ObserveOn(TaskPoolScheduler.Default)`. On the wire both types share LiteNetLib channel 0
ReliableOrdered, so arrival order is right — but the two observables are then delivered on
independent pool work items. `Fresh()` shares one `Seq` counter across presses and drags, so
a drag with seq N processed after press N+1 is rejected as a duplicate.

The window is microseconds and a drag is always followed by at least one down/up cycle
before the next event, so this is rare. It is also silent.

**Fix direction:** merge the two streams before `Fresh()` — `Observable.Merge` of the two
`_net.Stream` calls into one ordered subscription — or give each type its own sequence
space. Alternatively, `Fresh()` tolerates a small reorder window: accept `seq` if not in a
recent-seen set rather than if `> last`.

---

### R09 · Pending events flush into a panel that is not ready
**Status:** WONTFIX 2026-09-11; the pending queue removed 2026-09-12, `9f2a2f1`, log "The review register built" · **Severity:** significant · **Where:** `hook.js` constructor, `PanelServer.cs` Route

**Decision:** no ready gate, and `PanelServer`'s per-key pending queue goes away. Events for
a key with no connected panel are dropped, not held.

The reasoning: events are only ever held when the panel document is not running — the sim
reloaded it (view change, pop-out, cockpit reinitialisation), or the cockpit was still
loading when the peer started pressing. In both cases the document that eventually says
hello starts from its default state (first page, default range) while the peer's did not.
The panel is desynced by construction, and nothing replayed into it fixes that. Worse,
held presses were made against a state the panel is no longer in: "next page" three times
into a panel that reset to page one is three random inputs, not a replay. Dropping them is
the safer outcome — which is what happens today by accident, since the flush lands on an
empty shell.

What goes: `_pending`, `PendingMaxAge`, `PendingMaxCount`, `FlushPending`, and the
"or holds it for a panel that has not appeared yet" branch of `Route`. `Route` with no
socket for the key drops the event and counts it, so an undelivered event still shows in
the stats. What stays: the panel-side `missed` stat, which is how we would notice if this
assumption is wrong for some aircraft.

If someone later observes "presses during a panel reload are lost" — yes, deliberately,
and the reload lost the sync before the presses did.

For the record, what "ready" would have been, from the sim's `BaseInstrument`: the main
loop calls `Init` once `allInstrumentsLoaded && SimVar.IsReady()` (`_isInitialized`), then
counts frames (`_frameCount`). The `Hook` is constructed in `VCockpit.createInstrument`
right after `setupInstrument(template)`, before any of that has run.

---

*Original finding:*

`hello` goes out from the `Hook` constructor, which runs at template load — before the
instrument has laid out, and before its React tree has mounted. `FlushPending` answers the
hello immediately. Held events therefore either miss on a zero rect (`missed++`, dropped) or
resolve `elementFromPoint` onto an empty container and fire into nothing (`replayed++`, no
effect). The plan's justification — "safe to flush fully, the panel is locked in connecting
until flush completes" — assumed the flush lands on a live document.

**Fix direction:** the panel says when it can receive. Options: a `{t:'ready'}` message once
the rect is non-zero and a first frame has painted (`requestAnimationFrame` after
`instrument.isReady`, or after `Update` is first called on the instrument); or the pointer
agent buffers replays itself until its rect is non-zero and a short settle has passed. The
app-side pending queue then flushes on ready rather than on hello.

---

### R10 · The connecting state is never emitted
**Status:** FIXED 2026-09-12, `93fc60e`, log "The review register built" — including the `Configure()` state broadcast found on the way · **Severity:** significant (doc/code mismatch) · **Where:** `Coordinator.cs` OnLink, `INetwork.Connecting`, `HybridNetwork.cs` MergePeers, `PanelServer.cs` Configure

**Decision:** make the connecting state real. The workflow it protects: both pilots load
the aircraft, everything is at its default state, they connect without touching anything,
and they are in sync. The lock's job is to make "without touching anything" hold during
the handshake, when a click would be lost because there is no peer to send it to yet — up
to ~10 s on the joiner (direct attempt, then relay fallback).

How it is driven:

- *Joiner.* From the moment Join is pressed until a peer is live, or the attempt fails.
- *Host.* While a pending peer exists in the transport (LiteNetLib `Outgoing`, i.e. a
  handshake in progress). Over the relay the host has no handshake window — it receives
  `LinkReady` directly — so nothing to show there.
- *Coordinator rule.* Any connected peer → live. Else any pending peer, or a join in
  flight → connecting. Else none, or degraded if there was a live peer. A failed attempt
  therefore goes blue then clears, never amber, because it was never live.

This amends R01: rather than filtering pending peers *out* of `Peers`, keep them and mark
each `Peer` connected or not. The UI hides the unconnected ones and keeps every improvement
R01 listed; the Coordinator gets both signals from one list. R01's section carries the
amendment.

Two details from the plan review:

- *The hybrid merge must prefer connected before it prefers direct.* Today `MergePeers`
  keeps the direct entry when the same peer id appears on both transports. With the flag, a
  pending direct entry beside a connected relay entry would report connecting while sync is
  live over the relay.
- *"Join in flight" lives on `INetwork`*, set and cleared inside `Connect`, not in the view
  model — the Coordinator does not see the view model. Both the joiner's connecting state
  and the failed-join `TakeControl()` read from that one place.

Also found while explaining this: the blue lock the user saw in testing was *not* this
state. The agent locks itself blue when constructed and holds until the first `state`
message. On hello that is milliseconds (config then state, back to back). But
`Configure()`'s broadcast on profile load sends config with no state after it, so every
profile load that lands on already-connected panels shows blue until the next 2 s renewal.
Fix alongside: `Configure()` broadcasts state with config, or the agent does not lock until
it has heard a state at all.

---

*Original finding:*

`SessionState.Connecting` is defined, the overlay has a state for it, the commit message and
[11-fsc-implementation-plan](11-fsc-implementation-plan.md) describe it as the boot lock
"until the session is live". `Coordinator` only ever sets none, live and degraded. The
connecting overlay exists only as `pointer.js`'s local default between construction and the
first state reply — milliseconds.

**Decide:** is a connecting lock wanted? If yes, the app knows when `Connect` is in flight
(`JoinCommand`'s `IsBusy`) and could emit it; the joiner would lock from pressing Join until
the peer is live. If no, remove the state from `SessionState`, from the overlay's `STATES`,
and from the plan's lifecycle table, so the record stops promising it.

---

## Minor

### R11 · The A220 profile does not belong in this repo
**Status:** DECIDED 2026-09-11; salvage diff written 2026-09-12 (`results/a220-profile-salvage-2026-09-12.txt`) and it is not empty — 20 variables the branch's copy names in a `get:` are absent from the profiles copy — so the file stays until the user has read that list; removal is one `git rm` before push · **Severity:** PR shape · **Where:** `Definitions/synaptic_a220.yaml`

**Decision:** the file comes off the branch before anything is pushed — not only off the
PR. Not touched yet: the user may have edits in it that are not backed up anywhere else,
so the removal is preceded by a diff against the profiles repo's copy to salvage anything
missing there.

What the check found: the profiles repo already has `definitions/synaptic_a220.yaml`,
nearly twice the size of the branch's copy, moved on since August, and already carrying
`pointer: [DisplayUnits|config=Default]`. The branch's copy is a stale snapshot. Nothing on
the branch reads it — the app loads profiles from its install folder, which the installer
populates from the profile server — so it was never what the app used in testing either.

---

*Original finding:*

`main`'s `Definitions/` holds only `modules/*.yaml`; aircraft profiles are served from the
profiles repo. This 628-line file is new on the branch, its header says UNVERIFIED with a
placeholder filename, and it would ship inside the app. Move the `pointer:` entry to the
profile in `fscopilot-profiles` and drop the file from the PR branch. It can stay on
`ahead-pointer-forwarding` if that is convenient for local testing.

---

### R12 · The panel listener accepts any origin
**Status:** step one FIXED 2026-09-12, `242e7d1` (the Origin is logged on every connection); step two DECIDED, waiting on one sim session's log line before the http(s) rejection goes in · **Severity:** minor (local attack surface) · **Where:** `PanelServer.cs` AcceptLoop

**Decision:** two steps, in order. Log the `Origin` header at Debug on every accepted
connection, so one sim session confirms what Coherent actually sends. Then reject
connections whose origin starts with `http://` or `https://` — browsers always send one and
it always has that scheme; Coherent sends `coui://` or nothing. Logging first is the
insurance against locking out the sim itself.

---

*Original finding:*

`AcceptWebSocketAsync(null)` with no origin check. Any browser tab on the machine can open
`ws://127.0.0.1:9020/`, send a `hello` for a key and receive the peer's pointer events, or
send `pointer` messages that are forwarded to the peer's cockpit if the key is in the
profile. Loopback-only, so the attacker is already on the machine, but the cost of closing
it is one line: reject when `Origin` starts with `http://` or `https://`. Browsers always send
it; Coherent sends `coui://` or nothing. Log the origin at Debug first to confirm what
Coherent actually sends.

---

### R13 · A state renewal can land between the goodbye and the close
**Status:** FIXED 2026-09-12, `242e7d1`, log "The review register built" · **Severity:** minor · **Where:** `PanelServer.cs` Shutdown, Farewell, SendAsync; `channel.js` onmessage unchanged

**Decision:** `Shutdown` disposes `_d` (which owns the 2 s renewal timer) *before* sending
the farewells, not after; and a `_closing` flag set at the top of `Shutdown` makes the send
path refuse anything but the goodbye, which also covers any other stray send landing in the
same instant (a `Configure` broadcast from a profile load, say). The panel-side rule —
any message after the goodbye spends it — stays as it is; it is right.

---

*Original finding:*

`channel.js` assigns `_deliberate` on *every* message, by design, so any traffic after `bye`
spends the flag. The 2 s `Observable.Interval` broadcast runs concurrently with `Farewell`:
if it acquires the socket's `SendLock` between the `bye` send and `CloseOutputAsync`, the
panel receives `state` after `bye`, the flag resets, and the quit is reported as a break —
the exact case the goodbye was added to prevent. Sub-millisecond window, but real.

**Fix direction:** a `_closing` flag set at the top of `Shutdown` and checked in `Send`, or
dispose `_d` (which owns the interval) before the farewell rather than after.

---

### R14 · Adding two packet types breaks compatibility with every older build
**Status:** FIXED 2026-09-12, `833ea67` (the comment block); the PR sentence is the user's to write · **Severity:** PR shape · **Where:** `Pointer.cs` header, `Codecs.cs` Schema, `Coordinator.cs` RegisterPacket

**Decision:** nothing to build, two things to write. The compatibility break is stated in
one plain sentence in the PR text (the user writes and submits the PR; that is not part
of the implementation work). And the packet definitions in `Pointer.cs` carry one comment
block listing the final wire shape, so it is visible in one place rather than reconstructed
from several commits.

After R03, R06 and R08 that shape is no longer "two new packet types". It is:

- one `PointerEvent` replacing `PointerPress` and `PointerDrag`: kind byte, the press
  fields always present, the drag path empty for a press;
- `GapMs` on it — milliseconds since the previous gesture's end on the capturing side;
- `PointerAck(Session, Seq)` from the receiver;
- a reason payload on the direct-path disconnect, which is not a packet and does not
  touch the fingerprint;
- R16's outcome, if `Interact` widens.

The relay does not care: it checks that the two *peers* match each other, not that they
match the relay.

---

*Original finding:*

The schema hash covers every registered type, so this build rejects every older build with
"Both sides must use the same FS Copilot version". Intended and unavoidable — the record
says so in [04-transport](04-transport.md) — but the PR description has to say it plainly,
and it is the reason `Session`, `Seq` and `Flags` are on the wire from day one.

---

### R15 · History is ordered by wall clock, not by Seq
**Status:** FIXED 2026-09-12, `257ec0e` (the resend orders by Seq; the timestamp is gone from the history) · **Severity:** minor · **Where:** `Coordinator.cs` ResendHistory

**Decision:** the resend orders by `Seq`, and the timestamp comes off the history entry —
nothing reads it once R03 removes the age window. Both halves of the finding are already
gone on paper: R08's single stream assigns `Seq` in capture order, and R03's
"everything after the last ack" has no eviction inside the resent range, so no spurious gap
warning. Saying "by Seq" rather than "don't sort" makes the intent survive a refactor.

---

*Original finding:*

`ResendHistory` orders by `At`. `Seq` is assigned in the subscribe lambda *before* `Record`
takes the lock, and presses and drags arrive on separate pool threads, so two events can be
recorded with `At` in the opposite order to their `Seq`. The receiver then drops the
lower-`Seq` one as a duplicate. The monotonic `Seq` is right there; order by it.

Related: after the live ring evicts entries, a resend on the `includeAll: false` path can
present a `Seq` gap to the receiver and trigger a spurious "events never arrived" warning.
Goes away if R04 removes that path.

---

### R16 · The Interact exclusion keys on the identifier prefix
**Status:** FIXED 2026-09-12 — documented beside `ignore:` in the profiles repo, `docs/desktop-app.md` (the only place profile keys are described; `pointer:` had no entry there at all); the precise version stays deferred · **Severity:** minor · **Where:** `Coordinator.cs` PointerFilter

**Decision:** keep the prefix match and say so where `pointer:` is documented: the
element-name exclusion is by identifier, so opting in one config of an identifier excludes
every config of it, and partial opt-in across configs is not supported. Revisit only if an
aircraft turns up that needs it. `Interact` does not widen, so R14's list stays one item
shorter.

Checked: the A220 exposes exactly one DisplayUnits document
(`instrument.html?config=Default`), so the prefix does not bite there; and where
identifiers are reused (CTP, FCP) a profile would opt in all of them or none.

Rejected for now: carrying the full key in the element-name messages. Precise, but those
messages ride the 512-byte bus, some instrument URLs are long, and `ignore:` matches bare
identifiers today so it would need the same prefix logic in reverse. More moving parts than
the problem deserves.

---

*Original finding:*

`PointerFilter.Instruments` is built by cutting each key at `|`. Opting in
`DisplayUnits|config=Default` removes *every* `DisplayUnits` instrument from element-name
sync, not only the opted config. `Interact` carries the bare identifier, so a precise match
is not possible without widening `Interact` — which changes the schema (R14), so if it is
going to happen it should happen in this PR. Otherwise document the prefix behaviour beside
the `pointer:` key in the profile docs, since it is not obvious.

---

## Found during the walkthrough

### R17 · Replay under a blocking overlay hit-tests the overlay
**Status:** FIXED 2026-09-12, `9f2a2f1` (with R06); the synchronous-style assumption is still the one in-sim check owed · **Severity:** significant · **Where:** `overlay.js` hitTest; `pointer.js` `_replayPress`, `_replayDrag`

Found while deciding R06. `replay` and `_replayDrag` locate their target with
`document.elementFromPoint`. A blocking overlay is a `position:fixed` node at the maximum
z-index with hit-testing enabled, so while any lock is showing the lookup returns the
overlay, and the synthetic events go to it instead of the instrument. Today this is avoided
only by ordering: the app broadcasts `live` before it resends history, and the hello reply
sends `state` before it flushes pending, so the panel happens to be unlocked when events
arrive. Nothing enforces that, and the `replaying` overlay from R06 would have made it
constant.

**Decision:** part 5 of R06 — the overlay ignores hit-testing for the duration of each
synchronous `elementFromPoint` call and is restored in a try/finally. Applies to every
overlay state, not only `replaying`.

---

## Not findings, but worth knowing before the PR

- **HttpListener on `127.0.0.1` needs no URL ACL** for a non-admin user; `localhost` would.
  The choice is right, keep it.
- **`_pending` in PanelServer is bounded per key but not in key count.** Keys come from the
  peer and are filtered against the profile before `Route`, so the set is bounded by the
  profile. Fine as long as that filter stays in front of it.
- **Real input during a `degraded` lock still reaches the panel through the un-covered
  parts of the document** (see R05). Once R05 is fixed the lock and the capture agree on
  the same rectangle.
- **The dev echo path proves panel → app → panel on one machine.** Nothing on this list is
  reachable that way; R01–R04 and R08 need two apps with a real peer link, and R06/R09 need
  a burst or a boot flush. A two-machine session, or two apps on one machine on different
  port ranges, is the test rig for the blockers.
