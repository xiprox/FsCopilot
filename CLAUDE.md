# fsc-pointer-playground

Experiments toward pointer-forwarding interaction sync for FS Copilot. `README.md` is the
product-level description; **[docs/index.md](docs/index.md) is the map and you should read it
before working here.** This file is for working in the repo.

## Before you do anything

Read [docs/build/plan.md](docs/build/plan.md), then the top of
[docs/build/log.md](docs/build/log.md). Between them they say what has actually been run and
what it found. The design record in `docs/01-06` is a **proposal** — treat any of it as
amended by the log where the two disagree, and do not assume something works because a design
doc describes it.

## The related repositories

Neither is being built on. Both are read for reference and neither should be modified from
here.

| Path | What it is |
| --- | --- |
| `../fscopilot` | FS Copilot itself. The `src` submodule is somebody else's project — this work targets an upstream PR eventually, so changes there stay minimal and in-idiom. |
| `../fsc-editor` | A profile editor with its own WASM module (`link/`) and heavy sim communication. The reference for how to build and ship a Community package, and the source of this repo's doc convention. |

## Working here

**Record findings as they happen**, in `docs/build/log.md`, not at the end. A probe result
that changes scope is worth more than the code written that day. Save the raw output into
`results/` and commit it — the log is the reading, `results/` is the evidence.

**Amend design docs by pointer, never silently.** If a result overturns something in
`docs/01-06`, add a blockquote at the top of that doc naming the log entry. Do not rewrite the
design record mid-build. `docs/06-open-questions.md` is the exception: it is a register and is
updated in place.

**A negative result is a result.** Write it down with the same care as a positive one,
including what was tried and why it was abandoned. That is the part git history cannot tell
anyone.

**Never delete a file or `git checkout` / `restore` / `stash` a path as a side effect of a
requested change.** Deleting something is a separate proposal — ask first, in the same message
where you say why.

## The simulator is a manual dependency

MSFS is slow, stateful, and cannot be scripted into a flight with the right aircraft and the
right panel selected. Batch everything that needs it into deliberate sessions. Write the probe
so it answers as much as possible in one paste, prints a self-contained report, and stashes
its raw data on a global for follow-up questions.

Whether probes can be driven remotely at all is Q00, and it is unresolved.

## Rules that come from the sim, not from us

**Bridge JS needs no build.** Files under
`%APPDATA%/Microsoft Flight Simulator 2024/Packages/Community/fscopilot-bridge/html_ui/` are
loaded as-is; edit, reload the panel, done. There is no .NET SDK on this machine — the FS
Copilot desktop app is compiled in Visual Studio and errors come back by hand — so put as much
of the work as possible in JS.

**Do not ship a second package that patches `VCockpit.js`.** `fscopilot-bridge` already
overrides that core file. Two packages patching one path is a conflict. Put probe and
prototype code in the bridge's own `html_ui/`, with an install/uninstall script so the tree
returns to stock.

**Prefix anything that runs in the sim or speaks on a bus with `FSCPP_`.** Never `FSC_`, which
is FS Copilot's, and never `FSCEDITOR_`, which is fsc-editor's. All three packages live in the
same Community folder and share the same buses.

## Probes

One file per question, numbered `pNN`, named after what it settles. `.mjs` runs under Node
from the repo root and is wired into `package.json`. `.js` is pasted whole into the Coherent GT
debugger console.

A console probe should: print a readable report rather than return an object, stash full data
on a `window.__PNN` global with helper methods for follow-up, degrade gracefully when a step
fails rather than throwing, and open with a comment saying which question it settles and which
panel to select.

Node probes take no dependencies. Node 22 is what is installed.
