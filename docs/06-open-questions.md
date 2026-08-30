# Open questions

    Purpose:    Everything that has to be verified before it is depended on.
    Depends on: 02-approach, 03-scope, 04-transport
    Decides:    nothing — it records what is not yet decided

The register. Unlike the rest of the design record this file **is** updated in place as
questions settle: status changes here, evidence goes in [build/log.md](build/log.md), and the
`Answered by` line points at the log entry.

Questions are grouped by what they gate, not by difficulty. A question with no probe named
has not been thought about hard enough to run yet.

---

## Gate A — can Claude run the experiments?

### Q00 · Is the Coherent GT debugger scriptable from outside the sim?
**Status:** ANSWERED yes · **Probe:** `probes/p00-debugger.mjs` · **Answered by** "Q00 is yes" in [build/log.md](build/log.md)

MSFS hosts a WebKit Web Inspector backend on `127.0.0.1:19999`. `GET /pagelist.json` lists every
inspectable document; `ws://127.0.0.1:19999/devtools/page/N` evaluates arbitrary JavaScript in
one. Driven by `probes/lib/inspector.mjs` through `npm run pages | probe | eval`.

**Caution that cost a probe once:** confirm the port's owning process is *alive* before reading
anything into its behaviour. An orphaned socket accepts connections and answers nothing, which
is indistinguishable from a server that dislikes your protocol. `p00` now refuses to interpret
a dead owner.

---

## Gate B — does pointer forwarding work at all?

### Q01 · Does MSFS deliver `pointerdown` / `mousedown` / `mousemove` to a panel document, or only `mouseup`?
**Status:** open, probe ready · **Probe:** `probes/p02-capture.js`

FS Copilot only ever listens for `mouseup`, so everything else is unverified. The A220 binds
`onPointerDown`, which is suggestive but not proof — React may be synthesising pointer events
from mouse events.

**Changes:** whether press-and-hold and drag are capturable at all, and whether the replay
sequence needs real `PointerEvent`s.

### Q02 · Are cockpit clicks delivered as trusted events?
**Status:** open · **Probe:** `probes/p02-capture.js`

**Changes:** whether `ev.isTrusted` is usable as a second loop breaker alongside `selfEmit`.

### Q03 · Does a synthetic pointer sequence at coordinates drive a real React/SVG display?
**Status:** open, probe ready · **Probe:** `probes/p03-replay.js`

The core of the proposal. Target: A220 `CTP`.

**Now the only question that matters.** With Q05 closed negative, the WASM surface is gone and
everything this project can still deliver sits behind this one answer. Note the WASM failure
does **not** predict it: there the simulator owned the hit-target and discarded our
coordinates, whereas a React or canvas display does its own hit-testing inside the panel
document with nothing in between.

**Changes:** everything. A negative result kills the approach for the class of aircraft it was
designed for, and there is no second idea behind it.

### Q04 · Do the two machines agree on the instrument element's bounding rect?
**Status:** open, deferred to the two-PC phase · **Probe:** none yet

Coordinates are normalised against the instrument's own `getBoundingClientRect()`, which should
cancel the `vDisplaySize / vLogicalSize` scaling. Needs confirming nothing else varies with
graphics settings. Low risk — normalising is free insurance either way.

---

## Gate C — how far does it reach?

### Q05 · Does `WasmInstrument.html` route DOM input into the WASM module?
**Status:** CLOSED, negative · **Probes:** `p01`, `p06`, `p07` · **Answered by** "Same-tick injection loses too" in [build/log.md](build/log.md)

It routes the events but not the targeting. Hover belongs to the sim, the press latches onto
whatever the receiving pilot is pointing at, and the cursor has no writable position anywhere.
Full write-up in [07-wasm-surface](07-wasm-surface.md).

### Q06 · Can a `coui://` page reach into a cross-origin iframe?
**Status:** open · **Probe:** `probes/p04-iframe.js` — needs an aircraft with a genuine cross-origin panel (Fenix). The A350 EFB turned out to be plain HTML DOM with only an `about:blank` viewer iframe, so it does not test this.

Two problems, not one — reach, and the fact that capture has to happen in the child regardless.
[03-scope](03-scope.md) has both.

**Changes:** whether external-app panels are reachable. Weigh against the fact that EFBs are
often deliberately unsynced.

### Q07 · What is the TDS GTN 750 actually made of?
**Status:** open · **Probe:** same as Q06, run on its panel

`TDSGTNXiFlightSimEXE` runs as an external process, which suggests an iframe onto Garmin's
trainer. It could equally be a live-view `<img>` like the A350. Do not assume.

---

## Gate D — transport

### Q08 · Can a `coui://` page open a WebSocket to `ws://127.0.0.1`?
**Status:** open, probe ready · **Probes:** `probes/p05-server.mjs` + `probes/p05-websocket.js` (server tested end to end)

The Fenix EFB proves outbound HTTP to loopback works from a cockpit document. WebSocket is
untested. [04-transport](04-transport.md) has what it buys.

**Changes:** whether the 512-byte single-slot bus is a constraint to design around or one to
delete.

### Q09 · Does the existing bus survive a burst?
**Status:** open, moot if Q08 lands · **Probe:** none yet

Whether two `SetClientData` calls in one frame both arrive, or coalesce.

---

## Settled

- **Q00 — yes.** MSFS hosts a WebKit inspector on 127.0.0.1:19999; probes run over the wire.
- **Q05 — no, permanently.** WASM displays cannot be driven by injected input. See
  [07-wasm-surface](07-wasm-surface.md).

---

## Explicitly out of scope

Fixing existing FS Copilot bugs found during the deep dive — the `keyCode` type mismatch, the
`_id2El` collision overwrite, the `size >` off-by-one, the missing `return` in `bus.js`. They
are recorded in the deep dive and are somebody's future work.
