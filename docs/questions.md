# Open questions

The register. Each question names what would settle it and what the answer changes. Status
moves `open` → `answered` when a probe result lands in `findings.md` with evidence in
`results/`.

Questions are grouped by what they gate, not by difficulty.

---

## Gate A — can Claude run the experiments?

### Q00 · Is the Coherent GT debugger scriptable from outside the sim?
**Status:** open · **Probe:** `probes/p00-debugger.mjs`

Port 19999 accepts a TCP connection while the debugger app is open, but answers neither a
plain HTTP `GET /` nor a WebSocket upgrade at `/`. Either it speaks a protocol we have not
guessed, it wants a different path, or 19999 belongs to the debugger UI rather than to the
sim's inspector backend.

**Changes:** whether every probe below can be run and re-run automatically, or has to be
pasted into a console by hand. Worth an hour before accepting the manual loop.

---

## Gate B — does pointer forwarding work at all?

### Q01 · Does MSFS deliver `pointerdown` / `mousedown` / `mousemove` to a panel document, or only `mouseup`?
**Status:** open · **Probe:** `probes/p02-capture.js` *(not written yet)*

FS Copilot only ever listens for `mouseup`, so the rest is unverified. The A220's own code
binds `onPointerDown`, which is suggestive but not proof — React may be synthesising them
from mouse events.

**Changes:** whether drag and press-and-hold are capturable at all, and whether the replay
sequence needs real `PointerEvent`s.

### Q02 · Are cockpit clicks delivered as trusted events?
**Status:** open · **Probe:** `probes/p02-capture.js`

**Changes:** whether `ev.isTrusted` is usable as a loop breaker alongside `selfEmit`.

### Q03 · Does a synthetic pointer sequence at coordinates actually drive a React/SVG display?
**Status:** open · **Probe:** `probes/p03-replay.js` *(not written yet)*

The core of the whole proposal. Target: A220 `CTP` (small, React over SVG, failure mode
fully understood).

**Changes:** everything. A negative result here kills the approach for the class of aircraft
it was designed for.

### Q04 · Do the two machines agree on the instrument element's bounding rect?
**Status:** open · **Probe:** deferred to the two-PC phase

Coordinates are to be normalised against the instrument element's own
`getBoundingClientRect()`, which should cancel the `vDisplaySize / vLogicalSize` scaling in
`setupInstrument`. Needs confirming that nothing else varies with graphics settings.

---

## Gate C — how far does it reach?

### Q05 · Does `WasmInstrument.html` route DOM input into the WASM module?
**Status:** open · **Probe:** `probes/p01-wasm-shell.js`

The A350's displays are `<wasm-instrument>` → `<div id="Mainframe">` → `<wasm-sim-canvas>` →
`<img src="WasmGaugeLiveView_25">`. The `<img>` is a live-view texture blit. The element
carries `data-input-group="WASM-INSTRUMENT"`, which points at the sim routing input natively.

If the shell installs listeners and forwards coordinates over a named channel, we can call
that channel ourselves and the A350, A400M and PMDG come into range. If it is purely a
display wrapper, no DOM-level scheme ever reaches them.

**Changes:** whether this fixes two aircraft or five.

### Q06 · Can a `coui://` page reach into a cross-origin iframe?
**Status:** open · **Probe:** `probes/p04-iframe.js` *(not written yet)*

The Fenix EFB is `<iframe src="http://localhost:8083/index.html">` inside a `coui://` parent.
Under normal same-origin policy `contentDocument` is `null`. Coherent GT is historically
permissive and `coui://` may be privileged — untested either way.

Note the separate, larger problem: a real click inside an iframe is dispatched into the
**child's** document, so the parent's listener never fires. Capture has to live in the child
regardless of whether we can reach it.

**Changes:** whether external-app panels (Fenix EFB, likely TDS GTN) are reachable at all.
Note that EFBs are often deliberately *not* synced — PMDG's profile ignores `PMDGTablet` —
so this may not be worth much even if the answer is yes.

### Q07 · What is the TDS GTN 750 actually made of?
**Status:** open · **Probe:** same as Q06, run on its panel

`TDSGTNXiFlightSimEXE` runs as an external process, so the guess is an iframe onto Garmin's
trainer. It could equally be a live-view `<img>` like the A350. Do not assume.

---

## Gate D — transport

### Q08 · Can a `coui://` page open a WebSocket to `ws://127.0.0.1`?
**Status:** open · **Probe:** `probes/p05-websocket.js` *(not written yet)*

The Fenix EFB loads `http://localhost:8083` inside a panel, which proves Coherent GT does
outbound HTTP to loopback. If WebSocket works too, the interaction path can bypass CommBus,
the WASM module and SimConnect entirely: full duplex, no size cap, per-panel connections
instead of a broadcast every panel has to parse.

Server cost is near zero — `HttpListener` + `AcceptWebSocketAsync` are both BCL, no new
NuGet. Would need checking against `PublishTrimmed`.

**Changes:** whether the 512-byte single-slot JSON bus is a constraint we design around or
one we delete.

### Q09 · Does the existing bus survive a burst?
**Status:** open · **Probe:** deferred until after Q08

`FSC_BUS_OUT` / `FSC_BUS_IN` are single 512-byte SimConnect client-data areas with `ON_SET`
notification, currently carrying about one message per second. Whether a 20–30 Hz drag
stream coalesces or drops is unknown. Moot if Q08 says yes.

Note the cap is self-imposed: SimConnect client-data areas go to 8 KB. Widening `str_msg` is
a one-line change either side, but it is a WASM ABI change and needs a version bump.

---

## Explicitly out of scope

Fixing existing FS Copilot bugs found during the deep dive — the `keyCode` type mismatch,
the `_id2El` collision overwrite, the `size >` off-by-one, the missing `return` in `bus.js`.
They are recorded in the deep dive and are somebody's future work, not this project's.
