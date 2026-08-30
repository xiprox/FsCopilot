# Reach

    Purpose:    Which cockpit surfaces pointer forwarding can address, and which it cannot.
    Depends on: 01-problem, 02-approach
    Decides:    the surface taxonomy, and which questions gate which aircraft

> **Amended by the build.** The WASM row is settled and it is **unreachable**.
> The module's hover is owned by the simulator's cockpit raycast against the
> physical cursor; injected `WASM_MOUSE_MOVE` never writes it, and
> `WASM_MOUSE_DOWN` presses the current hover regardless of the coordinates it
> carries. Three timings were tried, including same-tick. See "Same-tick
> injection loses too" in [build/log.md](build/log.md).

The single most useful thing to know about this project is that "does it work in aircraft X"
is not one question. There are five kinds of surface, they fail for different reasons, and
pointer forwarding reaches three of them outright, one conditionally, and one not at all.

## The taxonomy

| Surface | Today | With pointer forwarding | Gated by |
| --- | --- | --- | --- |
| Classic HTML gauges | works | works | — leave on the existing scheme |
| React over HTML DOM | fragile | expected to work | Q01, Q03 |
| React over SVG | dead | expected to work | Q01, Q03 |
| Canvas | dead | expected to work | Q01, Q03 |
| WASM gauge | dead | **unreachable** | Q05 — closed, negative |
| External-app iframe | dead | **unknown** | Q06 |
| Toolbar / in-game panels | dead | dead | never reach `VCockpitPanel` |

"Expected to work" means the mechanism is understood and nothing known stands in the way. It
does not mean tested. Q03 is the single probe that converts three rows from expected to known.

## Same-document surfaces

React-over-SVG, canvas and plain HTML all live in the panel document, and there is no boundary
between the capture point and the handler. This is the class the approach was designed for and
the class where a negative result would kill it.

Test target is the A220 `CTP`: small, React over SVG, and its failure mode is fully understood,
so a negative result is diagnostic rather than mysterious.

## WASM gauges — Q05

The A350's displays are `<wasm-instrument>` wrapping an `<img>` fed by a live-view texture.
Whether a DOM event reaches the module depends entirely on what `WasmInstrument.html` does,
and that is a core sim file that is streamed rather than installed, so it cannot be read from
disk. It *can* be read from the Coherent debugger, which is what `probes/p01-wasm-shell.js`
does.

Three outcomes, in descending order of how much we want them:

1. **The shell forwards coordinates over a named channel** (CommBus, `Coherent.trigger`). Then
   we call that channel directly, skip DOM synthesis entirely, and get exact semantics. Best
   case by a distance.
2. **The shell listens to the DOM and forwards.** Then synthetic pointer events work and the
   approach generalises with no special casing.
3. **The shell is a display wrapper and nothing more.** Then the sim routes cockpit input to
   the gauge natively, no DOM-level scheme ever reaches it, and A350 / A400M / PMDG are out of
   range permanently.

This one question is the difference between fixing two aircraft and fixing five.

## Iframes — Q06

Two separate problems, and the second is the harder one.

**Reach.** A cross-origin child (`coui://` parent, `http://localhost:8083` child, as the Fenix
EFB is) exposes a `null` `contentDocument` under normal same-origin policy. Coherent GT is
historically permissive and `coui://` may be privileged; untested either way.

**Capture location.** Even for a *same-origin* child, a real click inside the iframe is
dispatched into the child's document and the parent never sees it. Capture has to run inside
the child regardless. For a same-origin child that is a recursive descent — walk
`contentDocument`, attach there, forward through the parent's channel, and extend the routing
key with a frame path (see [05-integration](05-integration.md)). For a cross-origin child it
requires either permissive SOP or the addon developer's cooperation.

Worth weighing before spending much on this: EFBs are frequently *deliberately* unsynced.
PMDG's profile ignores `PMDGTablet`, and each pilot generally wants their own charts. The Fenix
EFB may not be worth the work even if the answer is yes.

The TDS GTN 750 is an open question of its own (Q07) — `TDSGTNXiFlightSimEXE` runs as an
external process, which suggests an iframe onto Garmin's trainer, but it could equally be a
live-view `<img>` like the A350. Same probe answers it.

## Higher layers than the DOM

Worth stating so it is not re-derived: there is no generic layer above DOM events.

| Layer | Reach | Generic? |
| --- | --- | --- |
| DOM pointer events | anything whose UI logic runs in Coherent | **yes** |
| The instrument's own message channel | exact semantics, no guessing | no — per addon |
| Sim input system (`H:` / `K:` / `L:`) | model-behaviour controls | already covered by profile definitions; the gap *is* everything with no simvar footprint |
| OS input injection | everything | non-starter — fights the local cursor, needs the view aimed at the panel |

The second row is where the Q05 best case lives, and it is the escape hatch for anything that
crosses a process boundary. It is per-addon by nature, which is the opposite of what this
project is for — so it stays an escape hatch, not a strategy.
