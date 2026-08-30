# The Coherent GT environment

    Purpose:    What the simulator's JavaScript engine actually provides, and how
                to find out anything else about it without guessing.
    Depends on: nothing
    Decides:    what injected code may use, and where MSFS's own source is read

This exists because a design document confidently specified dispatching
`PointerEvent`s, and the constructor does not exist in this engine. That was
discoverable in thirty seconds. This file is those thirty seconds, run once and
written down, plus the tooling to answer the next question of the same shape.

Measured on MSFS 2024, 2026-08-30, in a `VCockpit` panel document.
Re-run `probes/p08-environment.js` after a simulator update — this is exactly the
sort of thing that changes silently.

## The engine

    Mozilla/5.0 (Windows NT 6.2; Win64; x64) AppleWebKit/604.1.38
    (KHTML, like Gecko) Chrome/49.0.2623 Safari/604.1.38 CoherentGT/2.0

**Chrome 49.** Released in 2016. Almost everything below follows from that one
fact, and it is the single most useful thing to remember about this environment:
when in doubt, assume the web of early 2016.

## What injected code may use

The hard constraint, and the one most likely to bite. These are **syntax errors**,
not missing functions — a single `?.` anywhere in an injected file makes the whole
file fail to parse:

| Not available | Use instead |
| --- | --- |
| optional chaining `a?.b` | `a && a.b` |
| nullish coalescing `??` | `x == null ? d : x` |
| logical assignment `\|\|=` `&&=` `??=` | explicit assignment |
| class fields, private `#x` | assign in the constructor |
| `Object.fromEntries` | a `reduce` |
| `Array.prototype.at` | `a[a.length - 1]` |
| `String.prototype.replaceAll` | `split().join()` or a `/g` regex |
| numeric separators `1_000` | `1000` |
| `Promise.allSettled` | `Promise.all` over catch-wrapped promises |

Available and safe: `const` / `let`, arrow functions, template literals,
destructuring, spread, `async` / `await`, generators, `class` (without fields),
`Map` / `Set` / `WeakMap` / `WeakSet` / `WeakRef`, `Proxy`, `Reflect`, `Symbol`,
`globalThis`, `Array.prototype.flat`, `TextEncoder` / `TextDecoder`, `URL`,
`URLSearchParams`, `crypto.getRandomValues`.

## Web APIs

**Missing, and relevant to this project:**

| Missing | Consequence |
| --- | --- |
| `PointerEvent` | Replay dispatches `MouseEvent` only. Any framework handler bound to `onPointerDown` — the A220 has several — is dead code in the simulator. |
| `DragEvent` | HTML5 drag-and-drop cannot be synthesised. Drag must be modelled as mousedown/mousemove/mouseup. |
| `navigator.maxTouchPoints`, `setPointerCapture` | No touch or capture model at all. `TouchEvent` exists as a constructor but nothing reports touch support. |
| `document.elementsFromPoint` (plural) | Only the topmost element at a point is available. No hit-test stack. |
| `IntersectionObserver`, `ResizeObserver` | Visibility and size changes must be polled. |
| `Node.prototype.isConnected` | Use `document.contains(node)`. |
| `AbortController` | No cancellation for `fetch`; use `XMLHttpRequest.abort`. |
| `Worker`, `structuredClone`, `BroadcastChannel` | Everything runs on the one thread, and cross-document messaging is `postMessage` only. |
| `requestIdleCallback`, `queueMicrotask` | Use `setTimeout` and `Promise.resolve().then`. |
| `performance.getEntriesByType` | Resource enumeration comes from the inspector, not from the page. |
| `indexedDB`, `caches` | `localStorage` and `sessionStorage` are the only storage. |
| `Intl`, `BigInt`, `FinalizationRegistry` | Format numbers by hand. |

**Present, and worth knowing:**

`MutationObserver` (the basis of the reaction measurement in `p03`),
`document.elementFromPoint` (what coordinate replay depends on), `customElements`,
`ShadowRoot` and `attachShadow`, `getComputedStyle`, `fetch`, `XMLHttpRequest`,
**`WebSocket`**, `EventSource`, `localStorage`, `requestAnimationFrame`,
`MessageChannel`, `SharedArrayBuffer`.

`WebSocket` being present is the interesting one — it makes Q08 in
[06-open-questions](06-open-questions.md) a question about network policy rather
than about capability.

## MSFS globals

Present on `window`: `Coherent`, `SimVar`, `Include`, `RegisterViewListener`,
`RegisterCommBusListener`, `Avionics`, `Simplane`, `GameState`, `EDITION_MODE`,
`Utils`, `checkAutoload`, `diffAndSetAttribute`, `diffAndSetStyle`,
`StyleProperty`.

**A probe caveat worth remembering:** `BaseInstrument` appears "missing" when
tested as `window.BaseInstrument`, yet `BaseInstrument.js` is loaded and the class
plainly works. A top-level `class X {}` creates a binding in the global *lexical*
environment, which is not a property of `window`. Test class declarations with
`typeof X !== "undefined"`, never with a property lookup — otherwise the answer is
confidently wrong.

`Coherent` itself exposes roughly fifty members. The ones that matter here are
`call`, `trigger`, `on`, `off`, and the `_Register` / `_Unregister` pair; the rest
is Coherent's data-binding model, unused by cockpit code.

## Reading the simulator's own source

MSFS 2024 streams its core packages rather than installing them, so the JavaScript
that runs the cockpit is not on disk anywhere. It is all loaded into panel
documents, and the inspector will hand it over.

    npm run sources -- <page> list           what a document loaded
    npm run sources -- <page> grep <text>    full-text search across all of it
    npm run sources -- <page> get <fragment> print one file
    npm run sources -- <page> dump           save every script to sim-sources/

A single `VCockpit` panel loads around 19 scripts and 4.5 MB, including
`BaseInstrument.js`, `VCockpit.js`, `WasmSimCanvas.js`, `simvar.js`, `coherent.js`,
`XMLLogic.js` and the aircraft's own bundles. Different panel types load different
sets, so dump more than one.

Dumps land in `sim-sources/`, which is gitignored — it is Asobo's code, kept
locally for grepping rather than committed.

### Inspector domains that work

Established by probing; there is no published list for this build.

| Domain | Notes |
| --- | --- |
| `Runtime.evaluate` | Arbitrary JavaScript, returns values. The workhorse. |
| `Console.enable` | Pushes `Console.messageAdded`. The page-to-us channel behind [08-testbed](08-testbed.md). Replays its backlog on connect. |
| `Page.getResourceTree` / `getResourceContent` | Enumerate and read everything a document loaded. |
| `Page.searchInResources` | Full-text search. Its index can miss things, so `sources grep` also scans directly. |
| `Debugger.enable` | Accepted. Breakpoints and `scriptParsed` unexplored. |
| `Network.enable`, `CSS.enable`, `Heap.enable`, `Worker.enable`, `Canvas.enable`, `ApplicationCache.enable` | Accepted, unexplored. |
| `DOM.enable`, `Timeline.enable` | **Not present** in this build. |

The connection is `ws://127.0.0.1:19999/devtools/page/<id>`, and `/pagelist.json`
lists documents with the title `VCockpitNN - <instrumentIdentifier>`. Page ids
change between sessions; select by title.

**One operational trap, already paid for once:** confirm the process owning port
19999 is *alive* before drawing conclusions from its behaviour. An orphaned
listening socket — MSFS's port inherited by an addon's external process that
outlived it — accepts connections and answers nothing, which is indistinguishable
from a server refusing your protocol.
