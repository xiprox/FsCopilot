# Pointer forwarding

Mirroring cockpit-display input between two pilots by forwarding **where the pointer went**
rather than **what it hit** — so that sync stops depending on the display having addressable
DOM elements, which most modern ones do not.

## How this record works

It is in two halves with different lifetimes, and knowing which is which matters more than
anything else here.

**The design record** — `01-10` — is what the work *is*, including the parts that turned out
to be dead ends. It is stable. It is amended
only by a pointer line at the top of a file naming the log entry that overturned something,
**never by silent rewriting**, because a design doc that quietly changed is worse than one
that is openly out of date. The one exception is
[06-open-questions](06-open-questions.md), which is a register and is updated in place.

**The build record** — `build/` — is what is actually being *run*, in what order, and what the
running has found. It is live and it moves.

> **Picking this up fresh?** The mechanism is proven and a working prototype exists.
>
> A synthetic mouse click at forwarded coordinates drives a React/SVG cockpit display, and a
> Community package captures cockpit input and carries it to a local process over a WebSocket
> — no WASM, no SimConnect, no CommBus, and it works with DevMode off. Capture is verified
> lossless at 33/33.
>
> Read [build/plan.md](build/plan.md) for where each stage stands, then the top of
> [build/log.md](build/log.md), then [10-module](10-module.md) for what actually runs. Only
> then reach for the rest of the design record, and treat any of it as amended by the log
> where the two disagree.
>
> Two things to know before forming a plan. **WASM-rendered displays are permanently out of
> reach** — [07-wasm-surface](07-wasm-surface.md) says why, and it is not a matter of trying
> harder. And **anything you write that runs inside a panel is Chrome 49**, where a stray `?.`
> takes out the whole file — read [09-environment](09-environment.md) before writing a line.
>
> Drag gestures work — tier 3 is proven in the cockpit, with a bounded drift caveat tracked
> as Q10. What is left is two-machine testing, and the work has moved to graduation: the
> production design for folding this into FS Copilot itself is decided and recorded in
> [11-fsc-implementation-plan](11-fsc-implementation-plan.md).

Each design part is self-contained. The descriptions below name the decisions inside, so you
can tell which parts those are without opening anything. Every part opens with three lines —
`Purpose`, `Depends on`, `Decides` — so a file can be judged before it is read in full.
Cross-links are deliberate and sparse: following one should be a choice, not a chain that
drags the whole proposal into context.

## Build

| File | What is in it |
| --- | --- |
| [build/runsheet.md](build/runsheet.md) | The order to run probes in during one sim session, which panel each needs selected, and what to save. Use this when you are about to fly. |
| [build/plan.md](build/plan.md) | **Start here.** The seven stages, what each one answers, its exit criterion, where it stands. Stage 1 is the one that decides whether any of this is real. Working notes on how to probe without wasting sim sessions. |
| [build/log.md](build/log.md) | What the probes found and what changed as a result, newest first. Including approaches tried and abandoned, which is the part git history cannot tell anyone. |

## Design

| Part | What is in it |
| --- | --- |
| [01-problem](01-problem.md) | Why the existing scheme fails, per surface, mechanically. Four different failure modes — named nothing useful, nothing to name, nothing behind the name, never captured — and why only three of them are the same problem. |
| [02-approach](02-approach.md) | The mechanism. What is captured and in what tiers, why coordinates are normalised against the instrument's own rect, the exact replay sequence and why it needs real `PointerEvent`s, and why this is opt-in rather than an automatic fallback. |
| [03-scope](03-scope.md) | The surface taxonomy — which of five kinds of cockpit display this reaches, conditionally reaches, or cannot. The two iframe problems. Why there is no generic layer above DOM events. |
| [04-transport](04-transport.md) | The 512-byte single-slot bus, three ways to widen it, and the case for bypassing it with a local WebSocket. The schema handshake that makes every wire change a hard compatibility break. |
| [05-integration](05-integration.md) | Routing key, profile shape, how config reaches a panel that came up late, where experimental code lives and why not in a second package, naming, and what graduating into FS Copilot would mean. |
| [07-wasm-surface](07-wasm-surface.md) | **Closed, negative.** What a WasmInstrument display is made of, the full `WASM_MOUSE_*` channel table, why injected coordinates are discarded while the press latches, the cursor architecture read out of an unstripped binary, and the three things that would reopen it. |
| [10-module](10-module.md) | **The prototype, and how to build it.** A Community package of HTML and JS only — no WASM, no SDK. How our loader gets into every panel, why the patch is smaller than FS Copilot's and fixes one of its bugs, and the boundary that kept swapping transports cheap. |
| [08-testbed](08-testbed.md) | How the mechanism is exercised end to end with **nothing installed in the simulator** — the inspector injects and the console reports back. Why not a modified `fscopilot-bridge`, and the one rule that keeps the result portable. |
| [09-environment](09-environment.md) | **Read before writing injected code.** Coherent GT is Chrome 49: no `PointerEvent`, no optional chaining, no `??`. What is missing, what is present, and how to read the simulator's own source instead of guessing at it. |
| [06-open-questions](06-open-questions.md) | The register. Ten questions grouped by what they gate, each with the probe that would settle it and what the answer changes. Updated in place. |
| [11-fsc-implementation-plan](11-fsc-implementation-plan.md) | **The adopted production plan.** How pointer forwarding lands inside FS Copilot itself: WebSocket sidecar on ports 9020–9024, `PointerPress`/`PointerDrag` peer packets with sequence numbers, the `pointer:` profile key, gap-replay with a two-mode history buffer, and the blue-lock/red-warning overlay policy. Supersedes parts of 04 and 05, called out inside. |

## Probes

One file per question. `.mjs` runs under Node from the repo root; `.js` is pasted whole into
the Coherent GT debugger console with the correct panel selected in the frame picker — every
panel is its own document and they see different things.

Raw output goes in `results/`, committed. `build/log.md` is the reading of it; `results/` is
the evidence, and a later session will want to re-read rather than trust a summary.

## Names

Settled, because the surrounding projects have already claimed several obvious words
(`bridge` is FS Copilot's package name, `link` is fsc-editor's, `hook` and `interact` are
existing FS Copilot concepts):

| Name | What it refers to |
| --- | --- |
| **Pointer forwarding** | The whole approach — coordinates instead of element identity. |
| **Capture** | Turning a real cockpit click into a message. |
| **Replay** | Turning a message back into a synthetic press on the peer. |
| **Surface** | One kind of display, by how it renders: HTML, SVG, canvas, WASM, external iframe. |
| **Probe** | One script that answers one question. Numbered, `pNN`. |
| **Tier** | How much of a gesture is forwarded — press, press-and-hold, drag. |
| **Shell** | `WasmInstrument.html`, the core sim file wrapping a WASM gauge. |
