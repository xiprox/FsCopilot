# PR preparation

    Purpose:    Everything needed to write the upstream PR and defend it in review.
                Reference, not draft text.
    Depends on: 11-fsc-implementation-plan, 12-pre-pr-review
    Decides:    nothing — it collects what was already decided so it can be found

Reviewed against `main..ahead-pointer-forwarding` at `833ea67`.

Section 0 is the whole feature in one place. 1–3 are what the thing *is* in detail. 4 is why it is
that. 5–6 are what changed. 7–9 are how it behaves. 10–13 are what it does not do. 14–17 are what
is true, what will be asked, and what has to happen before the branch is pushed.

---

## 0 · The feature, complete

### 0.1 In one paragraph

A second interaction-sync path that forwards **where the pointer went** instead of **what it
hit**. A cockpit display opts in per instrument through a `pointer:` list in the aircraft profile.
When on, clicks and drags on that instrument are captured in the panel document, normalised to
fractions of the instrument's bounding rect, carried to the desktop app over a localhost
WebSocket, sent to the peer as a packet, and replayed there by hit-testing the same relative
position. The existing element-name path stays the default for everything else and is suppressed,
in both directions, only for instruments in pointer mode.

### 0.2 What it can carry

| Capability | Detail |
| --- | --- |
| Discrete press | Down and up position both carried. Buttons, softkeys, switches. |
| Press-and-hold | `HoldMs`, down-to-up, replayed as a real hold. |
| Drag | Full sampled path, ~30 Hz, up to 240 points (≈8 s), sent as one packet at mouse-up. |
| Any mouse button | `Button` is on the wire. The existing capture's `ev.button !== 0` gate is deliberately not inherited — right-click is a real control on some panels. |
| Gesture pacing | `GapMs`, the idle time before a gesture, capped at 1 s, preserved on replay. A panel that loads a page after a click gets the time the pilot gave it. |
| Displays reached | HTML, SVG (incl. React-into-SVG), canvas — anything whose input model is a DOM event at a position. |
| Displays not reached | WASM gauges, cross-origin iframes. §10. |

Not carried, by decision: wheel, double-click, keyboard. §11.

### 0.3 How it turns on and off

- **Off by default, everywhere.** An instrument is in events mode unless the profile names its key.
- **Runtime switchable.** A profile load broadcasts a new `config`; instruments enter or leave
  pointer mode without a panel reload.
- **No app, no change.** If `config` never arrives, the instrument stays in events mode forever
  and no overlay is ever shown. Solo flight with the app closed is untouched.
- **One agent per document.** On a multi-instrument document the first opted-in instrument wins;
  the rest stay in events mode with a warning logged.

### 0.4 Edge cases, and what each does

**Gesture capture**

| Case | Handling |
| --- | --- |
| Movement under 4 px between down and up | Classified as a press, not a drag. |
| Jitter between drag samples (<2 px) | Discarded. |
| Drag longer than 240 points | Path truncated at capture; the gesture still sends. |
| Mid-drag pause | Preserved, clamped to 250 ms per step, so replay pauses rather than stalls. |
| Drag with no mouse-up (pointer leaves, focus lost) | 3 s deadman closes the gesture. |
| Click outside the instrument rect | Not captured. Capture listens on the instrument element, not the document (R05). |
| Coordinates slightly outside [0,1] | Sent as-is, never clamped. 0.02 tolerance where only the point can be tested. Counted as `outside`. |
| Our own replayed event seen by capture | Dropped via the `selfEmit` flag. Counted as `echo`. |

**Replay**

| Case | Handling |
| --- | --- |
| Two gestures arrive together | Serial queue, one at a time, in `Seq` order. Never concurrent (R06). |
| A resend burst arrives at once | Each waits its *remaining* gap, not the full one, so live gestures add no latency. |
| `elementFromPoint` finds nothing at that position | Gesture abandoned, queue released immediately, counted as `missed`. |
| An overlay is covering the instrument | Hit-testing is disabled for the duration of each synchronous `elementFromPoint` and restored in a `finally`. The page is single-threaded, so no real click can land in the window (R17). |
| A replay's timers never complete | 3 s deadman releases the queue, logs, counts `stalled`. One bad gesture cannot jam the rest. |
| Real pilot input during a replay | Blocked visibly by the teal REPLAYING overlay, not silently swallowed (R07). Counted as `locked`. |
| A gesture finishes synchronously | Re-entrant `_pump` returns immediately rather than recursing. |

**Routing and filtering**

| Case | Handling |
| --- | --- |
| Event for a key no panel has helloed | Dropped and counted. Deliberately not held — a panel that helloes later was reloaded and has reset to its default state, so replaying into it would be random input, not a catch-up (R09). |
| Several documents helloed the same key | All receive the event. |
| Peer's profile lists a key ours does not | Inbound filter drops it. The profile filter runs on both ends. |
| Instrument is in pointer mode | Excluded from `Interact` in **both** directions, matched on the identifier prefix, so one press never actuates twice. |
| Packet with more than 1024 path points | Rejected at decode as malformed rather than allocating. |
| Duplicate `(Session, Seq)` | Dropped. History resends overlap by design. |
| Gap in `Seq` | Applied anyway; logs how many never arrived and warns that panels may be desynced. |

**Link and session**

| Case | Handling |
| --- | --- |
| App not running | Panels back off across the port range forever. No config, no overlay, events mode. |
| App starts after the sim | Panels reconnect and re-hello; every hook's identity is replayed. |
| Panel reloads (view or aircraft change) | Reconnects, re-hellos. Queued captures survive if under 30 s old. |
| Port 9020 squatted | Next free port in 9020–9024, both ends. |
| Second app instance | Binds the next free port; its panels find it. |
| All five ports taken | Feature off, fails open. Desktop app shows an error; panels see it as "app not running", deliberately, so solo flight stays clean. |
| App quits deliberately | `bye` first, then close. Panel clears with no warning. |
| App crashes or is killed | No `bye`. Renewals stop; after 8 s any lock degrades to red non-blocking; red retracts after 10 s. |
| Peer link drops | Session `degraded`. Slave's pointer panels lock amber; master's stay clear. History accumulates. |
| Peer returns within 5 min | Unacked history resends in `Seq` order with gaps preserved; lock lifts. |
| Peer does not return in 5 min | Session ends, lock lifts, desync warning logged. |
| Peer leaves on purpose | Session ends immediately. No lock, no history held. Distinguished by a `"left"` payload on the disconnect (direct) or the relay's close code. |
| Peer restarts | New `Session` id makes it a new acker; the sender resends from the lowest ack it holds. |
| Third peer leaves a three-way session | Does not end the session for the rest. |
| Join attempt fails | Role restored to master; session goes `connecting` → `none`, never `degraded` — it was never live. |
| Profile loads after panels connected | `config` broadcast, then `state`, in that order. |
| Profile stops listing a key | That instrument returns to events mode. |
| Overlay stuck for any reason | `window.fscUnlock()` at a fixed global removes it. |

