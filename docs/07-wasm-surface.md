# The WASM surface

    Purpose:    Everything learned about WasmInstrument displays, and why they
                cannot be driven by injecting input.
    Depends on: 03-scope
    Decides:    that this surface is closed, and what would have to change to open it

Written after the surface was closed, so that nobody re-derives it. The A350, A400M,
PMDG and every other display declared as
`WasmInstrument/WasmInstrument.html?wasm_module=…&wasm_gauge=…` is out of reach for
pointer forwarding. This is *why*, in enough detail to recognise a future change.

Evidence is in [build/log.md](build/log.md) under the three 2026-08-30 WASM entries.

## What a WASM display is made of

```
<wasm-instrument data-input-group="WASM-INSTRUMENT" guid="94"
                 url="coui://…/WasmInstrument.html?wasm_module=inibuilds-A350.wasm&wasm_gauge=MFD">
  <div id="Mainframe">
    <wasm-sim-canvas>
      <img src="WasmGaugeLiveView_9">
```

Every node is an `HTMLElement`, including the custom elements — so the existing
element-naming scheme *does* name them and *does* emit interactions for them. The
`<img>` is a live-view texture the module renders into. There is no per-control node,
and never will be.

`WasmInstrument.html` is 379 bytes and imports two core scripts. The one that matters
is `coui://html_ui/JS/WasmSimCanvas.js`, 6778 bytes, readable through the inspector.

## The input channel

`WasmSimCanvas.connectedCallback` binds nine listeners **on the canvas element
itself** and forwards each to the simulator:

| DOM event | Coherent call | Arguments |
| --- | --- | --- |
| `mousedown` | `WASM_MOUSE_DOWN` | guid, clientX, clientY, button |
| `mouseup` | `WASM_MOUSE_UP` | guid, clientX, clientY, button |
| `click` | `WASM_CLICK` | guid, clientX, clientY, button |
| `dblclick` | `WASM_DBL_CLICK` | guid, clientX, clientY, button |
| `mousemove` | `WASM_MOUSE_MOVE` | guid, clientX, clientY, button |
| `mousewheel` | `WASM_MOUSE_WHEEL` | guid, clientX, clientY, deltaY |
| `mouseover` | `WASM_MOUSE_OVER` | guid, clientX, clientY, button |
| `mouseenter` | `WASM_MOUSE_ENTER` | guid |
| `mouseleave` | `WASM_MOUSE_LEAVE` | guid |

Three properties of this worth knowing:

- **Only mouse events.** No `PointerEvent` listeners at all. Anything targeting this
  surface must dispatch `MouseEvent`s.
- **`mousemove` is deduplicated** against the last forwarded position, so identical
  coordinates are dropped before they reach `Coherent.call`.
- **Middle and right buttons are special-cased.** `OnMouseDown`/`OnMouseUp` track
  button state for buttons 1 and 2 and synthesise `WASM_CLICK` themselves, calling
  `preventDefault` on the middle button.

## What the simulator does with it

This is the part that closes the surface, and it is not visible from the JavaScript.

Synthetic `MouseEvent`s dispatched anywhere in the canvas subtree bubble to the
canvas, and `Coherent.call` fires with our coordinates intact — verified by wrapping
`Coherent.call` and reading the arguments. The events are genuinely delivered.

**But the coordinates are discarded on the far side.**

- `WASM_MOUSE_DOWN` / `WASM_CLICK` are honoured, and mean *"press whatever is
  currently hovered"*. The coordinates they carry select nothing.
- `WASM_MOUSE_MOVE` does not move anything. 61 calls driven to the far corner of a
  display, all confirmed delivered, moved neither the hit-target nor the cursor the
  module draws.

Hover is established solely by the simulator's own cockpit raycast against the
**physical mouse**. Nothing in the DOM writes it.

Three timings were tried and all three behaved identically: a bare click; a realistic
approach of moves with a 400 ms dwell; and a same-tick sequence with no yield at all,
in case a per-frame raycast was overwriting an injected position. In each case the
control under the real cursor fired, up to 120 px from where every forwarded event
said to press.

### The press latches

The most consequential detail. An injected press delivered while nothing is hovered
is **not discarded** — it waits. When the real cursor is next moved onto a control,
that control fires immediately, with no new input.

So a sync built on this channel would not merely miss. It would press whatever the
receiving pilot happens to be pointing at, possibly seconds later, with no visible
cause. That is strictly worse than doing nothing.

> This also describes what FS Copilot does **today** on these panels. Its replay
> constructs `new MouseEvent(type, {bubbles, cancelable})` with no `clientX`/`clientY`,
> which reaches the canvas and becomes a real press against the receiving pilot's
> hover. Adding `ignore: WasmInstrument` to affected profiles is a safety fix
> independent of this project. PMDG's profile already does it.

## The cursor

The obvious escape — don't inject hover, *sync the cursor that owns it* — was
investigated and does not exist.

`inibuilds-A350.wasm` ships **unstripped**, so its symbol table is readable with
`strings`. It describes the cursor architecture directly:

```
DrawCursorCPT(NVGcontext*, float, float, bool)
DrawCursorFO(NVGcontext*, float, float, bool)
SwitchCaptainCursor(int)
SwitchFOCursor(int)
CursorLock / CursorUnlock / CursorHint
```

Two cursors, drawn at explicit float coordinates, able to switch between screens. Of
**1697** `INI_` variables in the binary (dumped to `results/a350-ini-vars.txt`),
exactly two concern the cursor:

- `INI_CPT_CURSOR_SCREEN` — which display the captain's cursor is on
- `INI_FO_CURSOR_SCREEN` — the same for the first officer

There is no X or Y variable. Position is not exposed at all. And the screen variables
are **outputs**: writing `INI_CPT_CURSOR_SCREEN` reads straight back as its previous
value, because the module re-asserts it.

The aircraft's behaviour XML contains no KCCU input events either — the `<Cursor>Hand</Cursor>`
entries there are MSFS's mouse-pointer *shape* hints, unrelated.

So the cursor is: drawn by the module, positioned from the physical mouse, reported
through a read-only variable, and writable through nothing.

## Is this iniBuilds-specific?

No, and this is the important generalisation. `WasmSimCanvas.js` is a **core simulator
file**, and the discarding of coordinates happens on the simulator's side of
`Coherent.call`, not inside the aircraft. The A350 was the aircraft on hand; the
finding belongs to `WasmInstrument` as a mechanism.

The cursor findings above *are* iniBuilds-specific in their detail — another vendor
may expose different variables — but the input path they sit on is shared, so a vendor
exposing a writable cursor position would be the exception rather than the fix.

## What would reopen this

One of:

1. **A channel that writes the simulator's cockpit hover or cursor position.** None is
   known. Every candidate found (`ShowVirtualMouse`, `Coherent.on("OnMouseEnter",
   _target, _x, _y)`, the `Raycast` channel in `VCockpit.js`) is simulator-to-JavaScript.
   Whether any accepts traffic in the other direction is unanswered and is a question
   about the simulator's input system, not about the DOM.
2. **A per-addon channel.** A vendor exposing their own control-level API — an `L:` var
   per softkey, a CommBus command — sidesteps the cursor entirely. This is the
   escape hatch in [03-scope](03-scope.md), and it is per-aircraft work rather than a
   general mechanism.
3. **Asobo changing `WASM_MOUSE_*` to honour its own coordinates.** The arguments are
   already in the call signature and already carry the right values. Nothing on the
   JavaScript side would have to change. This is worth an SDK feature request: the
   channel is one behaviour change away from making every WASM display syncable.
