# The problem

    Purpose:    Why FS Copilot's interaction sync fails, per surface, mechanically.
    Depends on: nothing
    Decides:    what a replacement has to do differently, and what it does not

FS Copilot already mirrors cockpit-display input between two pilots. It works on classic
HTML gauges and fails on most of what people actually fly now. This part says exactly why,
because the failures have four different mechanisms and only three of them are fixable by
the same change.

The full trace through the existing system — every hop, every gate, the whole signal path —
is the deep dive at
<https://claude.ai/code/artifact/4482196f-1f88-4dc4-bb74-feaf386eeac2>. This file is only
the part that motivates the work.

## What it does today

One `Hook` per instrument, created in the patched `VCockpit.js`. On a left-button `mouseup`
anywhere in the panel document it walks up from `ev.target` until it finds an element it has
named, sends that name to the peer, and the peer dispatches a synthetic
`mousedown` + `mouseup` + `click` on the element with the same name.

The name is positional: the element's path of `TAG:siblingIndex` up to the root, hashed.
Four properties follow, and every failure below is one of them.

- It is a path, not an identity — anything that reorders siblings renames the element.
- It is computed once at first sight and cached forever, so a node the framework later moves
  keeps a name that no longer describes where it is.
- Each machine computes it at *its own* insertion moment, so divergent render histories
  produce divergent names for the same button.
- Only `HTMLElement`s are named at all.

## The four failure mechanisms

**Named nothing useful.** `_initElement` gates on `el instanceof HTMLElement`, so no SVG node
is ever named. On a display rendered as React into SVG, the ancestor walk climbs the entire
tree and terminates on the mount `div`. Every click on the display resolves to the same name.
The message crosses correctly; the replay dispatches a click on a container with no handler.
*Verified: A220 `DisplayUnits` and `CTP` — `react-dom` `createRoot`, `createElementNS`,
`onClick` and `onPointerDown`.*

**Nothing to name.** A display drawn to a `<canvas>` has no per-control nodes at any point.
No naming scheme of any kind can address its buttons.
*Suspected: A220 `MKP`, `FCP`, `ISI` — no React, heavy canvas use in the bundle. Needs
confirming against the live DOM.*

**Nothing behind the name.** A WASM gauge's panel document is
`<wasm-instrument>` → `<div id="Mainframe">` → `<wasm-sim-canvas>` → `<img src="WasmGaugeLiveView_25">`.
Those are all `HTMLElement`s, so they *are* named and FS Copilot *does* emit interactions for
them — the replay lands on an `<img>` that is a live-view texture blit with nothing behind it.
The instrument carries `data-input-group="WASM-INSTRUMENT"`, which points at the sim routing
input to the gauge natively rather than through Coherent.
*Verified: A350 — every display in `panel.cfg` is `WasmInstrument.html?wasm_module=…`, only
the EFB is real HTML. PMDG's own profile concedes this by putting `WasmInstrument` in
`ignore:`.*

**Never captured.** A real click inside an `<iframe>` is dispatched into the child's document.
The parent's document-level listener never fires. This is not a replay failure — capture does
not happen.
*Verified: Fenix EFB — `<iframe src="http://localhost:8083/index.html">` inside a `coui://`
parent, which is also cross-origin.*

## What this means for a replacement

The first two mechanisms are the same problem wearing different clothes: the DOM has no
stable, addressable identity for the control. **Coordinates have no such requirement** — they
are what a canvas app already uses as its input model, and what an SVG display's handler will
receive anyway once the event reaches the right leaf. That is the case for pointer forwarding,
and it is a strong one.

The third and fourth are different in kind. They are process boundaries, not naming problems,
and coordinates do not cross a process boundary by themselves. Whether they can be crossed at
all is [03-scope](03-scope.md) and the open questions behind it.

## What is not in scope

Bugs found in the existing system while tracing it — the `keyCode` type mismatch that makes
keypress sync inert, the `_id2El` collision overwrite, the `size >` off-by-one in
`receive_gauge_msg`, the missing `return` in `bus.js`. They are recorded in the deep dive.
Fixing them is somebody's future work, not this project's.
