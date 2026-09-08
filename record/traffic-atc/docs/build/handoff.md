# Handoff · 2026-09-06, stage 4 built

Where the implementation stands and what the next session does. Delete this file when the
list is done and the log has absorbed anything worth keeping.

## Branch `ahead-traffic-atc` (worktree `C:\Users\wayne\dev\fsc\ahead-traffic-atc`)

Commits: d9d3c14 SimTraffic · aaa0a30 Settings · 74b99c2 packets + election · 62ecb93
traffic · f24c9ca audio · 6508a74 the ATC & Traffic card · 0f6a6bf copy edits · 62a61a2
the card's layout.

**Tree is clean and the card is done.** Built, run on the two-instance bed against a live
sim, and reviewed by hand. Nothing about the card is outstanding.

## What is left

1. **Readme note.** Same traffic pack and scenery on every machine, own AI traffic off;
   Windows 10 2004+ to host ATC audio.
2. **Rebuild `ahead`** with `profiles/tools/ahead-rebuild.sh`.
3. **Two-machine test.** Needs a second tester, as pointer-forwarding did. Everything so
   far is one machine, two app instances, one sim.
4. **Still untested:** fallback titles (helicopter, MSFS 2020); Tier 1 teardown beyond the
   session-end path, which is verified (leave → hosting stops on A, receiver clears on B,
   both cards disabled).
5. **Relay (2026-09-08, see `docs/04-transport.md`).** The fork now speaks relay protocol v2,
   which upstream's `p2p.fscopilot.com` does not: deploy `FsCopilot.Discovery` from this tree
   to the fork's own host and put that host in `Program.RelayHost`. Until then a bed needs a
   local relay (`dotnet run --project FsCopilot.Discovery -r win-x64 -p:SelfContained=false`)
   and both instances started with `--relay localhost --no-direct`. Run the two-instance bed
   that way once: traffic and ATC over the relay path have only been probed headlessly.

## The two-instance bed

Build, copy `bin/Debug/net9.0/win-x64` to `win-x64-b`, launch A (`--skip-install --debug`)
and B (`--skip-install --debug --traffic-shadow 80`), join B to A with A's code. The whole
loop scripts unattended — rebuild, relaunch, join, toggle, screenshot both windows — which
puts a layout change at about 20 seconds.

**A's client code is regenerated every run.** It is not derived from the install directory,
so a driver that reuses the previous run's code fails to join. Worse, the pair reconnects on
its own afterwards, so the failed join looks like it worked; read the code off the window
each run and check the Onboard list before trusting a screenshot.

`--traffic-shadow 80` on B is what makes the amber other-AI-traffic warning appear, so that
state is really exercised.

## Decisions that are easy to lose

- No arming: controls disabled until a peer is connected; whoever shares first hosts;
  hosting ends with the session; toggles are not persisted (only ATC app, volume, mute).
  A `settings.json` from an older build may still carry `shareTraffic` / `shareAtc` keys —
  `Settings` has no such properties, they are ignored and dropped on the next write.
- Headset icon for the card title (the user tried a tower and reverted: "let's not introduce
  new patterns").
- Window height 800.
- No take-over, no "None"/"Automatic" dropdown entries, no counts, no Onboard chips — the
  chips existed and were removed, because the card already says who shares what.
- A feature is one line: name in label type, status beside it in status type. The
  other-AI-traffic warning is the one status that stacks under the label instead.
- Feature rows are 32 px; the volume slider is not a feature row and must not be pinned to
  that height.
- All copy was reviewed by the user on 2026-09-06; the current strings are approved.