### 0.5 What it guarantees

- **An outage that ends within the session timeout loses nothing.** The slave is locked for the
  whole gap, so it cannot diverge, and ordered replay of the held history reconstructs sync
  exactly.
- **It fails open.** No app, no config, or no session means nothing changes and nothing is shown.
  Every suppressing state clears unconditionally.
- **Blocking requires a live app renewing the lock.** If renewals stop for 8 s, any blocking
  overlay degrades to non-blocking. A lock can never outlive the thing that justified it.
- **One press actuates once.** Pointer-mode instruments are excluded from the element-name path in
  both directions.
- **No aircraft changes behaviour until a profile says so.**

### 0.6 What it does not guarantee

- **Convergence when the panels are showing different pages.** The press lands on whatever is at
  that position. Recovery is by pressing a page button, which is at a fixed coordinate on both.
- **Convergence under concurrent conflicting input** on the same panel within a latency window.
- **Drag fidelity.** Replayed drags can drift, bounded and temporal rather than spatial.
- **Coverage of the boot tail** — the first seconds before panel JS loads. The connecting-state
  lock closes most of it, not all.

### 0.7 What it reports

Per-instrument counters, sent to the app every 60 s and written to the app log:
`captured`, `replayed`, `missed`, `echo`, `outside`, `locked`, `stalled`. Plus link counters
(`sent`, `received`, `dropped`, `reconnects`) and the instrument rect from the hello.

`missed > 0` localises a problem to `elementFromPoint` — rect disagreement between machines —
rather than to transport, which shows up as `Seq` gaps in the app log instead.

---

## 1 · Glossary

Use these consistently in the PR; the existing codebase already uses most of them, and drift is
what makes a reviewer lose the thread.

| Term | Means |
| --- | --- |
| **Panel** | One cockpit *document*. May host several instruments. |
| **Instrument** | One custom element inside a panel document, created by `setupInstrument`. The unit of opt-in. |
| **Display** | What an instrument renders as — HTML, SVG, canvas, WASM. What varies between aircraft. |
| **Key** | The routing address of an instrument: `instrumentIdentifier`, or `instrumentIdentifier\|querystring` where an aircraft reuses identifiers. What `pointer:` lists and what a hello carries. |
| **Capture** | Turning a real cockpit click into a message. |
| **Replay** | Turning a received message back into a synthetic press on the peer. |
| **Events mode** | The existing element-name sync. Default for every instrument. |
| **Pointer mode** | This feature. Per-instrument, profile-driven, switchable at runtime. |
| **Session** | The app's view of whether there is a peer: `none` / `connecting` / `live` / `degraded`. Drives the overlays. |
| **Overlay** | A div covering an instrument rect. Four states; three block input, one does not. |
| **Gesture** | One press (with hold) or one drag (with its sampled path). The unit on the wire. |

Avoid "surface" in the PR — it is a record-only term for the taxonomy of display kinds.

---

## 2 · Architecture

### 2.1 Components

```
  ── capturing machine ───────────────┐        ┌─── receiving machine ─────────────
                                      │        │
  panel document                      │        │  panel document
    VCockpit.js   (patched loader)    │        │    VCockpit.js
    hook.js       mode decision       │        │    hook.js
    channel.js    WS to the app  ─┐   │        │    channel.js   ◄─┐
    pointer.js    capture/replay  │   │        │    pointer.js     │
    overlay.js    the lock        │   │        │    overlay.js     │
                                  │   │        │                   │
  desktop app                     ▼   │        │  desktop app      │
    PanelServer   ws://127.0.0.1:9020 │        │    PanelServer  ──┘
    Coordinator   filter, Seq, history│        │    Coordinator  dedupe, ack
    INetwork      LiteNetLib      ────┼────────┼─►  INetwork
```

Two hops, two different protocols. Panel↔app is JSON over a localhost WebSocket. App↔peer is the
existing binary packet channel (direct or relay), with two new packet types.

### 2.2 The path of one press

1. Pilot clicks the A220 display unit.
2. `pointer.js` has capture-phase listeners on the **instrument element**. It classifies the
   gesture (press vs drag), normalises to fractions of the instrument's bounding rect, and
   computes `gap` — the idle time since the previous gesture ended.
3. `channel.js` sends `{t:"pointer", msg:{…}}` to the app.
4. `PanelServer.Handle` parses it, rebuilds per-step deltas, publishes on `Events`.
5. `Coordinator` filters against `_pointer.Keys` (the profile opt-in), stamps `Session`/`Seq`,
   appends to `_history` if a session exists, and calls `_net.SendAll`.
6. Wire. `ReliableOrdered` — LiteNetLib fragments the ~2.4 KB drag case.
7. Peer's `Coordinator` checks `Fresh(Session, Seq)` (dedupe + gap warning), filters against its
   *own* profile, hands to `PanelServer.Send`.
8. `PanelServer.Route` finds every socket that helloed that key, converts deltas back to
   absolute times, sends `{t:"pointer", …}`.
9. `hook.js` routes it to the document's `Pointer` instance. `pointer.js` queues it, waits the
   *remaining* gap, calls `elementFromPoint`, dispatches `mousedown`/`mouseup`/`click`.
10. Every ~3 s the receiver sends a `PointerAck` back. The sender drops acked history.

### 2.3 Where the design boundaries are

- **`PanelServer` knows nothing about peers or profiles.** It owns sockets, routing by key, and
  the JSON schema. `Configure()` is the only thing that tells it what a pointer key is, and it
  treats the list as opaque.
- **`Coordinator` owns all policy**: the profile filter, sequence stamping, history, acks, and
  the session state machine. It is the only thing that reads both `INetwork` and `PanelServer`.
- **`pointer.js` owns the gesture model**: what counts as a drag, how a path is sampled, how a
  gesture is replayed, and the replay queue. `hook.js` owns only the mode decision.
- **`overlay.js` knows nothing about sessions.** It renders a named state. `pointer.js` decides
  which state that is.

---

## 3 · The wire

### 3.1 Panel ↔ app (JSON over WebSocket)

| Direction | Message | Fields | Purpose |
| --- | --- | --- | --- |
| panel→app | `hello` | `name` (key), `url`, optional `rect` | Identifies one instrument on this socket. Re-sent on every reconnect. `rect` is the Q04 instrumentation. |
| app→panel | `config` | `pointer: [keys]` | The opt-in list. Sent on every hello *and* broadcast on profile load. |
| app→panel | `state` | `session`, `role` | Drives the overlay. Sent on change **and every 2 s**. |
| panel→app | `pointer` | `msg: {key,k,btn,hold,gap,down,up,path}` | A captured gesture. |
| app→panel | `pointer` | same shape | A peer's gesture to replay. |
| panel→app | `stats` | `link`, `key`, `pointer` counters | Every 60 s. Diagnostics. |
| app→panel | `bye` | — | Deliberate shutdown. Distinguishes quit from crash. |

