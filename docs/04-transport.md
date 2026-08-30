# Transport

    Purpose:    What carries an interaction between the two sims, and whether it can carry more.
    Depends on: 02-approach
    Decides:    whether the existing bus is widened or bypassed

> **Amended by the build.** The "bypass it" option below is no longer speculative.
> A `coui://` panel document holds a WebSocket to a local process, full duplex,
> **with DevMode off** — so the prototype uses no CommBus, no WASM and no
> SimConnect, and none of the constraints in this file apply to it. See "Q08 yes"
> and "The module works with DevMode off" in [build/log.md](build/log.md), and
> [10-module](10-module.md) for what was built.

## What exists

An interaction crosses seven hops each way. The middle two are the constraint:

    DOM → Hook → CommBus (JS) → WASM → SimConnect FSC_BUS_OUT → C# SimClient → network
                                       ╰─ 512-byte JSON, single slot ─╯

`FSC_BUS_OUT` and `FSC_BUS_IN` are SimConnect client-data areas holding one
`str_msg { char msg[512] }`, written with `SetClientData` and delivered `ON_SET`. Messages
are JSON. Inbound to JS, the WASM broadcasts with `FsCommBusBroadcast_JS`, so **every panel
document receives and parses every message** and discards the ones not addressed to it.

Today that carries roughly one message per second. Tier-1 pointer forwarding is the same order
of magnitude. Tier 3 — a 20–30 Hz drag stream — is not, and is the reason this file exists.

## Widening it

Three independent improvements, none of which require rearchitecting:

**The 512-byte cap is self-imposed.** SimConnect client-data areas go to 8 KB. Widening
`str_msg` is a one-line change on the WASM side and a `SizeConst` change on the C# side. It
raises message *size*, not *rate*, so it does not help drag — but it removes the ceiling on
config messages and long instrument names. It is an ABI change and needs a version bump on
both `k_version` and `WasmVersion`.

**Single slot under burst is unproven.** Whether two `SetClientData` calls in one frame both
arrive is Q09. A small ring — N slots plus a sequence counter — removes the question for the
cost of a few hundred bytes.

**JSON in a fixed char buffer is wasteful.** `p|CTP|3|0.4123|0.6710` is about a fifth of the
bytes of the equivalent object. Only worth doing if the bus stays in the path.

## Bypassing it

The Fenix EFB loads `http://localhost:8083` inside a panel. That is proof that Coherent GT
does outbound HTTP to loopback from a cockpit document. If it will also open a WebSocket, the
interaction path can skip CommBus, the WASM module and SimConnect entirely:

    panel document ⇄ ws://127.0.0.1:PORT ⇄ FS Copilot desktop app

What that buys: full duplex, no size cap, per-panel connections instead of a broadcast every
document has to parse, sub-millisecond latency, and a trivial config handshake. Tier 3 stops
being a bandwidth question.

What it costs: near nothing on the C# side. `HttpListener` + `AcceptWebSocketAsync` are both
BCL — no new NuGet package, which matters because the app is published `PublishTrimmed` and
`PublishSingleFile`. Trimming would need checking. Reconnect logic is required because panel
documents reload on view changes.

This is Q08 and it is a one-line probe. It is the highest-leverage open question in the
project after Q05.

The bus stays either way — variable sync uses it and there is no reason to move that. This
would be a dedicated interaction channel alongside it.

## The network hop

Between the two desktop apps, `Interact` packets go over LiteNetLib, reliable ordered, and
that part is fine. Tier-3 moves should go unreliable-sequenced; down and up stay reliable.

One hard constraint on any change to what crosses the wire: `Codecs.Schema` is a hash over
**every registered packet type**, and `P2PNetwork.OnConnectionRequest` rejects a peer whose
schema differs. Adding a field to `Interact` and adding a new packet type both break
connectivity with older builds. Not a reason to avoid either — just something that has to
ship on both sides at once, and a reason to get the payload shape right the first time rather
than iterating it across releases.
