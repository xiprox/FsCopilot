# traffic-atc

Sharing **AI traffic** and **ATC audio** across a shared-cockpit session, so that when tower
says "hold short 28, departing traffic" every pilot in the session sees the departing traffic
and hears the tower.

Nothing here ships. Prototypes live under `tools/` and run against one machine; folding the
result into FS Copilot is a separate stage with its own `ahead-traffic-atc` branch, cut only
when there is code to put on it.

**[docs/index.md](docs/index.md) is the map.**

## Status

**The prototype works, on one machine.** The traffic BeyondATC injected — read back from the
sim as ordinary AI objects, which is why any add-on's traffic works — replays into the sim
three times smoother than BeyondATC's own (measured, not eyeballed), with correct liveries,
engines and ground vehicles, at ~2 KB/s for 130 aircraft; ATC audio goes process → Opus →
UDP → speaker in ~130 ms with loss concealed. The implementation plan is approved —
[docs/03-implementation-plan.md](docs/03-implementation-plan.md) — and the work lives on the
`ahead-traffic-atc` branch. `recordings/traffic-2026-09-06-15-58-19-gated.ndjson` is the one
capture kept in the repository: every measured number came from it.

## Layout

| Path | What lives there |
| --- | --- |
| `docs/index.md` | The map. Start here. |
| `docs/01-design.md` | The proposal. Stable, amended by pointer only. |
| `docs/02-open-questions.md` | The register of assumptions still to be tested. Updated in place. |
| `docs/build/plan.md` | What is being run, in what order, and where it stands. Live. |
| `docs/build/log.md` | Findings as they happen. Newest at the top. |
| `tools/` | The single-machine prototype: reader, injector, audio loop. |
| `recordings/` | Traffic captures used by the injector. Committed when small. |