One socket per **document**, shared by every hook in it; a document with three instruments sends
three hellos on one socket. Routing is by key, so one inbound gesture can land on several sockets
if several documents helloed the same key.

### 3.2 App ↔ peer (binary packets)

Verbatim from the comment block at the top of `FsCopilot/Connection/Pointer.cs` — that block is
the canonical statement and the PR should point at it rather than restate it.

```
PointerEvent   Key      string   full panel key
               Session  u64      random per app run of the sender
               Seq      u32      monotonic per sender run; one space for presses and drags
               Flags    u8       reserved — flag bits do not change the schema, fields do
               Kind     u8       0 press, 1 drag
               Button   u8
               HoldMs   u16      press: down to up; drag: 0
               GapMs    u16      idle before this gesture on the capturing side, ≤1000
               DownX/Y  f32×2    rect fractions; press: down point, drag: first path point
               UpX/Y    f32×2    press: up point, drag: last path point
               Path     u16 count, then (u16 DtMs, f32 X, f32 Y) each; empty for a press

PointerAck     Session  u64      the sender session being acknowledged
               Seq      u32      "I have your session up to here"
               From     u64      the acker's own session, so a restarted peer is a new acker
```

Registered after `Surfaces`, ids 6 and 7. Sizes: press ~50–65 B, 240-point drag ~2.4 KB.

**Not a packet but part of the same change:** the direct-path disconnect carries a `"left"`
payload, and the relay's `PEER_LEFT` / `LEFT_ALL` close codes are read. See §5.2.

### 3.3 Why these fields are in v1 rather than added later

`Codecs.Schema` hashes every registered type. Adding a field later is a hard compatibility break
for every user. So anything that might ever be needed had to go in now:

- `Session` + `Seq` — needed for dedupe and gap detection, which only matter during an outage.
  Shipping without them would mean a second break to add gap-replay.
- `Flags` — the escape hatch. Flag bits can carry future meaning without changing the schema.
- `GapMs` — added during the review (R06). Without it a resend burst replays at wire speed, and a
  panel that loads a page after a click never gets the time the pilot gave it.

---

## 4 · Technical decisions

Each is: what was chosen, what it beat, and the reasoning that decided it.

### 4.1 Transport: WebSocket sidecar, not the CommBus

**Chosen:** `HttpListener` + `AcceptWebSocketAsync` on `127.0.0.1`, BCL only.

**Rejected — widen the existing bus.** The 512-byte cap is self-imposed (SimConnect client-data
areas go to 8 KB) and could be raised with a `SizeConst` change plus a WASM-side one-liner. Three
problems: it raises message *size*, not *rate*, so drag is still impossible; it is an ABI change
needing a version bump on both `k_version` and `WasmVersion`; and whether two `SetClientData`
calls in one frame both arrive was never established (Q09, still open).

**Rejected — JSON-in-fixed-buffer compaction.** Would have bought ~5× on message size. Only worth
doing if the bus stayed in the path, which it does not.

**Why the bypass won:** full duplex, no size cap, per-panel connections instead of a broadcast
every document must parse, sub-millisecond latency, and a trivial config handshake. Cost on the
C# side is near zero and adds no NuGet dependency — which matters because the app publishes
`PublishTrimmed` + `PublishSingleFile`.

**The precedent that made it plausible:** the Fenix EFB loads `http://localhost:8083` inside a
panel, proving Coherent GT does outbound loopback HTTP from a cockpit document. Q08 then proved
WebSocket specifically, with DevMode off.

**The bus is untouched.** Variable sync still uses it. This is a dedicated interaction channel
alongside it, not a replacement.

### 4.2 Port discovery: range scan 9020–9024

**Chosen:** app binds the first free port in the range; `channel.js` rotates the same range inside
its reconnect backoff.

**Rejected — a fixed port.** No recovery from a squatter or a second app instance.

**Rejected — announce the port over the WASM bus.** This makes the CommBus a bootstrap dependency
of the channel that exists specifically to bypass it. If the bus is down, the bypass is too.

**Why the scan won:** self-correcting with no side channel at all, and the failure mode is
benign — panels back off across the range forever, which is indistinguishable from the app simply
not running. That is deliberate: solo flight must stay clean.

### 4.3 Opt-in per instrument, not an automatic fallback

**Chosen:** a `pointer:` list in the profile.

**Rejected — "use the element name, fall back to coordinates when it misses".** This is the
obvious design and it does not work. On the A220 the name *does* resolve — to the mount `div` —
so the fallback never fires. At capture time a mount `div` and a real button are both just a
successful lookup; there is no signal that separates them.

**Second reason:** wholesale replacement would change behaviour for every aircraft that works
today. Opt-in changes nothing until a profile says so.

### 4.4 Coordinates normalised to the instrument rect

**Chosen:** `nx = (clientX - rect.left) / rect.width`, four decimals.

**Rejected — viewport pixels.** `setupInstrument` sizes the instrument by
`vDisplaySize / vLogicalSize`, a function of the sim's display resolution. Raw pixels would
require both machines to run identical graphics settings.

**Note:** whether the two machines actually disagree is Q04, still unanswered — the rect is
carried in the hello purely so one two-machine session settles it. Normalising costs nothing
either way, so it was treated as insurance rather than a bet. The 2026-09-08 session logged a
constant 7410×1110 on one side; the peer's log was never read.

Coordinates may legitimately fall slightly outside [0,1] and are **never clamped**.

### 4.5 One packet type, not two

**Chosen:** `PointerEvent` with a `Kind` discriminator.

**Rejected — separate `PointerPress` and `PointerDrag`.** This was the original design and was
changed during review (R08). A packet type is a *stream*: two types ride two observables with
independent scheduling hops, so a drag could be processed after the press that followed it and be
dropped as a stale duplicate. One type is one ordered stream from the wire to the panel socket,
and one sequence space.

### 4.6 History: everything after the last ack

**Chosen:** `PointerAck` from the receiver every ~3 s; the sender holds everything past the
lowest ack it has and resends on recovery.

**Rejected — the two-mode ring.** The original design kept a 50-event ring while live and
switched to full accumulation on degradation, with a 60-second age window on the receiving side.
Three problems found in review (R03, R04, R15): it resent events the peer had already applied; a
*fresh* session would receive the last 60 s of the other pilot's solo input; and ordering by wall
clock rather than `Seq` could reorder the resend.

**Why acks won:** the sender holds exactly what is unconfirmed, which is correct by construction
rather than by a heuristic window. A peer that kept running dedupes by `(Session, Seq)`; a peer
that restarted is a new acker (`From`) and gets caught up from the lowest ack held.

