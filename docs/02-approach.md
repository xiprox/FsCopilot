# Pointer forwarding

    Purpose:    The mechanism being proposed — capture, coordinate space, replay.
    Depends on: 01-problem
    Decides:    what is sent, in what units, how it is replayed, how loops are broken

> **Amended by the build.** Three corrections. **`PointerEvent` does not exist in
> Coherent GT** — the constructor is absent, so "dispatch real `PointerEvent`s"
> below is impossible, not merely unnecessary; the sequence is `mousedown`,
> `mouseup`, `click` with `MouseEvent` only. The approach itself is now
> **demonstrated** on a React/SVG display, so this file is no longer entirely a
> proposal — see "Q03 PASSES" in [build/log.md](build/log.md). And **tier 3
> (drag) works**: MSFS does deliver `mousemove` to a panel document while a
> button is held. See "Drag works" in [build/log.md](build/log.md), which also
> supersedes an earlier entry claiming drag was broken.

**Most of this file has now been verified against a running simulator.** It is the design the
probes in [build/plan.md](build/plan.md) exist to test. Where a probe has run, the log says so
and this file carries a pointer line.

Forward *where* the pointer went, not *what* it hit. The receiver re-enters its own DOM at
those coordinates and lets its own hit-testing decide what was pressed — which is what a
second pilot reaching for the same physical button would do anyway.

## Capture

Listen on the **instrument element** — the custom element `setupInstrument` appends to the
panel — rather than on `document`. Two reasons: it scopes events to one instrument for free,
and it gives a stable rect to measure against.

What to capture, in ascending order of cost:

| | Events | Covers |
| --- | --- | --- |
| tier 1 | `pointerdown`, `pointerup` | buttons, softkeys, switches — everything with a discrete press |
| tier 2 | + press duration | press-and-hold |
| tier 3 | + throttled `mousemove` while down | drag |

All three tiers are built and work. **Tier 3 works** — MSFS does deliver `mousemove` to a
panel document while a button is held, which was the question underneath it. See "Drag works"
in [build/log.md](build/log.md). An earlier entry reporting drag as broken is withdrawn: it
tested a build that had no drag code in it.

Replayed drags sometimes drift from the original gesture. Accepted as a known limitation
rather than diagnosed, since the case that matters is two machines and that cannot be tested
yet — recorded as Q10 in [06-open-questions](06-open-questions.md).

The bandwidth objection that originally gated tier 3 is gone: the transport is a WebSocket
with no size cap ([10-module](10-module.md)).

Scope decided 2026-08-30, from experience of these cockpits: **wheel and double-click are not
worth building** — wheel is a first-party MSFS cockpit control and is not injected into large
instrument panels, and double-click is not used. Keyboard is deferred to a second pass, though
`keydown`/`keypress`/`keyup` were observed arriving at a panel document and trusted, so it is
available when wanted.

## Coordinate space

Send fractions of the instrument element's own bounding rect, never viewport pixels:

    nx = (ev.clientX - rect.left) / rect.width
    ny = (ev.clientY - rect.top)  / rect.height

`setupInstrument` positions and sizes the instrument by `vDisplaySize / vLogicalSize`, which
is a function of the sim's own display resolution. Normalising against the resulting rect
cancels that scaling out, so the two machines agree without either of them knowing the other's
graphics settings. Four decimal places is sub-pixel on any panel MSFS renders.

This is the one design decision here that is cheap insurance rather than a guess: even if the
rects turn out to be identical everywhere (Q04), normalising costs nothing.

## Replay

    const el = document.elementFromPoint(px, py)

then dispatch on `el` with `clientX` / `clientY` set and `bubbles: true`.

**Dispatch real `PointerEvent`s.** The A220 binds `onPointerDown`; a `MouseEvent` alone will
not reach it. Feature-detect `window.PointerEvent` and fall back to mouse-only.

Sequence, in order: `pointerdown`, `mousedown`, `pointerup`, `mouseup`, `click`. Each carries
`button: 0`, `buttons: 1` on the down pair and `0` after, `isPrimary: true`, and a stable
`pointerId`. React's synthetic event system delegates at the root, so an event bubbling from
the correct leaf reaches the handler exactly as a real one does.

Drop the `ev.button !== 0` gate that the existing capture has. Right-click is a real control
on some panels and there is no reason to inherit that restriction here.

## Loop breaking

Keep the existing `selfEmit` flag on dispatched events and ignore them on capture. It is
proven and it is one property.

`ev.isTrusted === false` is a tempting second guard — synthetic events are untrusted by
construction, so it would catch anything that sets `selfEmit` incorrectly. It is only usable
if MSFS's own cockpit clicks arrive **trusted**, which is Q02. Do not rely on it before that
is answered.

## Why this is opt-in and not a fallback

"Use the element name, fall back to coordinates when the name misses" is the obvious design
and it does not work. On the A220 the name *does* resolve — to the mount `div` — so the
fallback never fires. There is no signal available at capture time that separates a useful
name from a useless container name.

Per-instrument opt-in in the profile is the honest answer. It also keeps every aircraft that
works today working today, which a wholesale replacement would not.
See [05-integration](05-integration.md).

## What this does not solve

Coordinate replay presses whatever is at that spot on the receiver. If the two displays are
showing different pages, the wrong thing gets pressed. The existing scheme has exactly the
same problem — a name resolves to whatever is at that position in *its* tree — but coordinates
make it visible, so it will get reported as a regression when it is not one.

It is arguably the correct behaviour for a shared physical panel. It needs to be a documented
property rather than a surprise.
