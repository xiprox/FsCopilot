# fsc-pointer-playground

Experiments toward a **pointer-forwarding** interaction sync for FS Copilot — mirroring
cockpit-display input between two pilots by forwarding pointer coordinates instead of DOM
element identities.

Nothing here ships. This is where the questions get answered and the answers get written
down. If a prototype works, folding it into `fscopilot` is a separate piece of work.

**[docs/index.md](docs/index.md) is the map.** It explains how the record is split and what
to read first. Read it before anything else in this repository.

## Status

Almost nothing is verified. The design record describes a proposal; no part of pointer
forwarding has been tested against a running simulator.
[docs/build/plan.md](docs/build/plan.md) has the real state.

## Commands

```bash
npm run probe:00
```

Node probes take no dependencies — Node 22 runs them directly. Console probes are pasted
whole into the Coherent GT debugger, with the correct panel selected in the frame picker.

## Layout

| Path | What lives there |
| --- | --- |
| `docs/index.md` | The map. Start here. |
| `docs/01-06` | The design record — the proposal. Stable, amended by pointer only. |
| `docs/build/` | The build record — what is being run and what it found. Live. |
| `probes/` | One file per question. `.mjs` runs under Node, `.js` is pasted into the debugger console. |
| `results/` | Raw probe output, committed. The evidence behind the log. |