**Accepted limit:** an acker that goes quiet for good — a third peer that left mid-session —
keeps its last ack as the floor, so history stops draining. `HistoryCap = 2000` is the safety
bound for that case, not a design parameter.

### 4.7 Drags: batched at mouse-up, not streamed live

**Chosen:** one reliable packet per gesture, full sampled path, ≤240 points.

**Rejected — live streaming.** More packets, more reordering risk, and no proven benefit. The
capture format does not change if streaming is wanted later, so this is reversible.

### 4.8 Blocking with an overlay div, not by suppressing events

**Chosen:** an absolutely-positioned div over the instrument rect.

**Rejected — swallowing events in a capture-phase handler.** A playground probe (p06) did exactly
this, and left an aircraft's clicks silently disabled with the handle needed to undo it already
garbage-collected.

**Why the div won:** the blocker and the explanation are one node. It cannot fail invisibly —
if it is blocking, it is visible — and removing it is the complete restore. `window.fscUnlock()`
is a fixed global that removes it when every other handle is gone.

**The hit-test problem (R17):** a blocking overlay is at max z-index, so `elementFromPoint` during
replay returns the overlay, not the display. The overlay disables its own hit-testing for the
duration of each synchronous `elementFromPoint` call and restores in a `finally`. The page is
single-threaded, so no real click can be delivered in the window.

### 4.9 Lock policy: the slave locks, the master does not

**Chosen:** during `degraded`, only the slave's pointer panels lock.

**Reasoning:** the master must be able to keep flying. Locking the slave means its panels cannot
diverge during the outage, so when the link returns, ordered replay of the held history
reconstructs sync exactly. That is the invariant: **an outage that ends within the session
timeout loses nothing.**

**Accepted caveat:** `MasterSwitch` is rough upstream (`//todo Temp solution`, assertive
handover, last-writer-wins). The lock policy *reads* that state and never writes it, so a
mid-outage role flip behaves exactly as it does today. Documented, not fixed.

### 4.10 Profile key shape: full keys

**Chosen:** `pointer:` takes `identifier|query`; `ignore:` keeps bare identifiers.

**Why they differ:** the A220 reuses `DisplayUnits` across instruments that differ only by
querystring, so a bare identifier cannot address one of them. `ignore:` never needed that
precision.

**The bridge:** `PointerFilter` derives an `Instruments` set (the prefix before `|`) because
`Interact` carries the bare identifier, and the double-actuation guard has to match on that.

### 4.11 No test project

**Chosen:** Serilog diagnostics plus panel `stats` messages.

**Reasoning:** the codebase has no test project to extend, adding the first one is a separate
argument with the maintainer, and nothing load-bearing here is unit-testable without the sim.
Verification was done offline with scratch harnesses (§14) instead.

### 4.12 One PR, not a series

**Chosen:** a single PR structured as clean sequential commits.

**Reasoning:** the maintainer is inactive, so a series has a high chance of stalling half-landed.
The two standalone bug fixes are first in the sequence and can be taken alone if he wants only
those.

---

## 5 · Changes to existing code

18 files, 6 new. Of the 12 modified, 3 are bug fixes, 5 are behaviour changes, 4 are plumbing.

### 5.1 Bug fixes — pre-existing, stand alone

**`VCockpit.js`: instruments dropped on multi-instrument documents**

- *Was:* `var templateToLoad = null` — a single slot. `VCockpitPanel` assigns to it for each
  instrument that arrives before the FS Copilot scripts finish loading. On a document with more
  than one instrument, every assignment but the last is overwritten, and only the last instrument
  gets a `Hook`.
- *Now:* `var templatesToLoad = []`, pushed to, drained in order once the scripts load, each
  construction wrapped in try/catch so one failure does not abandon the rest.
- *Blast radius:* every multi-instrument panel on every aircraft. This is the slow path only
  (scripts not yet loaded); the fast path already handled each instrument.
- *Exposes a second pre-existing issue:* with all instruments now getting Hooks, a document can
  construct duplicate `HtmlEvents` capture. Not fixed here — stated in the PR as a known
  consequence rather than silently changed.

**`Coordinator`: the profile `ignore` list only filtered outbound**

- *Was:* `_sim.Interactions.Where(i => !_ignore.Contains(...))` on the outbound side; the inbound
  `_net.Stream<Interact>()` had no filter at all.
- *Now:* filtered in both directions.
- *Why it matters today, independent of this feature:* FS Copilot's replayed `MouseEvent`s carry
  `clientX/Y = 0`. On a WASM gauge that latches a press at the origin. `ignore: [WasmInstrument]`
  in a profile is the existing mitigation — and it was only half-working, because a peer could
  still actuate the instrument this side's profile said to leave alone.

**`MainViewModel`: a failed join left the joiner a slave**

- *Was:* `Join()` demotes to slave before the connect attempt. Nothing restored the role if the
  attempt failed.
- *Now:* `if (result != ConnectionResult.Success) masterSwitch.TakeControl();`
- *Why it surfaced here:* the session state machine reads the master/slave role, so a joiner
  stuck as a slave with no peer would sit under a lock with no way out. The bug predates this
  feature.

### 5.2 Behaviour changes — review these carefully

**`Peer` gains `Connected`; the peer list stops reporting handshakes as peers**

- *Was:* `P2PNetwork` polled `GetPeersNonAlloc(peers, ConnectionState.Any)`. Every direct peer in
  LiteNetLib's `Outgoing` state — a handshake that may still fail — was published as a peer.
- *Now:* polls `Connected | Outgoing | EndPointChange` and flags `Connected: p.ConnectionState !=
  Outgoing`. Relay peers are `Connected: true` from the first tick, because `LinkReady` arrives
  only after the relay has checked the target is connected and the schemas match.
- *User-visible change:* `MainViewModel` filters to connected peers for the connection list, the
  count, and the connect sound. A handshake in progress is no longer shown, counted, or announced.
  **This changes existing UI behaviour.** It is the correct behaviour — a peer that may still fail
  is not a peer — but it is a change a maintainer should be told about rather than discover.
- *Second effect:* dropping `ConnectionState.Any` also means a peer shutting down after a Leave
  stops lingering for seconds looking like a failed handshake.

**`HybridNetwork` peer merge: a connected entry outranks one still handshaking**

- *Scope first, because the name misleads:* this is the peer **list**, which feeds the UI and the
  Coordinator's session state. It routes nothing. `SendAll` writes to both transports
  unconditionally and still does — per-peer routing isn't expressible through the current
  `INetwork` API, and this change doesn't attempt it.
- *Was:* `foreach (var p in relayPeers) dict.TryAdd(p.PeerId, p);` — the direct entry always won,
  connected or not.
