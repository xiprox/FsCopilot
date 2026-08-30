# fsc-pointer-playground

Experiments toward a **pointer-forwarding** interaction sync for FS Copilot — mirroring
cockpit-display input between two pilots by forwarding pointer coordinates instead of
DOM element identities.

Nothing here ships. This is where the questions get answered and the answers get written
down; if a prototype works, it graduates into `fscopilot` as a separate piece of work.

## Why

FS Copilot's current interaction sync names each clickable element by a hash of its
sibling-index path through the DOM, sends that name to the peer, and replays a synthetic
click on the element with the same name. It works for classic HTML gauges and fails for
everything else:

| Surface | Why it fails today |
| --- | --- |
| React over SVG (A220 `DisplayUnits`, `CTP`) | SVG nodes are skipped when ids are assigned, so every click resolves to the React mount `div` |
| Canvas (A220 `MKP`, `FCP`, `ISI`) | There are no per-control nodes to name |
| WASM gauge (A350, A400M, PMDG) | The DOM is a live-view `<img>`; nothing behind it consumes DOM events |
| External-app iframe (Fenix EFB, likely TDS GTN) | Clicks are dispatched into the child document; the parent's listener never fires |

Coordinates sidestep the naming problem entirely for anything whose UI logic runs in the
Coherent DOM. They do **not** cross a process boundary on their own, which is what the
WASM-gauge and iframe questions are about.

Background: the FS Copilot deep dive at
<https://claude.ai/code/artifact/4482196f-1f88-4dc4-bb74-feaf386eeac2>.

## Layout

| Path | What lives there |
| --- | --- |
| `docs/questions.md` | The register. Every open question, what would settle it, current status. Read this first. |
| `docs/findings.md` | Dated log of what each probe actually returned, and what it means. |
| `probes/` | One file per probe. `.mjs` runs under Node; `.js` is pasted into the Coherent GT debugger console. |
| `results/` | Raw probe output, committed. The evidence behind `findings.md`. |

## Running a probe

Node probes, from the repo root:

```bash
npm run probe:00
```

Console probes are pasted whole into the Coherent GT debugger's Console tab, with the
**correct panel selected in the frame picker** — every panel is its own document and they
see different things. Each one prints a report and stashes the full data on a `window.__PNN`
global for follow-up. Save the output into `results/` under the probe's name and a date.

## Conventions

Anything of ours that runs inside the simulator uses an `FSCPP_` prefix — never `FSC_`.
`fscopilot-bridge` and `fsc-editor-link` both live in the same Community folder and speak on
the same buses; colliding with either would be our fault, not theirs.

Where a probe needs code inside a panel document, prefer dropping it into the existing
`fscopilot-bridge` package's `html_ui/` folder over shipping a second package that patches
`VCockpit.js`. Two packages patching the same core file is a conflict, and bridge JS needs
no build step — edit, reload the panel, done.
