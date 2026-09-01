# Pointer forwarding in FS Copilot — implementation plan

    Purpose:    The adopted plan for productionizing pointer forwarding inside FS Copilot itself.
    Depends on: 02-approach, 04-transport, 05-integration, 09-environment, 10-module
    Decides:    transport, peer wire format, profile key, session/lock policy, gap-replay, PR shape

Decided 2026-09-01. This is the graduation plan [05-integration](05-integration.md) said
would be its own piece of work. Where this file and `01-10` disagree, this file wins — the
disagreements are called out below and pointered from the docs they amend. TDS, Fenix and
iframes are deliberately outside it.

## Context

The playground has proven the mechanism: capture cockpit mouse interactions in the panel
document, normalize to fractions of the instrument element's bounding rect, forward *where*
the pointer went, and let the receiver's own DOM hit-testing decide what got pressed.
Validated on the A220 React/SVG DisplayUnits (33/33 capture/replay, real dropdown opened,
drags working; drift bounded 0–12%, temporal not spatial). The WASM surface is permanently
unreachable (Q05 closed). This plan is the within-FS-Copilot productionization, targeting
`../fscopilot/src` (upstream submodule — minimal, in-idiom).

## Locked decisions (all user-confirmed)

1. **Panel ↔ app transport: WebSocket sidecar** — localhost WS server in the desktop app
   (BCL `HttpListener` + `AcceptWebSocketAsync`, no new NuGet). Q08 proved coui:// →
   ws://127.0.0.1 with DevMode off. CommBus/WASM bus untouched (variables only).
2. **Port: range scan 9020–9024.** App binds first free port in range; `channel.js`
   rotates through the range in its reconnect backoff. Self-correcting against squatters
   and second instances, no side channel. (WASM-announce rejected: makes the bus a
   bootstrap dependency of the channel built to bypass it.)
3. **Peer ↔ peer drags: batched** — one reliable packet per gesture at mouse-up, full
   sampled path (≤240 points). Presses also reliable. Live streaming is a later option;
   capture format doesn't change.
4. **Single PR** (maintainer inactive), structured as multiple clean commits.
5. **Profiles: new top-level `pointer:` key**, per-instrument opt-in with full keys
   (`instrumentIdentifier` or `instrumentIdentifier|querystring`). No served profiles carry
   the key until the release is out. Re-enabling `IgnoreUnmatchedProperties()` is **out of
   scope** — a known compat hazard for future profile keys (see Open risks).
6. **Full gap-replay robustness in v1**: sequence numbers in the wire from day one (schema
   hash forbids adding later), two-mode send history (small ring while live, **full
   accumulation from the moment the session degrades**), re-delivery on peer reconnect and
   on late panel hello, age cap with receiver-side waiver, desync surfacing beyond it.
7. **Lock policy: slave-only overlay lock** (blue) on pointer panels while the session is
   degraded, **and overlay up in "connecting" state** from the moment config arrives until
   the app confirms the session is live and buffers are flushed. A **red, non-blocking**
   overlay warns a configured pointer panel that its app link died; the all-ports-taken
   bind failure surfaces as a **desktop-app error only** (a never-connected panel cannot
   distinguish it from the app simply not running, and must stay clean for solo flight).
8. **No test project for now.** Diagnostics via Serilog + panel `{t:"stats"}` messages; no
   DevelopWindow panel.

## Verified source facts the design rests on

- `Coordinator.cs:47-51`: `_ignore` filters only *outbound* Interact; inbound stream is
  unfiltered (fix included). Registration order (part of `Codecs.Schema`, which
  hard-rejects mismatched peers): PeerTags → SetMaster → Update → Interact → Physics →
  Surfaces; new packets append after Surfaces in the Coordinator ctor.
- `Definitions.cs:24`: `.IgnoreUnmatchedProperties()` commented out (docs note only);
  `[DynamicDependency]` at line 46 shows trimming has bitten before → smoke-test publish.
- Interact/shared vars are symmetric (not master-gated); pointer sync follows that model.
- Reliable sends are LiteNetLib `ReliableOrdered` (fragments; ~2.4 KB drags fine).
  LiteNetLib provides keepalive + disconnect detection; `INetwork.Peers` exposes it — no
  new peer-level heartbeat needed.