- *Now:* a relay entry replaces a direct one only when the relay link is connected and the direct
  one is not. **Direct still wins whenever it is actually connected**, so the transport shown in
  the UI is unchanged in steady state.
- *Why:* `Connected` is new, so a direct entry can now mean "handshake in flight, may still fail".
  Under the old order, a live relay link beside an in-flight direct handshake for the same peer
  would be reported as the direct entry with `Connected: false` — the UI would hide a peer you are
  actively talking to, and the Coordinator would read `Link.Connecting` instead of `Link.Live` and
  drop the blue lock over the panels mid-session while packets flowed fine over relay.
- *The window is real:* `P2PNetwork` re-introduces on a 20 s interval, so direct attempts recur
  for a peer already held over relay.

**`INetwork` gains `PeerLeft`: a departure is told from an outage**

- *New capability, no prior equivalent.* The session machine has to distinguish "the other pilot
  left" (session over, unlock, drop history) from "the link dropped" (outage, lock the slave,
  hold history).
- *Direct path:* `DisconnectAll` carries a `"left"` payload, read back from
  `DisconnectInfo.AdditionalData`. **It rides the disconnect itself, not a packet sent before
  it** — the transport does not promise a packet queued before `Disconnect()` goes out ahead of
  the close.
- *Relay path:* reads the existing `PEER_LEFT` / `LEFT_ALL` close codes, which the discovery
  server already emits on `main`.
- *Anything else — timeout, crash, kill, a close with no payload — is an outage.*
- **Deployment caveat worth checking before the PR claims this works over relay:** the relay-side
  codes exist in `main`'s `FsCopilot.Discovery/Relay.cs`, but the *deployed* upstream relay has to
  be running a build that emits them.

**`INetwork` gains `Connecting`**

- True while a `Connect` is in flight. On `HybridNetwork` it covers the whole attempt — direct try
  and relay fallback together — because the children's individual flags blink off between the two.
- *Why it exists:* without it the Coordinator cannot distinguish "no peer yet, but the user
  pressed Join ten seconds ago" from "no peer, solo flight". Those need different overlays.

**`MainViewModel.Leave` now ends the pointer session**

- *Was:* `net.Disconnect(); masterSwitch.TakeControl();`
- *Now:* also `coordinator.EndSession()`.
- *Why:* leaving on purpose is not an outage. Without this the slave's panels would lock for five
  minutes after the user deliberately left.

**`App.axaml.cs`: the app announces its own shutdown**

- *New.* `PanelServer.Shutdown()` runs before `INetwork.Disconnect()` on the exit path.
- *Why:* a panel sees only a dropped socket, which is identical to a crash, and would warn the
  pilot that sync broke when nothing broke. A kill or a crash deliberately does *not* reach this,
  which is what leaves the warning for the cases that deserve it.

### 5.3 Plumbing — additive, low review weight

| File | Change |
| --- | --- |
| `Definitions.cs` | `pointer:` key mirroring `ignore:` exactly — `Config` → `DefinitionNode` → `Collect` → `string[] Pointer`. Include-tree merge comes free. |
| `Program.cs` | `PanelServer` singleton in both dev and non-dev branches. In dev it gets `EnableDevEcho()` and is fed the profile's pointer list directly, since dev has no `Coordinator`. |
| `MainViewModel.cs` | `ViewErrors.PanelChannel` for the all-ports-taken case. |
| `hook.js` | Mode decision. Hello on construction; `config` switches modes at runtime; `state` and `pointer` route to the document's `Pointer`. Both the emit path and the inbound dispatch are gated on `_pointerMode`. |
| `VCockpit.js` | Three new files in the Include chain (`channel.js`, `overlay.js`, `pointer.js`), inside the existing FSC-delimited blocks. |

---

## 6 · New code

### 6.1 Design level

| File | LOC | Responsibility | Explicitly not its job |
| --- | --- | --- | --- |
| `Connection/PanelServer.cs` | 469 | Socket lifecycle, per-key routing, the JSON schema, state broadcast, goodbye. | Peers, profiles, sequence numbers, session policy. |
| `Connection/Pointer.cs` | 148 | The two packet records and their codecs. Carries the canonical wire documentation. | Anything behavioural. |
| `HTML_UI/FsCopilot/channel.js` | 169 | WebSocket with port rotation, backoff, bounded age-capped queue, hello replay on reconnect. | Anything about pointers. It is a transport. |
| `HTML_UI/FsCopilot/pointer.js` | 589 | Gesture model: capture, classification, normalisation, the replay queue, stats. Owns which overlay state is showing. | The mode decision (that is `hook.js`). |
| `HTML_UI/FsCopilot/overlay.js` | 187 | Renders a named state as a div. Hit-test suppression during replay. | Knowing what a session is. |

`Definitions/synaptic_a220.yaml` (628 lines) is on the branch and **must come off before push** —
see §16.

### 6.2 Method level — `PanelServer`

Idiom note: concrete `sealed` class with no interface, matching `SimClient`. Inbound work is
exposed as `IObservable` with `.ObserveOn(TaskPoolScheduler.Default)`, also matching `SimClient`.

| Member | Notes |
| --- | --- |
| `Port` | The bound port, or `-1`. |
| `BindFailed : IObservable<bool>` | True when all five ports were taken. Drives the view error. |
| `Events : IObservable<PointerEvent>` | Captures from panels, presses and drags on one stream in capture order. `Session`/`Seq` unstamped here — the Coordinator owns those. |
| `Configure(keys)` | Replaces the opt-in list, broadcasts `config` **then** `state`. State must follow config: a panel entering pointer mode locks itself until it hears a state, so a profile load onto already-connected panels would otherwise flash the lock until the next renewal. |
| `SetSession(session, isMaster)` | Updates what the 2 s renewal broadcasts. |
| `Send(PointerEvent)` | Routes to every socket that helloed that key. Converts wire deltas back to absolute times. |
| `EnableDevEcho()` | `--dev` only. Reflects captures straight back, exercising panel→app→panel on one machine. The reflected press visibly actuates twice — that is the signal it works, not a bug. |
| `Shutdown()` | Disposes the renewal timer **first**, sets `_closing` so the send path refuses everything but the goodbye, then sends `bye` to each socket with a 750 ms total grace. |

Threading details a reviewer may ask about:

- Per-socket sends are serialised with a `SemaphoreSlim`.
- `Shutdown` is called from the UI thread on the way out. It uses `Task.Run(...).Wait()` rather
  than `await`, because awaiting a socket write on the UI thread would post the continuation back
  to the thread the `Wait` is blocking. Off the pool there is no context to deadlock against, and
  the grace period bounds it either way.
- Inbound messages over 256 KB are dropped — no legitimate panel message is that size.
- `Route` **drops and counts** events for a key no socket has helloed. It does not hold them: a
  document that helloes later was reloaded by the sim and starts from its default state, while
  the peer's did not. Replaying "next page" three times into a panel that reset to page one is
  three random inputs. (This replaced an earlier per-key pending queue — R09.)

