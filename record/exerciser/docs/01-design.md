# Exerciser design

    Purpose:    What the exerciser is, how it connects to FS Copilot, and how the pointer
                page turns a click on a picture into a click in the simulator.
    Depends on: log.md (p01, p02); pointer-forwarding/docs/11-fsc-implementation-plan.md
    Decides:    peer not dev mode; shell and pages; window capture; contain-fit mapping
    Status:     Started 2026-09-17. Sections marked OPEN are waiting on a decision.

## What it is for

Three uses, one app:

- **Testing on one machine.** Everything the bench cannot reach needs a real panel: the
  replay into the display, the replay queue's pacing, the overlays.
- **The maintainer testing the PR.** Run the PR's FSC, run the exerciser, join. No second
  machine, no second pilot, no build flags.
- **Recording a demo.** Both ends of every gesture on screen at once.

## Shape: a shell and pages

The shell owns what every feature shares: the connection to FSC, the session, and the
controls that break the session on purpose. A page owns one feature's view and inputs.
Pointer forwarding is the first page. The next PR adds a page.

A page gets from the shell:

- the peer link: send a packet, subscribe to a packet type
- FSC's panel channel: the current config and sync state, as FSC broadcasts them
- the session controls' state, so a page can show what an outage does to its feature

## Session: the exerciser is a peer

It joins FSC's session with the session code, the way a second pilot does. FSC runs
unmodified.

Dev mode was the alternative and does not test the feature. `--dev` builds no Coordinator
and no network; its echo sends a panel's capture straight back to the panels. The wire,
the acks, the resend history and the session state machine never run.

As a peer, a click in the exerciser goes exerciser → network → Coordinator → PanelServer →
pointer.js → display, and a click in the sim comes back the same way reversed (p01).

Shell controls, each a real session event:

| Control | What FSC sees |
| --- | --- |
| Join / Leave | a peer arriving, a peer leaving on purpose |
| Take control | the master handover |
| Drop link | the link going quiet with no goodbye: an outage |
| Rejoin | recovery; FSC resends everything after the last ack |

Drop link is followed by Rejoin, not by waiting. A dropped link does not come back on its
own (pointer-forwarding Q12).

A second connection goes to FSC's PanelServer as a plain WebSocket client. Its hello uses
a key nobody configures, so nothing is routed to it, and it receives `{t:"config"}` and
`{t:"state"}` like any panel (p01). That is where the page's panel list comes from. No
aircraft detection and no profile lookup.

## Relay

A same-machine pair crosses the relay twice, so every gesture pays the relay's round trip
twice (p01):

| Relay | ICMP from this machine | Gesture latency, one way |
| --- | --- | --- |
| local `FsCopilot.Discovery` | ~0 | ~0 |
| upstream `p2p.fscopilot.com` | 72 ms | ~145 ms |
| `fscrelay.ihsan.dev` | 116 ms | ~230 ms |

The default for one machine is a local relay started by the exerciser. FSC has to be
pointed at it too, which today takes `--relay localhost`.

The relay protocol has to match FSC's build. `ahead` speaks v2 to `fscrelay.ihsan.dev`;
the PR branch speaks v1 to `p2p.fscopilot.com`, and a v2 relay refuses it. Built from the
same branch as FSC, the exerciser speaks FSC's protocol automatically; only the host is a
setting.

**OPEN: the PR build has no `--relay`.** It arrived with BenchControl, which is test-only
and not in the PR. The maintainer would then be on the public relay, at ~145 ms, which is
fine for testing and visible in a recording.

## Wire compatibility

`Codecs.Schema` hashes each registered packet type's assembly-qualified name, so the
exerciser registers FSC's own types, in FSC's order: SetMaster, Update, Interact, Physics,
Surfaces, PointerEvent, PointerAck. Copies in another assembly can never match.

**OPEN: how to reach the three private ones** (Coordinator.Update, Coordinator.InteractCodec,
MasterSwitch.SetMaster):

- Reflection, as p01 did. No change to FSC. A rename in FSC fails at startup with a clear
  error, not silently.
- `internal` plus `InternalsVisibleTo("FsCopilot.Exerciser")`. A small visible change to FSC,
  and the compiler catches a rename.

Either way the exerciser also receives FSC's variable and physics traffic and ignores it.

## Pointer page

### Panels

The dropdown is the `pointer:` key list from `{t:"config"}`, live: loading another aircraft
replaces it.

### Pop-out