- FS Copilot's VCockpit.js patch: single `templateToLoad` slot drops instruments on
  multi-instrument documents (slow path) — pending-array fix included. Pre-existing
  fast-path duplicate-Hook/HtmlEvents issue documented in PR text, not silently changed.
- FS Copilot's replayed MouseEvents carry clientX/Y = 0 → latched presses on WasmInstrument
  aircraft; `ignore: [WasmInstrument]` profile entries are a real safety fix.
- MasterSwitch: everyone boots master, `Join()` demotes, handover is assertive
  (`SetMaster`, last writer wins, `//todo Temp solution`). The lock policy leans on this
  state — its roughness is a known caveat, not a blocker (both sides always hold *a* role).

## The single PR — commit structure

Ships app + rebuilt `src/FsCopilot.Bridge/Packages/fscopilot-bridge/` (regenerated
`layout.json`; `Installer.DeployBridge` hash-compares from there; sim restart required
after deploy — noted in PR text).

1. **VCockpit.js pending-array fix** (standalone bug fix commit).
2. **Interact safety**: inbound `_ignore` filter in `Coordinator`; `ignore:
   [WasmInstrument]` entries in affected `src/Definitions/*.yaml`.
3. **Panel channel**: `PanelServer` + `channel.js` + hello/config handshake + session-state
   broadcast (behavior-neutral: panels stay in events mode).
4. **Pointer feature**: `pointer.js`, `PointerPress`/`PointerDrag` + codecs, Coordinator
   wiring, `pointer:` in `Definitions`, mode gating in `hook.js`, gap-replay buffers,
   overlay lock.
5. **A220 profile `pointer:` entries + stats logging.**

## C# design

### PanelServer (`src/FsCopilot/Connection/PanelServer.cs`)

Concrete class beside `SimClient` (idiom: no interface). `HttpListener` binds first free
`http://127.0.0.1:{9020..9024}/`; accept loop on `Task.Run` + CTS; per-connection
`AcceptWebSocketAsync`, 16 KB receive loop, `JsonDocument` routed on `"t"` (reuse
`JsonExtensions`). Per-socket sends serialized via `SemaphoreSlim`. Inbound exposed as
`Subject<T>` with `.ObserveOn(TaskPoolScheduler.Default)` — the `SimClient` threading model.

```csharp
public sealed class PanelServer : IDisposable
{
    public IObservable<PointerPress> Presses { get; }
    public IObservable<PointerDrag>  Drags   { get; }
    public void Configure(IReadOnlyCollection<string> pointerKeys); // reply to hellos + broadcast
    public void SetSessionState(SessionState s); // none | connecting | live | degraded (+ role)
    public void Send(PointerPress p);  // buffers per key if no socket helloed that key yet
    public void Send(PointerDrag d);
}
```

- Sockets track helloed keys (multi-instrument documents hello once per Hook, one socket).
- Every hello gets an immediate `{t:"config", pointer:[...]}`; `Configure` also broadcasts
  (solves profile-load-after-hello ordering).
- **Inbound-side boot buffer**: events for keys with no helloed socket are queued per key
  and flushed in order on hello (safe to flush fully — the panel is locked in "connecting"
  until flush completes; see lock lifecycle).
- **State broadcast** `{t:"state", session:"none|connecting|live|degraded", role:"master|slave"}`
  sent on change **and every 2 s** (the renewal is the panel-side liveness signal).
- All-ports-taken: log warning, surface a `ViewErrors`-style error in the desktop UI
  ("Panel channel unavailable — ports 9020–9024 in use"), observables silent, feature
  fails open; panels back off (≤15 s) across the range forever (indistinguishable to them
  from the app not running — deliberate, so solo flight stays clean).
- DI: `AddSingleton<PanelServer>()` in both dev and non-dev branches; `Coordinator` ctor
  gains `PanelServer panels` (also fixes construction order).