### 6.3 Method level — `Coordinator` additions

| Member | Notes |
| --- | --- |
| `EndSession()` | Public. Called by `Leave` and by the degraded timeout. Drops history and acks, resets link to `None`, broadcasts `none`. |
| `OnLink(Link)` | The state machine. `Live` → resend history *only if recovering*; `Connecting` → start the outage clock if there was a peer; `None` → end session if the peer left, else `Degraded`. |
| `SendPointer(e)` | Appends to `_history` **only if `_hadPeer`**. Nothing is held with no session: a first contact must not receive the local pilot's solo input (R04). |
| `OnAck(ack)` | Records per-acker high-water marks, drops history below the *minimum* across all ackers. |
| `SendAcks()` | Every 3 s, only while `live`, only for sessions whose mark moved. An ack sent into a dropped link is lost with it, and marking it sent would leave the sender holding less than it needs. |
| `Fresh(session, seq)` | Dedupe plus gap warning. `seq <= last` → drop (history resends overlap by design). `seq > last + 1` → log how many never arrived. |
| `PointerFilter` | Immutable snapshot, swapped atomically on profile load. `Keys` (full) and `Instruments` (prefix) — see §4.10. |

---

## 7 · State machines

### 7.1 Session (app side, `Coordinator`)

Link is derived from `net.Peers` + `net.Connecting`:
`Live` if any peer is `Connected`; `Connecting` if a join is in flight or peers exist but none
connected; else `None`.

| From | Event | To | Side effects |
| --- | --- | --- | --- |
| none | link → connecting | connecting | — |
| none/connecting | link → live (first) | live | — (no resend: nothing to replay and nobody it would be right for) |
| live | link → connecting | connecting | start 5 min outage clock |
| live | link → none, peer left | none | `EndSession` — drop history |
| live | link → none, peer lost | degraded | start 5 min outage clock |
| degraded/connecting | link → live | live | **resend unacked history in `Seq` order** |
| degraded | 5 min elapsed | none | `EndSession`, log desync warning |
| any | user presses Leave | none | `EndSession` |

`_peerLeft` is set by `net.PeerLeft` and cleared by any tick showing a live link — so a third
peer leaving a three-way session does not turn the next real outage into a session end.

### 7.2 Overlay (panel side)

| Session | Master | Slave |
| --- | --- | --- |
| none (solo, or left) | clear | clear |
| connecting | **blue — CONNECTING** | **blue — CONNECTING** |
| live | clear | clear |
| degraded | clear (must keep flying) | **amber — SYNC DEGRADED** |
| app link lost (any prior state) | **red — SYNC BROKEN**, non-blocking | same |
| replaying a queued gesture | **teal — REPLAYING** | same |

Colours and copy are in `Overlay.STATES`. Three block; `lost` does not (`pointer-events: none`),
because when the app is gone nobody authoritative is alive to lift a lock. The red warning
retracts itself after 10 s rather than standing forever.

**The hard rule: blocking requires a live app renewing the lock.** If `state` renewals stop for
8 s, any blue/amber lock degrades to red — never to silence, never to a stuck block.

### 7.3 Panel mode (`hook.js`)

| Condition | Result |
| --- | --- |
| No `config` ever received | Events mode forever. Nothing changes. |
| `config` lists this key, no `Pointer` in document | Pointer mode; this hook becomes the owner; creates `window.fscPointer`. |
| `config` lists this key, a `Pointer` already owns the document | Events mode, warning logged. v1 limitation. |
| `config` no longer lists this key | `pointer.stop()`, back to events mode. |

---

## 8 · Constants, and why each has that value

### App side

| Constant | Value | Reasoning |
| --- | --- | --- |
| `Ports` | 9020–9024 | Five is enough for a squatter plus a second instance. Unregistered range. |
| `StateRenewal` | 2 s | The panel's liveness signal. Must be well under the panel's 8 s deadman. |
| `ShutdownGrace` | 750 ms | Runs on the way out of the process; a wedged socket must not hold the app open. Loopback delivery is sub-millisecond when it works at all. |
| `DegradedTimeout` | 5 min | Long enough for a router reboot or a sim reload; short enough that a slave is not locked out of its aircraft indefinitely. |
| `AckInterval` | 3 s | Bounds how much history a sender holds in the common case. |
| `HistoryCap` | 2000 | Safety bound only, for an acker that goes quiet forever. ~60 B presses / ≤2.4 KB drags keeps this well under a megabyte. |
| max inbound message | 256 KB | No legitimate panel message approaches this. |
| decode path limit | 1024 points | Capture bounds at 240; decode rejects beyond 1024 as malformed rather than allocating. |

### Panel side

| Constant | Value | Reasoning |
| --- | --- | --- |
| `DRAG_MIN_PX` | 4 | Below this the gesture was a press, not a drag. |
| `DRAG_SAMPLE_MS` | 33 | ~30 Hz. Matches panel render rates. |
| `DRAG_MIN_STEP_PX` | 2 | Ignores jitter between samples. |
| `DRAG_MAX_POINTS` | 240 | Bounds the message; ~8 s of dragging. |
| `DRAG_MAX_STEP_MS` | 250 | A mid-drag pause replays as a bounded pause rather than a stall. |
| `EDGE_TOLERANCE` | 0.02 | Rect fraction allowed outside [0,1] when only the point can be tested. |
| `GAP_MAX_MS` | 1000 | Idle time between gestures is preserved up to this. |
| `REPLAY_DEADMAN_MS` | 3000 | Past a gesture's expected end, release the queue rather than jamming everything behind it. |
| `STATE_DEADMAN_MS` | 8000 | Four missed renewals. Past this the app is presumed gone. |
| `LOST_LINGER_MS` | 10000 | How long the red warning stands before retracting. |
| `MAX_QUEUE` | 200 | Bounded outbound queue. |
| `MAX_QUEUE_AGE_MS` | 30000 | Age cap instead of drop-all on reconnect: stale input is worse than lost input, but boot-time captures must survive the connect delay. |
| `BACKOFF` | 500/1000/2000/4000 | Per lap of the port range. |

---

## 9 · Failure modes

Every one of these was designed to fail open. This table is the answer to "what happens if…".

