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
**Status:** ANSWERED, fully · **Probes:** `probes/p02-capture.js`, `probes/p09-drag-delivery.js` · mouse events only; `PointerEvent` does not exist in Coherent GT

**`mousemove` is delivered while a button is held**, which was the part still outstanding and
the one that gated drag. Confirmed by drag working end to end in the cockpit — see "Drag
works" in [build/log.md](build/log.md). `p09` is the instrument that re-answers this in
isolation if drag ever regresses; it separates a delivery fault from a threshold fault in one
cockpit pass.

FS Copilot only ever listens for `mouseup`, so everything else is unverified. The A220 binds
`onPointerDown`, which is suggestive but not proof — React may be synthesising pointer events
from mouse events.

**Changes:** whether press-and-hold and drag are capturable at all, and whether the replay
sequence needs real `PointerEvent`s.

### Q02 · Are cockpit clicks delivered as trusted events?
**Status:** ANSWERED yes · **Probe:** `probes/p02-capture.js` · real input is trusted; VCockpit.js's own synthetic enter/leave at (0,0) are not, which makes `isTrusted` a usable second loop breaker

**Changes:** whether `ev.isTrusted` is usable as a second loop breaker alongside `selfEmit`.

### Q03 · Does a synthetic pointer sequence at coordinates drive a real React/SVG display?
**Status:** ANSWERED YES · **Probe:** `probes/p03-replay.js` · **Answered by** "Q03 PASSES" in [build/log.md](build/log.md)

The core of the proposal. Target: A220 `CTP`.

**Now the only question that matters.** With Q05 closed negative, the WASM surface is gone and
everything this project can still deliver sits behind this one answer. Note the WASM failure
does **not** predict it: there the simulator owned the hit-target and discarded our
coordinates, whereas a React or canvas display does its own hit-testing inside the panel
document with nothing in between.

**Changes:** everything. A negative result kills the approach for the class of aircraft it was
designed for, and there is no second idea behind it.

### Q04 · Do the two machines agree on the instrument element's bounding rect?
**Status:** DEFERRED, assumed yes · no second machine available

The measured DisplayUnits rect is exactly `7410 x 1110`, which is the `pixel_size` line
from the aircraft's `panel.cfg`. If the rect is panel.cfg-derived it is identical on any
machine running the same addon build, which is the case that matters. Taken as an
assumption rather than a result, and normalising against the rect costs nothing either
way, so a wrong assumption here degrades gracefully.

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
**Status:** ANSWERED YES · **Probes:** `probes/p05-server.mjs` + `probes/p05-websocket.js` · full duplex confirmed from both ends, and **confirmed working with DevMode off**, which makes it shippable rather than merely convenient.

The Fenix EFB proves outbound HTTP to loopback works from a cockpit document. WebSocket is
untested. [04-transport](04-transport.md) has what it buys.

**Changes:** whether the 512-byte single-slot bus is a constraint to design around or one to
delete.

### Q09 · Does the existing bus survive a burst?
**Status:** open, moot if Q08 lands · **Probe:** none yet

Whether two `SetClientData` calls in one frame both arrive, or coalesce.

---

## Gate E — fidelity

### Q10 · Why do replayed drags drift from the original gesture?
**Status:** open, deliberately deferred · **Probe:** none yet

Drag works, but a replayed drag sometimes does not land where the captured one did. Deferred
on the reading that single-machine replay — the same rect, the same panel, capture and replay
racing in one document — is the likeliest source, and that the case which actually matters is
two machines, which cannot be tested until stage 6. Revisit if it shows up in real use.

One candidate was visible in the code and is **not** the single-machine explanation, so it
would survive to two machines. `replayDrag` rebuilds timing by summing per-step intervals
clamped to `DRAG_MAX_STEP_MS` (250ms), so replay is *shorter* than capture whenever any gap
between samples exceeded that. Such gaps are not exotic: `DRAG_MIN_STEP_PX` (2) gates out
sub-2px motion, so a slow deliberate pan emits sparse samples and can exceed 250ms between
them.

**Measured, and it is real but small.** `npm run drift` (`probes/p10-drag-drift.mjs`) replays
the exact loop from `agent.js` over the nine real drags in `recordings/`, needing no simulator:
replay runs **0–12% short**, worst case 186ms lost on a 1534ms gesture, and seven of the nine
lose under 4%. Almost every gesture has exactly one clamped gap, which is the pilot pausing
after mousedown before moving — the natural hesitation at the start of a pan.

So this is not enough on its own to explain a visible drift, and it is **temporal, not
spatial**: path coordinates are replayed verbatim, so a replayed drag's endpoint is exact. It
only becomes spatial if the receiving display does velocity, easing or inertia work on the
path. That makes it a real but secondary suspect, and it argues for the single-machine
explanation being the main one — as assumed.

Evidence: `results/p10-drag-drift-2026-08-30.txt`.

**Changes:** nothing yet. Fidelity, not viability — the mechanism is proven either way.

---

## Settled

- **Q00 — yes.** MSFS hosts a WebKit inspector on 127.0.0.1:19999; probes run over the wire.
- **Q05 — no, permanently.** WASM displays cannot be driven by injected input. See
  [07-wasm-surface](07-wasm-surface.md).
- **Q01 — mouse events only.** `PointerEvent` does not exist in Coherent GT.
- **Q02 — yes.** Real cockpit input is trusted; VCockpit.js's own synthetic events are not.
- **Q03 — YES.** A synthetic mouse click at captured coordinates opened a dropdown on the
  A220's React/SVG DisplayUnits. The approach works on the surface it was designed for.
- **Q08 — YES, and with DevMode off.** A `coui://` document holds a WebSocket to loopback,
  which is what deletes the CommBus/WASM/SimConnect pipeline from the design.
- **Drag — yes.** `mousemove` is delivered under a held button; tier 3 captures and replays.
  Fidelity is imperfect and tracked as Q10 above.

---

## Explicitly out of scope

Fixing existing FS Copilot bugs found during the deep dive — the `keyCode` type mismatch, the
`_id2El` collision overwrite, the `size >` off-by-one, the missing `return` in `bus.js`. They
are recorded in the deep dive and are somebody's future work.
