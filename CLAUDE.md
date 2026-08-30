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

**Probes are driven remotely**, not pasted. MSFS hosts a WebKit inspector on
`127.0.0.1:19999` and `probes/lib/inspector.mjs` drives it: `npm run pages`, `npm run probe`,
`npm run eval`. The only thing still needing a human is a *real* cockpit click and a pair of
eyes on the display — several results this project depends on could not have been measured
any other way.

## Rules that come from the sim, not from us

**Bridge JS needs no build.** Files under
`%APPDATA%/Microsoft Flight Simulator 2024/Packages/Community/fscopilot-bridge/html_ui/` are
loaded as-is; edit, reload the panel, done. There is no .NET SDK on this machine — the FS
Copilot desktop app is compiled in Visual Studio and errors come back by hand — so put as much
of the work as possible in JS.

**Install nothing into the simulator.** Experiments run through the remote inspector — see
[docs/08-testbed.md](docs/08-testbed.md). This supersedes the earlier plan of dropping code
into `fscopilot-bridge/html_ui/`: it iterates in seconds rather than sim restarts, cannot
break FS Copilot, and cannot be left behind.

If something ever does have to go in a package, do not patch `VCockpit.js` — `fscopilot-bridge`
already overrides that core file and two packages patching one path is a conflict.

**Anything injected into the pilot's own input path must fail open.** A wrapper left
suppressing silently disabled an aircraft's clicks, and the handle needed to undo it had
already been deleted. Clear the suppressing state unconditionally, expose restore at a fixed
global that survives losing every other handle, and add a deadman that un-installs on its own.

**Prefix anything that runs in the sim or speaks on a bus with `FSCPP_`.** Never `FSC_`, which
is FS Copilot's, and never `FSCEDITOR_`, which is fsc-editor's. All three packages live in the
same Community folder and share the same buses.

## Injected code is Chrome 49

Coherent GT is `Chrome/49.0.2623`. **Read [docs/09-environment.md](docs/09-environment.md)
before writing anything that runs inside a panel.** The traps are syntax-level, so they take
out a whole file rather than one line: no optional chaining, no `??`, no class fields, no
`Object.fromEntries`, no `Array.at`, no `String.replaceAll`, no `Promise.allSettled`.

`const`, arrow functions, template literals, destructuring, spread, `async`/`await` and
`class` without fields are all fine.

Do not guess at what the simulator provides. `npm run sources` reads MSFS's own JavaScript
out of the running engine, and `probes/p08-environment.js` re-audits the environment after a
simulator update.

## Probes

One file per question, numbered `pNN`, named after what it settles. `.mjs` runs under Node
from the repo root and is wired into `package.json`. `.js` is pasted whole into the Coherent GT
debugger console.

A console probe should: print a readable report rather than return an object, stash full data
on a `window.__PNN` global with helper methods for follow-up, degrade gracefully when a step
fails rather than throwing, and open with a comment saying which question it settles and which
panel to select.

Node probes take no dependencies. Node 22 is what is installed.