| Situation | Panel behaviour | App behaviour |
| --- | --- | --- |
| App not running | No config ever arrives → events mode, no overlay, nothing changes. Channel backs off across the range forever. | — |
| All five ports taken | Indistinguishable from the app not running, deliberately — solo flight stays clean. | Logs a warning, raises `ViewErrors.PanelChannel` in the desktop UI. Observables silent. |
| App crashes mid-session | Renewals stop; after 8 s any lock degrades to red non-blocking; red retracts after 10 s. | — |
| App quits deliberately | `bye` arrives first; panel clears without warning. | 750 ms grace, then dispose. |
| Peer link drops | Slave locks amber; master clear. | Session `degraded`, history accumulates, 5 min clock. |
| Peer returns within 5 min | Lock lifts, held history replays in order, gaps preserved. | `live`, `ResendHistory()`. |
| Peer does not return | Lock lifts at 5 min; desync warning logged. | `EndSession`. |
| Peer leaves on purpose | Lock never appears. | `EndSession` immediately. |
| Panel loads after the event | Event is dropped and counted — see §6.2. | — |
| Replay stalls | 3 s deadman releases the queue, logs, increments `stalled`. | — |
| Overlay stuck for any reason | `window.fscUnlock()` at a fixed global removes it. | — |
| Profile revokes the key | `pointer.stop()`, events mode resumes. | `Configure` broadcast. |
| Peer sends a key our profile doesn't list | Never reaches the panel. | Inbound filter drops it — defence against a peer with a different profile. |
| Malformed packet | — | Codec throws, `Codecs.Decode` drops it, logged once. |

---

## 10 · Known limits

| Limit | Detail |
| --- | --- |
| WASM displays unreachable | Permanent. Q05, closed negative. The gauge takes input natively via `data-input-group`; injected coordinates are discarded and the press latches onto whatever the real cursor hovers. |
| Cross-origin iframes | Not addressed. A process boundary, not a naming problem. Fenix EFB. |
| Divergent pages press the wrong thing | Inherent to input replay. The element-name scheme has the same property invisibly. Recovery: page buttons are at fixed coordinates, so both pilots pressing the same one resynchronises. |
| One pointer agent per document | v1. First opted-in instrument wins; the rest stay in events mode with a warning. A220 targets are all single-instrument. |
| Drag drift | Bounded 0–12%, temporal rather than spatial. Q10, open, not diagnosed. |
| Concurrent conflicting input | Within a latency window, both panels can diverge. No input-replay design fixes this without rollback. |
| Boot tail | The first seconds before panel JS loads are inherently uncoverable. The connecting-state lock closes most of it. |
| Real input during a degraded lock | Reaches the panel through uncovered parts of the document. R05 narrowed capture to the instrument rect, so the lock and capture now agree on the same rectangle. |

---

## 11 · Deliberately out of scope

| Not done | Why, and where it went |
| --- | --- |
| WASM displays | Impossible, not deferred. Q05 closed with evidence from an unstripped binary. |
| Cross-origin iframes | Q06 open; would need a different mechanism entirely. |
| Keyboard sync | `keydown`/`keypress`/`keyup` were observed arriving at panel documents and trusted, so it is available. Deliberately a second pass. |
| Wheel and double-click | Wheel is a first-party MSFS cockpit control and is not injected into large instrument panels; double-click is not used on these panels. Decided 2026-08-30 from cockpit experience. |
| Re-enabling `IgnoreUnmatchedProperties()` | Would fix the forward-compat hazard for profile keys. A separate argument, and a separate risk. Flagged in the PR as worth doing. |
| The A220 profile | Belongs in `fscopilot-profiles`, which already carries `pointer: [DisplayUnits\|config=Default]`. The branch copy is a stale snapshot nothing reads. |
| A test project | §4.11. |
| Master handover when the master leaves | The remaining pilot should arguably become master. Deferred as a separate piece of work — this PR reads the role and never writes it. |
| Fixing duplicate `HtmlEvents` on multi-instrument documents | Pre-existing, exposed by the `VCockpit.js` fix. Documented rather than silently changed. |

---

## 12 · Risks after merge

| Risk | Likelihood | Mitigation / what to say |
| --- | --- | --- |
| Trimming breaks `HttpListener`/`AcceptWebSocketAsync` | Low but real — the codebase has trim scars (`[DynamicDependency]` in `Definitions.cs`) | Smoke-test the published single-file exe. **Not yet done** — see §16. |
| `HttpListener` URL ACL on a clean non-admin machine | Low | `127.0.0.1` needs no ACL; `localhost` would. The choice is deliberate. Fallback if it bites: `TcpListener` + manual upgrade + `WebSocket.CreateFromStream`, ~60 lines, still no NuGet. |
| A `pointer:`-bearing profile bricks profile loading on older releases | Certain if it happens | No served profile carries the key until this ships. This is the single most likely way to hurt existing users. |
| Old clients rejected by the schema hash | Certain, by design | Version-gate the release. Unavoidable with any wire change. |
| Outage paths have never been flown | High that something is found | Instrumentation ships with it. Say so plainly in the PR. |
| `MasterSwitch` roughness surfaces through the lock | Medium | The lock reads the role and never writes it; behaviour is as today. Documented caveat. |
| Relay `PEER_LEFT` not deployed upstream | Unknown — **check before claiming it** | Degrades gracefully: a departure over an unaware relay is treated as an outage, so the slave locks for 5 min instead of unlocking immediately. |
| Local WebSocket attack surface | Low (loopback only) | R12 step one shipped (Origin logged). Step two — rejecting `http://`/`https://` origins — waits on one sim session. |

---

## 13 · Dead ends

Worth having to hand: a reviewer asking "did you try X?" is asking about one of these.

| Tried | Result |
| --- | --- |
| Injecting coordinates into a WASM gauge | The gauge presses whatever the **real cursor** hovers and ignores the injected coordinates entirely. |
| Same-tick injection to beat the cursor | Lost. The sim owns hover absolutely. |
| Syncing the cursor position instead | No position variable exists; the screen variable is an output only. |
| Wrapping a sim API in a probe | Disabled the aircraft's clicks, and the handle to undo it had already been deleted. This is why every injected path now fails open with a fixed global and a deadman. |
| Using the inspector console to carry recorded data | It silently drops a message identical to the previous one. Cost half a recording. |
| Element-name fallback when the name misses | Never fires — the name resolves to the mount `div`. §4.3. |
| Two packet types for press and drag | Reordering past the dedupe. §4.5. |
| Two-mode history ring with a 60 s window | Resent already-applied events; leaked solo input into a fresh session. §4.6. |
| Per-key pending queue flushed on hello | Replays stale input into a panel that reset to its default state. §6.2. |
| A capture-phase event swallower as the lock | Silent, invisible failure. §4.8. |
| A second Community package for the experiment | Conflicts with `fscopilot-bridge` — both override `VCockpit.js`. |

One false negative worth knowing about, because it is in the log: drag was reported broken on
2026-08-30 and the result was **void** — the build under test had no drag code in it. Drag works.

---

## 14 · Evidence

Do not assert anything in the PR that is not in this table.

