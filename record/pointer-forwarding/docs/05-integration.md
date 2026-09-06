# Integration

    Purpose:    How this is turned on, addressed, and eventually folded into FS Copilot.
    Depends on: 02-approach, 04-transport
    Decides:    routing key, profile shape, where experimental code lives, naming

> **Superseded in part by the production plan.** The graduation this file sketches is now
> decided and recorded in [11-fsc-implementation-plan](11-fsc-implementation-plan.md): the
> routing key gains a URL-query discriminator (full keys — the A220 reuses
> `instrumentIdentifier` across its CTPs, MKPs and FCPs), config delivery collapses into
> the WebSocket hello/config handshake, and delivery is a single PR rather than a series.
> See "production design" in [build/log.md](build/log.md). The routing-key rationale,
> opt-in argument and naming rules below still stand.

## The routing key

An interaction is addressed to an instrument by `instrumentIdentifier` — the same string the
existing `ignore:` list matches on, and the same one the receiving `Hook` checks before
dispatching. Reuse it. Inventing a second addressing scheme for the same objects is how two
of them end up disagreeing.

For a capture that happens inside an iframe (see [03-scope](03-scope.md)), the key extends with
a frame path:

    instrumentIdentifier '#' frameIndex[.frameIndex…]

Frame counts are small and stable in a way sibling indices are not, so the positional
fragility that sinks the element-name scheme does not apply at this granularity. It still has
to be computed identically on both machines.

## Profile shape

Per-instrument opt-in, alongside the existing `ignore:`:

```yaml
pointer:
  - DisplayUnits
  - CTP
```

Opt-in rather than automatic because a useless element name is indistinguishable from a useful
one at capture time — [02-approach](02-approach.md) has the reasoning. Opt-in also means every
aircraft that works today keeps working today.

## Getting config into the panel

The panel document has to know which mode it is in, and it comes up long after the app does —
panels load lazily and reload on view changes, so a one-shot broadcast at connect is not
enough.

**Have the Hook announce itself.** On construction it sends `{type:'hello', instrument: id}`;
the app replies with that one instrument's mode. Per-instrument replies stay far under the
512-byte cap, which a whole-list broadcast would not for a large aircraft. There is a
commented-out `Set(SimConfig)` envelope in `SimClient.cs` to model the message on.

If Q08 lands and the transport becomes a WebSocket, this collapses into the connection
handshake and stops being a design problem at all.

## Where experimental code lives

**Bridge JS needs no build.** Edit the files in
`Community/fscopilot-bridge/html_ui/FsCopilot/`, reload the panel, done. Given there is no
.NET SDK on the development machine — the desktop app is compiled in Visual Studio and errors
come back by hand — this is where nearly all the iteration should happen. Only the WASM module
and the C# app need a real build.

**Do not ship a second package that patches `VCockpit.js`.** `fscopilot-bridge` already
overrides that core file via `PackageOrderHint: PANEL_PATCH`; a second package patching the
same path is a conflict, and the sibling project `fsc-editor-link` treats "we patch no file
the sim or another package owns" as a property worth protecting. Drop probe and prototype code
into the bridge's own `html_ui/` folder instead, and keep an install/uninstall script so the
tree can be returned to stock.

## Naming

Anything of ours that runs inside the simulator or speaks on a bus uses an **`FSCPP_`**
prefix. Never `FSC_`.

`fscopilot-bridge` and `fsc-editor-link` both live in the same Community folder and speak on
the same buses. `fsc-editor-link` uses `FSCEDITOR_*` for exactly this reason. Colliding with
either would be our fault.

## Graduating

Nothing here ships from this repository. If a prototype works, the change to FS Copilot is a
separate piece of work with its own review, and it lands as an *additional* mode rather than a
replacement — the existing scheme keeps every aircraft it already serves.

Two things to carry across when that happens: the wire format wants to be right the first
time, because `Codecs.Schema` makes every protocol change a hard compatibility break
([04-transport](04-transport.md)); and a per-instrument sent / replayed / missed counter is
worth building early, because it answers "which aircraft does this work in" far faster than
flying them.
