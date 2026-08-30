# Pointer forwarding

    Purpose:    The mechanism being proposed — capture, coordinate space, replay.
    Depends on: 01-problem
    Decides:    what is sent, in what units, how it is replayed, how loops are broken

**Nothing in this file has been verified against a running simulator.** It is the design the
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
| tier 3 | + throttled `pointermove` while down | drag, scroll, swipe |

Tier 1 is the whole first cut. Tier 3 is a different bandwidth class and is gated separately —
see [04-transport](04-transport.md).

Whether MSFS delivers `pointerdown` to a panel document at all is unverified; FS Copilot only
ever listens for `mouseup`. That is Q01, and it decides whether tiers 2 and 3 exist.

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