| Claim | How verified | Evidence |
| --- | --- | --- |
| Synthetic clicks drive a React/SVG display | Opened a real dropdown on the A220 DisplayUnits, target an SVG `rect` | log 2026-08-30 "Q03 PASSES" |
| Capture is lossless | 33/33 including spam-clicking | log 2026-08-30 "Capture does not drop presses" |
| Works with DevMode off | Full module run | log 2026-08-30 "The module works with DevMode off" |
| `coui://` can open a WS to loopback | Q08 probe | log 2026-08-30 |
| Drags work | Tier 3 in the cockpit | log 2026-08-30 "Drag works" |
| WASM is unreachable | Channel table read from an unstripped binary | 07-wasm-surface |
| Two-machine operation | Three-way session, direct + relay, 2026-09-08 22:00–22:33 | `results/session-2026-09-08-app.txt` |
| — 168 captured / 70 replayed / 0 missed | app log, `DisplayUnits\|config=Default` | same |
| — rect constant at 7410×1110 | app log | same — **one side only; Q04 still open** |
| — panels rejoin after app restart | fourth port lap, queues intact | same |
| Codec round-trips | Offline harness | build log 2026-09-01 |
| Schema changes only by the two new packets | Ad-hoc before/after check | build log 2026-09-01 |
| `PanelServer` handshake, rotation, renewal | `ClientWebSocket` scratch harness | build log 2026-09-01 |
| Every panel file parses | `node --check` at every commit | build log 2026-09-12 |
| App builds clean at every commit | `dotnet build` | build log 2026-09-12 |

**Not verified — do not claim:** trimmed-publish survival, outage paths against a real peer
(history resend, degraded lock, session timeout), Q04 rect agreement across machines, the origin
rejection, and R08 reordering (believed absent on this side, never observed either way).

---

## 15 · Questions a reviewer will ask

| Question | One-line answer | Long answer |
| --- | --- | --- |
| Why not use the existing bus? | 512-byte single slot; a drag is ~2.4 KB, and widening raises size, not rate. | §4.1 |
| Why a new port range instead of announcing the port? | That would make the bus a bootstrap dependency of the channel built to bypass it. | §4.2 |
| Why not just fall back to coordinates when the name misses? | The name doesn't miss — it resolves to the mount div. | §4.3 |
| Does this break existing users? | Yes, twice: the schema hash rejects older peers, and a `pointer:` profile won't load on an older release. Both are stated and gated. | §12 |
| What happens if the desktop app isn't running? | Nothing. No config, no overlay, events mode forever. | §9 |
| Why five minutes? | Long enough for a router reboot, short enough not to lock a pilot out of their aircraft. | §8 |
| Why is the slave locked but not the master? | The master has to keep flying; locking the slave is what makes exact reconstruction possible. | §4.9 |
| What if the overlay gets stuck? | `window.fscUnlock()`, plus an 8 s deadman that degrades any lock to non-blocking. | §4.8, §9 |
| Is this tested with two machines? | Yes, once, live path only. Outage paths are untested. | §14 |
| Why one packet type with a discriminator? | Two types are two streams with independent scheduling — a drag could overtake a press. | §4.5 |
| Why is `Flags` there if it's unused? | The schema hash makes adding a field later a break for every user; flag bits aren't. | §3.3 |
| Why does `pointer:` use different key syntax from `ignore:`? | The A220 reuses identifiers across instruments; a bare identifier can't address one. | §4.10 |
| Why no tests? | No test project exists to extend, and nothing load-bearing is unit-testable without the sim. | §4.11 |
| Why is the peer list behaviour changing? | A handshake that may still fail was being shown, counted and announced as a peer. | §5.2 |
| Are you preferring relay over direct now? | No. The merge affects the peer *list*, not routing, and direct still wins whenever it is connected. `SendAll` writes to both transports as before. | §5.2 |
| What's the WASM story? | Permanently impossible, with evidence. Not a matter of effort. | §10, 07-wasm-surface |

---

## 16 · Pre-push checklist

**Blockers:**

- [ ] **Reshape the branch.** 14 commits, four of which fix code introduced earlier on the same
      branch (`12b8e4d` fixes `5a991a0`; `149b90d`…`833ea67` fix `30501dd`). A PR branch is cut
      fresh from `main` with the work moved across as clean commits. See §17.
- [ ] **Remove `Definitions/synaptic_a220.yaml`.** Salvage diff is written
      (`results/a220-profile-salvage-2026-09-12.txt`) and is **not empty** — 20 variables the
      branch copy names in a `get:` are absent from the profiles repo copy. Read that list, move
      anything wanted into `fscopilot-profiles`, then `git rm`.
- [ ] **R12 step two** — reject `http://`/`https://` origins. Waiting on one sim session to
      confirm what Coherent actually sends in the `Origin` header. Currently logged at Debug only.

**Verification owed:**

- [ ] `dotnet publish -c Release` and run the trimmed single-file exe — confirm `HttpListener` and
      `AcceptWebSocketAsync` survive trimming.
- [ ] `node --check` over every file under `PackageSources/HTML_UI` on the final reshaped branch.
- [ ] Confirm the deployed upstream relay emits `PEER_LEFT` / `LEFT_ALL`, or soften the claim.

**Decide before writing:**

- [ ] Whether to mention the pre-PR review (17 findings, 16 fixed) at all. It signals rigour and
      also signals "this had 17 bugs recently". My read: leave it out; the fixes are in the code.
- [ ] Whether to nudge on `IgnoreUnmatchedProperties()`. It is real and it is scope creep.

---

## 17 · Commit plan for the reshaped branch

Target shape. Each is independently buildable, and the first two are takeable alone.

| # | Commit | Contents |
| --- | --- | --- |
| 1 | `VCockpit.js`: keep every instrument on multi-instrument panels | `VCockpit.js` pending-array fix only |
| 2 | Apply the profile ignore list to inbound interactions too | `Coordinator` inbound filter; `ignore: [WasmInstrument]` entries if any belong here |
| 3 | Add a direct panel-to-app WebSocket channel | `PanelServer.cs`, `channel.js`, `VCockpit.js` include chain, DI in `Program.cs`, `ViewErrors.PanelChannel`. Behaviour-neutral: panels stay in events mode |
| 4 | Forward cockpit input by position, not element name | `Pointer.cs`, `pointer.js`, `overlay.js`, `hook.js` mode gating, `pointer:` in `Definitions.cs`, Coordinator wiring and filter |
| 5 | Tell a live session from an outage, and a departure from a loss | `Peer.Connected`, `INetwork.Connecting`/`PeerLeft`, P2P/Relay/Hybrid changes, `MainViewModel` peer filtering and failed-join fix, session state machine, history/acks, overlay policy, `App.axaml.cs` goodbye |
| 6 | Add pointer diagnostics | panel `stats`, `--dev` echo, per-key counters |

Commit 5 is the big one and cannot reasonably be split further — the session machine, the peer
semantics it reads, and the overlay policy it drives are one change. Say so in the PR rather than
letting the reviewer wonder.
