# fsc-pointer-playground

Experiments toward a **pointer-forwarding** interaction sync for FS Copilot — mirroring
cockpit-display input between two pilots by forwarding pointer coordinates instead of DOM
element identities.

Nothing here ships. This is where the questions get answered and the answers get written
down. If a prototype works, folding it into `fscopilot` is a separate piece of work.

**[docs/index.md](docs/index.md) is the map.** It explains how the record is split and what
to read first. Read it before anything else in this repository.

## Status

**The mechanism works.** A synthetic mouse click at forwarded coordinates drives a
React/SVG cockpit display, and a real Community package now captures cockpit input and
carries it to a local process over a WebSocket. WASM-rendered displays are permanently out
of reach, for reasons that are understood and documented.

[docs/build/plan.md](docs/build/plan.md) has the real state.

## Commands

```bash
npm run module:build     install the prototype package (restart MSFS after)
npm run host             the local process panels connect to
```

Then, for inspecting a running simulator:

```bash
npm run pages            every inspectable panel document
npm run probe            run a probe in one
npm run eval             evaluate an expression in one
npm run sources          read MSFS's own JavaScript out of the engine
```

Nothing here has dependencies — Node 22 runs it all directly.

## Layout

| Path | What lives there |
| --- | --- |
| `docs/index.md` | The map. Start here. |
| `docs/01-06` | The design record — the proposal. Stable, amended by pointer only. |
| `docs/build/` | The build record — what is being run and what it found. Live. |
| `probes/` | One file per question. `.mjs` runs under Node, `.js` is pasted into the debugger console. |
| `results/` | Raw probe output, committed. The evidence behind the log. |
