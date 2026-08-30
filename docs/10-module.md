# The prototype module

    Purpose:    The production-shaped implementation — what runs in the simulator,
                what it talks to, and how it is built and installed.
    Depends on: 02-approach, 08-testbed
    Decides:    the package layout, the transport, and the boundary that keeps the
                agent portable

Supersedes [08-testbed](08-testbed.md) as the way experiments are *run*. The
inspector remains, for inspecting.

## Why this replaced the testbed

The testbed carried data over the debugger's console channel, and that channel
silently drops a message identical to the one before it. Half a session's worth of
presses disappeared with no error anywhere, and a round was spent debugging our own
scaffolding rather than the mechanism.

The mechanism was already proven by then (Q03). Once that is true, the scaffolding
stops paying for itself.

## Shape

```
  MSFS panel document                    a local process
  ┌────────────────────────┐             ┌──────────────────┐
  │ VCockpit.js (patched)  │             │  host/server.mjs │
  │   ↓ Include.addImports │             │                  │
  │ link.js  agent.js      │  WebSocket  │  live display    │
  │ boot.js ───────────────┼────────────►│  record NDJSON   │
  │                        │◄────────────┤  replay          │
  └────────────────────────┘             └──────────────────┘
```

No WASM module, no SimConnect, no CommBus, no C#. Q08 established that a `coui://`
document can hold a WebSocket to loopback, full duplex, which removes that entire
pipeline along with the 512-byte cap, the single-slot client-data area, the
broadcast every panel has to parse, and the schema handshake that makes any wire
change a hard compatibility break.

## The files, and which one matters

| File | Role |
| --- | --- |
| `module/PackageSources/html_ui/FSCPP/agent.js` | Capture, normalisation, replay, loop breaking. **The only file with mechanism in it, and the one that eventually ships inside FS Copilot.** Knows nothing about transport. |
| `.../FSCPP/link.js` | WebSocket client. Backoff reconnect, bounded queue, identifies itself by panel key. |
| `.../FSCPP/boot.js` | Twenty lines joining the two. The only file that changes for a different transport. |
| `.../Pages/VCockpit/Core/VCockpit.js` | Patched core file — the loader. Generated, never edited by hand. |
| `host/ws.mjs` | Dependency-free RFC 6455 server, ~120 lines. |
| `host/server.mjs` | The local process: live display, recording, replay. |

`agent.js` is a factory — `FSCPP_Agent(instrument)` — so the module and the
testbed use the same file rather than two copies that drift.

## Getting code into a panel

There is one way: override `html_ui/Pages/VCockpit/Core/VCockpit.js`. It is the
only file the simulator loads into every VCockpit panel. FS Copilot does the same,
for the same reason.

MSFS 2024 streams its core packages, so the stock file is not on disk. FS Copilot's
copy is — and its own header says it is a direct copy plus two delimited blocks.
`module/tools/derive-vcockpit.mjs` strips those blocks to recover the stock file,
then injects our loader. It asserts on every marker and refuses to emit a
half-stripped file, so a simulator update that changes the shape fails loudly
rather than producing something subtly wrong.

**Our patch is smaller than FS Copilot's, and fixes one bug in passing.** We do not
need the `addEventListener` monkeypatch — that exists to decide which elements are
worth naming, and we do not name elements. And pending instruments go in an
**array**: FS Copilot's single `templateToLoad` slot silently drops every
instrument but the last when a panel has more than one and the imports have not
resolved yet. The A350 has such a panel.

## Building

```
npm run module:build     derive, package, install to Community
npm run module:check     report install state and conflicts
npm run module:remove    uninstall
npm run host             the local process
```

HTML and JavaScript only, so no MSFS SDK and no `fspackagetool` — `manifest.json`
and `layout.json` are written directly. (`fsc-editor/link/` needs the whole SDK
toolchain because it ships a WASM module; this does not.)

**A sim restart is needed after installing**, because `layout.json` is read at
startup. Editing the JavaScript afterwards is not a rebuild — the files load as-is,
so reloading the panel picks them up. That is the fast loop and it is why as much
logic as possible lives in JS.

## The conflict, and why the build refuses

`fscopilot-bridge` overrides the same `VCockpit.js`. Whichever the simulator
resolves last wins, and that depends on load order we do not control — so the
result is silently one or the other, which is the worst possible way to run an
experiment.

`build.mjs` refuses to install while it is present and prints the rename command.
Move it back when finished testing.

## Deliberately not built

**Two-machine forwarding.** The host does not connect to a peer host, because
there is no second machine to test against. The shape is in
[08-testbed](08-testbed.md).

**Echo back to the originating panel.** On one machine that would replay the
pilot's own press at them — the loop the agent exists to break, not a feature.
Replay is explicit, via the host's `p` command.

## What is still unproven about it

Q08 was demonstrated **with the inspector running**. A shipped build must work with
DevMode off, and whether a `coui://` document can reach loopback in a normal
session is untested. If it cannot, `boot.js` and `link.js` are the files that
change and `agent.js` is not — which is the whole point of that boundary.
