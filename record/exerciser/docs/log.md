# Exerciser log

    Purpose:  What the probes found, and what it changed.
    Design:   01-design.md
    Order:    newest first

Same entry format as `record/pointer-forwarding/docs/build/log.md`. `Affects:` names the
design sections an entry amends.

---

## 2026-09-17 — FSC sends panel rects to a watcher, and most cockpit hellos have none

    Question:  where the pointer page gets the instrument's aspect ratio (01-design OPEN)
    Expected:  That FSC could pass on the rect each hello already carries, and that a
               hello without one would be a rare boot-order case.
    Found:     The first holds. b9a03a4 on ahead-pointer-forwarding: a client sends
               {t:"watch"} and gets config, state and {t:"panels"}, every helloed key with
               its rect, re-sent when the list changes. Seven protocol checks pass against
               fake panels, including that panels which never watch never receive it.

               The second is false. With the A220 loaded for about 26 minutes, its
               cockpit panels connected and 9 of 14 hellos had no rect, CTP and MKP among
               them. DisplayUnits (7410x1110) and three FCPs had one; WasmInstrument
               reported 10x10. hook.js measures once, in the Hook constructor, before
               those instruments are laid out, and channel.js re-sends that hello
               unchanged on every reconnect. So null is the normal case for most
               instruments, and it does not heal while the document lives.
    Changed:   The rect source is decided and built. The pointer page cannot rely on it
               yet: the fix is on the panel side (measure when the hello is sent, and
               hello again once a rect first appears), which is a bridge package change
               and not yet agreed.
    Affects:   01-design "Mapping"
    Evidence:  results/p03-panel-watch-2026-09-17.txt, probes/p03-panel-watch/

## 2026-09-17 — A pop-out capture maps to rect fractions by contain-fit, within 6.5 px

    Question:  can the exerciser show a live image of a panel and turn a click on it into
               the fractions pointer.js uses?
    Expected:  That the Coherent inspector could supply the image, and that a pop-out, if
               needed instead, might crop or stretch the instrument unpredictably.
    Found:     The inspector cannot. It has no Page.captureScreenshot and no screencast,
               and refuses Page.snapshotRect and Page.snapshotNode with "Could not capture
               snapshot".

               The OS can. PrintWindow with PW_RENDERFULLCONTENT returns the sim's DirectX
               windows with real content, including the parts of a window that are off the
               monitor. An A220 DISPLAYUNITS pop-out is its own top-level window, class
               AceApp, titled with the instrument identifier.

               Five squares placed through the inspector at known fractions of the
               instrument rect (7410x1110) were found in the pop-out capture (7394x1071).
               The sim draws the instrument contain-fit and centred: height fills the
               window at scale 0.965, width is scaled by the same factor, and about 122 px
               of bar sits either side. Predicting marker positions with that rule is off
               by at most 6.3 px, at the right edge; the residual is the sim's horizontal
               scale running 0.15% under its vertical one. In instrument pixels that is
               6.5 px across a 7410 px strip.

               PrintWindow cost: 33.5 ms median on the 2401x1360 main window, 62.6 ms on
               the 7394x1071 pop-out. The call re-renders the whole window whatever part
               is shown.
    Changed:   Capture is window capture, not the inspector, so the exerciser needs no
               inspector and works with DevMode off. Mapping needs one number the capture
               does not carry: the instrument's aspect ratio. Contain-fit is exact enough
               to use without calibration. PrintWindow's cost at strip sizes points to
               Windows.Graphics.Capture for anything recorded.
    Affects:   01-design "Capture" and "Mapping"
    Evidence:  results/p02-popout-mapping-2026-09-17.txt,
               results/p02-popout-markers-2026-09-17.png, probes/p02-popout-mapping/.
               capture.ps1 gathers commands first run inline; it was re-run against the
               same pop-out and gave identical centroids.

## 2026-09-17 — A separate process can join FSC as a peer and trade PointerEvents both ways

    Question:  can the exerciser be a peer of an unmodified FSC build rather than a mode
               inside it?
    Expected:  Yes, if it registers the same packets in the same order; unknown whether
               two peers on one machine would link through the public rendezvous.
    Found:     Yes, in both directions, against ahead-pointer-forwarding at 1ba64cc. The
               probe's press reached a fake panel unchanged and FSC acked it; a capture
               from the panel reached the probe unchanged.

               Codecs.Schema hashes each packet type's assembly-qualified name, so the
               exerciser has to register FSC's own types, not copies. Three are private
               nested types (Coordinator.Update, Coordinator.InteractCodec,
               MasterSwitch.SetMaster); the probe reached them by reflection.

               The link went Relay and stayed there for 45 s. FSC's direct attempt was
               rejected NOT_FOUND and not retried. Relay ping 113-180 ms through
               51.250.94.202; ICMP from this machine is 72 ms to that relay and 116 ms to
               fscrelay.ihsan.dev, and a same-machine pair crosses the relay twice.

               A native WebSocket client (no Origin header) is accepted by PanelServer.
               Its hello, under a key nobody configured, gets {t:"config"} with the live
               pointer key list and the {t:"state"} broadcasts, and nothing is routed to
               it. When the sim loaded Synaptic_A220 mid-run, the A220's keys arrived on
               that connection unprompted.

               The first run failed for a reason outside the question: the sim was
               starting, FSC loaded a profile-less F-35 and broadcast an empty key list
               over the bench's, and dropped the probe's press at the inbound filter.
    Changed:   The exerciser is a peer. Panel list and session state come from FSC's
               panel channel, so no aircraft or profile detection. One-machine use wants a
               local relay: the public one adds ~145 ms each way and fscrelay more.
               Wire compatibility is an open decision (reflection or InternalsVisibleTo).
    Affects:   01-design "Session", "Relay", "Wire compatibility"
    Evidence:  results/p01-peer-join-2026-09-17.txt, probes/p01-peer-join/
