# Handoff · 2026-09-06, stage 4 in progress

Where the implementation stands and what the next session does. Delete this file when the
list is done and the log has absorbed anything worth keeping.

## Branch `ahead-traffic-atc` (worktree `C:\Users\wayne\dev\fsc\ahead-traffic-atc`)

Commits: d9d3c14 SimTraffic · aaa0a30 Settings · 74b99c2 packets + election · 62ecb93
traffic · f24c9ca audio · 6508a74 the ATC & Traffic card · 0f6a6bf copy edits.

**Uncommitted, unbuilt: the layout pass** (`MainWindow.axaml`, `MainWindowStyles.axaml`,
`ShareViewModel.cs`). Approved by the user item by item:

1. Feature rows fixed at 32 px (`DockPanel.ShareRow`) so the card does not change height
   between toggle and "shared by" states.
2. The other-AI-traffic warning stays in place under the Traffic label, amber `#FFC107`
   (`ShareStatus Warning`). *Not* moved to a note line — the user rejected that.
3. ATC row + app dropdown / ATC row + volume slider grouped in a `StackPanel Spacing="6"`.
4. Dropdown matches the Client code TextBox: padding 10, background `#66000000`, and the
   theme's accent (purple on this machine) overridden for focus and item selection.
5. Slider row 32 px, 16 px thumb; mute button 32×32 with a 16 px icon.
6. Toggle margin `10,0,-12,0` to swallow the Fluent template's 12 px content column so the
   switch sits flush with the card's right edge.
7. Notes left as they are (user: they serve different purposes).
8. Status strings lost their ● / ○ prefixes and are capitalised.

Next: stop any running `FsCopilot.exe`, `dotnet build FsCopilot/FsCopilot.csproj -c Debug`,
copy `bin/Debug/net9.0/win-x64` to `win-x64-b`, launch A (`--skip-install --debug`) and B
(`--skip-install --debug --traffic-shadow 80`), join B to A with A's code, switch both
toggles on in A, screenshot the idle / hosting / receiving states, show the user, commit.

## After that

- Readme note: same traffic pack and scenery, own AI traffic off; Windows 10 2004+ to host
  ATC audio.
- Rebuild `ahead` with `profiles/tools/ahead-rebuild.sh`.
- Untested: fallback titles (helicopter, MSFS 2020), two-machine test (needs a second
  tester), Tier 1 teardown checks beyond the session-end path (which is verified: leave →
  hosting stops on A, receiver clears on B, both cards disabled).

## Decisions that are easy to lose

- No arming: controls disabled until a peer is connected; whoever shares first hosts;
  hosting ends with the session; toggles are not persisted (only ATC app, volume, mute).
- Headset icon for the card title (the user tried a tower and reverted: "let's not introduce
  new patterns").
- Window height 800.
- No take-over, no "None"/"Automatic" dropdown entries, no counts, no bottom-strip badges.
- All new copy was reviewed by the user on 2026-09-06; the current strings are approved.