The user pops the instrument out in the sim (Right-Alt + click). The pop-out is a
top-level window of class `AceApp` titled with the instrument identifier; on the A220,
`DISPLAYUNITS` for `DisplayUnits|config=Default` (p02).

Matching by title is a shortcut, not the mechanism:

- Two instruments can share an identifier. The A220 has two CTP documents, whose keys
  differ only in the query string, and the title carries no query string.
- Only one aircraft has been checked, so the title rule is unconfirmed elsewhere.

So the page lists the sim's pop-out windows with a thumbnail each, preselects the one whose
title matches, and asks when there is no match or more than one.

### Capture

Window capture by the OS. The Coherent inspector has no working snapshot (p02).

PrintWindow works and needs no setup: 33 ms per frame for the main window, 63 ms for the
7394x1071 A220 pop-out, because it re-renders the whole window every call. Windows.Graphics.
Capture copies GPU frames at display rate and is what the page uses; PrintWindow stays as
the fallback.

Not yet measured: the simulator's frame rate while capturing.

### Mapping

A click has to become fractions of the instrument's rect, the numbers pointer.js sends and
replays. Two scalings sit between a pixel in the exerciser and a fraction:

1. **Sim to pop-out.** The sim draws the instrument into the pop-out contain-fit and
   centred. The 7410x1110 A220 strip in a 7394x1071 window: height fills it at scale 0.965,
   width scales by the same 0.965, and 122 px of bar is left either side. Predicting where
   five markers land with that rule was off by at most 6.5 instrument px (p02), from the sim's
   horizontal scale running 0.15% under its vertical one. Nothing corrects for that. It is
   under a pixel of exerciser screen at any practical zoom, and no button is that small.
2. **Pop-out to exerciser.** The page draws the capture at whatever size its view is.
   This is a plain view transform the page controls, so it inverts exactly.

The page inverts both: exerciser pixel → pop-out pixel → strip the bars → divide by the
drawn instrument size. The window is resizable in the sim; the transform is recomputed from
the capture size every frame, so resizing the pop-out needs nothing.

**OPEN: where the instrument's aspect ratio comes from.** Step 1 cannot be inverted without
it, and the capture does not carry it. The bars cannot be detected reliably: a display's own
background is black too.

- FSC already receives each panel's rect in its hello (`rect`, 7410x1110 here) and only logs
  it. Sending the rects to panel-channel clients makes the mapping automatic. A small change
  in FSC.
- The pop-out's initial size approximates the aspect (6.90 against the true 6.68), but that
  is 3% off and wrong once the window is resized.
- Reading `panel.cfg` from the aircraft package needs the package path and is per-aircraft
  parsing.

### View

Default: the whole instrument, fitted to the view. The mapping above holds at any size.

Zoom and pan, by wheel and drag, are there for strips. The A220 instrument is five screens
side by side at 6.7:1: fitted to an 1800 px wide view, each screen is 360 px wide and a
synoptic tab button (STATUS, AIR, DOOR) is about 45x12 px. On a 5120 px monitor fitted is readable. Zoom is a view
transform only, so it never needs configuring and never affects the mapping.

### Input from the exerciser

Pointer down, move and up on the view become a press or a drag with the thresholds
pointer.js uses (4 px, 33 ms, 2 px sampling, 240 points), and go out as a PointerEvent. The
thresholds are a copy; each copy's comment names the other.

The exerciser's own gestures are drawn on the view: a ring at a press, the path for a drag.

### Input from the sim

A PointerEvent from FSC is drawn on the view at its fractions: a pulse at a press, the drag
path redrawn at its recorded timing with a fading tail.

A drag reaches the exerciser at mouse-up, because the wire carries a drag as one packet.
Replaying its path at recorded timing shows the whole gesture, arriving just after the hand
finished it.

No marker is added to pointer.js. The sim shows what the display does, nothing more.

### Recording and replay

Record writes every PointerEvent sent or received to NDJSON, with arrival time. Replay
sends a recording's events at their recorded gaps. The pointer-forwarding
`recordings/*.ndjson` (v4) convert with a small shim.

## Known limits

- One machine. Two machines disagreeing on an instrument rect (pointer-forwarding Q04) is
  out of reach.
- WASM-rendered displays capture fine and still cannot receive a replay
  (pointer-forwarding 07-wasm-surface).
- A dropped link needs a rejoin (Q12).

## Where it ships

**OPEN.** A project in the solution, `FsCopilot.Exerciser`, as the last commit of the PR so
the maintainer can drop it; or kept on an `ahead-*` branch with a binary linked from the PR.