- Session state machine lives app-side, fed by `INetwork.Peers` + MasterSwitch:
  *none* (never connected / intentionally left) → *connecting* (peer handshaking or panels
  not yet flushed) → *live* → *degraded* (had a peer, lost it). Intentional Leave → *none*.
  Degraded > 5 min → treated as session end → *none* (unlocks the slave; desync notice).

### Packets (`src/FsCopilot/Connection/Pointer.cs`)

Two records (the 1-byte registration id is the discriminator; press/drag share no shape).
Style follows `Interact` + `Update.Codec`. **`Session` + `Seq` are in the wire from day
one** — the schema hash forbids retrofitting.

```csharp
public record PointerPress(string Key, ulong Session, uint Seq, byte Flags, byte Button,
    ushort HoldMs, float DownX, float DownY, float UpX, float UpY)
{ public class Codec : IPacketCodec<PointerPress> { ... } }

public record PointerDrag(string Key, ulong Session, uint Seq, byte Flags, byte Button,
    PointerDrag.Point[] Path)
{
    public readonly record struct Point(ushort DtMs, float X, float Y); // delta vs previous
    public class Codec : IPacketCodec<PointerDrag> { ... }
}
```

- `Session`: random per app run (same pattern as Coordinator's Physics sessionId).
  `Seq`: one monotonic counter per app run across all keys.
- Registered after Surfaces (ids 6, 7). `Flags` reserved for within-schema evolution.
- Encode order = record order; BinaryWriter strings; drag `ushort Count` clamped to 240 on
  encode, **decode rejects > 1024** (throw → `Codecs.Decode` drops packet; add a log-once
  on pointer decode failure — today's silent null would hide systematic rejection).
- Coordinates are float32 rect fractions; may fall slightly outside [0,1]; never clamp.
- Sizes: press ~50–65 B; 240-point drag ~2.4 KB.

### Coordinator wiring + gap-replay

`volatile PointerFilter _pointer` — immutable snapshot: `Keys` (full keys) and derived
`Instruments` (prefix before `|`, to match `Interact.Instrument`). `Load()` swaps the
snapshot atomically and calls `panels.Configure(defs.Pointer)`.

```csharp
// panel -> peers (profile filter; symmetric, never master-gated), stamping Session/Seq,
// and appending to the send-history ring
panels.Presses.Where(p => _pointer.Keys.Contains(p.Key)).Subscribe(p => SendAndRecord(p));
panels.Drags  .Where(d => _pointer.Keys.Contains(d.Key)).Subscribe(d => SendAndRecord(d));
// peers -> panel (receive-side filter = defence vs a peer with a different profile),
// deduped by (Session, Seq)
_net.Stream<PointerPress>().Where(Fresh).Where(p => _pointer.Keys.Contains(p.Key)).Subscribe(panels.Send);
_net.Stream<PointerDrag>() .Where(Fresh).Where(d => _pointer.Keys.Contains(d.Key)).Subscribe(panels.Send);
// double-actuation guard, both Interact directions:
.Where(i => !_ignore.Contains(i.Instrument) && !_pointer.Instruments.Contains(i.Instrument))
```

**Gap-replay, two-mode history:** while the session is *live*, the sender keeps a small
ring (last ~50 events per key, wall-clock stamped locally) to cover reconnect races. On
transition to *degraded*, the sender switches to **accumulation mode: every event from the
degradation instant is kept**, bounded naturally by the 5-minute degraded→session-end
timeout (events are ~60 B presses / ≤2.4 KB drags — well under a megabyte even for a busy
outage). On peer (re)connect (`INetwork.Peers`), sender blindly re-sends the held history —
receiver dedupes by `(Session, Seq)`. Receiver policy on replayed events: if its panel was
locked for the entire gap (slave during outage, or "connecting" at boot), apply
**everything in order** — the panel couldn't diverge, so ordered replay reconstructs sync
exactly. **Invariant: an outage that ends within the session timeout loses nothing.**
Otherwise (receiver wasn't locked) apply only events younger than **60 s** and raise a
desync notice if older ones existed. A `Seq` jump beyond the held history → desync notice.
Session end (timeout or Leave) discards the accumulation buffer.

Self-echo is structurally impossible app-side (captures only go out; receipts only go to
panels); panel guards (`selfEmit`, `isTrusted`, `replaying` counter) remain the second line.

### Profiles (`Definitions.cs`)

Mirror `ignore` exactly: `Config.Pointer` → `DefinitionNode` → `Collect` →
`public string[] Pointer { get; }`. Include-tree merge comes free. Document for profile
authors: `pointer:` takes **full keys** (`DisplayUnits|config=Default`), unlike `ignore:`'s
bare identifiers.

## JS design (`PackageSources/HTML_UI/FsCopilot/`)

FS Copilot idiom throughout (classes, `[FsCopilot]` log prefixes, `selfEmit`, no `FSCPP_`
globals). Chrome 49 rules stand — [09-environment](09-environment.md) is the reference; no
`?.`/`??`/class fields/`PointerEvent`.

- **`channel.js`** — `class Channel extends Emitter`, port of playground `link.js`: WS
  with port rotation 9020–9024 inside backoff [500..15000] ms, bounded queue 200 with
  **age cap instead of drop-all on reconnect** (stale input is worse than lost input, but
  boot-time captures must survive the connect delay), `{t:"hello", name, url}` on open,
  re-hello on reconnect. One per document (`window.fscChannel` guard).
- **`pointer.js`** — `class Pointer`, port of playground `agent.js` verbatim in logic:
  capture-phase document listeners, 4-decimal rect-fraction normalization, `keyFor`
  (identifier + URL query), press/drag classification (4 px / 33 ms / 2 px / 240 pts /
  250 ms clamp), MouseEvent-only replay via `elementFromPoint`, `replaying` counter, 3 s
  drag deadman, `stop()`, `stats()`. One instance per document (v1: first pointer-opted
  instrument wins on multi-instrument documents — A220 targets are all single-instrument;
  documented). **Also owns the lock overlay** (below).
- **`hook.js`** — decision point. Hello over shared Channel; on `{t:"config"}`: key listed →
  pointer mode (suppress this Hook's events.js emit path + inbound interact for this
  instrument; create/wire `Pointer`); not listed → events mode; config revoked →
  `pointer.stop()` + re-enable events. Modes switch at runtime. No config ever → events
  mode forever. Fail open.
- **`VCockpit.js`** — inside the existing FSC-delimited blocks: Include chain +
  `channel.js`/`pointer.js`; pending-array fix. `fscListeners` monkeypatch stays
  (events.js needs it).
- WS schema: `{t:"hello"}` / `{t:"config"}` / `{t:"state"}` / `{t:"pointer", msg:{...}}` /
  `{t:"stats"}` every 60 s.

### The overlays (in `pointer.js`)

**Mechanism: overlay divs, not event suppression.** An absolutely-positioned div covering
the instrument rect swallows input by existing — the blocker and the visible warning are
the same DOM node, so it cannot silently eat input the way the p06 wrapper did, and
removal is the complete restore.

Two overlays, one hard rule — **blocking requires a live app renewing the lock**:

- **Blue = lock** (blocking): diagonal blue cross + "FS Copilot connecting…" /
  "FS Copilot disconnected". Shown only while `{t:"state"}` renewals justify it.
- **Red = warning** (non-blocking, `pointer-events: none` — clicks pass through): shown by
  a **configured pointer panel** whose app link died (WS closed / renewals stopped ~8 s) —
  including while it was locked blue (blue degrades to red, never to silence). Red never
  blocks, because when the app is gone nobody authoritative is alive to lift a lock.
- **Fixed global**: `window.fscUnlock()` force-removes either, surviving all other handles.
- **Never the resting state**: a panel with no app, no config, or no session shows nothing.

States per pointer-mode panel:
| Condition | Master | Slave |
|---|---|---|
| session none (solo / left) | clear | clear |
| connecting (boot, until config + session live + buffers flushed) | **blue lock** | **blue lock** |
| live | clear | clear |
| degraded (peer lost, app alive) | clear (must fly) | **blue lock** |
| app link lost (configured pointer panel; any prior state) | **red warning** | **red warning** |

Slave-only degraded lock means the master's outage inputs replay onto an undiverged panel
on reconnect (exact reconstruction); degraded > 5 min → session end → unlock + desync
notice.

## Known property (document, don't hide)

Concurrent conflicting inputs on the *same* panel within a latency window (or during the
first seconds of boot before panel JS loads — inherently uncoverable) can diverge the
panels: each side applies its own input first, and page navigation doesn't commute. No
input-replay design fixes this without rollback, which panels can't do; the element-name
scheme has the same property invisibly. Mitigations: the connecting-state lock closes the
boot tail; readiness UI ("panels connecting… 3/5" — app knows expected vs helloed keys);
convention (one pilot per panel); and the documented recovery: **panel chrome sits at fixed
coordinates, so both pilots pressing the same page button converges a diverged panel.**

## Verification

**No sim:** codec round-trips (incl. Count-bound rejection, Session/Seq dedupe); ad-hoc
`Codecs.Schema` before/after check (changes only by the two new packets); `PanelServer`
exercised with `ClientWebSocket` from a scratch console harness (hello→config,
pointer-in→packet-out, port-rotation with a squatted 9020, state renewal cadence); offline
replay of playground `recordings/*.ndjson` through a WS client asserting
JSON→record→bytes→record losslessness. `dotnet publish` (Release) and run the trimmed
single-file exe to verify HttpListener/AcceptWebSocketAsync survive trimming.

**One machine + sim:** channel soak with DevMode off (hello, reconnect across panel
reloads/aircraft changes/app+sim restarts, port rotation); `--dev`-only echo switch (app
reflects captures to same-key panels) reproducing the 33/33 run through PanelServer;
confirm pointer instruments emit no interact CommBus traffic (Verbose log) while
events-mode instruments still do; overlays: kill the app mid-lock → blue degrades to red
(non-blocking) in ~8 s and clicks pass through; `window.fscUnlock()` works from the
inspector; squat all five ports → desktop app shows the panel-channel error.

**Two machines (instrumentation built in for the remote tester):** Q04 rect agreement —
`rect` included in hello, logged app-side, diff both logs from one session. Q10 /
transport feel — Session/Seq plus per-key sent/forwarded/replayed/missed counters in app
log and panel stats; scripted checklist (33-press A220 set + map pans + a forced
disconnect/reconnect to exercise gap-replay and the slave lock); `missed > 0` localizes to
`elementFromPoint` (rect disagreement) vs transport (Seq gaps).

## Open risks

1. **HttpListener URL ACL** — non-elevated loopback should be exempt; verify on a clean
   non-admin machine early. Fallback keeping "no new NuGet": `TcpListener` + manual
   upgrade + `WebSocket.CreateFromStream` (~60 lines).
2. **Trimming** — smoke-test the published exe (codebase has trim scars).
3. **`--dev` has no Coordinator** → nothing calls `Configure`; dev panels stay in events
   mode (acceptable; echo switch covers dev testing).
4. **MasterSwitch roughness** (`//todo Temp solution`, assertive handover) — the lock
   policy reads it but never writes it; a mid-outage role flip is last-writer-wins as
   today. Documented caveat.
5. **Multi-instrument documents** — pending-array fix exposes the pre-existing
   duplicate-HtmlEvents capture issue; pointer mode is one-agent-per-document in v1. Both
   stated in PR text.
6. **Old clients vs new profiles** — `IgnoreUnmatchedProperties` stays off (out of scope):
   a `pointer:`-bearing profile bricks profile loading on older releases. No served
   profiles carry the key until the release is adopted.
7. **`pointer:` full keys vs `ignore:` bare identifiers** — bridged by the derived
   Instruments set; must be documented for profile authors.

## What this amends in the design record

- [04-transport](04-transport.md): the bypass is no longer just the prototype's transport —
  it is the adopted production design. The bus-widening options remain the documented
  fallback if upstream rejects a second channel.
- [05-integration](05-integration.md): the routing key gains the URL-query discriminator
  (full keys in `pointer:`), config delivery collapses into the WS hello/config handshake,
  and the "graduating" section is superseded by this plan (single PR, not a series).
- [06-open-questions](06-open-questions.md): Q04 and Q10 get their answer path — the
  instrumentation ships in the PR and the remote tester's session answers both.
